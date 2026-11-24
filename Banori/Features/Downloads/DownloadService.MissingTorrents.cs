using Banori.Data;
using Banori.Features.Settings;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;

namespace Banori.Features.Downloads;

/// <summary>
///     种子缺失巡检：只观察与记录下载器中是否仍存在已完成任务的种子，不翻转 Status、不删除文件、
///     不重新添加种子。首次成功查无留下待复核证据，第二次成功查无且配置一致才确认缺失；
///     三态（查到／成功查无／未知）严格区分，未知不写任何字段。
/// </summary>
public sealed partial class DownloadService
{
    private sealed record MissingCheckCandidate(
        Guid              Id,
        string            InfoHash,
        string            SavePath,
        DownloadTaskStatus Status,
        DateTimeOffset?   NextMissingCheckAtUtc,
        DateTimeOffset?   TorrentMissingAtUtc,
        DateTimeOffset?   MissingCheckPendingAtUtc,
        string?           MissingCheckConfigKey);

    private static readonly Expression<Func<DownloadTask, MissingCheckCandidate>> MissingCheckProjection =
        task => new MissingCheckCandidate(task.Id, task.InfoHash, task.SavePath, task.Status,
                                          task.NextMissingCheckAtUtc, task.TorrentMissingAtUtc,
                                          task.MissingCheckPendingAtUtc, task.MissingCheckConfigKey);

    /// <summary>
    ///     一轮缺种巡检。协调门只包住「读候选 + 一次批量查询 + 条件写」；复查等待靠 NextMissingCheckAtUtc
    ///     到期由下一轮驱动，不在门内 Task.Delay。登录失败／超时／解析失败一律按未知跳过。
    /// </summary>
    internal async Task ReconcileMissingTorrentsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);

        var now       = timeProvider.GetUtcNow();
        var batchSize = Math.Max(1, timing.MissingCheckBatchSize);
        var batch     = await LoadMissingCheckBatchAsync(now, batchSize, cancellationToken);
        // 没有到期候选就不登录 qB：空闲轮次不再因为巡检产生连接与查询。
        if (batch.Count == 0) return;

        // 指纹与会话必须来自同一次读取：两次读取之间切换 qB 时不能把 A/B 两次查无当作同一配置的确认。
        var (settings, password) = await qbittorrentSettings.GetConnectionAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return;
        var configKey = BuildMissingCheckConfigKey(settings);

        // 同一 hash 的终态任务可并存，去重后只发一次批量查询，复用现有会话接口。
        var hashes = batch.Select(candidate => candidate.InfoHash)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        HashSet<string> foundHashes;
        try
        {
            await using var session = await sessionFactory.CreateAsync(settings, password, cancellationToken);
            var found = await session.FindTorrentsAsync(hashes, cancellationToken);
            foundHashes = found.Select(snapshot => snapshot.Hash)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 未知：不写任何字段、不推进到期，留待下一轮。
            logger.LogWarning(exception, "缺种巡检：查询下载器失败，本轮跳过。");
            return;
        }

        foreach (var candidate in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyMissingCheckAsync(candidate, foundHashes.Contains(candidate.InfoHash),
                                         configKey, now, cancellationToken);
        }
    }

    /// <summary>
    ///     取有界巡检批次：已到期复核优先，剩余名额再给未排期存量。SQLite 不支持 DateTimeOffset 的
    ///     服务端排序/比较，时间排序交给数据库存储列（provider 中立 SQL），到期判断在内存完成；
    ///     两者都按稳定键取，避免早期记录先被 Take 截掉或同一时刻互相挤出批次。
    /// </summary>
    private async Task<List<MissingCheckCandidate>> LoadMissingCheckBatchAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scheduled = await db.DownloadTasks
                                .FromSql($"""
                                     SELECT * FROM DownloadTasks
                                     WHERE Status = {"Completed"} AND NextMissingCheckAtUtc IS NOT NULL
                                     ORDER BY NextMissingCheckAtUtc, InfoHash, Id
                                     LIMIT {batchSize}
                                     """)
                                .AsNoTracking()
                                .Select(MissingCheckProjection)
                                .ToListAsync(cancellationToken);

        var batch     = scheduled.Where(candidate => candidate.NextMissingCheckAtUtc <= now)
                                 .Take(batchSize).ToList();
        var remaining = batchSize - batch.Count;
        if (remaining > 0)
        {
            var backlog = await db.DownloadTasks.AsNoTracking()
                                  .Where(task => task.Status == DownloadTaskStatus.Completed &&
                                                 task.NextMissingCheckAtUtc == null)
                                  .OrderBy(task => task.InfoHash).ThenBy(task => task.Id)
                                  .Take(remaining)
                                  .Select(MissingCheckProjection)
                                  .ToListAsync(cancellationToken);
            batch.AddRange(backlog);
        }

        return batch;
    }

    private async Task ApplyMissingCheckAsync(
        MissingCheckCandidate candidate, bool found, string configKey,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var current = await db.DownloadTasks.AsNoTracking()
                              .Where(task => task.Id == candidate.Id)
                              .Select(task => new
                              {
                                  task.Status, task.InfoHash, task.TorrentMissingAtUtc,
                                  task.MissingCheckPendingAtUtc, task.MissingCheckConfigKey
                              })
                              .SingleOrDefaultAsync(cancellationToken);

        // 应用结果前核对任务仍是 Completed 且 hash 未变；期间被用户操作则整条跳过。
        if (current is null || current.Status != DownloadTaskStatus.Completed ||
            !string.Equals(current.InfoHash, candidate.InfoHash, StringComparison.OrdinalIgnoreCase))
            return;

        DateTimeOffset? missingAt        = current.TorrentMissingAtUtc;
        DateTimeOffset? pendingAt        = current.MissingCheckPendingAtUtc;
        string?         storedConfigKey  = current.MissingCheckConfigKey;
        DateTimeOffset  nextCheck;

        if (found)
        {
            // 种子回来：清除全部缺种证据，按长间隔继续观察。
            missingAt       = null;
            pendingAt       = null;
            storedConfigKey = null;
            nextCheck       = LongDue(candidate.InfoHash, candidate.Id, now);
        }
        else if (current.TorrentMissingAtUtc is null)
        {
            if (current.MissingCheckPendingAtUtc is null ||
                !string.Equals(current.MissingCheckConfigKey, configKey, StringComparison.Ordinal))
            {
                // 首次查无，或 qB 配置已变化：重设待复核证据。
                pendingAt       = now;
                storedConfigKey = configKey;
                nextCheck       = now + timing.MissingCheckReviewInterval;
            }
            else
            {
                // 第二次成功查无且配置一致、任务与 hash 均未变：确认缺失。
                missingAt       = now;
                pendingAt       = null;
                storedConfigKey = null;
                nextCheck       = LongDue(candidate.InfoHash, candidate.Id, now);
            }
        }
        else
        {
            // 已确认缺失且仍未查到：保留首次确认时间，只推进下次复查。
            nextCheck = LongDue(candidate.InfoHash, candidate.Id, now);
        }

        await db.DownloadTasks
                .Where(task => task.Id == candidate.Id &&
                               task.Status == DownloadTaskStatus.Completed &&
                               task.InfoHash == candidate.InfoHash)
                .ExecuteUpdateAsync(setters => setters
                                                   .SetProperty(task => task.TorrentMissingAtUtc, missingAt)
                                                   .SetProperty(task => task.MissingCheckPendingAtUtc, pendingAt)
                                                   .SetProperty(task => task.MissingCheckConfigKey, storedConfigKey)
                                                   .SetProperty(task => task.NextMissingCheckAtUtc, nextCheck),
                                     cancellationToken);
        if (missingAt != current.TorrentMissingAtUtc) changeNotifier?.Notify(candidate.Id, true);
    }

    private DateTimeOffset LongDue(string infoHash, Guid taskId, DateTimeOffset now)
    {
        var window = timing.MissingCheckJitterWindow;
        var jitter = window > TimeSpan.Zero
            ? TimeSpan.FromTicks((long)StableBucket($"{infoHash}:{taskId:D}", (ulong)window.Ticks))
            : TimeSpan.Zero;
        return now + timing.MissingCheckLongInterval + jitter;
    }

    /// <summary>从 qB 连接地址、用户名、保存路径与代理派生稳定指纹；不含密码，长度固定为 64 位十六进制。</summary>
    internal static string BuildMissingCheckConfigKey(QbittorrentSettings settings)
    {
        var material = string.Join('\u001f', settings.BaseUrl.Trim().TrimEnd('/'), settings.Username,
                                   settings.SavePath.Trim(), settings.ProxyId?.ToString() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static ulong StableBucket(string key, ulong modulus)
    {
        if (modulus == 0) return 0;
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key))) % modulus;
    }
}
