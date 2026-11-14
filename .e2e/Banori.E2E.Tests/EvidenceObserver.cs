using System.Text;
using System.Text.Json;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.E2E.Tests;

/// <summary>
///     观察器：轮询 DownloadTask 全字段 + qB torrents 快照，追加写入证据 JSONL
///     （每行一个采样点：时间戳、任务字段快照、qB 侧种子状态）。轮询默认 2s，
///     总超时参数化（默认 10min）。只读观察，绝不修改数据库或 qB 状态。
/// </summary>
public sealed class EvidenceObserver(AcceptanceHost host, QbApiClient qb, string evidencePath)
{
    public async Task ObserveUntilAsync(Func<DownloadTask?, IReadOnlyList<QbTorrentInfo>, bool> done,
                                        TimeSpan? timeout = null, TimeSpan? interval = null,
                                        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));
        var step     = interval ?? TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var task     = await LoadTaskAsync(cancellationToken);
            var torrents = await qb.ListTorrentsAsync(cancellationToken);
            AppendSample(task, torrents);
            if (done(task, torrents))
                return;
            await Task.Delay(step, cancellationToken);
        }

        throw new TimeoutException(
            $"观察超时（{(timeout ?? TimeSpan.FromMinutes(10)).TotalSeconds:F0}s），最后状态已写入 {evidencePath}。");
    }

    /// <summary>取最近创建的任务（含文件表）。SQLite 不支持 DateTimeOffset 的 ORDER BY，
    /// 全量取回后内存排序（与 DownloadTaskQueries 的键集排序同约束）。</summary>
    public async Task<DownloadTask?> LoadTaskAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                             .CreateDbContextAsync(cancellationToken);
        var tasks = await db.DownloadTasks.AsNoTracking()
                            .Include(value => value.Files)
                            .ToListAsync(cancellationToken);
        return tasks.OrderByDescending(value => value.CreatedAtUtc).FirstOrDefault();
    }

    private void AppendSample(DownloadTask? task, IReadOnlyList<QbTorrentInfo> torrents)
    {
        var sample = new
        {
            timestamp_utc = DateTime.UtcNow,
            task = task is null ? null : new
            {
                task.Id,
                task.InfoHash,
                task.Status,
                task.PreparationStage,
                task.IsSystemPaused,
                task.IsUserPaused,
                task.IsolationState,
                task.SubjectId,
                task.Episode,
                task.Version,
                task.NamingSnapshot,
                task.NamingIngredients,
                task.SiteSource,
                task.SavePath,
                task.DownloadRootPath,
                task.MediaDirectory,
                task.SeedDirectory,
                task.PathDecision,
                task.VerificationIntent,
                task.RunRecovery,
                task.ArchiveReview,
                task.Progress,
                task.TotalSize,
                task.SelectedTotalBytes,
                task.SelectionSyncPending,
                task.PendingSavePath,
                task.FailedStep,
                task.Error,
                task.SubmittedAtUtc,
                task.CompletedAtUtc,
                task.NextObservationAtUtc,
                task.LastObservedCategory,
                files = task.Files.Select(file => new
                {
                    file.FileIndex,
                    file.OriginalRelativePath,
                    file.Kind,
                    file.IsSelectedForDownload,
                    file.SelectionReason,
                    file.VerificationOutcome,
                    file.CurrentRelativePath,
                    file.Size
                }).ToList()
            },
            qb_torrents = torrents.Select(torrent => new
            {
                torrent.Hash,
                torrent.Name,
                torrent.State,
                torrent.Progress,
                torrent.Size,
                torrent.Downloaded,
                torrent.AmountLeft,
                torrent.SavePath,
                torrent.Tags
            }).ToList()
        };
        File.AppendAllText(evidencePath, JsonSerializer.Serialize(sample, JsonOptions) + "\n", Encoding.UTF8);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };
}
