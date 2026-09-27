using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Dashboard;

/// <summary>总览页统计查询。</summary>
public sealed class DashboardQueries(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             enabled = await db.MediaSubscriptions.CountAsync(item => item.IsEnabled, cancellationToken);
        var pending = await db.RefreshWorkItems.CountAsync(item => item.Status == RefreshWorkStatus.Pending ||
                                                                   item.Status == RefreshWorkStatus.Running,
                                                           cancellationToken);
        var accepted = await db.FeedItems.CountAsync(item => item.Status == FeedItemStatus.Accepted, cancellationToken);
        var attention = await db.FeedItems.CountAsync(item => item.Status == FeedItemStatus.NeedsReview ||
                                                              item.Status == FeedItemStatus.Failed,
                                                      cancellationToken);
        var activeDownloads =
            await db.DownloadTasks.CountAsync(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                                      task.Status == DownloadTaskStatus.Submitting    ||
                                                      task.Status == DownloadTaskStatus.Downloading   ||
                                                      task.Status == DownloadTaskStatus.Paused,
                                              cancellationToken);
        var completionTimes = await db.MediaSubscriptions.AsNoTracking()
                                      .Select(item => item.LastRefreshCompletedAtUtc)
                                      .ToListAsync(cancellationToken);
        return new DashboardSnapshot(enabled, pending, accepted, attention, activeDownloads,
                                     completionTimes.Where(value => value is not null).Max());
    }
}
