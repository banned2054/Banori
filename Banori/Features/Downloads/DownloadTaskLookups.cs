using Banned.AniParser;
using Banori.Data;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     下载任务的数据库级查重与跨流程共享判定（已完成现任守卫、选中总量）：
///     登记入口与完成链共用同一实现，避免两处语义各自演化漂移。
/// </summary>
internal static class DownloadTaskLookups
{
    /// <summary>版本取该集正片的文件名解析；无法解析的单集承载文件才使用任务版本。</summary>
    internal static int EpisodeFileVersion(AniParser parser,  DownloadTask task, DownloadTaskFile file,
                                           decimal   episode, IEnumerable<DownloadTaskFile>? taskFiles = null)
    {
        var parsed = parser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath));
        if (file.Kind == DownloadFileKind.Anime && parsed?.Episode is not null) return parsed.Version;
        return IsSingleEpisodeCarrier(parser, task, file, episode, taskFiles) ? task.Version : 1;
    }

    /// <summary>唯一选中正片可使用同集任务快照；字幕和附件不把单集任务变成合集。</summary>
    internal static bool IsSingleEpisodeCarrier(AniParser parser,  DownloadTask task, DownloadTaskFile file,
                                                decimal   episode, IEnumerable<DownloadTaskFile>? taskFiles = null)
    {
        if (task.Episode != episode || !file.IsSelectedForDownload || !CanUseTaskEpisodeFallback(parser, file))
            return false;
        var files = taskFiles ?? task.Files;
        return file.Kind == DownloadFileKind.Anime
            ? files.Count(candidate => candidate is { Kind: DownloadFileKind.Anime, IsSelectedForDownload: true }) == 1
            : files.Count() == 1;
    }

    internal static bool CanUseTaskEpisodeFallback(AniParser parser, DownloadTaskFile file)
    {
        if (file.EpisodeNumberOverride is not null)
            return file.EpisodeEndOverride is null || file.EpisodeEndOverride == file.EpisodeNumberOverride;
        if (file.EpisodeEndOverride is not null) return false;
        var parsed = parser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath));
        return parsed is not { StartEpisode: { } start, EndEpisode: { } end } || start == end;
    }

    /// <summary>
    ///     已完成现任守卫：现任所属任务已完成、未被声明替换且版本不高于挑战方时，允许把现任记入
    ///     每集登记表或由非登记优胜兜底切换现任。调用方必须传入该集正片文件的有效版本；仅在
    ///     确认单集且文件名不可解析时，才允许使用任务级版本回退。
    /// </summary>
    internal static bool IsTakeoverEligibleIncumbent(
        DownloadTaskStatus incumbentStatus, Guid? incumbentReplacedByTaskId, int incumbentVersion,
        int                challengerVersion)
        => incumbentStatus == DownloadTaskStatus.Completed &&
           incumbentReplacedByTaskId is null               &&
           incumbentVersion <= challengerVersion;

    /// <summary>重算所选内容总大小：文件选择变化后统一刷新，选中口径只在此定义。</summary>
    internal static void RefreshSelectedTotals(DownloadTask task)
    {
        task.SelectedTotalBytes = task.Files.Where(file => file.IsSelectedForDownload).Sum(file => file.Size);
    }

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
