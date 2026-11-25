using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>全局分享率同步到 qBittorrent 的执行结果。</summary>
public sealed record ShareLimitSyncResult(
    int     MatchedTasks,
    int     SyncedTorrents,
    bool    DownloaderConfigured,
    string? ErrorMessage);

/// <summary>
///     分享率配置的全局实时同步与提交/接管下发：
///     按任务 SiteSource 命中当前配置，未分配分组的来源一律沿用 qBittorrent 全局限制。
/// </summary>
public sealed partial class DownloadService
{
    /// <summary>
    ///     分享率配置保存后调用：把受影响分组（或全部任务）在 qBittorrent 中存在的种子批量更新为最新分享限制。
    ///     下载器未启用或临时不可达时不抛异常，由返回值向设置页汇报结果。
    /// </summary>
    public async Task<ShareLimitSyncResult> SyncShareLimitsAsync(
        IReadOnlyCollection<string>? affectedSiteSources = null, CancellationToken cancellationToken = default)
    {
        if (shareLimitSettingsStore is null) return new ShareLimitSyncResult(0, 0, false, null);

        var shareLimits = await shareLimitSettingsStore.GetAsync(cancellationToken);
        HashSet<string>? targetSources = affectedSiteSources is { Count: > 0 }
            ? affectedSiteSources.Select(ShareLimitSettings.NormalizeSiteSource)
                                 .Where(source => source is not null)
                                 .Select(source => source!)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        List<(string InfoHash, string? SiteSource)> candidates;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var tasks = await db.DownloadTasks.AsNoTracking()
                                .Where(task => task.TorrentMissingAtUtc == null &&
                                               (task.Status == DownloadTaskStatus.Submitting  ||
                                                task.Status == DownloadTaskStatus.Downloading ||
                                                task.Status == DownloadTaskStatus.Paused      ||
                                                task.Status == DownloadTaskStatus.Completed))
                                .Select(task => new { task.InfoHash, task.SiteSource })
                                .ToListAsync(cancellationToken);

            candidates = tasks.Where(task => !string.IsNullOrWhiteSpace(task.InfoHash))
                              .Where(task => targetSources is null ||
                                             (ShareLimitSettings.NormalizeSiteSource(task.SiteSource) is { } source &&
                                              targetSources.Contains(source)))
                              .Select(task => (task.InfoHash.Trim().ToLowerInvariant(), task.SiteSource))
                              .DistinctBy(item => item.Item1, StringComparer.OrdinalIgnoreCase)
                              .ToList();
        }

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var (settings, _) = await qbittorrentSettings.GetConnectionAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            return new ShareLimitSyncResult(candidates.Count, 0, false, null);

        if (candidates.Count == 0)
            return new ShareLimitSyncResult(0, 0, true, null);

        try
        {
            await using var session  = await sessionFactory.CreateAsync(cancellationToken);
            var             existing = await session.FindTorrentsAsync(candidates.Select(item => item.InfoHash).ToArray(),
                                                                       cancellationToken);
            var existingHashes = existing.Select(snapshot => snapshot.Hash)
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var groups = candidates.Where(item => existingHashes.Contains(item.InfoHash))
                                   .GroupBy(item => shareLimits.ResolveForSiteSource(item.SiteSource))
                                   .ToList();

            var syncedCount = 0;
            foreach (var group in groups)
            {
                var hashes = group.Select(item => item.InfoHash).ToArray();
                if (hashes.Length == 0) continue;

                var limit = group.Key;
                await session.SetShareLimitsAsync(hashes, limit.RatioLimit, limit.SeedingTimeLimitMinutes,
                                                  limit.InactiveSeedingTimeLimitMinutes, cancellationToken);
                syncedCount += hashes.Length;
            }

            logger.LogInformation("分享率配置已同步到下载器：命中 {Matched} 个任务，实际更新 {Synced} 个种子。",
                                  candidates.Count, syncedCount);
            return new ShareLimitSyncResult(candidates.Count, syncedCount, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "分享率配置同步到下载器失败。");
            return new ShareLimitSyncResult(candidates.Count, 0, true, exception.Message);
        }
    }

    /// <summary>提交或接管种子后按当前分享率配置下发限制；仅当命中非全局策略时才额外调用下载器。</summary>
    private async Task ApplyConfiguredShareLimitAsync(
        IQbittorrentSession session, string infoHash, string? siteSource, CancellationToken cancellationToken)
    {
        if (shareLimitSettingsStore is null || string.IsNullOrWhiteSpace(infoHash)) return;

        var settings = await shareLimitSettingsStore.GetAsync(cancellationToken);
        var limit    = settings.ResolveForSiteSource(siteSource);
        if (limit.IsGlobal) return;

        try
        {
            await session.SetShareLimitsAsync([infoHash], limit.RatioLimit, limit.SeedingTimeLimitMinutes,
                                              limit.InactiveSeedingTimeLimitMinutes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "为种子 {InfoHash}（来源 {SiteSource}）设置分享率限制失败。",
                              infoHash, siteSource ?? "(none)");
        }
    }
}
