using Banned.Qbittorrent.Exceptions;
using Banori.Core.Rules;
using Banori.Core.Torrents;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     种子名模板实时同步：不读取也不回写 NamingIngredients.Template 快照。
///     模板变更时按单集／合集分流更新未删除且未丢失任务的目标名，并由对账异步推送到 qBittorrent。
/// </summary>
public sealed partial class DownloadService
{
    private sealed record PendingTorrentRename(
        Guid               Id,
        string             InfoHash,
        string             NamingSnapshot,
        DownloadTaskStatus Status);

    /// <summary>
    ///     种子名模板修改后调用：单集模板变更仅通知单集任务，合集模板变更仅通知多集合集任务。
    ///     先落库更新目标名并标记待改名，再唤醒对账异步同步到 qBittorrent；不改写 NamingIngredients。
    /// </summary>
    public async Task<int> NotifyTorrentNamingChangedAsync(
        bool singleChanged, bool collectionChanged, CancellationToken cancellationToken = default)
    {
        if (!singleChanged && !collectionChanged) return 0;
        if (namingTemplateStore is null) return 0;

        var singleTemplate = singleChanged
            ? await namingTemplateStore.GetAsync(cancellationToken)
            : null;
        var collectionTemplate = collectionChanged
            ? await namingTemplateStore.GetCollectionTemplateAsync(cancellationToken)
            : null;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.DownloadTasks
                            .Where(task => task.Status != DownloadTaskStatus.Lost &&
                                           task.Status != DownloadTaskStatus.Replaced)
                            .ToListAsync(cancellationToken);

        var affectedIds            = new List<Guid>();
        var hasPendingRemoteRename = false;
        foreach (var task in tasks)
        {
            var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
            if (ingredients is null ||
                string.IsNullOrWhiteSpace(ingredients.Title) ||
                string.IsNullOrWhiteSpace(ingredients.EpisodeText))
                continue;

            var isCollection = ReleaseNamePreviewPolicy.IsCollectionEpisodeText(ingredients.EpisodeText);
            if (isCollection && !collectionChanged) continue;
            if (!isCollection && !singleChanged) continue;

            var activeTemplate = isCollection ? collectionTemplate : singleTemplate;
            var targetName     = namingPolicy.Create(ingredients with { Template = activeTemplate });
            if (string.Equals(task.NamingSnapshot, targetName, StringComparison.Ordinal)) continue;

            task.NamingSnapshot = targetName;
            if (task is
                {
                    Status: DownloadTaskStatus.Submitting
                         or DownloadTaskStatus.Downloading
                         or DownloadTaskStatus.Paused
                         or DownloadTaskStatus.Completed,
                    TorrentMissingAtUtc: null
                })
            {
                task.TorrentRenamePending = true;
                hasPendingRemoteRename    = true;
            }

            affectedIds.Add(task.Id);
        }

        if (affectedIds.Count == 0) return 0;

        await db.SaveChangesAsync(cancellationToken);
        changeNotifier?.Notify(affectedIds, false);
        if (hasPendingRemoteRename) reconcilePulse?.Pulse();
        logger.LogInformation("种子名模板已变更，已更新 {Count} 个任务的目标名称（单集={Single}，合集={Collection}）。",
                              affectedIds.Count, singleChanged, collectionChanged);
        return affectedIds.Count;
    }

    /// <summary>按当前全局模板渲染种子名，忽略 NamingIngredients 中可能残留的旧 Template 字段。</summary>
    private async Task<string> RenderLiveTorrentNameAsync(
        TorrentNamingIngredients? ingredients, string fallbackName, CancellationToken cancellationToken)
    {
        if (ingredients is null || namingTemplateStore is null) return fallbackName;

        var liveTemplate = ReleaseNamePreviewPolicy.IsCollectionEpisodeText(ingredients.EpisodeText)
            ? await namingTemplateStore.GetCollectionTemplateAsync(cancellationToken)
            : await namingTemplateStore.GetAsync(cancellationToken);
        return namingPolicy.Create(ingredients with { Template = liveTemplate });
    }

    private async Task SyncPendingTorrentRenamesUnderGateAsync(
        IQbittorrentSession session, List<PendingTorrentRename> pendingRenames, CancellationToken cancellationToken)
    {
        if (pendingRenames.Count == 0) return;

        var remoteTargets = new List<PendingTorrentRename>();
        foreach (var item in pendingRenames)
        {
            if (item.Status is DownloadTaskStatus.Submitting
                            or DownloadTaskStatus.Downloading
                            or DownloadTaskStatus.Paused
                            or DownloadTaskStatus.Completed)
            {
                remoteTargets.Add(item);
                continue;
            }

            await ClearTorrentRenamePendingAsync(item.Id, null, cancellationToken);
        }

        if (remoteTargets.Count == 0) return;

        var hashes = remoteTargets.Select(item => item.InfoHash)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .ToList();
        var snapshots = (await session.FindTorrentsAsync(hashes, cancellationToken))
                       .GroupBy(snapshot => snapshot.Hash, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in remoteTargets)
        {
            if (!snapshots.TryGetValue(item.InfoHash, out var snapshot))
            {
                // 已完成任务若已从下载器移除，清除待改名标记避免空转；未完成任务留给常规对账处理。
                if (item.Status == DownloadTaskStatus.Completed)
                    await ClearTorrentRenamePendingAsync(item.Id, item.NamingSnapshot, cancellationToken);
                continue;
            }

            try
            {
                if (!string.Equals(snapshot.Name, item.NamingSnapshot, StringComparison.Ordinal))
                    await session.RenameTorrentAsync(snapshot.Hash, item.NamingSnapshot, cancellationToken);

                await ClearTorrentRenamePendingAsync(item.Id, item.NamingSnapshot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (QbittorrentNotFoundException)
            {
                if (item.Status == DownloadTaskStatus.Completed)
                    await ClearTorrentRenamePendingAsync(item.Id, item.NamingSnapshot, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "同步种子改名失败：任务 {TaskId}，目标名 {TargetName}，将在下一轮重试。",
                                  item.Id, item.NamingSnapshot);
            }
        }
    }

    private async Task ClearTorrentRenamePendingAsync(
        Guid taskId, string? expectedName, CancellationToken cancellationToken)
    {
        await using var db     = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             entity = await db.DownloadTasks.SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
        if (entity is null || !entity.TorrentRenamePending) return;
        if (expectedName is not null && !string.Equals(entity.NamingSnapshot, expectedName, StringComparison.Ordinal))
            return;

        entity.TorrentRenamePending = false;
        await db.SaveChangesAsync(cancellationToken);
    }
}
