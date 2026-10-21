using Banned.AniParser;
using Banori.Data;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     下载任务的数据库级查重与跨流程共享判定（已完成现任守卫、选中总量、承接文件存在性）：
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
        DownloadTaskStatus incumbentStatus,  Guid? incumbentReplacedByTaskId,
        int                incumbentVersion, int   challengerVersion)
        => incumbentStatus == DownloadTaskStatus.Completed &&
           incumbentReplacedByTaskId is null               &&
           incumbentVersion <= challengerVersion;

    /// <summary>重算所选内容总大小：文件选择变化后统一刷新，选中口径只在此定义。</summary>
    internal static void RefreshSelectedTotals(DownloadTask task)
    {
        task.SelectedTotalBytes = task.Files.Where(file => file.IsSelectedForDownload).Sum(file => file.Size);
    }

    /// <summary>任务存在被逐集承接的文件时，移除种子必须保留内容（deleteFiles=false）。</summary>
    internal static bool HasSupersededFiles(DownloadTask task)
        => task.Files.Any(file => file.SupersededByTaskId != null);

    /// <summary>
    ///     文件的职责集数：文件级集数（人工覆盖 &gt; 修正解析）优先，唯一选中正片承载时回退任务级
    ///     集数；与登记、完成切换的覆盖口径一致，供职责判定与包含承接复用。
    /// </summary>
    internal static decimal? DutyEpisode(
        AniParser parser, DownloadTask task, DownloadTaskFile file, IReadOnlyList<DownloadTaskFile> taskFiles,
        Func<DownloadTask, DownloadTaskFile, decimal?> episodeOf)
        => episodeOf(task, file) ??
           (task.Episode is { } episode && IsSingleEpisodeCarrier(parser, task, file, episode, taskFiles)
               ? episode
               : null);

    /// <summary>
    ///     单个文件是否仍承担职责：人工覆盖的选中文件是人工固定资源；内容文件在其集数上的现任
    ///     属于其他任务时已被覆盖（数据另有人承载），集数无法解析的内容视为独有职责；Seed 附件
    ///     的回收是独立策略，不单独构成职责。
    /// </summary>
    internal static bool FileHoldsResidualDuty(
        AniParser parser, DownloadTask task, DownloadTaskFile file, IReadOnlyList<DownloadTaskFile> taskFiles,
        IReadOnlyList<EpisodeVersion> rows, HashSet<Guid> ownFileIds,
        Func<DownloadTask, DownloadTaskFile, decimal?> episodeOf)
    {
        if (!file.IsSelectedForDownload) return false;
        if (file.IsManualOverride) return true;
        if (file.Kind == DownloadFileKind.Seed) return false;

        var episode = DutyEpisode(parser, task, file, taskFiles, episodeOf);
        if (episode is null) return true;
        var incumbent = rows.FirstOrDefault(row => row.Episode == episode)?.IncumbentVideoFileId;
        return incumbent is null || ownFileIds.Contains(incumbent.Value);
    }

    /// <summary>
    ///     职责耗尽判定（冗余种子清理与包含承接共用）：仍被每集登记表引用（现任或待完成优胜）、
    ///     仍存在未被其他现任覆盖的选中内容文件（含无法解析集数的独有内容）、存在人工固定资源，
    ///     或存在未清的执行意图与操作日志（改名、归档待核对、校验、隔离）时，任务仍有职责，
    ///     不得退出。SelectionSyncPending 不在此列：选择下推只是执行尾巴，退出后无需再下推。
    /// </summary>
    internal static bool HasResidualDuties(
        AniParser                     parser, DownloadTask task, IReadOnlyList<DownloadTaskFile> files,
        IReadOnlyList<EpisodeVersion> rows,   Func<DownloadTask, DownloadTaskFile, decimal?> episodeOf)
    {
        if (task.VerificationIntent is not null                ||
            task.IsolationState != DownloadIsolationState.None ||
            task.PendingSavePath is not null                   ||
            task.ArchiveReview is not null                     ||
            files.Any(file => file.PendingRenameFrom is not null))
            return true;

        var ownFileIds = files.Select(file => file.Id).ToHashSet();
        if (rows.Any(row => ownFileIds.Contains(row.IncumbentVideoFileId ?? Guid.Empty) ||
                            ownFileIds.Contains(row.PendingVideoFileId   ?? Guid.Empty)))
            return true;

        return files.Any(file => FileHoldsResidualDuty(parser, task, file, files, rows, ownFileIds, episodeOf));
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
        ApplicationDbContext db, string infoHash, CancellationToken cancellationToken) =>
        await db.DownloadTasks.AsNoTracking()
                .Where(task => task.InfoHash == infoHash &&
                               task.Status   == DownloadTaskStatus.Completed)
                .Select(task => (Guid?)task.Id).FirstOrDefaultAsync(cancellationToken);
}
