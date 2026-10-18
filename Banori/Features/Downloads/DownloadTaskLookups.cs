using Banori.Data;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     下载任务的数据库级查重与替换目标查找：登记入口与完成链共用同一规则，避免两份实现漂移。
/// </summary>
internal static class DownloadTaskLookups
{
    /// <summary>
    ///     活动任务查重：同 hash 一律冲突；因低优先级被替换的终止记录同样挡住重复登记（其存在即查重依据，
    ///     重复添加该资源直接终止）；条目路径额外覆盖同条目换源的情况，上传路径没有条目来源。
    /// </summary>
    internal static async Task<Guid?> FindActiveDuplicateIdAsync(
        ApplicationDbContext db, string infoHash, Guid? feedItemId, CancellationToken cancellationToken)
    {
        var query = db.DownloadTasks.AsNoTracking()
                      .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                     task.Status == DownloadTaskStatus.Submitting    ||
                                     task.Status == DownloadTaskStatus.Downloading   ||
                                     task.Status == DownloadTaskStatus.Paused        ||
                                     task.Status == DownloadTaskStatus.Replaced);
        query = feedItemId is null
            ? query.Where(task => task.InfoHash == infoHash)
            : query.Where(task => task.InfoHash == infoHash || task.FeedItemId == feedItemId);
        return await query.Select(task => (Guid?)task.Id).FirstOrDefaultAsync(cancellationToken);
    }

    internal static async Task<Guid?> FindCompletedDuplicateIdAsync(
        ApplicationDbContext db, string infoHash, CancellationToken cancellationToken)
    {
        return await db.DownloadTasks.AsNoTracking()
                       .Where(task => task.InfoHash == infoHash &&
                                      task.Status   == DownloadTaskStatus.Completed)
                       .Select(task => (Guid?)task.Id)
                       .FirstOrDefaultAsync(cancellationToken);
    }
}
