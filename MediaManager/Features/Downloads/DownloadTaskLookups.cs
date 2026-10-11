using MediaManager.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Downloads;

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

    /// <summary>
    ///     查找本任务完成后应标记为被替换的旧任务：同 Subject + Episode、不同 hash、未被他任务替换的已完成任务，
    ///     优先更高版本。失败任务没有可供替代的旧文件；不越过比自己更高的版本，避免旧版本反向替换新资源。
    /// </summary>
    internal static async Task<Guid?> FindReplacementTargetAsync(ApplicationDbContext db, int? subjectId,
                                                                 decimal? episode, string infoHash, int version,
                                                                 CancellationToken cancellationToken)
    {
        if (subjectId is null || episode is null) return null;

        var candidates = await db.DownloadTasks.AsNoTracking()
                                 .Where(candidate => candidate.SubjectId        == subjectId                    &&
                                                     candidate.Episode          == episode                      &&
                                                     candidate.InfoHash         != infoHash                     &&
                                                     candidate.ReplacedByTaskId == null                         &&
                                                     candidate.Status           == DownloadTaskStatus.Completed &&
                                                     candidate.Version          <= version)
                                 .Select(candidate => new { candidate.Id, candidate.Version, candidate.CreatedAtUtc })
                                 .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序（与代码库其余处理一致）。
        return candidates.OrderByDescending(candidate => candidate.Version)
                         .ThenByDescending(candidate => candidate.CreatedAtUtc)
                         .Select(candidate => (Guid?)candidate.Id)
                         .FirstOrDefault();
    }
}
