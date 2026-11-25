using Banned.AniParser;
using Banned.AniParser.Models;
using Banned.AniParser.Models.Enums;
using Banned.Qbittorrent.Exceptions;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     下载闭环：创建意图、提交下载器、对账至完成。
///     提交前后都按 info-hash 对账：下载器中已存在的同 hash 种子（自己上次提交未确认，或用户/外部工具预先添加）
///     直接接管推进，不重复提交；接管时补打来源 tag 并把隔离路径对齐为种子真实位置。
///     提交时随种子设置来源 tag 与命名快照名；数据库唯一约束兜底并发。单条任务失败不影响其他任务，更不影响既有资源。
/// </summary>
public sealed partial class DownloadService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    DownloadCoordinationGate                coordinationGate,
    IQbittorrentSessionFactory              sessionFactory,
    ManageQbittorrentSettings               qbittorrentSettings,
    NotificationEnqueuer                    notificationEnqueuer,
    AniParser                               aniParser,
    ReleaseNamePreviewPolicy                namingPolicy,
    SubjectMetadataCache                    subjectCache,
    TimeProvider                            timeProvider,
    ILogger<DownloadService>                logger,
    SubjectEpisodeCorrectionPolicy          episodeCorrectionPolicy,
    DownloadFileService?                    downloadFileService     = null,
    DownloadSubmissionPathValidator?        submissionPathValidator = null,
    DownloadEpisodeResolver?                episodeResolver         = null,
    DownloadTimingPolicy?                   timingPolicy            = null,
    DownloadIntakeService?                  intakeService           = null,
    DownloadTaskChangeNotifier?             changeNotifier          = null,
    NamingTemplateStore?                    namingTemplateStore     = null,
    ReconcilePulse?                         reconcilePulse          = null,
    ShareLimitSettingsStore?                shareLimitSettingsStore = null)
{
    /// <summary>响应丢失后的自动重新提交上限；达到上限视为提交失败，等待人工重试。</summary>
    public const int MaxAutoSubmitAttempts = 5;

    /// <summary>所有下载登记的前置门禁：解析 Bangumi 绑定，未绑定返回 null（调用方拒绝下载并要求补 ID）。</summary>
    private const string SubjectUnboundMessage = "未能绑定 Bangumi Subject，请在表单补充 Bangumi ID 后重试。";

    /// <summary>同集择优落选墓碑的终止步骤标记（与登记入口同口径）；该类记录从未提交或接管过下载器资源。</summary>
    private const string SelectionLossStep = "择优";

    /// <summary>旧版布局一次性扫描标记：扫描结束后落库，之后的启动与对账不再重复全表扫描。</summary>
    private const string LegacyLayoutMigrationMarkerKey = "Downloads.LegacyLayoutMigration.Completed";

    /// <summary>能解析出集数的内容文件扩展名：视频与字幕；其余一律视为附属文件。</summary>
    private static readonly string[] ContentFileExtensions =
        [".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".mov", ".flv", ".ass", ".ssa", ".srt", ".sup"];

    /// <summary>
    ///     本进程的观察者 Id：写入校验意图的 ObserverInstanceId。DownloadService 按对账轮次创建
    ///     （Scoped，worker 每轮解析新实例），实例级 Id 会把每一轮都误判为重启；重启作废必须以
    ///     进程生命周期为单位——应用重启后新进程发现意图来自其他实例，即作废本轮请求前基线、
    ///     校验观察、完成见证与请求锚点（保留候选映射），下一轮重新捕获基线并重发。
    /// </summary>
    private static readonly Guid processInstanceId = Guid.NewGuid();

    private readonly DownloadEpisodeResolver episodeResolver =
        episodeResolver ?? new DownloadEpisodeResolver(aniParser, episodeCorrectionPolicy);

    /// <summary>
    ///     时间策略：窗口到期前不补发请求、查询轮次不消耗预算。未注册时用 Immediate 保持测试旧行为；
    ///     生产端在 Program.cs 注册 Default。
    /// </summary>
    private readonly DownloadTimingPolicy timing = timingPolicy ?? DownloadTimingPolicy.Immediate;


    /// <summary>一轮对账：提交待提交任务、核对未确认与下载中任务。下载器未启用或未配置时静默跳过。</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        // 指纹与会话必须来自同一次读取：清理查询与确认标记若跨两次读取，期间切换 qB 时会把 B 上
        // 的查无记成 A 上已清理，切回 A 后旧种子被永久跳过。
        var (settings, password) = await qbittorrentSettings.GetConnectionAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return;
        // 清理确认按下载器配置与承接任务限定作用域：换 qB 或换承接者都要重新确认，不能永久跳过。
        var cleanupConfigKey = BuildMissingCheckConfigKey(settings);
        // 旧版纯正片布局只扫一次；扫描结果无论是迁移还是安全跳过都留下标记，未确认的远端移动由
        // 同一份 pending 日志在后续轮次恢复，扫描本身不重复规划。
        if (downloadFileService is not null) await MigrateLegacyLayoutsAsync(cancellationToken);
        List<DownloadTask> tasks;
        bool               hasRetiredCleanup;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                           task.Status == DownloadTaskStatus.Submitting ||
                                           task.Status == DownloadTaskStatus.Downloading ||
                                           task.Status == DownloadTaskStatus.Paused ||
                                           // 完成后仍在观察窗口内的做种恢复：完成与做种恢复分离推进。
                                           (task.Status == DownloadTaskStatus.Completed && task.RunRecovery != null &&
                                            task.NextObservationAtUtc != null))
                            .ToListAsync(cancellationToken);
            // 存在被替换墓碑时清理环仍需执行：全部任务静默（完成/被替换）不能让冗余种子无限滞留。
            hasRetiredCleanup = await db.DownloadTasks.AsNoTracking()
                                        .AnyAsync(task => task.Status           == DownloadTaskStatus.Replaced &&
                                                          task.ReplacedByTaskId != null                        &&
                                                          (task.MissingCleanupConfirmedAtUtc == null                 ||
                                                           task.MissingCleanupScopeKey       == null                 ||
                                                           !task.MissingCleanupScopeKey.StartsWith(cleanupConfigKey) ||
                                                           task.MissingCleanupReplacedById != task.ReplacedByTaskId),
                                                  cancellationToken);
        }

        // SQLite 不支持 DateTimeOffset 服务端排序；先创建先处理，在内存中排序。
        tasks = tasks.OrderBy(task => task.CreatedAtUtc).ToList();

        List<(Guid Id, string InfoHash, DateTimeOffset? NextObservationAtUtc)> pendingFileTasks;
        List<PendingTorrentRename>                                             pendingRenameTasks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            pendingFileTasks = downloadFileService is null
                ? []
                : await db.DownloadTasks.AsNoTracking()
                          .Where(task => task.IsolationState     == DownloadIsolationState.None &&
                                         task.VerificationIntent == null                        &&
                                         (task.PendingSavePath != null ||
                                          task.Files.Any(file => file.PendingRenameTo != null)))
                          .Select(task => new ValueTuple<Guid, string, DateTimeOffset?>
                                      (task.Id, task.InfoHash, task.NextObservationAtUtc))
                          .ToListAsync(cancellationToken);
            pendingRenameTasks = await db.DownloadTasks.AsNoTracking()
                                         .Where(task => task.TorrentRenamePending)
                                         .Select(task => new PendingTorrentRename(
                                                     task.Id, task.InfoHash, task.NamingSnapshot, task.Status))
                                         .ToListAsync(cancellationToken);
        }

        if (tasks.Count == 0 && pendingFileTasks.Count == 0 && pendingRenameTasks.Count == 0 && !hasRetiredCleanup)
            return;

        // 会话与清理指纹共用开头读取的连接快照，杜绝两次读取之间切换下载器导致的误确认。
        await using var session = await sessionFactory.CreateAsync(settings, password, cancellationToken);
        await SyncPendingTorrentRenamesUnderGateAsync(session, pendingRenameTasks, cancellationToken);

        // 校验或移动中的种子不得执行冲突计划（改名/移动）：恢复链先看快照状态，
        // 处于 Checking 或 IsMoving 的任务本轮跳过，待其稳定后再恢复。
        Dictionary<string, TorrentSnapshot> pendingSnapshots;
        if (pendingFileTasks.Count == 0)
            pendingSnapshots = new Dictionary<string, TorrentSnapshot>(StringComparer.OrdinalIgnoreCase);
        else
            pendingSnapshots =
                (await session.FindTorrentsAsync(pendingFileTasks
                                                .Where(task => task.NextObservationAtUtc is not { } due ||
                                                               timeProvider.GetUtcNow() >= due)
                                                .Select(task => task.InfoHash).ToList(),
                                                 cancellationToken))
               .GroupBy(snapshot => snapshot.Hash, StringComparer.OrdinalIgnoreCase)
               .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (taskId, infoHash, nextObservation) in pendingFileTasks)
            try
            {
                // 未到观察时间的任务本轮跳过：等待布局确认窗口或采样间隔，不产生恢复请求。
                if (nextObservation is { } due && timeProvider.GetUtcNow() < due) continue;
                if (pendingSnapshots.GetValueOrDefault(infoHash) is
                    { Category: QbittorrentTorrentCategory.Checking } or { IsMoving: true })
                    continue;

                await downloadFileService!.RecoverPendingUnderGateAsync(session, taskId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadFileIdentityException exception)
            {
                var current = await LoadTaskAsync(taskId, cancellationToken);
                await MarkFailedAsync(taskId, current.Status, "文件整理", exception.Message, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "恢复下载任务 {TaskId} 的文件操作失败，下一轮重试。", taskId);
            }

        if (pendingFileTasks.Count > 0)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var refreshed = await db.DownloadTasks.AsNoTracking()
                                    .Where(task => pendingFileTasks.Select(task => task.Id).Contains(task.Id))
                                    .ToDictionaryAsync(task => task.Id, cancellationToken);
            tasks = tasks.Select(task => refreshed.GetValueOrDefault(task.Id) ?? task).ToList();
        }

        // 登记期替换与冗余退出只落库，不在登记事务内等待 qB；在全局协调门内清理被替换资源。
        // 单项清理失败只记录日志，下一轮按同样的记录继续，不阻塞本轮提交与核对。
        // 冗余退出一律保留内容移除种子（deleteFiles=false）：承接方尚未完成时旧数据仍是恢复线索，
        // 完成后仍是其他种子可复用的数据。远端清理需要授权证据：纯落选墓碑（同集择优终止记录）
        // 从未提交或接管过下载器资源，不构成清理授权；同 hash 的外部种子必须保守保留。
        List<DownloadTask> superseded;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            superseded = await db.DownloadTasks.AsNoTracking()
                                 .Include(task => task.Files)
                                 .Where(task => task.Status           == DownloadTaskStatus.Replaced &&
                                                task.ReplacedByTaskId != null                        &&
                                                (task.MissingCleanupConfirmedAtUtc == null                 ||
                                                 task.MissingCleanupScopeKey       == null                 ||
                                                 !task.MissingCleanupScopeKey.StartsWith(cleanupConfigKey) ||
                                                 task.MissingCleanupReplacedById != task.ReplacedByTaskId))
                                 .ToListAsync(cancellationToken);
        }

        // 先筛出有资格且作用域未确认的墓碑，再一次性批查 hash：查询失败按未知处理，不落任何标记。
        var cleanupTasks = new List<DownloadTask>();
        foreach (var oldTask in superseded)
        {
            if (oldTask.FailedStep == SelectionLossStep) continue;
            // 未清的执行意图与操作日志是恢复线索：改名日志两端任一非空都视为未收敛，
            // pending 收敛前不动远端资源，避免丢失恢复信息。
            if (oldTask.PendingSavePath is not null                   ||
                oldTask.VerificationIntent is not null                ||
                oldTask.ArchiveReview is not null                     ||
                oldTask.IsolationState != DownloadIsolationState.None ||
                oldTask.Files.Any(file => file.PendingRenameFrom is not null ||
                                          file.PendingRenameTo is not null))
                continue;
            // 清理必须有正向生命周期证据（提交、同步接管或完成过）：待提交即被替代的记录
            // 从未拥有下载器资源，没有证据一律保守跳过，即便同 hash 种子位于确认根也不清理。
            if (oldTask is { AttemptCount: 0, LastSyncedAtUtc: null, CompletedAtUtc: null, SubmittedAtUtc : null })
                continue;
            // 确认标记绑定下载器与本次承接：换 qB 或重新获得种子后再次被替代时必须重新确认。
            if (oldTask.MissingCleanupConfirmedAtUtc is not null &&
                string.Equals(oldTask.MissingCleanupScopeKey,
                              BuildMissingCleanupScopeKey(cleanupConfigKey, oldTask.ReplacedByTaskId),
                              StringComparison.Ordinal))
                continue;
            if (await IsActiveTaskForHashAsync(oldTask.InfoHash, oldTask.Id, cancellationToken)) continue;
            cleanupTasks.Add(oldTask);
        }

        if (cleanupTasks.Count > 0)
        {
            var cleanupHashes = cleanupTasks.Select(task => task.InfoHash)
                                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, List<TorrentSnapshot>> cleanupFound;
            var                                       cleanupQuerySucceeded = true;
            try
            {
                cleanupFound = (await session.FindTorrentsAsync(cleanupHashes, cancellationToken))
                              .GroupBy(snapshot => snapshot.Hash, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(group => group.Key, group => group.ToList(),
                                            StringComparer.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 查询未知不等于查无：保留全部墓碑的清理资格，下一轮重试。
                logger.LogError(exception, "批量查询被替代任务的种子失败，下一轮重试。");
                cleanupFound          = [];
                cleanupQuerySucceeded = false;
            }

            if (cleanupQuerySucceeded)
            {
                var confirmedAbsent = new List<DownloadTask>();
                var deleteTasks     = new List<DownloadTask>();
                foreach (var oldTask in cleanupTasks)
                {
                    if (!cleanupFound.TryGetValue(oldTask.InfoHash, out var foundOld))
                    {
                        // 成功查无：清理目标已达成，落确认标记退出后续轮次的清理资格。
                        confirmedAbsent.Add(oldTask);
                        continue;
                    }

                    // 对所有待清理任务核对实际根与确认根（统一规范化比较）：受管种子被外部移动、
                    // 或同 hash 外部种子位于别处时一律保守保留，路径异常交由人工或后续轮次处理。
                    if (foundOld.Any(snapshot => !MatchesIsolationPath(snapshot.SavePath, oldTask.SavePath)))
                        continue;
                    deleteTasks.Add(oldTask);
                }

                foreach (var task in confirmedAbsent)
                    await ConfirmMissingCleanupAsync(task, cleanupConfigKey, cancellationToken);

                if (deleteTasks.Count > 0)
                {
                    var deleteHashes = deleteTasks.Select(task => task.InfoHash)
                                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    try
                    {
                        // 承接方仍需旧数据作恢复线索：删除种子一律 deleteFiles=false。
                        await session.DeleteTorrentsAsync(deleteHashes, false, cancellationToken);
                        // 删除请求返回不等于种子已消失；按 hash 成功查无才收尾清理资格。
                        var survivors = (await session.FindTorrentsAsync(deleteHashes, cancellationToken))
                                       .Select(snapshot => snapshot.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var task in deleteTasks.Where(task => !survivors.Contains(task.InfoHash)))
                            await ConfirmMissingCleanupAsync(task, cleanupConfigKey, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "清理被顶替任务的下载器资源失败，下一轮重试。");
                    }
                }
            }
        }

        var reconcileHashes = tasks
                             .Where(task =>
                                        task.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused ||
                                        // 做种恢复观察也需要快照；运行中的种子必须能被找到。
                                        task is { Status: DownloadTaskStatus.Completed, RunRecovery: not null })
                             .Select(task => task.InfoHash)
                             .ToList();
        var foundByHash = (await session.FindTorrentsAsync(reconcileHashes, cancellationToken))
                         .GroupBy(snapshot => snapshot.Hash)
                         .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks.Where(task => task.Status is DownloadTaskStatus.Downloading
                                                             or DownloadTaskStatus.Paused ||
                                                 task is
                                                 {
                                                     Status              : DownloadTaskStatus.Completed,
                                                     RunRecovery         : not null,
                                                     NextObservationAtUtc: not null
                                                 }))
            try
            {
                // 未到观察时间的任务本轮跳过：等待可见性/确认窗口或采样间隔，不产生查询推进与请求。
                if (task.NextObservationAtUtc is { } due && timeProvider.GetUtcNow() < due) continue;
                await ReconcileTrackedAsync(session, task, foundByHash.GetValueOrDefault(task.InfoHash),
                                            cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadFileIdentityException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "对账下载任务 {TaskId} 失败，下一轮重试。", task.Id);
            }

        foreach (var task in tasks.Where(task => task.Status is DownloadTaskStatus.PendingSubmit
                                                             or DownloadTaskStatus.Submitting))
            try
            {
                var current = await LoadTaskAsync(task.Id, cancellationToken);
                if (current.Status is not (DownloadTaskStatus.PendingSubmit or DownloadTaskStatus.Submitting)) continue;
                // 提交可见性窗口未到期的任务本轮不重复提交。
                if (current.NextObservationAtUtc is { } due && timeProvider.GetUtcNow() < due) continue;
                await SubmitAsync(session, current, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "提交下载任务 {TaskId} 失败，下一轮重试。", task.Id);
            }
    }

    /// <summary>
    ///     一次性把旧版“全局根 + 媒体目录前缀”的纯正片任务迁到媒体根。只有本应用提交过、目录快照
    ///     齐全、种子原始元数据/qB 文件身份/路径/大小全部吻合且没有未收敛状态的任务才参与；其余
    ///     一律安全跳过并记录原因，绝不猜测迁移。
    /// </summary>
    private async Task MigrateLegacyLayoutsAsync(CancellationToken cancellationToken)
    {
        bool markerExists;
        try
        {
            await using var markerDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            markerExists =
                await markerDb.ApplicationSettings.AnyAsync(setting => setting.Key == LegacyLayoutMigrationMarkerKey,
                                                            cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "读取旧版下载布局迁移标记失败，本轮跳过扫描。");
            return;
        }

        if (markerExists) return;

        List<DownloadTask> candidates;
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            candidates = await db.DownloadTasks.Include(task => task.Files).AsNoTracking()
                                 .Where(task => (task.Status == DownloadTaskStatus.Downloading ||
                                                 task.Status == DownloadTaskStatus.Paused      ||
                                                 task.Status == DownloadTaskStatus.Completed)              &&
                                                task.SubmittedAtUtc     != null                            &&
                                                task.ReplacesTaskId     == null                            &&
                                                task.ReplacedByTaskId   == null                            &&
                                                task.VerificationIntent == null                            &&
                                                task.IsolationState     == DownloadIsolationState.None     &&
                                                task.PendingSavePath    == null                            &&
                                                task.ArchiveReview      == null                            &&
                                                task.RunRecovery        == null                            &&
                                                task.Files.All(file => file.Kind != DownloadFileKind.Seed) &&
                                                !task.Files.Any(file => file.PendingRenameFrom != null ||
                                                                        file.PendingRenameTo   != null))
                                 .ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "读取旧版下载布局迁移候选失败，本轮跳过扫描。");
            return;
        }

        if (candidates.Count == 0)
        {
            await TryMarkLegacyLayoutMigrationAsync(cancellationToken);
            return;
        }

        void Skip(DownloadTask task, string reason) =>
            logger.LogInformation("跳过旧版下载布局迁移，任务 {TaskId}：{Reason}", task.Id, reason);

        var scanInterrupted = false;
        try
        {
            await using var                 session  = await sessionFactory.CreateAsync(cancellationToken);
            IReadOnlyList<TorrentSnapshot>? torrents = null;
            foreach (var candidate in candidates)
                try
                {
                    if (!TryPlanLegacyMigration(candidate, out var plan, out var reason))
                    {
                        Skip(candidate, reason);
                        continue;
                    }

                    torrents ??= await session.ListTorrentsAsync(null, cancellationToken);
                    var snapshot = torrents.FirstOrDefault(value =>
                                                               string.Equals(value.Hash, candidate.InfoHash,
                                                                             StringComparison.OrdinalIgnoreCase));
                    if (snapshot is null)
                    {
                        Skip(candidate, "下载器中不存在对应种子。");
                        continue;
                    }

                    if (snapshot.IsMoving ||
                        snapshot.Category is QbittorrentTorrentCategory.Checking
                                          or QbittorrentTorrentCategory.Error
                                          or QbittorrentTorrentCategory.Unknown)
                    {
                        Skip(candidate, "下载器仍在移动或检查种子。");
                        continue;
                    }

                    if (!string.Equals(DownloadPathNamePolicy.NormalizePath(snapshot.SavePath),
                                       DownloadPathNamePolicy.NormalizePath(candidate.SavePath),
                                       StringComparison.Ordinal))
                    {
                        Skip(candidate, "下载器保存根与任务记录不符，可能已被外部接管。");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(candidate.SiteSource) &&
                        !snapshot.Tags.Contains(candidate.SiteSource, StringComparer.OrdinalIgnoreCase))
                    {
                        Skip(candidate, "下载器来源标签与任务记录不符，归属不明。");
                        continue;
                    }

                    var remote = await session.ListFilesAsync(candidate.InfoHash, cancellationToken);
                    if (!MatchesLegacyRemoteFiles(candidate, snapshot.SavePath, remote))
                    {
                        Skip(candidate, "下载器文件身份、路径或大小与任务记录不符。");
                        continue;
                    }

                    if (!MatchesLegacyOriginalFiles(candidate))
                    {
                        Skip(candidate, "原始种子文件身份或大小与任务记录不符。");
                        continue;
                    }

                    if (!await downloadFileService!.ApplyLegacyMigrationUnderGateAsync(session, candidate.Id,
                            plan.Root, plan.Paths, cancellationToken))
                        logger.LogInformation("旧版下载布局迁移已登记待执行日志，任务 {TaskId} 由后续对账恢复。",
                                              candidate.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TargetPathOccupiedException exception)
                {
                    Skip(candidate, exception.Message);
                }
                catch (DownloadFileIdentityException exception)
                {
                    Skip(candidate, exception.Message);
                }
                catch (ArgumentException exception)
                {
                    Skip(candidate, exception.Message);
                }
                catch (InvalidOperationException exception)
                {
                    Skip(candidate, exception.Message);
                }
                catch (Exception exception)
                {
                    scanInterrupted = true;
                    logger.LogWarning(exception, "旧版下载布局迁移单条失败，任务 {TaskId} 留待下一轮重试。",
                                      candidate.Id);
                }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            scanInterrupted = true;
            logger.LogWarning(exception, "旧版下载布局迁移扫描中断，本轮不写一次性标记。");
        }

        // 只有全部候选处理完毕才写标记；已落库的操作继续由 pending 日志恢复。
        if (!scanInterrupted) await TryMarkLegacyLayoutMigrationAsync(cancellationToken);
    }

    private async Task TryMarkLegacyLayoutMigrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (await db.ApplicationSettings.AnyAsync(setting => setting.Key == LegacyLayoutMigrationMarkerKey,
                                                      cancellationToken))
                return;
            var now = timeProvider.GetUtcNow();
            db.ApplicationSettings.Add(new ApplicationSetting
            {
                Key          = LegacyLayoutMigrationMarkerKey,
                Value        = now.ToString("O"),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "写入旧版下载布局迁移标记失败，下一轮重新扫描。");
        }
    }

    /// <summary>计算并逐文件核对旧布局迁移计划：相对路径只去掉媒体目录前缀，绝对路径必须逐字节相等。</summary>
    private static bool TryPlanLegacyMigration(
        DownloadTask task, out (string Root, IReadOnlyDictionary<int, string> Paths) plan, out string reason)
    {
        plan   = default!;
        reason = string.Empty;
        // 同 hash 接管只说明下载器里早有种子，不代表本应用成功提交过。SubmittedAtUtc 在 Add 请求前
        // 就落库，接管还会补来源标签，二者都不能充当成功提交证据；归属不明的任务绝不自动迁移。
        // 旧归档日志已确认的任务更不能被一次性扫描按新版规则重新切根——它可能还没写成 Completed，
        // 重启后必须沿用已落库的旧布局继续收尾。
        if (DownloadPathDecision.FromJson(task.PathDecision) is
            {
                Kind: { } kind and (DownloadPathDecisionKinds.SameHashAdoption
                                 or DownloadPathDecisionKinds.LegacyArchiveFinal)
            })
        {
            reason = kind == DownloadPathDecisionKinds.SameHashAdoption
                ? "同 hash 接管缺少成功提交证据，归属不明。"
                : "旧归档日志已确认，完成收尾沿用旧布局。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.DownloadRootPath) || task.MediaDirectory is null ||
            task.SeedDirectory is null)
        {
            reason = "缺少下载根或目录快照。";
            return false;
        }

        var baseRoot = DownloadPathNamePolicy.NormalizePath(task.DownloadRootPath);
        var media    = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory);
        RelativeDownloadPathPolicy.Normalize(task.SeedDirectory);
        var mediaRoot = DownloadPathNamePolicy.JoinSavePath(baseRoot, media);
        if (!string.Equals(DownloadPathNamePolicy.NormalizePath(task.SavePath), baseRoot,
                           StringComparison.Ordinal))
        {
            reason = "保存根不是旧版全局下载根。";
            return false;
        }

        if (task.Files.Count == 0)
        {
            reason = "没有已登记的文件。";
            return false;
        }

        if (task.Files.Any(file => file.VerificationOutcome == DownloadFileVerificationOutcomes.ReusedVerified))
        {
            reason = "存在共享复用证据。";
            return false;
        }

        var prefix = media + "/";
        var paths  = new Dictionary<int, string>();
        foreach (var file in task.Files)
        {
            var current = RelativeDownloadPathPolicy.Normalize(file.CurrentRelativePath);
            if (!current.StartsWith(prefix, StringComparison.Ordinal))
            {
                reason = "文件路径不以媒体目录快照为前缀，无法证明属于旧布局。";
                return false;
            }

            var relative = RelativeDownloadPathPolicy.Normalize(current[prefix.Length..]);
            if (!string.Equals(DownloadPathNamePolicy.JoinSavePath(mediaRoot, relative),
                               DownloadPathNamePolicy.JoinSavePath(baseRoot, current),
                               StringComparison.Ordinal))
            {
                reason = "迁移后无法保持文件绝对路径不变。";
                return false;
            }

            paths.Add(file.FileIndex, relative);
        }

        if (paths.Values.Distinct(StringComparer.Ordinal).Count() != paths.Count ||
            paths.Values.Any(left => paths.Values.Any(right => right.StartsWith(left + "/", StringComparison.Ordinal))))
        {
            reason = "迁移后的相对路径互相冲突。";
            return false;
        }

        plan = (mediaRoot, paths);
        return true;
    }

    /// <summary>qB 清单按绝对路径与大小一一对应，且路径唯一：任何多余或歧义文件都使迁移不安全。</summary>
    private static bool MatchesLegacyRemoteFiles(
        DownloadTask task, string savePath, IReadOnlyList<TorrentFileSnapshot> remote)
    {
        if (remote.Count != task.Files.Count) return false;
        var absolutePaths = remote
                           .Select(file =>
                                       DownloadPathNamePolicy.NormalizePath(DownloadPathNamePolicy
                                                                               .JoinSavePath(savePath, file.Path)))
                           .ToHashSet(StringComparer.Ordinal);
        if (absolutePaths.Count != remote.Count) return false;
        return task.Files.All(file =>
        {
            var absolute = DownloadPathNamePolicy.JoinSavePath(savePath, file.CurrentRelativePath);
            var observed = remote.Where(value =>
                                            string.Equals(DownloadPathNamePolicy.JoinSavePath(savePath, value.Path),
                                                          absolute, StringComparison.Ordinal))
                                 .ToList();
            return observed.Count == 1 && observed[0].Size == file.Size && absolutePaths.Contains(absolute);
        });
    }

    /// <summary>原始 .torrent 元数据与持久化原始路径/大小必须一一对应，防止迁移到别家的同路径种子。</summary>
    private static bool MatchesLegacyOriginalFiles(DownloadTask task)
    {
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile)) return false;
        var remaining = torrent.Files.ToDictionary(file =>
                                                       TorrentDownloadPolicy
                                                          .GetRelativePath(torrent, file, isMultiFile),
                                                   StringComparer.Ordinal);
        if (remaining.Count != task.Files.Count) return false;
        foreach (var file in task.Files)
            if (!remaining.Remove(file.OriginalRelativePath.Replace('\\', '/'), out var entry) ||
                file.Size != entry.Length)
                return false;
        return true;
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status != DownloadTaskStatus.Downloading) throw new InvalidOperationException("只有下载中的任务可以暂停。");

        // 人工暂停会中断尚未收尾的校验：作废范围不能只看是否已采到 Checking——请求已发出、
        // 首次观察未到时同样可能被中断，本轮证据与请求锚点一律先于暂停请求作废（恢复只解除
        // 人工暂停，不会重新校验）。作废同时把暂停记为第一次停止请求：停止生效前残留的旧
        // Checking 不再被归属，由停止确认的期限与预算处理。ReadyToRun 例外：校验已通过并
        // 登记复用，暂停只是等待启动确认，保留验证结果与恢复语义。
        if (DownloadVerificationIntent.FromJson(task.VerificationIntent) is
            { RecheckRequestedAtUtc: not null, Step: not DownloadVerificationStep.ReadyToRun } checkingIntent)
        {
            var voided = ResetRoundEvidence(checkingIntent, DownloadVerificationStep.Requested) with
            {
                TimeoutStopRequestedAtUtc = timeProvider.GetUtcNow(), TimeoutStopAttempts = 1
            };
            await SaveVerificationIntentAsync(taskId, DownloadTaskStatus.Downloading, voided, cancellationToken);
            logger.LogInformation("任务 {TaskId} 人工暂停中断了校验，本轮校验证据已作废，恢复后重新发起校验。", taskId);
        }

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.PauseAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status                    = DownloadTaskStatus.Paused;
            update.IsUserPaused              = true;
            // 页面发起的人工暂停来源明确，覆盖此前可能存在的外部停止观察记录。
            update.ExternalStopObservedAtUtc = null;
            // 活跃的启动恢复随人工暂停一并停止自动重试：记录保留为 UserPaused（不删除），
            // 供页面解释「为什么没启动」，人工恢复后由对账按既有预算继续有界启动。
            if (DownloadRunRecovery.FromJson(task.RunRecovery) is
                not { State: DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown } activeRun)
                return;
            var pausedRun = activeRun with
            {
                State = DownloadRunStates.UserPaused,
                Reason = "用户暂停了任务；不自动恢复",
                LastObservedAtUtc = timeProvider.GetUtcNow()
            };
            update.RunRecovery = pausedRun.ToJson();
        }, cancellationToken);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status != DownloadTaskStatus.Paused || !task.IsUserPaused)
            throw new InvalidOperationException("只有人工暂停的任务可以恢复。");

        // 校验未确认后的人工恢复等价于重新发起一轮校验：重置证据与请求台账、轮次 +1，
        // 候选映射与文件原样保留；下一轮对账立即重发 recheck，不隔离、不重下。未确认任务
        // 处于系统暂停，不直接启动下载器——先保持停止完成校验，通过后才恢复做种。
        if (DownloadVerificationIntent.FromJson(task.VerificationIntent) is
            { Step: DownloadVerificationStep.Unconfirmed } unconfirmed)
        {
            await SaveVerificationIntentAsync(taskId, task.Status, ResetUnconfirmedRound(unconfirmed),
                                              cancellationToken);
            await UpdateTaskAsync(taskId, task.Status, update =>
            {
                update.Status                    = DownloadTaskStatus.Downloading;
                update.IsSystemPaused            = true;
                update.IsUserPaused              = false;
                update.ExternalStopObservedAtUtc = null;
                update.PreparationStage          = DownloadPreparationStage.AwaitingVerification;
                update.FailedStep                = null;
                update.Error                     = null;
                update.NextObservationAtUtc      = timeProvider.GetUtcNow() + timing.PreparationSampleInterval;
            }, cancellationToken);
            logger.LogInformation("任务 {TaskId} 从校验未确认恢复：候选与文件保留，下一轮对账重发 recheck。",
                                  taskId);
            return;
        }


        // 准备流程未完成时，恢复只解除人工暂停：不直接启动下载器——否则落选文件会绕过
        // 文件选择计划直接开始下载。任务回到待准备状态，由对账继续推进并在准备完成后启动。
        if (task.IsSystemPaused)
        {
            await UpdateTaskAsync(taskId, task.Status, update =>
            {
                update.Status                    = DownloadTaskStatus.Downloading;
                update.IsUserPaused              = false;
                update.ExternalStopObservedAtUtc = null;
            }, cancellationToken);
            return;
        }

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.ResumeAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status                    = DownloadTaskStatus.Downloading;
            update.IsUserPaused              = false;
            update.ExternalStopObservedAtUtc = null;
        }, cancellationToken);
    }

    /// <summary>
    ///     人工重试/重下载：失败、丢失或已完成但种子在下载器中缺失的任务重新进入受控流程。
    ///     入口先按 hash 核对下载器现状——缺种任务的种子又回来时只清标记；查无才走受控重下；
    ///     结果未知不改任何状态。普通重试保留未确认布局日志，只重置请求预算；缺种重下才建立新一轮记录。
    /// </summary>
    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        if (task is not ({ Status: DownloadTaskStatus.Failed or DownloadTaskStatus.Lost } or
                         { Status: DownloadTaskStatus.Completed, TorrentMissingAtUtc: not null }))
            throw new InvalidOperationException("只有失败、丢失或已完成但种子缺失的任务可以重试。");

        // 同 hash 已有活动任务时明确拒绝：唯一索引之外先给出可展示原因，避免重复提交抢占同一资源。
        if (await IsActiveTaskForHashAsync(task.InfoHash, task.Id, cancellationToken))
            throw new InvalidOperationException("该种子的其他任务正在下载中，请等待其结束后重试。");

        bool torrentPresent;
        try
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            torrentPresent = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken)).Count > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "人工重试：查询下载器中任务 {TaskId} 的种子失败，未改动任务状态。", taskId);
            throw new InvalidOperationException("查询下载器中的种子失败，请稍后重试。", exception);
        }

        // 只有缺种任务的“种子又回来了”才清标记退出：Failed/Lost 的种子仍在下载器时属于原有的
        // “重试准备/布局流程”语义，继续走受控重试，不因种子存在而拒绝人工重试。
        if (torrentPresent && task.TorrentMissingAtUtc is not null)
        {
            await UpdateTaskAsync(taskId, task.Status, update =>
            {
                update.TorrentMissingAtUtc      = null;
                update.MissingCheckPendingAtUtc = null;
                update.MissingCheckConfigKey    = null;
            }, cancellationToken);
            throw new InvalidOperationException("下载器中已存在该种子，缺种标记已清除，无需重新下载。");
        }

        // 存量失败任务可能在登记时未建立“规则优胜 → 旧成片”的替换关系：重试前按已落库的
        // 优胜选择补建一次，只补已完成现任，不改写其他任务或本任务已有的文件选择。补建失败
        // 不阻塞重试——已提交的种子由接管流程先恢复任务专属暂存布局，后续归档仍按同一占用
        // 检查给出真实原因。
        if (task.FeedItemId is not null && intakeService is not null)
            try
            {
                await intakeService.EnsureSelectionWinnerReplacementAsync(taskId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "任务 {TaskId} 重试前补建同集替换关系失败，按现有记录继续重试。", taskId);
            }

        // 上一次 Add 可能已被下载器接受但尚未可见：人工重试同样先尊重添加可见性窗口，
        // 不能凭“查无”立即再发一条 Add 绕过占用与 hash 对账。
        if (DownloadRoundLayout.IsPending(task) && WithinAddVisibilityWindow(task))
            throw new InvalidOperationException("上一次添加尚未在下载器中可见，请在确认窗口后重试。");

        // 旧校验意图尚未收敛：转隔离独立下载（deleteFiles=false，旧成片留在磁盘），这是“需独立
        // 重下”路径；无旧意图时下一轮 SubmitAsync 由 TryPlanVerificationAddAsync 重新枚举归档
        // 复用候选（含本任务旧成片）并新建 DownloadVerificationMapping，映射原成片并真实校验。
        // 种子已不在下载器时（含明确缺种确认）才开启新一轮；仍在下载器的普通重试只重置预算。
        var redownload = !torrentPresent || task.TorrentMissingAtUtc is not null;
        if (DownloadVerificationIntent.FromJson(task.VerificationIntent) is { } intent)
            await EnterIsolationAsync(task, intent, "人工重新下载，废弃旧校验证据并转为独立下载。", cancellationToken);

        await UpdateTaskAsync(taskId, task.Status, update =>
                              {
                                  update.Status                      = DownloadTaskStatus.PendingSubmit;
                                  update.AttemptCount                = 0;
                                  update.FailedStep                  = null;
                                  update.Error                       = null;
                                  // 两类重试共用请求预算重置：原种子仍在 qB、异步改名或移动可能已生效的普通重试
                                  // 必须保留 PendingSavePath 与逐文件改名日志，否则后续对账失去判断依据。
                                  update.PendingLayoutRequestedAtUtc = null;
                                  update.PendingLayoutRequestCount   = 0;
                                  update.SelectionRequestCount       = 0;
                                  update.NextObservationAtUtc        = null;
                                  if (!redownload) return;

                                  // 只有确认开启缺种重下才建立新一轮记录：清除旧意图、旧路径台账和缺种证据，
                                  // 由 TryPlanVerificationAddAsync 重新建立映射；保留 CompletedAtUtc、VerificationPath、
                                  // SupersededByTaskId、TorrentFile 与 NamingSnapshot，旧成片路径与逐集引用继续有效。
                                  update.VerificationIntent       = null;
                                  update.PendingSavePath          = null;
                                  // 待确认提交根是尚未确认 Add 的占用证据：人工重试、失败状态与重启都保留，
                                  // 直到按 hash 确认接管或明确拒绝后收尾释放。
                                  update.TorrentMissingAtUtc      = null;
                                  update.MissingCheckPendingAtUtc = null;
                                  update.MissingCheckConfigKey    = null;
                              }, cancellationToken,
                              async (db, updated) =>
                              {
                                  if (!redownload) return;
                                  // 先固化已发布成片布局：后续 Add、映射与改名只推进本轮下载器布局，
                                  // 进程重启或响应丢失时仍能从两份持久路径解析出旧成片完整位置。
                                  var files = await db.DownloadTaskFiles
                                                      .Where(file => file.DownloadTaskId == taskId)
                                                      .ToListAsync(cancellationToken);
                                  DownloadRoundLayout.CapturePublished(updated, files);
                                  // 未收敛的改名日志属于旧路径证据：新一轮由 TryPlanVerificationAddAsync 重新规划，
                                  // 不把上一轮的目标路径带回来。VerificationPath / SupersededByTaskId 保持不动。
                                  foreach (var file in files)
                                  {
                                      file.PendingRenameFrom = null;
                                      file.PendingRenameTo   = null;
                                  }
                              });
    }

    /// <summary>人工重新校验：保留候选映射、文件与种子，只重置本轮证据并等待下一轮重发 recheck；不隔离、不重下。</summary>
    public async Task ReverifyAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        var       intent            = DownloadVerificationIntent.FromJson(task.VerificationIntent);
        if (task.Status         != DownloadTaskStatus.Paused   ||
            task.IsolationState != DownloadIsolationState.None ||
            intent?.Step        != DownloadVerificationStep.Unconfirmed)
            throw new InvalidOperationException("只有校验未确认的任务可以重新校验。");

        await SaveVerificationIntentAsync(taskId, task.Status, ResetUnconfirmedRound(intent), cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status               = DownloadTaskStatus.Downloading;
            update.IsSystemPaused       = true;
            update.IsUserPaused         = false;
            update.PreparationStage     = DownloadPreparationStage.AwaitingVerification;
            update.FailedStep           = null;
            update.Error                = null;
            update.NextObservationAtUtc = null;
        }, cancellationToken);
        logger.LogInformation("任务 {TaskId} 已重新校验：保留候选与文件，重置本轮证据等待重发 recheck。",
                              taskId);
    }

    /// <summary>
    ///     删除面板的预检：下载器中当前是否存在该任务的种子。下载器未启用、未配置或暂时不可达时
    ///     返回 null（未知），面板仍让用户自行决定是否勾选删除下载器种子。
    /// </summary>
    public async Task<bool?> TorrentInDownloaderAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);

        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return null;

        try
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            var             found   = await session.FindTorrentsAsync([task.InfoHash], cancellationToken);
            return found.Count > 0;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "删除预检：查询下载器中任务 {TaskId} 的种子失败，按未知处理。", taskId);
            return null;
        }
    }

    /// <summary>
    ///     删除任务记录。deleteFromDownloader 时按 hash 直接删除下载器中的种子（可连带文件）；
    ///     下载器对不存在的 hash 静默忽略，无需先查询，整个操作幂等可重试。
    ///     被本任务替换的旧任务回退为已完成（其文件仍在，重新成为当前版本）；
    ///     其他任务对本任务的替换声明一并清除，不留悬空引用。
    /// </summary>
    public async Task DeleteAsync(
        Guid taskId, bool deleteFromDownloader, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);

        if (deleteFromDownloader)
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            await session.DeleteTorrentAsync(task.InfoHash, deleteFiles, cancellationToken);
        }

        IReadOnlyList<Guid> affectedIds;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var tracked = await db.DownloadTasks.SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
            if (tracked is null) return;

            affectedIds = await ClearReplacementReferencesAsync(db, [taskId], cancellationToken);
            db.DownloadTasks.Remove(tracked);
            await db.SaveChangesAsync(cancellationToken);
        }

        changeNotifier?.Notify(affectedIds, true);

        logger.LogInformation("已删除下载任务 {TaskId}（hash {InfoHash}，删除器种子：{DeleteTorrent}，连带文件：{DeleteFiles}）。",
                              taskId, task.InfoHash, deleteFromDownloader, deleteFiles);
    }

    /// <summary>批量删除面板预检：现存任务数与其中在下载器里能找到种子的数量；DownloaderChecked 为 false 表示下载器状态未知。</summary>
    public async Task<BulkDeletePrecheck> PrecheckBulkDeleteAsync(
        IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default)
    {
        List<string> hashes;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            hashes = await db.DownloadTasks.AsNoTracking()
                             .Where(task => taskIds.Contains(task.Id))
                             .Select(task => task.InfoHash)
                             .ToListAsync(cancellationToken);
        }

        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            return new BulkDeletePrecheck(hashes.Count, 0, false);

        try
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            var             found   = await session.FindTorrentsAsync(hashes, cancellationToken);
            return new BulkDeletePrecheck(hashes.Count, found.Count, true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "批量删除预检：查询下载器中 {Count} 个任务的种子失败，按未知处理。", hashes.Count);
            return new BulkDeletePrecheck(hashes.Count, 0, false);
        }
    }

    /// <summary>
    ///     批量删除：与单任务删除同一语义（先下载器后数据库、幂等、清理替换引用并回退被替换任务），
    ///     但一次会话完成下载器删除、一次落库完成记录删除；已不存在的任务 Id 静默跳过。
    /// </summary>
    public async Task<int> DeleteManyAsync(
        IReadOnlyCollection<Guid> taskIds, bool deleteFromDownloader, bool deleteFiles,
        CancellationToken         cancellationToken = default)
    {
        if (taskIds.Count == 0) return 0;

        List<DownloadTask> tasks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => taskIds.Contains(task.Id))
                            .ToListAsync(cancellationToken);
        }

        if (tasks.Count == 0) return 0;

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => taskIds.Contains(task.Id))
                            .ToListAsync(cancellationToken);
        }

        if (tasks.Count == 0) return 0;

        if (deleteFromDownloader)
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            // 下载器对不存在的 hash 静默忽略，按任务 hash 直接批量删除即可，无需先逐个确认存在。
            await session.DeleteTorrentsAsync(tasks.Select(task => task.InfoHash).ToList(), deleteFiles,
                                              cancellationToken);
        }

        int                 deletedCount;
        IReadOnlyList<Guid> affectedIds;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var ids     = tasks.Select(task => task.Id).ToList();
            var tracked = await db.DownloadTasks.Where(task => ids.Contains(task.Id)).ToListAsync(cancellationToken);
            deletedCount = tracked.Count;

            affectedIds = await ClearReplacementReferencesAsync(db, ids, cancellationToken);
            db.DownloadTasks.RemoveRange(tracked);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (deletedCount > 0 || affectedIds.Count > 0) changeNotifier?.Notify(affectedIds, true);

        logger.LogInformation("已批量删除 {Count} 个下载任务（删除器种子：{DeleteTorrent}，连带文件：{DeleteFiles}）。",
                              deletedCount, deleteFromDownloader, deleteFiles);
        return deletedCount;
    }

    /// <summary>清理指向待删任务的替换引用：替换者被删的旧任务回退为已完成，其余任务对待删任务的替换声明置空；逐集承接的文件在所属任务仍活动时恢复下载职责；每集登记表回退待完成与现任引用。</summary>
    private async Task<IReadOnlyList<Guid>> ClearReplacementReferencesAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> deletedIds, CancellationToken cancellationToken)
    {
        var affectedIds = new HashSet<Guid>(deletedIds);
        var referencing = await db.DownloadTasks
                                  .Where(other => (other.ReplacesTaskId != null &&
                                                   deletedIds.Contains(other.ReplacesTaskId.Value)) ||
                                                  (other.ReplacedByTaskId != null &&
                                                   deletedIds.Contains(other.ReplacedByTaskId.Value)))
                                  .ToListAsync(cancellationToken);
        foreach (var other in referencing)
        {
            affectedIds.Add(other.Id);
            if (other.ReplacedByTaskId is { } replacedBy && deletedIds.Contains(replacedBy))
            {
                // 替换者已删除：仅确有完成记录的旧资源回退为已完成；未提交落选者保留
                // Replaced 墓碑，避免一次删除同时复活多个候选造成同集重复下载。
                other.ReplacedByTaskId = null;
                if (other is { Status: DownloadTaskStatus.Replaced, CompletedAtUtc: not null })
                    other.Status = DownloadTaskStatus.Completed;
            }

            if (other.ReplacesTaskId is { } replaces && deletedIds.Contains(replaces))
                other.ReplacesTaskId = null;
        }

        // 删除中间承接方时先沿该集的承接链转交后继；没有有效后继才恢复活动任务的文件选择。
        // 已被整任务翻转或进入终态的所属任务只清理引用，不复活，与上面的墓碑规则保持一致。
        var referringFiles = await db.DownloadTaskFiles
                                     .Include(file => file.DownloadTask).ThenInclude(parent => parent!.Files)
                                     .Where(file => file.SupersededByTaskId != null &&
                                                    deletedIds.Contains(file.SupersededByTaskId.Value))
                                     .ToListAsync(cancellationToken);
        var deletedTasks = await db.DownloadTasks.Include(task => task.Files)
                                   .Where(task => deletedIds.Contains(task.Id))
                                   .ToDictionaryAsync(task => task.Id, cancellationToken);
        var changedParents = new HashSet<DownloadTask>();
        foreach (var file in referringFiles)
        {
            var parent = file.DownloadTask;
            if (parent is null) continue;
            var episode        = episodeResolver.EpisodeOf(parent, file);
            var originalTarget = file.SupersededByTaskId!.Value;
            var targetId       = originalTarget;
            var visited        = new HashSet<Guid>();
            while (visited.Add(targetId) && deletedTasks.TryGetValue(targetId, out var deletedTask))
            {
                var bridge = episode is { } key
                    ? deletedTask.Files.FirstOrDefault(candidate =>
                                                           episodeResolver.EpisodeOf(deletedTask, candidate) == key)
                    : null;
                if (bridge is null && deletedTask.Episode is { } taskEpisode &&
                    taskEpisode == (episode ?? parent.Episode))
                {
                    var nonSeedFiles = deletedTask.Files.Where(candidate => candidate.Kind != DownloadFileKind.Seed)
                                                  .ToList();
                    if (nonSeedFiles.Count == 1 && episodeResolver.EpisodeOf(deletedTask, nonSeedFiles[0]) is null &&
                        DownloadTaskLookups.CanUseTaskEpisodeFallback(aniParser, nonSeedFiles[0]))
                        bridge = nonSeedFiles[0];
                    else if (deletedTask.Files.Count == 1 &&
                             DownloadTaskLookups.CanUseTaskEpisodeFallback(aniParser, deletedTask.Files.Single()))
                        bridge = deletedTask.Files.Single();
                }

                if (bridge?.SupersededByTaskId is not { } next || next == targetId) break;
                targetId = next;
            }

            var successor = targetId == parent.Id
                ? null
                : await db.DownloadTasks.AsNoTracking()
                          .Where(task => task.Id     == targetId                  && !deletedIds.Contains(task.Id) &&
                                         task.Status != DownloadTaskStatus.Failed &&
                                         task.Status != DownloadTaskStatus.Lost)
                          .Select(task => new { task.Id, task.NamingSnapshot }).FirstOrDefaultAsync(cancellationToken);
            if (targetId != originalTarget && successor is not null)
            {
                file.SupersededByTaskId = successor.Id;
                file.SelectionReason    = $"已被更优任务替代：{successor.NamingSnapshot}";
                file.UpdatedAtUtc       = timeProvider.GetUtcNow();
                continue;
            }

            file.SupersededByTaskId = null;
            if (parent.Status is not (DownloadTaskStatus.PendingSubmit or DownloadTaskStatus.Submitting or
                                      DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)) continue;
            file.IsSelectedForDownload = true;
            if (file.VerificationOutcome == DownloadFileVerificationOutcomes.ReusedVerified)
                file.VerificationOutcome = DownloadFileVerificationOutcomes.ReuseUnverified;
            file.SelectionReason = null;
            file.UpdatedAtUtc    = timeProvider.GetUtcNow();
            changedParents.Add(parent);
        }

        foreach (var parent in changedParents)
        {
            DownloadTaskLookups.RefreshSelectedTotals(parent);
            parent.SelectionSyncPending = true;
        }

        // 每集登记表：待完成优胜随承接方删除而清空；现任被删时回退到它承接的上一任（也删则清空）。
        var deletedFileIds = (await db.DownloadTaskFiles.AsNoTracking()
                                      .Where(file => deletedIds.Contains(file.DownloadTaskId))
                                      .Select(file => file.Id)
                                      .ToListAsync(cancellationToken)).ToHashSet();
        if (deletedFileIds.Count > 0)
        {
            var rows = await db.EpisodeVersions
                               .Where(row => row.PendingVideoFileId    != null ||
                                             row.IncumbentVideoFileId  != null ||
                                             row.SupersededVideoFileId != null)
                               .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (row.PendingVideoFileId is { } pending && deletedFileIds.Contains(pending))
                    row.PendingVideoFileId = null;
                if (row.IncumbentVideoFileId is { } incumbent && deletedFileIds.Contains(incumbent))
                {
                    row.IncumbentVideoFileId = row.SupersededVideoFileId is { } superseded &&
                                               !deletedFileIds.Contains(superseded)
                        ? superseded
                        : null;
                    row.SupersededVideoFileId = null;
                }
                else if (row.SupersededVideoFileId is { } superseded && deletedFileIds.Contains(superseded))
                {
                    row.SupersededVideoFileId = null;
                }
            }
        }

        // 逐集引用回退后，仍被现任/待完成引用且确有完成记录的旧 Replaced 任务与恢复后的职责
        // 重新一致：恢复 Completed 并清除过期的替换声明。不能只看 ReplacedByTaskId 是否指向
        // 本次删除——最后一位承接者仍在时，被删的中间承接方已使职责提前回退；未完成过的
        // 落选墓碑永不复活，也不自动重新添加种子。
        var restoreCandidates = await db.DownloadTasks
                                        .Include(task => task.Files)
                                        .Where(task => task.Status         == DownloadTaskStatus.Replaced &&
                                                       task.CompletedAtUtc != null)
                                        .ToListAsync(cancellationToken);
        if (restoreCandidates.Count == 0) return [.. affectedIds];
        // 必须以 tracking 实体加载：EF identity resolution 复用同 DbContext 中刚修改的逐集引用；
        // AsNoTracking + 匿名投影会绕过变更跟踪，从数据库读到 SaveChanges 之前的旧值。
        var referencedRows = await db.EpisodeVersions
                                     .Where(row => row.IncumbentVideoFileId != null ||
                                                   row.PendingVideoFileId   != null)
                                     .ToListAsync(cancellationToken);
        var stillReferencedIds = referencedRows
                                .SelectMany(row => new[] { row.IncumbentVideoFileId, row.PendingVideoFileId })
                                .OfType<Guid>()
                                .ToHashSet();
        foreach (var candidate in restoreCandidates.Where(candidate =>
                                                              candidate.Files.Any(file =>
                                                                  stillReferencedIds.Contains(file.Id))))
        {
            candidate.Status           = DownloadTaskStatus.Completed;
            candidate.ReplacedByTaskId = null;
            affectedIds.Add(candidate.Id);
        }

        return [.. affectedIds];
    }

    /// <summary>
    ///     完成收尾的逐集现任切换：选中正片按文件级集数（人工覆盖 &gt; 修正解析；唯一正片解析不出
    ///     集数时回退任务级集数）逐集登记为可播放版本，原先指向旧文件的关系保留为承接来源，
    ///     供删除回退；本任务此前登记的待完成优胜一并清空。
    /// </summary>
    private async Task SwitchEpisodeIncumbentsAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken cancellationToken)
    {
        if (task.SubjectId is not { } subjectId) return;
        var sourceScope = SourceScopes.FromSiteSource(task.SiteSource);
        var taskFiles = await db.DownloadTaskFiles.AsNoTracking()
                                .Where(file => file.DownloadTaskId == task.Id)
                                .ToListAsync(cancellationToken);
        var videos = taskFiles.Where(file => file is { Kind: DownloadFileKind.Anime, IsSelectedForDownload: true })
                              .ToList();
        if (videos.Count == 0 && task.Episode is not null)
        {
            // 文件全部不可解析（无内容分类）的单文件任务：任务级集数身份承载唯一文件。
            var selected = taskFiles.Where(file => file.IsSelectedForDownload).ToList();
            if (selected.Count == 1 &&
                DownloadTaskLookups.CanUseTaskEpisodeFallback(aniParser, selected[0])) videos = selected;
        }

        if (videos.Count == 0) return;

        var byEpisode = new Dictionary<decimal, DownloadTaskFile>();
        foreach (var video in videos)
        {
            var episode = episodeResolver.EpisodeOf(task, video) ??
                          (task.Episode is { } taskEpisode &&
                           DownloadTaskLookups.IsSingleEpisodeCarrier(aniParser, task, video, taskEpisode, taskFiles)
                              ? taskEpisode
                              : null);
            if (episode is not { } key || !byEpisode.TryAdd(key, video)) continue;
        }

        if (byEpisode.Count == 0) return;

        var fileIds = videos.Select(file => file.Id).ToHashSet();
        var rows = await db.EpisodeVersions
                           .Where(row => row.SubjectId == subjectId)
                           .ToListAsync(cancellationToken);
        var incumbentFileIds = rows.Where(row => row.IncumbentVideoFileId is not null)
                                   .Select(row => row.IncumbentVideoFileId!.Value)
                                   .ToList();
        var incumbentOwners = incumbentFileIds.Count == 0
            ? []
            : await db.DownloadTasks.Include(owner => owner.Files)
                      .Where(owner => owner.Files.Any(file => incumbentFileIds.Contains(file.Id)))
                      .ToListAsync(cancellationToken);
        var incumbentGuards =
            new Dictionary<Guid, (int Version, DownloadTaskStatus Status, Guid? ReplacedByTaskId,
                string? Group, EnumLanguage? Language)>();
        foreach (var row in rows.Where(row => row.SourceScope          == sourceScope &&
                                              row.IncumbentVideoFileId is not null))
        {
            var fileId = row.IncumbentVideoFileId!.Value;
            var owner  = incumbentOwners.FirstOrDefault(candidate => candidate.Files.Any(file => file.Id == fileId));
            var file   = owner?.Files.FirstOrDefault(candidate => candidate.Id == fileId);
            if (owner is null || file is null) continue;
            var (group, language) = ParseGroupAndLanguage(aniParser, file.OriginalRelativePath,
                                                          owner.NamingSnapshot);
            incumbentGuards[fileId] = (DownloadTaskLookups.EpisodeFileVersion(aniParser, owner, file, row.Episode),
                                       owner.Status, owner.ReplacedByTaskId, group, language);
        }

        var retiredOwnerIds = new List<Guid>();
        foreach (var (episode, video) in byEpisode)
        {
            var row = rows.FirstOrDefault(item => item.Episode == episode && item.SourceScope == sourceScope);
            var switchOwnRow = true;
            if (row is null)
            {
                row = new EpisodeVersion { SubjectId = subjectId, Episode = episode, SourceScope = sourceScope };
                db.EpisodeVersions.Add(row);
                rows.Add(row);
            }
            else if (row.IncumbentVideoFileId is { } incumbent &&
                     (row.PendingVideoFileId is not { } pending || !fileIds.Contains(pending)))
            {
                // 本任务不是登记在案的待完成优胜（未在对决中胜出，或属于不互相竞争的来源）。
                // 例外沿用条目链完成期替换的版本守卫（判定集中在 DownloadTaskLookups，与登记期
                // 同一实现）：现任所属任务已完成且版本不高于本集正片文件时仍允许切换——覆盖两个订阅
                // 条目任务先后完成、登记时互相豁免的场景。字幕组与语言都可比时只在同分区内切换。
                var (group, language) = ParseGroupAndLanguage(aniParser, video.OriginalRelativePath,
                                                              task.NamingSnapshot);
                if (!incumbentGuards.TryGetValue(incumbent, out var guard) ||
                    !DownloadTaskLookups.IsTakeoverEligibleIncumbent(guard.Status, guard.ReplacedByTaskId,
                                                                     guard.Version,
                                                                     DownloadTaskLookups.EpisodeFileVersion(aniParser,
                                                                         task, video, episode, taskFiles),
                                                                     guard.Group, guard.Language,
                                                                     group, language))
                    switchOwnRow = false;
            }

            if (switchOwnRow)
            {
                if (row.IncumbentVideoFileId is { } previousIncumbent && previousIncumbent != video.Id)
                {
                    row.SupersededVideoFileId = previousIncumbent;
                    var previousOwner = incumbentOwners.FirstOrDefault(owner =>
                                                                           owner.Files.Any(file => file.Id ==
                                                                               previousIncumbent));
                    if (previousOwner is not null) retiredOwnerIds.Add(previousOwner.Id);
                }

                row.IncumbentVideoFileId = video.Id;
                if (row.PendingVideoFileId is { } ownPending && fileIds.Contains(ownPending))
                    row.PendingVideoFileId = null;
                row.UpdatedAtUtc = timeProvider.GetUtcNow();
            }

            // 跨来源顶替切换：只切换登记期明确挂了本任务 PendingVideoFileId 的异源旧版行，
            // 未挂 Pending 的同组同语言同版本跨源共存行保持不动。
            foreach (var crossRow in rows.Where(item => item.Episode            == episode    &&
                                                        item.SourceScope        != sourceScope &&
                                                        item.PendingVideoFileId is { } pendingId &&
                                                        fileIds.Contains(pendingId)))
            {
                if (crossRow.IncumbentVideoFileId is { } previousCross && previousCross != video.Id)
                {
                    crossRow.SupersededVideoFileId = previousCross;
                    var previousOwner = incumbentOwners.FirstOrDefault(owner =>
                                                                           owner.Files.Any(file => file.Id ==
                                                                               previousCross));
                    if (previousOwner is not null) retiredOwnerIds.Add(previousOwner.Id);
                }

                crossRow.IncumbentVideoFileId = video.Id;
                crossRow.PendingVideoFileId   = null;
                crossRow.UpdatedAtUtc         = timeProvider.GetUtcNow();
            }
        }

        // 冗余退出评估：被顶替的旧现任职责可能就此全部耗尽——已完成旧任务不再豁免（设计
        // 第四阶段），最后一项职责退出后即翻转为被替换，由对账清理环统一保留内容移除。
        await TryRetireExhaustedOwnersAsync(db, retiredOwnerIds, task.Id, cancellationToken);
    }

    /// <summary>
    ///     完成期版本守卫的字幕组与语言：文件名可解析时取解析结果，解析失败才回退命名要素快照；
    ///     任一值缺失时由守卫本身按历史版本规则处理，不在此猜测。
    /// </summary>
    private static (string? Group, EnumLanguage? Language) ParseGroupAndLanguage(
        AniParser parser, string path, string? namingIngredients)
    {
        var parsed = parser.Parse(DownloadEpisodeResolver.FileName(path));
        if (parsed is not null) return (parsed.Group, parsed.Language);

        var ingredients = TorrentNamingIngredients.FromJson(namingIngredients);
        var language = Enum.TryParse(ingredients?.Language, true, out EnumLanguage parsedLanguage)
            ? parsedLanguage
            : (EnumLanguage?)null;
        return (ingredients?.Group, language);
    }

    /// <summary>
    ///     冗余退出评估（设计第四阶段）：检查职责是否全部耗尽——登记表不再引用其文件、选中内容
    ///     均已被其他现任覆盖、无人工固定资源且无未清操作日志（见 HasResidualDuties）。耗尽的
    ///     旧任务翻转为 Replaced 墓碑，下载器种子由对账清理环统一保留内容移除（deleteFiles=false），
    ///     完成时间不再构成豁免；多任务分别承接时，最后一项职责退出后在此收敛。调用方负责保存。
    /// </summary>
    private async Task TryRetireExhaustedOwnersAsync(
        ApplicationDbContext db, IEnumerable<Guid> ownerTaskIds, Guid triggerTaskId, CancellationToken ct)
    {
        foreach (var ownerId in ownerTaskIds.Where(id => id != triggerTaskId).Distinct())
        {
            var owner = await db.DownloadTasks.Include(task => task.Files)
                                .SingleOrDefaultAsync(task => task.Id == ownerId, ct);
            if (owner is null || owner.ReplacedByTaskId is not null ||
                owner.Status is not (DownloadTaskStatus.Completed or DownloadTaskStatus.Downloading
                                                                  or DownloadTaskStatus.Paused))
                continue;
            if (owner.SubjectId is not { } subjectId) continue;

            var sourceScope = SourceScopes.FromSiteSource(owner.SiteSource);
            var rows        = await db.EpisodeVersions.Where(row => row.SubjectId == subjectId).ToListAsync(ct);
            if (DownloadTaskLookups.HasResidualDuties(aniParser, owner, owner.Files.ToList(), rows,
                                                      episodeResolver.EpisodeOf, sourceScope))
                continue;

            owner.Status           = DownloadTaskStatus.Replaced;
            owner.ReplacedByTaskId = triggerTaskId;
            owner.FailedStep       = null;
            owner.Error            = null;
            var trigger = await db.DownloadTasks.SingleOrDefaultAsync(task => task.Id == triggerTaskId, ct);
            trigger?.ReplacesTaskId ??= owner.Id;
            logger.LogInformation("任务 {TaskId} 的全部职责已被承接（触发方 {TriggerTaskId}），翻转为被替换，等待冗余清理移除种子。",
                                  owner.Id, triggerTaskId);
        }
    }


    private async Task SubmitAsync(IQbittorrentSession session, DownloadTask task, CancellationToken cancellationToken)
    {
        // Reconcile 的任务列表可能在其它流程登记替换前读取；门禁内再次取当前状态，旧快照不得提交。
        task = await LoadTaskAsync(task.Id, cancellationToken);

        // 提交前先按 hash 对账：下载器中已存在的同 hash 种子直接接管——既覆盖“自己上次提交已生效
        // 但未收到确认”，也覆盖“用户或其他工具预先添加的种子”。数据库活动唯一索引保证同 hash
        // 至多一个活动任务，接管不会与自身其他任务互相抢占。
        var existing = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken))
           .FirstOrDefault();
        // 响应丢失或人工重试仍须恢复原状态机，不能按普通接管改名、移动或完成共享候选。
        if (task.IsolationState != DownloadIsolationState.None || task.VerificationIntent is not null)
        {
            // 按 hash 找到种子即证明 Add 已生效。纯新任务可直接发布保存根；已归档任务重下时必须
            // 等到校验/归档成功才发布，旧成片路径在下载期间继续保持有效。
            var preserveOldPieces = task.CompletedAtUtc is not null;
            if (existing is { } observed && task.PendingSubmitSavePath is { Length: > 0 } planned)
            {
                await UpdateTaskAsync(task.Id, task.Status, update =>
                {
                    if (preserveOldPieces)
                    {
                        if (!MatchesIsolationPath(observed.SavePath, planned))
                            update.PendingSubmitSavePath = DownloadPathNamePolicy.NormalizePath(observed.SavePath);
                        return;
                    }

                    update.SavePath = MatchesIsolationPath(observed.SavePath, planned)
                        ? planned
                        : DownloadPathNamePolicy.NormalizePath(observed.SavePath);
                    update.PendingSubmitSavePath = null;
                }, cancellationToken);
                task = await LoadTaskAsync(task.Id, cancellationToken);
            }

            // 响应丢失后由 hash 对账恢复：保留旧成片的任务须把逐文件引用对齐到下载器原始布局。
            await UpdateTaskAsync(task.Id, task.Status,
                                  update => update.Status = DownloadTaskStatus.Downloading,
                                  cancellationToken,
                                  preserveOldPieces && existing is not null
                                      ? (db, _) => StartNewRoundFilesAsync(db, task.Id, cancellationToken)
                                      : null);
            await ReconcileTrackedAsync(session, await LoadTaskAsync(task.Id, cancellationToken), existing,
                                        cancellationToken);
            return;
        }

        if (existing is not null)
        {
            await AdoptExistingAsync(task, existing, session, cancellationToken);
            return;
        }

        switch (task)
        {
            // 可见性窗口先于预算判断：最后一次 Add 请求可能已被下载器接受但响应丢失，
            // 必须让它等满自己的窗口再决定是否耗尽预算，不能在第 1 秒就按预算耗尽转 Failed。
            // hash 对账未命中先等待窗口到期；查询轮次与 pulse 不消耗提交预算。
            case { Status: DownloadTaskStatus.Submitting, LastSubmitAttemptAtUtc: { } attemptedAt } when
                timeProvider.GetUtcNow() < attemptedAt + timing.AddVisibilityWindow :
                await DeferObservationAsync(task.Id, task.Status,
                                            timeProvider.GetUtcNow() + timing.PreparationSampleInterval,
                                            cancellationToken);
                return;
            case { Status: DownloadTaskStatus.Submitting, AttemptCount: >= MaxAutoSubmitAttempts } :
                await MarkFailedAsync(task.Id, task.Status, "提交", "多次提交后仍未在下载器中发现该任务，请检查下载器后人工重试。",
                                      cancellationToken);
                return;
        }

        // 本次 Add 之前该任务是否已持有待确认提交根占用：结果未知的 Add（响应丢失、暂不可见、
        // 跨进程恢复或人工重试保留）在发出请求前就写入 PendingSubmitSavePath。后一次同 hash Add
        // 的明确拒绝只能否定那一次请求，无法否定先前未知请求——它仍可能写入了自己的根。
        var hadUnconfirmedSubmission = DownloadRoundLayout.PendingAddRoots(task).Count > 0;

        // 一个规划流程：已有候选先校验；需要替换旧版时暂存；其余任务直接在正式根下载。
        (string CandidateRoot, string IntentJson)? verificationAdd;

        string addRoot;
        var    replacing = false;
        var    withFiles = await LoadTaskWithFilesAsync(task.Id, cancellationToken) ?? task;
        try
        {
            verificationAdd = await TryPlanVerificationAddAsync(session, task, cancellationToken);
            replacing = task.ReplacesTaskId is not null || task.CompletedAtUtc is not null ||
                        (downloadFileService is not null &&
                         await downloadFileService.HasReplacementAsync(task.Id, cancellationToken));
            // 纯正片包（无 Seed 附件）可以直接在动画目录下载：后者是当前布局中清白任务的正式根，
            // 归档时 Plan 保留该根并原地改名，省去从全局根迁入的一次 SetLocation。含 Seed 的包、
            // 复用校验与替换暂存仍沿用各自根目录——它们需要媒体目录与做种目录共用同一个父根。
            var mediaRoot = downloadFileService is not null                   &&
                            !replacing                                        &&
                            !string.IsNullOrWhiteSpace(task.DownloadRootPath) &&
                            !string.IsNullOrWhiteSpace(task.MediaDirectory)   &&
                            withFiles.Files.Count > 0                         &&
                            withFiles.Files.All(file => file.Kind != DownloadFileKind.Seed)
                ? DownloadPathNamePolicy.JoinSavePath(task.DownloadRootPath!, task.MediaDirectory!)
                : null;
            addRoot = verificationAdd?.CandidateRoot ??
                      (string.IsNullOrWhiteSpace(task.DownloadRootPath) ? task.SavePath :
                          replacing ? DownloadTaskPlacement.TaskRoot(task.DownloadRootPath, task.Id) :
                                      mediaRoot ?? task.DownloadRootPath);
            // 仅用于本次路径检查与验证；新一轮的成片路径落 PendingSubmitSavePath，Add 确认前不覆盖旧 SavePath。
            task.SavePath = addRoot;
            if (verificationAdd is null                   && !string.IsNullOrWhiteSpace(task.DownloadRootPath) &&
                !DownloadTaskPlacement.IsTaskScoped(task) && downloadFileService is not null                   &&
                await InitialLayoutConflictsAsync(session, withFiles, addRoot, cancellationToken))
                throw new TargetPathOccupiedException("种子的原始布局被其他任务占用，不能在该目录直接添加，请先处理冲突。");
        }
        catch (InvalidOperationException exception)
        {
            await MarkFailedAsync(task.Id, task.Status, "提交路径", exception.Message, cancellationToken);
            return;
        }

        // 本次目标根此前是否已有未决 Add：同根补发被拒时那一次仍可能生效，占用必须保留；
        // 目标根是本次新登记的，明确拒绝只撤销这一次的占用，其他根上的先前请求不受影响。
        var targetRootHadPendingAdd = DownloadRoundLayout.HasPendingAddRoot(task, addRoot);

        // 路径决策与提交同一事务落库：.tasks 暂存、候选根与正式根的选择原因可追溯、可诊断。
        var decidedAt = timeProvider.GetUtcNow();
        var pathDecision = new DownloadPathDecision
        {
            DecidedAtUtc  = decidedAt,
            Root          = verificationAdd?.CandidateRoot ?? addRoot,
            RelatedTaskId = task.ReplacesTaskId,
            Kind = verificationAdd is not null ? DownloadPathDecisionKinds.ReuseVerification
                : replacing                    ? DownloadPathDecisionKinds.ReplacementStaging
                                                 : DownloadPathDecisionKinds.FinalRoot,
            Reason = verificationAdd is not null
                ? "发现可复用候选数据，在候选根受控映射并校验后再决定复用或隔离。"
                : replacing
                    ? "存在待替换旧版或本任务旧成片，先在 .tasks 任务目录暂存，完成后统一归档替换。"
                    : "无复用候选且无待替换版本，直接在正式根下载。"
        };

        if (!string.IsNullOrWhiteSpace(task.DownloadRootPath) && verificationAdd is null)
            try
            {
                await (submissionPathValidator ?? new DownloadSubmissionPathValidator(dbContextFactory))
                   .ValidateAsync(task, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "提交路径", exception.Message, cancellationToken);
                return;
            }

        // 请求发出前先落库 Submitting：下载器已接收而应用在保存结果前退出时，重启按 Hash 对账而不是重复提交。
        // 同时保存准备意图：种子将以停止状态添加，文件选择与命名确认完成前不得开始网络下载；
        // 崩溃恢复按“Submitting + IsSystemPaused”识别这是本应用发起的准备暂停，不会误判为人工暂停。
        // 新根只写 PendingSubmitSavePath：Add 未确认成功前，旧成片路径与逐文件引用保持原样。
        // 补发规划改根时不能覆盖首条未决 Add 的实际写入根：首条结果收敛前它仍可能被下载器写入。
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.Status                 =   DownloadTaskStatus.Submitting;
            update.AttemptCount           +=  1;
            update.SubmittedAtUtc         ??= timeProvider.GetUtcNow();
            update.LastSubmitAttemptAtUtc =   timeProvider.GetUtcNow();
            update.IsSystemPaused         =   true;
            update.PreparationStage       =   DownloadPreparationStage.Planned;
            update.PathDecision           =   pathDecision.ToJson();
            if (!hadUnconfirmedSubmission)
                update.PendingSubmitSavePath = verificationAdd?.CandidateRoot ?? addRoot;
            DownloadRoundLayout.RegisterPendingAdd(update, addRoot);
            if (verificationAdd is not null) update.VerificationIntent = verificationAdd.Value.IntentJson;
        }, cancellationToken);

        string response;
        try
        {
            response = await session.AddTorrentAsync(task.TorrentFile, addRoot,
                                                     TagsFor(task), task.NamingSnapshot,
                                                     true, cancellationToken);
            if (string.Equals(response.Trim(), "Ok.", StringComparison.OrdinalIgnoreCase))
                await ApplyConfiguredShareLimitAsync(session, task.InfoHash, task.SiteSource, cancellationToken);
        }
        catch (Exception exception) when (exception is QbittorrentLoginFailedException
                                                    or QbittorrentUnauthorizedException
                                                    or QbittorrentForbiddenException)
        {
            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "认证",
                                  $"下载器认证失败，请检查设置页的地址与账号：{exception.Message}", cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is QbittorrentBadRequestException or QbittorrentNotFoundException)
        {
            // 明确拒绝只否定本轮请求：本轮新登记、此前没有未决 Add 的根被撤销，让其他任务可以使用；
            // 更早的未决请求（同根或其他根）仍可能生效，其占用必须保留并继续等待收敛。
            // 本轮若规划过校验意图，它同样只服务这条被否定的请求：残留会把恢复可见的旧根种子
            // 引入校验恢复分支并按候选根不匹配转入隔离删除。
            if (hadUnconfirmedSubmission)
            {
                logger.LogWarning(exception, "任务 {TaskId} 的补发 Add 被下载器拒绝，先前请求结果未知，继续保留路径占用。",
                                  task.Id);
                await DeferObservationAsync(task.Id, DownloadTaskStatus.Submitting,
                                            timeProvider.GetUtcNow() + timing.AddVisibilityWindow, cancellationToken,
                                            targetRootHadPendingAdd ? null : addRoot,
                                            verificationAdd is not null);
                return;
            }

            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝了提交：{exception.Message}",
                                  cancellationToken, true);
            return;
        }
        catch (QbittorrentException exception)
        {
            // 服务器 5xx 或网络中断：Submitting 已在请求发出前落库，等待下一轮按 Hash 对账。
            logger.LogWarning(exception, "提交下载任务 {TaskId} 后未收到确认，等待按 Hash 对账。", task.Id);
            return;
        }

        // Add 可能在请求发出后被下载器接受，即使协调门禁按预期阻止并发登记；
        // 最终持久状态仍作一次权威检查，若任务在外部事务中已被替换则清除迟到资源。
        var afterAdd = await LoadTaskAsync(task.Id, cancellationToken);
        if (afterAdd.Status is not (DownloadTaskStatus.Submitting or DownloadTaskStatus.PendingSubmit))
        {
            var lateTorrent = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken)).FirstOrDefault();
            if (lateTorrent is not null)
                await session.DeleteTorrentAsync(task.InfoHash, true, cancellationToken);
            return;
        }

        var responseText = (response ?? string.Empty).Trim();
        if (responseText.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            // "Fails." 也可能是种子已被下载器接收（如外部预先添加、响应语义差异）：再按 hash 确认一次，找到即接管。
            var confirmed = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken))
               .FirstOrDefault();
            if (confirmed is not null)
            {
                await AdoptExistingAsync(task, confirmed, session, cancellationToken);
                return;
            }

            // 按 hash 查无才是真正的明确拒绝，但只否定本轮请求：本次规划的新根此前没有未决 Add，
            // 撤销这一次的登记即可；同根已有更早未决请求时（响应丢失、跨进程恢复或人工重试保留）
            // 这次 Fails. 只能说明本次补发被拒——该请求可能仍在下载器中（只是暂时查不到），
            // 占用必须保留。两种情形都继续等待先前请求收敛，不丢弃其接管机会。
            // 本轮规划出的校验意图同样随被否定的请求失效，不再指向不会发生的候选校验。
            if (hadUnconfirmedSubmission)
            {
                logger.LogWarning("任务 {TaskId} 的补发 Add 被拒绝且按 hash 查无，先前请求结果未知，继续保留路径占用。",
                                  task.Id);
                await DeferObservationAsync(task.Id, DownloadTaskStatus.Submitting,
                                            timeProvider.GetUtcNow() + timing.AddVisibilityWindow, cancellationToken,
                                            targetRootHadPendingAdd ? null : addRoot,
                                            verificationAdd is not null);
                return;
            }

            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝该种子：{responseText}",
                                  cancellationToken, true);
            return;
        }

        // Add 已确认成功：纯新任务立即发布保存根；已归档任务重下时只把逐文件引用切到种子原始文件名，
        // 保存根仍由 PendingSubmitSavePath 解释，成片根在校验或归档成功前保持不动。
        // 补发可能规划过新根：本次 Ok. 只证明新根的请求被接受，先前结果未知的请求仍可能在旧根生效，
        // 因此已有未决根时保留台账（其中已含旧根与本次新根），等 hash 可见或明确收敛后再统一清理。
        var preserveArchivedPieces = task.CompletedAtUtc is not null;
        await UpdateTaskAsync(task.Id, DownloadTaskStatus.Submitting,
                              update =>
                              {
                                  update.Status                = DownloadTaskStatus.Downloading;
                                  update.PendingSubmitSavePath = addRoot;
                                  if (!hadUnconfirmedSubmission) DownloadRoundLayout.ClearPendingAdds(update);
                                  if (preserveArchivedPieces) return;
                                  update.SavePath              = addRoot;
                                  update.PendingSubmitSavePath = null;
                              },
                              cancellationToken,
                              async (db, updated) =>
                              {
                                  if (preserveArchivedPieces)
                                      await StartNewRoundFilesAsync(db, task.Id, cancellationToken);
                                  await notificationEnqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted,
                                                                             "下载开始",
                                                                             $"{DescribeTask(updated)}：已提交到下载器开始下载。命名：{updated.NamingSnapshot}",
                                                                             cancellationToken);
                              });

        // 准备推进（文件选择 → 命名 → 启动）由下一轮对账按下载器真实状态驱动：种子此刻处于
        // 停止状态，本轮不做文件操作，崩溃或响应丢失都由对账幂等补做。
    }

    /// <summary>
    ///     提交前统一识别已有数据并生成校验布局；无候选则返回普通下载规划。已有候选与新文件共用下载根，原始布局存在冲突时明确阻止提交。
    /// </summary>
    private async Task<(string CandidateRoot, string IntentJson)?> TryPlanVerificationAddAsync(
        IQbittorrentSession session, DownloadTask task, CancellationToken ct)
    {
        if (downloadFileService is null || string.IsNullOrWhiteSpace(task.DownloadRootPath)) return null;

        IReadOnlyList<(int FileIndex, string TargetPath)> targets;
        try
        {
            targets = await downloadFileService.ComputeArchivalTargetsAsync(task.Id, ct);
        }
        catch (InvalidOperationException)
        {
            // 命名要素缺失等规划错误：候选无从谈起，由普通准备阶段呈现命名错误。
            return null;
        }

        if (targets.Count == 0) return null;

        var candidates = await downloadFileService.EnumerateReuseCandidatesAsync(session, task.Id, targets, ct);
        // 用户只删种、磁盘上仍保留成片时，普通枚举会排除本任务，必须补上本任务自己的旧归档证据；
        // 这些路径同样只是候选，仍由受控映射和 recheck 决定是否真的可复用。
        if (task.CompletedAtUtc is not null)
            candidates =
            [
                .. candidates,
                .. await downloadFileService.EnumerateOwnReuseCandidatesAsync(task.Id, targets, ct)
            ];
        if (candidates.Count == 0) return null;
        candidates = candidates.GroupBy(candidate => candidate.FileIndex)
                               .Select(group => group.First()).ToList();

        var withFiles = await LoadTaskWithFilesAsync(task.Id, ct);
        if (withFiles is null) return null;

        var hasReplacement = await downloadFileService.HasReplacementAsync(task.Id, ct);
        // 与 SubmitAsync 同一口径：无 Seed、非替换的纯正片任务以媒体目录为正式根。复用校验必须
        // 使用这个根，否则 addRoot 会被 CandidateRoot 拉回全局下载根，文件相对路径带上媒体目录前缀。
        var mediaRoot = task.ReplacesTaskId is null                       &&
                        task.CompletedAtUtc is null                       &&
                        !hasReplacement                                   &&
                        !string.IsNullOrWhiteSpace(task.DownloadRootPath) &&
                        !string.IsNullOrWhiteSpace(task.MediaDirectory)   &&
                        withFiles.Files.Count > 0                         &&
                        withFiles.Files.All(file => file.Kind != DownloadFileKind.Seed)
            ? DownloadPathNamePolicy.JoinSavePath(task.DownloadRootPath!, task.MediaDirectory!)
            : null;
        var candidateRoot = mediaRoot ??
                            DownloadPathNamePolicy.NormalizePath(task.DownloadRootPath ?? task.SavePath);

        if (await InitialLayoutConflictsAsync(session, withFiles, candidateRoot, ct,
                                              candidates.GroupBy(item => item.AbsolutePath,
                                                                 StringComparer.OrdinalIgnoreCase)
                                                        .ToDictionary(group => group.Key, group => group.First().Size,
                                                                      StringComparer.OrdinalIgnoreCase)))
            throw new TargetPathOccupiedException("校验任务的原始布局存在非候选占用，无法安全添加。");
        // 曾完成的任务重下时旧成片仍占用归档目标：非候选文件必须落在任务专属暂存路径，
        // 未被证实可复用前不得写入正式目标。
        var replacing = task.CompletedAtUtc is not null || hasReplacement;

        var files = new List<DownloadVerificationMapping>();
        foreach (var file in withFiles.Files)
        {
            var    target    = targets.FirstOrDefault(pair => pair.FileIndex    == file.FileIndex);
            var    candidate = candidates.FirstOrDefault(item => item.FileIndex == file.FileIndex);
            string verificationPath;
            var    isCandidate = false;
            if (file.IsSelectedForDownload && candidate != default &&
                EqualVerificationPath(candidate.AbsolutePath, target.TargetPath))
            {
                verificationPath = candidate.AbsolutePath;
                isCandidate      = true;
            }
            else
            {
                verificationPath = file.IsSelectedForDownload && !replacing && target != default
                    ? target.TargetPath
                    : DownloadPathNamePolicy.JoinSavePath(candidateRoot,
                                                          $".tasks-{withFiles.Id:N}/{file.OriginalRelativePath}");
            }

            files.Add(new DownloadVerificationMapping(file.FileIndex,
                                                      DownloadPathNamePolicy.NormalizePath(verificationPath),
                                                      file.Size, file.IsSelectedForDownload,
                                                      target == default ? null : target.TargetPath, isCandidate));
        }

        var intent = new DownloadVerificationIntent
        {
            Round              = 1, Step = DownloadVerificationStep.Mapping, CreatedAtUtc = timeProvider.GetUtcNow(),
            CandidateRoot      = candidateRoot, Files = files,
            // 意图在本进程创建，写入当前进程实例 Id：重启作废守卫要求 owner 非空才会比较实例归属，
            // 若生产创建路径不落值，守卫恒不触发，旧基线与见证会被重启后的进程静默沿用。
            ObserverInstanceId = processInstanceId
        };
        return (candidateRoot, intent.ToJson());
    }

    private static bool EqualVerificationPath(string left, string right) =>
        string.Equals(DownloadPathNamePolicy.NormalizePath(left), DownloadPathNamePolicy.NormalizePath(right),
                      StringComparison.Ordinal);


    /// <summary>
    ///     候选根初始布局冲突检查：按种子实际内容布局推导（单文件为文件名、多文件为元数据相对路径），
    ///     检查全部文件（含未选中）的真实初始路径及映射后的停靠命名空间，与下载器种子及数据库任务的可见路径
    ///     做相等与父子重叠比对。候选根本身是共享位置，任何可见冲突都意味着无法证明初始映射安全，整体降级。
    /// </summary>
    private async Task<bool> InitialLayoutConflictsAsync(
        IQbittorrentSession                session, DownloadTask task, string candidateRoot, CancellationToken ct,
        IReadOnlyDictionary<string, long>? candidates = null)
    {
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile)) return true;
        var layout = torrent.Files
                            .SelectMany(file =>
                             {
                                 var relative = TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile);
                                 return new[]
                                 {
                                     (Path : DownloadPathNamePolicy.JoinSavePath(candidateRoot, relative),
                                      Size : file.Length),
                                     (Path : DownloadPathNamePolicy.JoinSavePath(candidateRoot,
                                          $".tasks-{task.Id:N}/{relative}"),
                                      Size : file.Length)
                                 };
                             }).ToList();

        List<DownloadTask> others;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            others = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .Where(value => value.Id != task.Id).ToListAsync(ct);
        }

        foreach (var other in others)
        {
            var hasUnsettledLayout = other.PendingSavePath is not null       ||
                                     other.PendingSubmitSavePath is not null ||
                                     other.Files.Any(file => file.PendingRenameFrom is not null ||
                                                             file.PendingRenameTo is not null) ||
                                     other.VerificationIntent is not null                      ||
                                     other.IsolationState != DownloadIsolationState.None;
            var occupiedPaths = other.Files.Count == 0
                ? new List<(string Path, long? Size)> { (other.SavePath, null) }
                : other.Files.SelectMany(file => DownloadRoundLayout.OccupiedPaths(other, file)
                                                                    .Select(path => (Path : path,
                                                                                Size : (long?)file.Size)))
                       .ToList();
            // 待确认提交根可能已被下载器接受但尚未可见：种子以原始文件名落在该根下，
            // 必须和 PendingSavePath 一样纳入占用检查，不能让第二个不同 hash 任务获准写入同一路径。
            if (other.Files.Count == 0)
                foreach (var pendingRoot in DownloadRoundLayout.PendingAddRoots(other))
                    occupiedPaths.Add((pendingRoot, null));

            foreach (var occupied in occupiedPaths)
            foreach (var item in layout)
            {
                if (!PathsOverlapAbsolute(item.Path, occupied.Path)) continue;
                if (candidates is not null                              &&
                    !hasUnsettledLayout                                 &&
                    EqualVerificationPath(item.Path, occupied.Path)     &&
                    item.Size == occupied.Size                          &&
                    candidates.TryGetValue(occupied.Path, out var size) &&
                    size == occupied.Size) continue;
                return true;
            }
        }

        var managedHashes = others.Select(value => value.InfoHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingSubmitHashes = others.Where(value => DownloadRoundLayout.IsPending(value))
                                        .Select(value => value.InfoHash)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in await session.ListTorrentsAsync(null, ct))
        {
            if (string.Equals(other.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var root = DownloadPathNamePolicy.NormalizePath(other.SavePath).TrimEnd('/');
                foreach (var file in await session.ListFilesAsync(other.Hash, ct))
                {
                    var occupied = root + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                    if (layout.Any(item => PathsOverlapAbsolute(item.Path, occupied) &&
                                           !(candidates is not null &&
                                             other is
                                             {
                                                 IsMoving: false,
                                                 Category: not (QbittorrentTorrentCategory.Checking
                                                             or QbittorrentTorrentCategory.Error
                                                             or QbittorrentTorrentCategory.Unknown)
                                             } &&
                                             !pendingSubmitHashes.Contains(other.Hash) &&
                                             (!managedHashes.Contains(other.Hash) ||
                                              file.Size <= 0 || file.Progress >= 1 || !file.IsSelectedForDownload) &&
                                             EqualVerificationPath(item.Path, occupied) &&
                                             item.Size == file.Size &&
                                             candidates.TryGetValue(occupied, out var size) &&
                                             size == file.Size))) return true;
                }
            }
            catch (ArgumentException)
            {
                // 无法解析保存根的外部种子跳过。
            }
        }

        return false;
    }

    private static bool PathsOverlapAbsolute(string left, string right)
    {
        var l = DownloadPathNamePolicy.NormalizePath(left).TrimEnd('/');
        var r = DownloadPathNamePolicy.NormalizePath(right).TrimEnd('/');
        return string.Equals(l, r, StringComparison.OrdinalIgnoreCase)   ||
               l.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ||
               r.StartsWith(l + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveVerificationIntentAsync(
        Guid taskId, DownloadTaskStatus expectedStatus, DownloadVerificationIntent intent, CancellationToken ct)
    {
        // 旧数据/旧路径产生的意图可能缺少 ObserverInstanceId：空 id 只说明创建时未记录归属，
        // 并不能证明它来自其他进程，因此按「本进程新建」补写当前实例 Id 即可。
        // 已有非空且不同的 id 表示确实来自其他进程实例，保持原值交给重启作废守卫处理，不得改写。
        if (intent.ObserverInstanceId is null) intent = intent with { ObserverInstanceId = processInstanceId };
        await UpdateTaskAsync(taskId, expectedStatus,
                              update => update.VerificationIntent = intent.ToJson(), ct);
    }

    /// <summary>
    ///     推迟本任务的下一次观察：等待确认窗口或采样间隔到期。到期时间持久化，重启后仍按此调度；
    ///     到期前的对账轮次跳过本任务，不影响其他任务。
    ///     revokeAddRoot 用于明确拒绝后只撤销本次新登记、且此前没有更早未决 Add 的根。
    ///     clearVerificationIntent 用于补发被明确拒绝后作废本次请求持久化的校验意图：该请求已被
    ///     下载器否定，候选根不会再有本轮校验，残留意图会让旧根种子恢复可见时误走校验恢复分支。
    ///     SubmitAsync 的规划路径只在任务无意图时可达（入口存在校验意图即走恢复分支并返回），
    ///     清空不会连带任何更早请求的恢复信息；旧根未决占用由 revokeAddRoot 的口径独立保留。
    /// </summary>
    private Task DeferObservationAsync(
        Guid    taskId,               DownloadTaskStatus expectedStatus, DateTimeOffset dueUtc, CancellationToken ct,
        string? revokeAddRoot = null, bool               clearVerificationIntent = false) =>
        UpdateTaskAsync(taskId, expectedStatus,
                        update =>
                        {
                            update.NextObservationAtUtc = dueUtc;
                            if (revokeAddRoot is not null)
                                DownloadRoundLayout.RevokePendingAddRoot(update, revokeAddRoot);
                            if (clearVerificationIntent) update.VerificationIntent = null;
                        }, ct);

    /// <summary>
    ///     添加可见性窗口判断：请求已发出（或响应结果未知）且从未在下载器观察到该种子时，短暂查不到
    ///     只推迟观察，不触发 Lost/隔离；窗口过后仍缺失按原有缺失语义处理。窗口按最近一次 Add 请求计算，
    ///     重试成功后获得全新的窗口，不沿用首次提交时间。
    /// </summary>
    private bool WithinAddVisibilityWindow(DownloadTask task) =>
        LatestAddRequestAt(task) is { } at && timeProvider.GetUtcNow() < at + timing.AddVisibilityWindow;

    /// <summary>最近一次 Add 请求时间（首次提交或之后的重试/隔离重加）；null 表示从未发起。</summary>
    private static DateTimeOffset? LatestAddRequestAt(DownloadTask task) =>
        task is { SubmittedAtUtc: { } submitted, LastSubmitAttemptAtUtc: { } attempted }
            ? submitted > attempted ? submitted : attempted
            : task.LastSubmitAttemptAtUtc ?? task.SubmittedAtUtc;

    /// <summary>
    ///     文件清单宽限：hash 已可见而逐文件清单尚未就绪（qB 仍在装载元数据）时，同样按添加可见性
    ///     窗口等待；窗口过后清单仍不完整才是真实的身份冲突。不要求任务从未同步过——隔离重加等
    ///     已同步任务同样适用。
    /// </summary>
    private bool WithinManifestGraceWindow(DownloadTask task) =>
        LatestAddRequestAt(task) is { } at && timeProvider.GetUtcNow() < at + timing.AddVisibilityWindow;

    /// <summary>校验期运行状态对应的风险结论：Mapping 之外的非做种运行可能补写共享文件；做种收尾不算。</summary>
    private static bool NextUnverifiedWriteRisk(
        DownloadVerificationIntent intent, QbittorrentTorrentCategory category) =>
        intent.UnverifiedWriteRisk == true                                                                  ||
        (intent.Step != DownloadVerificationStep.Mapping && category != QbittorrentTorrentCategory.Seeding) ||
        intent is { PauseAttempts: > 0, UnverifiedWriteRisk: null };

    /// <summary>作废已积累的结果稳定样本：再次校验或移动使退出校验前的观察结果不再有效。</summary>
    private static DownloadVerificationIntent VoidStabilitySamples(DownloadVerificationIntent intent) => intent with
    {
        StabilitySample = null,
        StabilityStartedAtUtc = null,
        StabilityObservedAtUtc = null,
        StabilitySampleAtUtc = null,
        StabilitySampleCount = 0
    };

    /// <summary>
    ///     作废本轮校验观察并回到指定子步骤：校验锚点（请求时间与启动窗口）、有效进展、结果稳定
    ///     样本与停止台账一并清空，新轮次（补发 recheck 或移动后重验）从零积累证据，绝不沿用上一轮
    ///     证据；作废与下一轮请求之间观察到的 Checking 属于上一轮残留，不得归属为新轮证据。移动等待
    ///     起点同样随轮次作废清空，需要继续等待的移动由调用方按首次观察时刻重新记录。
    /// </summary>
    private static DownloadVerificationIntent ResetRoundEvidence(
        DownloadVerificationIntent intent, string step) => intent with
    {
        Step = step,
        ObservedChecking = false,
        RecheckRequestedAtUtc = null,
        RecheckAttemptedAtUtc = null,
        Baseline = null,
        PreRequestCheckingSinceUtc = null,
        ResumeDataSeenAtUtc = null,
        CompletionEvidenceKind = null,
        CompletionEvidenceAtUtc = null,
        DeadlineUtc = null,
        MovingObservedAtUtc = null,
        CheckStartedAtUtc = null,
        LastCheckProgressAtUtc = null,
        LastCheckProgress = null,
        StabilitySample = null,
        StabilityStartedAtUtc = null,
        StabilityObservedAtUtc = null,
        StabilitySampleAtUtc = null,
        StabilitySampleCount = 0,
        TimeoutStopRequestedAtUtc = null,
        TimeoutStopAttempts = 0,
        ObservedRawState = null,
        RecheckAppliedAtUtc = null
    };

    /// <summary>
    ///     未确认任务重新发起校验的重置：作废上一轮证据与请求台账、轮次 +1，保留候选映射、
    ///     保存根与文件；下一轮从「尚未发出」重发。人工「重新校验」与未确认后的人工恢复共用。
    /// </summary>
    private static DownloadVerificationIntent ResetUnconfirmedRound(DownloadVerificationIntent intent) =>
        ResetRoundEvidence(intent, DownloadVerificationStep.Requested) with
        {
            Round = intent.Round + 1,
            RequestState = DownloadVerificationRequestState.NotSent,
            SendFailureCount = 0,
            LastRequestError = null
        };

    /// <summary>
    ///     recheck 的发送与请求状态证据：发送前先做请求前门控（下载器已在校验/移动时同一轮不重发），
    ///     捕获并落库请求前基线 + 请求状态（Unknown）与尝试锚点，再发送。发送抛 qB/网络异常时不冒泡、
    ///     不消耗 IssuedCount，保留基线与锚点按采样间隔重试；连续失败达到上限转校验未确认。
    ///     发送成功后才登记 IssuedCount/请求时间/启动窗口，并立即查询一次快照——快速校验可能在下一个
    ///     采样间隔前就结束。立即观察只接受 checkingDL/checkingUP；checkingResumeData 只记录提示。
    /// </summary>
    private async Task IssueRecheckAndObserveAsync(
        IQbittorrentSession session, DownloadTask task, DownloadVerificationIntent intent, CancellationToken ct)
    {
        var now      = timeProvider.GetUtcNow();
        var snapshot = await TryFindSnapshotAsync(session, task.InfoHash, ct);

        // 请求前门控：即将发送时下载器已有校验/移动，本轮绝不叠加请求，也不把这段外部校验归为本轮证据。
        // 等待起点随意图持久化；同一轮不重发，超过校验启动窗口仍无法开始才转未确认（不隔离）。
        if (snapshot is { Category: QbittorrentTorrentCategory.Checking } or { IsMoving: true })
        {
            if (IsResumeDataRawState(snapshot.RawState))
                intent = intent with { ResumeDataSeenAtUtc = intent.ResumeDataSeenAtUtc ?? now };
            var waitingSince = intent.PreRequestCheckingSinceUtc ?? now;
            if (now - waitingSince >= timing.CheckingStartWindow)
            {
                await EnterVerificationUnconfirmedAsync(task, intent, "请求前下载器已有校验尚未结束，无法开始本轮校验；未改动任何文件，可重新校验或核对下载器。",
                                                        ct);
                return;
            }

            var waiting = intent with
            {
                Step = DownloadVerificationStep.Requested,
                PreRequestCheckingSinceUtc = waitingSince
            };
            await SaveVerificationIntentAsync(task.Id, task.Status, waiting, ct);
            await UpdateTaskAsync(task.Id, task.Status,
                                  update =>
                                  {
                                      update.NextObservationAtUtc =
                                          now + timing.PreparationSampleInterval;
                                      update.PreparationStage = DownloadPreparationStage.AwaitingVerification;
                                  }, ct);
            logger.LogInformation("任务 {TaskId} 请求前门控：下载器已有校验/移动尚未结束，本轮不发送 recheck。",
                                  task.Id);
            return;
        }

        // 请求前基线包含 pieces_have/pieces_num：qB 在 recheck 请求后同步清零 pieces，再恢复为校验结果，
        // 基线大于 0 是识别「请求已被该 hash 接收」的前提。查询失败不阻断发送，回退到快照基线见证。
        var baselineProperties = await TryGetPropertiesAsync(session, task.InfoHash, ct);
        var baseline = snapshot is null
            ? null
            : new DownloadVerificationBaseline(snapshot.Progress, snapshot.AmountLeft, snapshot.Completed,
                                               snapshot.CompletionOn, snapshot.TotalSize, snapshot.Category,
                                               snapshot.RawState, snapshot.IsMoving, now, now, intent.Round,
                                               baselineProperties?.PiecesHave, baselineProperties?.PiecesNum);
        var prepared = ResetRoundEvidence(intent, DownloadVerificationStep.Requested) with
        {
            RequestState = DownloadVerificationRequestState.Unknown,
            RecheckAttemptedAtUtc = now,
            Baseline = baseline,
            LastRequestError = null
        };
        await SaveVerificationIntentAsync(task.Id, task.Status, prepared, ct);

        try
        {
            await session.RecheckTorrentAsync(task.InfoHash, ct);
        }
        catch (Exception exception) when (IsRecheckTransportFailure(exception, ct))
        {
            var failureCount = prepared.SendFailureCount + 1;
            var failed = prepared with
            {
                RequestState = DownloadVerificationRequestState.SendFailed,
                SendFailureCount = failureCount,
                LastRequestError = exception.Message
            };
            if (failureCount >= timing.MaxRecheckSendFailures)
            {
                await EnterVerificationUnconfirmedAsync(task, failed, "校验请求连续发送失败（网络或下载器异常，"      +
                                                                      "无法确认请求是否已被下载器接收），校验执行未确认；" +
                                                                      "未改动任何文件，可重新校验或核对下载器。", ct);
                return;
            }

            await SaveVerificationIntentAsync(task.Id, task.Status, failed, ct);
            await UpdateTaskAsync(task.Id, task.Status,
                                  update =>
                                  {
                                      update.NextObservationAtUtc =
                                          timeProvider.GetUtcNow() + timing.PreparationSampleInterval;
                                      update.PreparationStage = DownloadPreparationStage.AwaitingVerification;
                                  }, ct);
            logger.LogWarning(exception,
                              "任务 {TaskId} 的 recheck 请求发送失败（连续第 {Failures} 次），保留请求预算等待重试。",
                              task.Id, failureCount);
            return;
        }

        var issuedAt = timeProvider.GetUtcNow();
        var issued = prepared with
        {
            RequestState = DownloadVerificationRequestState.Sent,
            SendFailureCount = 0,
            IssuedCount = prepared.IssuedCount + 1,
            RecheckRequestedAtUtc = issuedAt,
            DeadlineUtc = issuedAt + timing.CheckingStartWindow
        };

        // 请求后立即读取 pieces：基线大于 0 而现在掉落（或明显下降）证明请求已被该 hash 接收并开始
        // 重新校验——比 HTTP 200 与 checking 采样可靠。查询失败/为空不阻断，回退到基线见证。
        var appliedProperties = await TryGetPropertiesAsync(session, task.InfoHash, ct);
        if (appliedProperties is { } applied && baseline?.PiecesHave is { } baselinePieces &&
            applied.PiecesHave < baselinePieces)
            issued = issued with { RecheckAppliedAtUtc = issuedAt };

        // 发出请求后的立即观察：只接受 checkingDL/checkingUP 作为本轮内容校验证据；checkingResumeData
        // 只校验恢复数据，仅记录提示时间，不设 ObservedChecking。观察失败只告警，不退整轮。
        var observed = await TryFindSnapshotAsync(session, task.InfoHash, ct);
        if (observed is { Category: QbittorrentTorrentCategory.Checking })
        {
            if (IsDataCheckingRawState(observed.RawState))
                issued = issued with
                {
                    ObservedChecking = true,
                    Step = DownloadVerificationStep.Observing,
                    CheckStartedAtUtc = issuedAt,
                    LastCheckProgressAtUtc = issuedAt,
                    LastCheckProgress = observed.Progress,
                    ObservedRawState = observed.RawState
                };
            else if (IsResumeDataRawState(observed.RawState))
                issued = issued with { ResumeDataSeenAtUtc = issuedAt };
        }

        await SaveVerificationIntentAsync(task.Id, task.Status, issued, ct);
        await UpdateTaskAsync(task.Id, task.Status,
                              update =>
                              {
                                  update.NextObservationAtUtc = issuedAt + timing.PreparationSampleInterval;
                                  update.PreparationStage     = DownloadPreparationStage.AwaitingVerification;
                              }, ct);
        logger.LogInformation("任务 {TaskId} 已发出 recheck 请求（第 {Issued} 次，发送状态 {State}，立即观察{Observed}）。",
                              task.Id, issued.IssuedCount, issued.RequestState,
                              issued.ObservedChecking ? "到校验中" : "未进入校验");
    }

    /// <summary>请求发送期间的查询失败不得把整轮对账变成异常：按未观察到处理，由后续窗口与预算推进。</summary>
    private async Task<TorrentSnapshot?> TryFindSnapshotAsync(
        IQbittorrentSession session, string infoHash, CancellationToken ct)
    {
        try
        {
            return (await session.FindTorrentsAsync([infoHash], ct)).SingleOrDefault();
        }
        catch (Exception exception) when (IsRecheckTransportFailure(exception, ct))
        {
            logger.LogWarning(exception, "查询 recheck 请求后的种子快照失败，按未观察到校验阶段处理。");
            return null;
        }
    }

    /// <summary>pieces 属性查询失败不得阻断校验流程：返回 null，由调用方回退到快照基线证据。</summary>
    private async Task<TorrentPropertiesSnapshot?> TryGetPropertiesAsync(
        IQbittorrentSession session, string infoHash, CancellationToken ct)
    {
        try
        {
            return await session.GetTorrentPropertiesAsync(infoHash, ct);
        }
        catch (Exception exception) when (IsRecheckTransportFailure(exception, ct) ||
                                          exception is NotSupportedException or InvalidOperationException)
        {
            logger.LogWarning(exception, "查询种子 pieces 属性失败，回退到快照基线证据。");
            return null;
        }
    }

    /// <summary>qB/网络传输失败：请求可能已被接收，业务不得据此断言未发送；用户取消不在此列。</summary>
    private static bool IsRecheckTransportFailure(Exception exception, CancellationToken ct) =>
        exception is QbittorrentException or HttpRequestException or IOException ||
        (exception is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>
    ///     普通采样只允许 checkingDL/checkingUP 证明本次数据校验；checkingResumeData 只校验恢复数据，
    ///     不能当作内容校验证据。RawState 缺失（旧意图或未提供原始状态的会话）保持原有归属语义。
    /// </summary>
    private static bool IsDataCheckingRawState(string? rawState) =>
        string.IsNullOrWhiteSpace(rawState)                                       ||
        string.Equals(rawState, "checkingDL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(rawState, "checkingUP", StringComparison.OrdinalIgnoreCase);

    /// <summary>checkingResumeData 只校验恢复数据（添加/启动/重启产物），任何时刻都不作内容校验证据。</summary>
    private static bool IsResumeDataRawState(string? rawState) =>
        string.Equals(rawState, "checkingResumeData", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     完成见证评估（先于补发）：请求前基线 + 本轮请求锚点 + 停止态快照的完整性方向变化。
    ///     基线未完成 → 请求后停止态完成 = 本轮校验执行并补齐/确认数据；基线已完成 → 请求后完整性
    ///     下降 = 本轮校验确实执行且发现缺失（此时不得登记复用，进入结果读取确认缺失）。
    ///     RecheckApplied（pieces 掉落）可替代 Checking 观察作为「本轮执行」证据：之后停止态完整
    ///     成立完成见证、停止态 pieces 不完整确认缺失。基线完整 → 仍完整不构成任何证据。
    ///     见证只给出方向；逐文件结论仍由结果读取、映射一致性与稳定窗口核对决定。
    /// </summary>
    private async Task<string?> TryEvaluateCompletionEvidenceAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot snapshot, DownloadVerificationIntent intent,
        CancellationToken   ct)
    {
        if (intent.Baseline is not { } baseline || intent.RecheckAttemptedAtUtc is null) return null;
        if (intent.RequestState is null or DownloadVerificationRequestState.NotSent) return null;
        if (snapshot is not { Category: QbittorrentTorrentCategory.Stopped, IsMoving: false }) return null;
        if (intent.TimeoutStopRequestedAtUtc is not null) return null;

        // 共享写入风险未排除时不得用停止态完成充数：风险已记录、或有暂停记录但风险未知都保持不可证。
        if (intent.UnverifiedWriteRisk == true || intent is { PauseAttempts: > 0, UnverifiedWriteRisk: null })
            return null;

        var properties       = await TryGetPropertiesAsync(session, task.InfoHash, ct);
        var piecesIncomplete = properties is { PiecesNum: > 0 } value && value.PiecesHave < value.PiecesNum;
        if (IsStoppedComplete(snapshot))
        {
            // 基线完整 → 仍完整：无法把当前完成归因于本轮请求，保持未确认（原因文案另行区分）。
            if (baseline.Progress >= 1.0 && intent.RecheckAppliedAtUtc is null) return null;
            return DownloadVerificationCompletionEvidence.BaselineIncompleteToComplete;
        }

        // 完整性下降：基线已完成（progress<1 / amount_left 增大 / raw UP→DL），或 pieces 已证明
        // 本轮执行且当前 pieces 不完整（部分恢复的校验结果）。
        var directionDown =
            (snapshot.Progress < 1.0    && IsBaselineComplete(baseline))   ||
            (snapshot.AmountLeft is > 0 && baseline.AmountLeft is not > 0) ||
            (string.Equals(baseline.RawState, "stoppedUP", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(snapshot.RawState, "stoppedDL", StringComparison.OrdinalIgnoreCase)) ||
            (intent.RecheckAppliedAtUtc is not null && piecesIncomplete);
        return directionDown ? DownloadVerificationCompletionEvidence.BaselineCompleteToIncomplete : null;
    }

    /// <summary>停止态完成：raw stoppedUP，或进度满且剩余量归零（completion_on 只作辅助，不作必要条件）。</summary>
    private static bool IsStoppedComplete(TorrentSnapshot snapshot) =>
        string.Equals(snapshot.RawState, "stoppedUP", StringComparison.OrdinalIgnoreCase) ||
        snapshot is { Progress: >= 1.0, AmountLeft: null or 0 };

    /// <summary>请求前基线是否已是完整状态；用于区分「无法归因的完成」与「从未观察到校验」的未确认原因。</summary>
    private static bool IsBaselineComplete(DownloadVerificationBaseline baseline) =>
        baseline is { Progress: >= 1.0, AmountLeft: null or 0 };

    /// <summary>
    ///     进入校验未确认态：保持暂停、不隔离、不删种、不重下；保留候选映射与意图（Step=Unconfirmed），
    ///     清空自动观察调度，等待人工「重新校验」。人工暂停标志原样保留——用户已要求停止时，
    ///     未确认不得把任务改写为可自动重新校验的系统暂停。恢复/归档职责一律不在此处触发。
    /// </summary>
    private async Task EnterVerificationUnconfirmedAsync(
        DownloadTask task, DownloadVerificationIntent intent, string error, CancellationToken ct)
    {
        var unconfirmed = VoidStabilitySamples(intent) with
        {
            Step = DownloadVerificationStep.Unconfirmed,
            DeadlineUtc = null,
            MovingObservedAtUtc = null
        };
        logger.LogWarning("任务 {TaskId} 复用校验未确认：{Error}", task.Id, error);
        await using var db      = await dbContextFactory.CreateDbContextAsync(ct);
        var             tracked = await db.DownloadTasks.SingleAsync(value => value.Id == task.Id, ct);
        if (tracked.Status is not (DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)) return;

        tracked.Status               = DownloadTaskStatus.Paused;
        tracked.IsSystemPaused       = true;
        tracked.PreparationStage     = DownloadPreparationStage.AwaitingVerification;
        tracked.VerificationIntent   = unconfirmed.ToJson();
        tracked.FailedStep           = "复用校验";
        tracked.Error                = error.Length > 1000 ? error[..1000] : error;
        tracked.NextObservationAtUtc = null;
        await db.SaveChangesAsync(ct);
        changeNotifier?.Notify(task.Id, true);
    }

    /// <summary>
    ///     完成/验证后的启动恢复：先按快照计算应然状态。已运行直接确认；用户暂停与下载器端外部停止
    ///     不自动拉起；仍停止时 journal-first 落库 AwaitingConfirm 再发 Resume，并立即观察一次。
    ///     返回写盘后的恢复记录；null 表示无需保留记录（ContinueDownload 已确认运行并清空）。
    ///     任何失败都绝不改写 Status/FailedStep/Error/CompletedAtUtc，绝不重新校验、隔离或重新下载。
    /// </summary>
    private async Task<DownloadRunRecovery?> StartRunWithRecoveryAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot stable, string purpose, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (task.IsUserPaused)
        {
            var paused = new DownloadRunRecovery
            {
                Purpose           = purpose,
                State             = DownloadRunStates.UserPaused,
                LastObservedAtUtc = now,
                ObservedCategory  = stable.Category.ToString(),
                Reason            = "任务处于人工暂停；对账不会自动启动"
            };
            await SaveRunRecoveryAsync(task.Id, task.Status, paused, null, ct);
            return paused;
        }

        if (stable.Category is QbittorrentTorrentCategory.Seeding or QbittorrentTorrentCategory.Downloading)
            return await ConfirmRunRunningAsync(session, task, purpose, stable, null, ct);

        // 校验/移动/错误快照不构成启动条件：不请求、不记录，交由既有校验/对账路径处理。
        if (stable.Category != QbittorrentTorrentCategory.Stopped) return null;

        if (task.ExternalStopObservedAtUtc is null) return await IssueRunResumeAsync(session, task, purpose, null, ct);
        var stopped = new DownloadRunRecovery
        {
            Purpose           = purpose,
            State             = DownloadRunStates.DownloaderStopped,
            LastObservedAtUtc = now,
            ObservedCategory  = stable.Category.ToString(),
            Reason            = "启动前已观察到下载器端外部停止；不自动拉起"
        };
        await SaveRunRecoveryAsync(task.Id, task.Status, stopped, null, ct);
        return stopped;
    }

    /// <summary>
    ///     journal-first 发出一次 Resume：请求台账先落库（AwaitingConfirm + 请求时间 + Unknown），再发送，
    ///     并立即观察一次——快速生效时无需等下一轮。响应丢失不断言失败：转 ResponseUnknown 由观察确认。
    /// </summary>
    private async Task<DownloadRunRecovery?> IssueRunResumeAsync(
        IQbittorrentSession session, DownloadTask task, string purpose, DownloadRunRecovery? previous,
        CancellationToken   ct)
    {
        var now = timeProvider.GetUtcNow();
        var recovery = new DownloadRunRecovery
        {
            Purpose        = purpose,
            State          = DownloadRunStates.AwaitingConfirm,
            AttemptCount   = (previous?.AttemptCount ?? 0) + 1,
            RequestedAtUtc = now,
            RequestOutcome = DownloadRunRequestOutcomes.Unknown,
            Reason         = previous?.Reason
        };
        // journal-first：崩溃后仍能看到「启动待确认」，重启后按记录继续观察。
        await SaveRunRecoveryAsync(task.Id, task.Status, recovery,
                                   now + timing.ControlConfirmationWindow, ct);

        try
        {
            await session.ResumeAsync(task.InfoHash, ct);
            recovery = recovery with { RequestOutcome = DownloadRunRequestOutcomes.Sent };
        }
        catch (Exception exception) when (exception is QbittorrentNotFoundException or QbittorrentBadRequestException)
        {
            // 下载器明确拒绝（种子已不存在等）：与「响应丢失」分开记录，转终态等待人工核对，
            // 不再自动重试；文件与任务状态保持原样。
            recovery = recovery with
            {
                State = DownloadRunStates.DownloaderStopped,
                RequestOutcome = DownloadRunRequestOutcomes.Rejected,
                Reason = "下载器明确拒绝启动请求（种子可能已被删除）",
                LastRequestError = exception.Message
            };
            logger.LogWarning(exception, "任务 {TaskId} 启动请求被下载器拒绝，转待人工核对。", task.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !ct.IsCancellationRequested)
        {
            recovery = recovery with
            {
                State = DownloadRunStates.ResponseUnknown,
                RequestOutcome = DownloadRunRequestOutcomes.Unknown,
                LastRequestError = exception.Message
            };
            logger.LogWarning(exception, "任务 {TaskId} 启动请求响应丢失（第 {Attempt} 次），转入观察确认。",
                              task.Id, recovery.AttemptCount);
        }

        // 立即观察一次：已运行即确认，无需再等确认窗口。
        var observed = await TryFindSnapshotAsync(session, task.InfoHash, ct);
        if (observed is { Category: QbittorrentTorrentCategory.Seeding or QbittorrentTorrentCategory.Downloading })
            return await ConfirmRunRunningAsync(session, task, purpose, observed, recovery, ct);

        await SaveRunRecoveryAsync(task.Id, task.Status, recovery,
                                   now + timing.ControlConfirmationWindow, ct);
        return recovery;
    }

    /// <summary>
    ///     观察到运行：ContinueDownload 清空记录（连同系统暂停）回到普通下载跟踪；Seeding 记录
    ///     ConfirmedRunning 并 best-effort 通告 tracker——通告失败只记录，绝不否定数据完整性、不重新校验。
    /// </summary>
    private async Task<DownloadRunRecovery?> ConfirmRunRunningAsync(
        IQbittorrentSession  session, DownloadTask      task, string purpose, TorrentSnapshot snapshot,
        DownloadRunRecovery? current, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (purpose == DownloadRunPurposes.ContinueDownload)
        {
            await SaveRunRecoveryAsync(task.Id, task.Status, null, null, ct);
            logger.LogInformation("任务 {TaskId} 已观察到下载运行，启动恢复记录已清空，回到普通下载跟踪。", task.Id);
            return null;
        }

        var recovery = (current ?? new DownloadRunRecovery { Purpose = purpose }) with
        {
            State = DownloadRunStates.ConfirmedRunning,
            ObservedRunningAtUtc = current?.ObservedRunningAtUtc ?? now,
            LastObservedAtUtc = now,
            ObservedCategory = snapshot.Category.ToString()
        };
        try
        {
            await session.ReannounceAsync(task.InfoHash, ct);
            recovery = recovery with { ReannounceAttemptedAtUtc = now };
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !ct.IsCancellationRequested)
        {
            recovery = recovery with { LastReannounceError = exception.Message };
            logger.LogWarning(exception, "任务 {TaskId} 做种恢复后通告 tracker 失败；不影响已确认的数据完整性。",
                              task.Id);
        }

        await SaveRunRecoveryAsync(task.Id, task.Status, recovery, null, ct);
        return recovery;
    }

    /// <summary>
    ///     启动恢复的观察推进：只对 AwaitingConfirm/ResponseUnknown 状态动作。观察到运行即确认；
    ///     Sent 后停止是下载器策略（分享限制/人工），不再自动拉起；响应未知按确认窗口先观察、有界重试；
    ///     用户暂停停自动。绝不调用 recheck，绝不写 FailedStep/Error/CompletedAtUtc。
    /// </summary>
    private async Task AdvanceRunRecoveryAsync(
        IQbittorrentSession session,  DownloadTask      task, TorrentSnapshot? snapshot,
        DownloadRunRecovery recovery, CancellationToken cancellationToken)
    {
        // 用户解除人工暂停后（人工「恢复」把任务交回系统），按既有预算继续有界启动。
        if (recovery.State == DownloadRunStates.UserPaused &&
            task is { Status: DownloadTaskStatus.Downloading, IsUserPaused: false })
        {
            await IssueRunResumeAsync(session, task, recovery.Purpose, recovery, cancellationToken);
            return;
        }

        if (recovery.State is not (DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown))
        {
            // 终态记录不再自动推进；清掉残留的到期时间，worker 恢复空闲节奏。
            if (task.NextObservationAtUtc is not null)
                await UpdateTaskAsync(task.Id, task.Status,
                                      update => update.NextObservationAtUtc = null, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (task is { Status: DownloadTaskStatus.Downloading, IsUserPaused: true })
        {
            await SaveRunRecoveryAsync(task.Id, task.Status, recovery with
            {
                State = DownloadRunStates.UserPaused,
                Reason = "用户暂停了任务；不自动恢复",
                LastObservedAtUtc = now
            }, null, cancellationToken);
            return;
        }

        switch (snapshot)
        {
            case null :
                // 种子已消失：只记录，不改任务状态、不清理文件。
                await SaveRunRecoveryAsync(task.Id, task.Status, recovery with
                {
                    State = DownloadRunStates.DownloaderStopped,
                    Reason = "下载器中已无该种子",
                    LastObservedAtUtc = now,
                    ObservedCategory = null
                }, null, cancellationToken);
                return;
            case { IsMoving: true } or { Category: QbittorrentTorrentCategory.Checking } :
                // 校验/移动期间绝不 recheck、不隔离、不下载：只推迟观察。
                await UpdateTaskAsync(task.Id, task.Status,
                                      update => update.NextObservationAtUtc =
                                          now + timing.PreparationSampleInterval, cancellationToken);
                return;
            case { Category: QbittorrentTorrentCategory.Error } :
                await SaveRunRecoveryAsync(task.Id, task.Status, recovery with
                {
                    State = DownloadRunStates.FailedRetryable,
                    Reason = "下载器报告任务出错；文件保持原样，可人工恢复",
                    LastObservedAtUtc = now,
                    ObservedCategory = snapshot.Category.ToString()
                }, null, cancellationToken);
                return;
            case { Category: QbittorrentTorrentCategory.Seeding or QbittorrentTorrentCategory.Downloading } :
                await ConfirmRunRunningAsync(session, task, recovery.Purpose, snapshot, recovery,
                                             cancellationToken);
                return;
        }

        // 停止态：Sent 后停止是下载器策略或人工，不再自动拉起；Unknown 按确认窗口先观察再决定。
        if (recovery.RequestOutcome == DownloadRunRequestOutcomes.Sent)
        {
            await SaveRunRecoveryAsync(task.Id, task.Status, recovery with
            {
                State = DownloadRunStates.DownloaderStopped,
                Reason = "启动请求已被接受后停止（下载器策略或人工）；不自动再次拉起",
                LastObservedAtUtc = now,
                ObservedCategory = snapshot.Category.ToString()
            }, null, cancellationToken);
            return;
        }

        if (recovery.ObservedRunningAtUtc is not null ||
            now < (recovery.RequestedAtUtc ?? now) + timing.ControlConfirmationWindow)
        {
            // 响应未知：确认窗口内只观察，不盲目重发；已观察到运行则永不再自动 Resume。
            await UpdateTaskAsync(task.Id, task.Status,
                                  update => update.NextObservationAtUtc = recovery.ObservedRunningAtUtc is not null
                                      ? null
                                      : (recovery.RequestedAtUtc ?? now) + timing.ControlConfirmationWindow,
                                  cancellationToken);
            return;
        }

        if (recovery.AttemptCount >= timing.MaxRunResumeAttempts)
        {
            await SaveRunRecoveryAsync(task.Id, task.Status, recovery with
            {
                State = DownloadRunStates.FailedRetryable,
                Reason = $"启动请求 {recovery.AttemptCount} 次仍未确认生效；已停止自动重试，可人工恢复",
                LastObservedAtUtc = now,
                ObservedCategory = snapshot.Category.ToString()
            }, null, cancellationToken);
            return;
        }

        await IssueRunResumeAsync(session, task, recovery.Purpose, recovery, cancellationToken);
    }

    /// <summary>启动恢复记录落库：recovery 为 null 时原子清空（ContinueDownload 确认运行后回到普通跟踪）。</summary>
    private async Task SaveRunRecoveryAsync(
        Guid            taskId,               DownloadTaskStatus expectedStatus, DownloadRunRecovery? recovery,
        DateTimeOffset? nextObservationAtUtc, CancellationToken  cancellationToken)
    {
        await UpdateTaskAsync(taskId, expectedStatus, update =>
        {
            update.RunRecovery          = recovery?.ToJson();
            update.NextObservationAtUtc = nextObservationAtUtc;
            // 记录清空即回到普通下载跟踪：系统暂停的启动等待语义结束。
            if (recovery is null) update.IsSystemPaused = false;
        }, cancellationToken);
    }

    /// <summary>
    ///     人工恢复启动：完整任务恢复做种，部分完成继续下载；单次请求、失败即回，不重新校验、
    ///     不隔离、不重新添加种子。只接受 FailedRetryable/DownloaderStopped；待观察状态交给对账推进。
    /// </summary>
    public async Task ResumeRunAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        var       recovery          = DownloadRunRecovery.FromJson(task.RunRecovery);
        if (recovery?.State is not (DownloadRunStates.FailedRetryable or DownloadRunStates.DownloaderStopped))
            throw new InvalidOperationException("只有启动未恢复的任务可以人工恢复。");

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        var result = await IssueRunResumeAsync(session, task, recovery.Purpose, recovery, cancellationToken);
        if (result is null)
        {
            logger.LogInformation("任务 {TaskId} 继续下载已观察到运行，启动恢复记录已清空。", taskId);
            return;
        }

        logger.LogInformation("任务 {TaskId} 人工恢复启动已发出（{RunState}），等待对账观察确认。",
                              taskId, result.State);
    }

    /// <summary>
    ///     活跃校验的推进驱动。快照无法区分「谁启动了运行」，校验期出现无法解释的运行状态一律先暂停；
    ///     人工暂停意图保留（IsUserPaused 全程不动）。完成证据：当前轮次观察到 Checking 且结束、
    ///     映射未变、无移动、无错误，并在停止状态读取文件结果。仅做种收尾的暂停不单独作废证据；
    ///     未确认共享数据时出现下载则保留风险，即使已停止也不得沿用旧证据。已落库的启动意图单独恢复。
    /// </summary>
    private async Task AdvanceVerificationAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot? snapshot, DownloadVerificationIntent intent,
        CancellationToken   ct)
    {
        // 校验未确认是等待人工「重新校验」的终点，不是自动推进态：空闲对账不得重发 recheck、
        // 重置证据或绕过人工确认，否则任务会在无人工介入时反复进退。
        if (intent.Step == DownloadVerificationStep.Unconfirmed) return;
        var loaded = await LoadTaskWithFilesAsync(task.Id, ct);
        if (loaded?.Status is not (DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)) return;
        task = loaded;
        var readyToRun = intent.Step == DownloadVerificationStep.ReadyToRun;

        // 重启作废：意图来自其他进程实例时，本轮请求前基线、校验观察、完成见证与请求锚点全部作废
        // （保留候选映射），回到「尚未发出」，下一轮重新捕获基线并重发。重启后的完成状态可能来自
        // 旧 resume data，不得凭旧基线判定完成，也不得凭重启后的部分进度判定缺失。
        if (intent.ObserverInstanceId is { } owner && owner != processInstanceId)
        {
            if (intent.Step == DownloadVerificationStep.Mapping)
            {
                // 映射阶段尚未发出 recheck：只接管实例归属，映射与首次请求按原流程继续。
                var adopted = intent with { ObserverInstanceId = processInstanceId };
                await SaveVerificationIntentAsync(task.Id, task.Status, adopted, ct);
            }
            else
            {
                var invalidated = ResetRoundEvidence(intent, DownloadVerificationStep.Requested) with
                {
                    ObserverInstanceId = processInstanceId,
                    RequestState = DownloadVerificationRequestState.NotSent,
                    SendFailureCount = 0,
                    LastRequestError = null
                };
                await SaveVerificationIntentAsync(task.Id, task.Status, invalidated, ct);
                await DeferObservationAsync(task.Id, task.Status,
                                            timeProvider.GetUtcNow() + timing.PreparationSampleInterval, ct);
                logger.LogInformation("任务 {TaskId} 的校验意图来自其他应用实例，本轮证据已作废，下一轮重新捕获基线并重发。", task.Id);
                return;
            }
        }

        if (snapshot is null)
        {
            // 刚添加的种子可能短暂查不到：可见性窗口内不转隔离（隔离会删除重加，造成无谓搅动）。
            if (WithinAddVisibilityWindow(task))
            {
                await DeferObservationAsync(task.Id, task.Status,
                                            timeProvider.GetUtcNow() + timing.PreparationSampleInterval, ct);
                return;
            }

            await EnterIsolationAsync(task, intent, "校验期间下载器中的任务已消失，转为独立下载。", ct);
            return;
        }

        if (!MatchesIsolationPath(snapshot.SavePath, intent.CandidateRoot)                       ||
            !MatchesIsolationPath(DownloadRoundLayout.Root(task), intent.CandidateRoot)          ||
            intent.Files.Count                                             != task.Files.Count   ||
            intent.Files.Select(file => file.FileIndex).Distinct().Count() != intent.Files.Count ||
            intent.Files.Any(mapping => task.Files.All(file => file.FileIndex             != mapping.FileIndex ||
                                                               file.Size                  != mapping.Size      ||
                                                               file.IsSelectedForDownload != mapping.Selected)))
        {
            await EnterIsolationAsync(task, intent, "校验的保存根、文件身份或选择计划已改变，结果作废。", ct);
            return;
        }

        var now = timeProvider.GetUtcNow();
        switch (snapshot)
        {
            case { Category: QbittorrentTorrentCategory.Checking } or { IsMoving: true } :
            {
                // 校验/移动期间的观察同步：进度、采样节奏与意图变更一次落库，各等待路径共用。
                async Task SyncCheckingRoundAsync()
                {
                    await UpdateTaskAsync(task.Id, task.Status, update =>
                    {
                        update.Progress             = snapshot.Progress;
                        update.TotalSize            = snapshot.TotalSize;
                        update.LastSyncedAtUtc      = now;
                        // 校验/移动期间按长校验采样节奏观察，窗口到期前的轮次跳过本任务。
                        update.NextObservationAtUtc = now + timing.VerificationSampleInterval;
                        update.VerificationIntent   = intent.ToJson();
                    }, ct);
                }

                string TimeoutStopReason()
                {
                    // 已作废轮次的残留校验没有本轮锚点可计期限：按残留未退出说明原因。
                    if (intent is { Step: DownloadVerificationStep.Requested, RecheckRequestedAtUtc: null })
                        return "已作废轮次的校验残留未退出";
                    return now - (intent.CheckStartedAtUtc ?? now) >= timing.VerificationRoundLimit
                        ? "单轮校验超过总上限"
                        : "校验长时间无有效进展";
                }

                async Task IssueTimeoutStopAsync()
                {
                    intent = intent with
                    {
                        TimeoutStopRequestedAtUtc = now, TimeoutStopAttempts = intent.TimeoutStopAttempts + 1
                    };
                    await SaveVerificationIntentAsync(task.Id, task.Status, intent, ct);
                    await session.PauseAsync(task.InfoHash, ct);
                    logger.LogWarning("任务 {TaskId} {Reason}（进度 {Progress:P1}），退出前先停止校验的临时运行（第 {Attempts} 次请求）。",
                                      task.Id, TimeoutStopReason(), snapshot.Progress, intent.TimeoutStopAttempts);
                }

                // Moving 不是 Checking：本轮校验后发生的移动可能已改变目标，证据作废、回到等待校验开始；
                // 映射阶段的移动只等待其自然结束。移动等待独立计时：起点只在首次观察时记录、重复快照
                // 不重置，前面校验的耗时不得占用移动等待额度——期限从起点起算，超期才按持续超时受阻。
                if (snapshot.IsMoving)
                {
                    var movingSince = intent.MovingObservedAtUtc ?? now;
                    if (now - movingSince >= timing.VerificationNoProgressWindow)
                    {
                        await MarkFailedAsync(task.Id, task.Status, "复用校验", "校验或移动持续超时，请人工核对下载器。", ct);
                        return;
                    }

                    if (intent.ObservedChecking)
                    {
                        await SaveVerificationIntentAsync(task.Id, task.Status,
                                                          ResetRoundEvidence(intent, DownloadVerificationStep.Requested)
                                                              with
                                                              {
                                                                  MovingObservedAtUtc = movingSince
                                                              }, ct);
                        return;
                    }

                    if (intent.MovingObservedAtUtc is null)
                        intent = intent with { MovingObservedAtUtc = movingSince };
                    await SyncCheckingRoundAsync();
                    return;
                }

                // 映射阶段的外部校验不可归属任何请求：等待其结束并保留有界兜底，超时按无法解释处理。
                if (intent.Step == DownloadVerificationStep.Mapping &&
                    now         >= (intent.DeadlineUtc ?? intent.CreatedAtUtc + timing.VerificationNoProgressWindow))
                {
                    await MarkFailedAsync(task.Id, task.Status, "复用校验", "校验或移动持续超时，请人工核对下载器。", ct);
                    return;
                }

                // 旧意图（本轮实现前落库）缺校验锚点：恢复观察的第一轮补齐并落库，两个期限从补齐时刻起算。
                if (intent is { ObservedChecking: true, CheckStartedAtUtc: null })
                    intent = intent with
                    {
                        CheckStartedAtUtc = now,
                        LastCheckProgressAtUtc = intent.LastCheckProgressAtUtc ?? now,
                        LastCheckProgress = intent.LastCheckProgress           ?? snapshot.Progress
                    };

                // 已作废轮次的残留校验不可归属任何请求（作废未伴随停止请求记录时首遇即发出）：
                // 停止确认的期限与预算接管——窗口内等待、有界补发、耗尽受阻；停稳后由补发路径
                // 重新发起可信校验，不得无限等待一个已作废的校验自然结束。重新校验待发/发送结果未知
                // 时不能把 Checking 当成已作废残留，否则会误停可能正是本次请求的校验。
                if (intent is
                    {
                        Step                     : DownloadVerificationStep.Requested,
                        RecheckRequestedAtUtc    : null,
                        RequestState             : null or DownloadVerificationRequestState.Sent,
                        TimeoutStopRequestedAtUtc: null
                    })
                {
                    await IssueTimeoutStopAsync();
                    return;
                }

                // 请求前观察到的外部校验不能充当本轮请求的完成证据：只归属本轮请求的 Checking 记为证据。
                // checkingResumeData 不是内容校验证据；无原始状态的旧意图/会话保持原有语义。
                if (intent.Step != DownloadVerificationStep.Mapping                                          &&
                    intent is { IssuedCount: > 0, RecheckRequestedAtUtc: not null, ObservedChecking: false } &&
                    IsDataCheckingRawState(snapshot.RawState))
                    intent = intent with
                    {
                        ObservedChecking = true,
                        Step = DownloadVerificationStep.Observing,
                        CheckStartedAtUtc = intent.CheckStartedAtUtc           ?? now,
                        LastCheckProgressAtUtc = intent.LastCheckProgressAtUtc ?? now,
                        LastCheckProgress = intent.LastCheckProgress           ?? snapshot.Progress,
                        ObservedRawState = snapshot.RawState
                    };

                // 校验进行中：退出校验前积累的稳定样本已作废（再次校验后的旧结果不再有效）。
                if (intent.StabilitySample is not null)
                    intent = VoidStabilitySamples(intent);

                // 已决定到期退出后，停止确认独立于进度：校验仍在推进也不能重置期限、绕过停止请求的确认窗口。
                if (intent.TimeoutStopRequestedAtUtc is { } stopRequestedAt)
                {
                    if (now < stopRequestedAt + timing.ControlConfirmationWindow)
                    {
                        await SyncCheckingRoundAsync();
                        return;
                    }

                    if (intent.TimeoutStopAttempts >= timing.MaxVerificationPauseAttempts)
                    {
                        await MarkFailedAsync(task.Id, task.Status, "复用校验",
                                              $"{TimeoutStopReason()}且停止请求未生效，为保护共享数据已停止推进，请人工核对下载器。", ct);
                        return;
                    }

                    await IssueTimeoutStopAsync();
                    return;
                }

                // “无进展”只由有意义的校验进展重置：重复同样快照、请求成功或网络重试不算进展。
                if (intent.ObservedChecking && snapshot.Progress != intent.LastCheckProgress)
                    intent = intent with { LastCheckProgress = snapshot.Progress, LastCheckProgressAtUtc = now };

                // 校验中的两个独立期限：无进展期限按最后有效进展计算，单轮总上限防进度抖动无限续期。
                // 到期不直接判死：先停止确认（校验的临时运行不带离自动推进）；停止生效后按「校验被中断」受阻。
                if (now - (intent.LastCheckProgressAtUtc ?? now) >= timing.VerificationNoProgressWindow ||
                    now - (intent.CheckStartedAtUtc      ?? now) >= timing.VerificationRoundLimit)
                {
                    await IssueTimeoutStopAsync();
                    return;
                }

                await SyncCheckingRoundAsync();
                return;
            }
            // 校验后进入错误状态：结果不可解释，不算通过，转独立下载（先于运行分支处理）。
            case { Category: QbittorrentTorrentCategory.Error } :
                await EnterIsolationAsync(task, intent, "校验结束后任务进入错误状态，结果不可用，转为独立下载。", ct);
                return;
        }

        switch (readyToRun)
        {
            case false when snapshot is not { Category: QbittorrentTorrentCategory.Stopped } :
            {
                // 运行中（校验后自动恢复或外部启动，快照无法区分来源）：统一先暂停再观察；
                // 暂停屡次不生效则转待核对，绝不带着未证实的共享写入继续。
                // 暂停确认窗口内只观察不补发：查询轮次与 pulse 不消耗暂停预算；旧意图无请求时间戳按已到期处理。
                var pausedAt = timeProvider.GetUtcNow();

                // 等待/发出停止确认期间同步本轮内容观察：校验已结束，进度语义是所选内容进度，
                // 不得沿用上一轮校验进度。调度独立于类别判断：Unknown 保留旧进度与观察时间，
                // 但采样安排不得跳过，否则停止确认退回空闲轮询节奏。
                async Task SyncStopConfirmObservationAsync(DateTimeOffset? deferUntil)
                {
                    if (snapshot.Category is not (QbittorrentTorrentCategory.Downloading
                                               or QbittorrentTorrentCategory.Seeding))
                    {
                        if (deferUntil is { } due) await DeferObservationAsync(task.Id, task.Status, due, ct);
                        return;
                    }

                    var observed = await WithSelectedContentProgressAsync(session, task, snapshot, ct);
                    await UpdateTaskAsync(task.Id, task.Status, update =>
                    {
                        update.Progress        = observed.Progress;
                        update.TotalSize       = observed.TotalSize;
                        update.LastSyncedAtUtc = pausedAt;
                        if (deferUntil is { } due) update.NextObservationAtUtc = due;
                    }, ct);
                }

                if (intent.PauseRequestedAtUtc is { } requestedAt &&
                    pausedAt < requestedAt + timing.ControlConfirmationWindow)
                {
                    // 窗口只限制补发暂停，不限制风险观察：确认窗口内新看到的下载/排队运行
                    // 同样可能补写共享文件，必须记入风险；暂停不能撤销已发生的写入。
                    var risk = NextUnverifiedWriteRisk(intent, snapshot.Category);
                    if (risk != intent.UnverifiedWriteRisk)
                        await SaveVerificationIntentAsync(task.Id, task.Status,
                                                          intent with { UnverifiedWriteRisk = risk }, ct);

                    // 风险采样按采样间隔继续，不等整个确认期限：窗口中途从做种转为下载的快照
                    // 必须被观察到（推迟到窗口末会跳过中途快照、漏记风险）；只有暂停补发等窗口过期。
                    await SyncStopConfirmObservationAsync(pausedAt + timing.VerificationSampleInterval);
                    return;
                }

                if (intent.PauseAttempts >= timing.MaxVerificationPauseAttempts)
                {
                    await MarkFailedAsync(task.Id, task.Status, "复用校验",
                                          "校验期间任务持续处于运行状态且暂停不生效，为保护共享数据已停止推进，请人工核对。",
                                          ct);
                    return;
                }

                await SaveVerificationIntentAsync(task.Id, task.Status,
                                                  intent with
                                                  {
                                                      PauseAttempts = intent.PauseAttempts + 1,
                                                      PauseRequestedAtUtc = pausedAt,
                                                      UnverifiedWriteRisk =
                                                      NextUnverifiedWriteRisk(intent, snapshot.Category)
                                                  }, ct);
                await session.PauseAsync(task.InfoHash, ct);
                await SyncStopConfirmObservationAsync(null);
                logger.LogWarning("任务 {TaskId} 校验期间出现无法解释的运行状态，已发出暂停（第 {Attempts} 次）。",
                                  task.Id, intent.PauseAttempts + 1);
                return;
            }
            // 暂停不能撤销已经发生的共享写入；旧意图有暂停记录却缺少风险分类时也不能认定安全。
            case false when intent.Step != DownloadVerificationStep.Mapping &&
                            (intent.UnverifiedWriteRisk == true ||
                             intent is { PauseAttempts: > 0, UnverifiedWriteRisk: null }) :
                await MarkFailedAsync(task.Id, task.Status, "复用校验",
                                      "校验确认前曾出现可能补写共享文件的运行，旧证据不可继续使用；任务已停止，请核对后重试。", ct);
                return;
        }

        // 停止态快照确认先前的保护暂停已生效：清除待确认时间戳（暂停次数与风险台账保留），
        // 展示不再停留「等待停止确认」；此后再次出现运行状态仍按台账与预算重新发出暂停。
        if (intent.PauseRequestedAtUtc is not null)
        {
            intent = intent with { PauseRequestedAtUtc = null };
            await SaveVerificationIntentAsync(task.Id, task.Status, intent, ct);
        }

        if (task.IsUserPaused) return;

        if (intent.Step == DownloadVerificationStep.Mapping)
        {
            // 先确认文件选择，再执行受控映射；选择变更发出后当轮返回。
            if (await TryApplyFileSelectionAsync(session, task, ct)) return;
            if (downloadFileService is null) return;

            // 辅助条件：候选文件必须零进度——存在已写数据时改名 onto 候选会移动数据，整体降级。
            var candidateRelatives = CandidateRelativePaths(intent);
            if (candidateRelatives.Count == 0)
            {
                await EnterIsolationAsync(task, intent, "校验意图缺少候选映射，转为独立下载。", ct);
                return;
            }

            if (await AnyCandidateHasProgressAsync(session, task, candidateRelatives, ct))
            {
                await EnterIsolationAsync(task, intent, "候选文件在映射前已有数据进度，无法安全映射，转为独立下载。",
                                          ct);
                return;
            }

            try
            {
                if (!await downloadFileService
                       .ApplyVerificationMappingUnderGateAsync(session, task.Id, candidateRelatives, ct)) return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or QbittorrentBadRequestException)
            {
                await EnterIsolationAsync(task, intent, $"受控映射不能安全完成：{exception.Message}", ct);
                return;
            }

            // 映射确认完成：统一走「先落库请求状态、发送、立即观察」路径（首次与补发共用）。
            await IssueRecheckAndObserveAsync(session, task, intent, ct);
            return;
        }

        if (!intent.ObservedChecking)
        {
            // 先见证、后补发：请求前基线到请求后停止态的完整性方向变化（或 pieces 掉落证明请求已
            // 被接收）可证明本轮校验已执行，快速校验完全躲过状态采样时也无需补发——补发会让新基线
            // 变成「已完成」，永久失去见证。
            var evidence = await TryEvaluateCompletionEvidenceAsync(session, task, snapshot, intent, ct);
            if (evidence is not null)
            {
                intent = intent with
                {
                    ObservedChecking = true,
                    Step = DownloadVerificationStep.Observing,
                    CompletionEvidenceKind = evidence,
                    CompletionEvidenceAtUtc = now,
                    CheckStartedAtUtc = intent.CheckStartedAtUtc           ?? intent.RecheckRequestedAtUtc ?? now,
                    LastCheckProgressAtUtc = intent.LastCheckProgressAtUtc ?? now,
                    LastCheckProgress = intent.LastCheckProgress           ?? snapshot.Progress
                };
                await SaveVerificationIntentAsync(task.Id, task.Status, intent, ct);
                logger.LogInformation("任务 {TaskId} 未观察到校验阶段，凭请求前基线与停止态完整性变化（{Evidence}）确认本轮校验已执行。",
                                      task.Id, evidence);
            }

            // 人工重新校验后尚未发出：下一轮立即重发，不再等待启动窗口。
            else if (intent.RequestState == DownloadVerificationRequestState.NotSent)
            {
                await IssueRecheckAndObserveAsync(session, task, intent, ct);
                return;
            }

            // 尚未观察到 Checking 且未取得见证：校验启动窗口内持续快速观察（不因非 Checking 立即下结论，
            // 也不睡到窗口末）；窗口过期且有界预算未耗尽时补发一次并重置本轮证据；仍无则保留「校验执行
            // 未确认」——不凭重复 100% 证明校验执行，也不因漏采 Checking 断言数据损坏、删除数据或自动重新下载。
            else if (intent.DeadlineUtc is { } deadline && now < deadline)
            {
                await DeferObservationAsync(task.Id, task.Status, now + timing.PreparationSampleInterval, ct);
                return;
            }
            else if (intent.IssuedCount < timing.MaxRecheckIssues)
            {
                await IssueRecheckAndObserveAsync(session, task, intent, ct);
                return;
            }
            else
            {
                // 原因文案区分：基线已完整时当前完成无法归因于本轮请求，与「从未观察到校验」分别提示。
                var unconfirmedError = intent.Baseline is { } baseline && IsBaselineComplete(baseline)
                    ? "请求前数据已是完整状态，无法把当前完成归因于本轮校验请求，本轮校验执行未确认；"
                    + "未改动任何文件，可重新校验或核对下载器。"
                    : "多次请求校验后仍未观察到校验阶段，校验执行未确认；未改动任何文件，可重新校验或核对下载器。";
                await EnterVerificationUnconfirmedAsync(task, intent, unconfirmedError, ct);
                return;
            }
        }

        // 本轮校验曾被主动停止（无进展或单轮上限到期后发出的停止请求）：校验执行情况已不可确认——
        // 停止后读到的文件结果可能是中断时刻的部分结果或旧快照，既不能登记复用，也不能确认缺失。
        if (!readyToRun && intent is { ObservedChecking: true, TimeoutStopRequestedAtUtc: not null })
        {
            await EnterVerificationUnconfirmedAsync(task, intent,
                                                    "校验因到期被主动停止，本轮校验执行未确认；未改动任何文件，可重新校验或核对下载器。", ct);
            return;
        }

        // 本轮已观察到 Checking 且已结束：核对映射未变，读取文件结果与回读的任务状态并按稳定窗口核对。
        var remoteFiles = await session.ListFilesAsync(task.InfoHash, ct);
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile))
            throw new DownloadFileIdentityException("校验任务的种子元数据无法解析。");
        if (remoteFiles.Count                                         != task.Files.Count ||
            remoteFiles.Select(file => file.Index).Distinct().Count() != remoteFiles.Count)
        {
            await EnterIsolationAsync(task, intent, "校验后的文件清单不完整或身份重复。", ct);
            return;
        }

        var indexByPath = TorrentDownloadPolicy.GetQbittorrentFileIndices(task.TorrentFile, torrent, isMultiFile);
        var remoteByIndex = remoteFiles.GroupBy(file => file.Index)
                                       .ToDictionary(group => group.Key, group => group.First());
        var candidatesFailed    = new List<string>();
        var allSelectedComplete = true;
        var sampleParts         = new List<string>();
        foreach (var mapping in intent.Files)
        {
            var file = task.Files.Single(item => item.FileIndex == mapping.FileIndex);
            if (!indexByPath.TryGetValue(file.OriginalRelativePath, out var qbIndex) ||
                !remoteByIndex.TryGetValue(qbIndex, out var remote)                  ||
                remote.Size                  != mapping.Size                         ||
                remote.IsSelectedForDownload != mapping.Selected                     ||
                (readyToRun && mapping.IsCandidate &&
                 (file.VerificationOutcome != DownloadFileVerificationOutcomes.ReusedVerified ||
                  !EqualVerificationPath(file.VerificationPath ?? string.Empty, mapping.VerificationPath))) ||
                !EqualVerificationPath(DownloadRoundLayout.CurrentAbsolute(task, file),
                                       mapping.VerificationPath) ||
                !EqualVerificationPath(JoinAbsolute(intent.CandidateRoot, remote.Path), mapping.VerificationPath))
            {
                // 映射在校验期间发生变化：结果作废，转独立下载。
                await EnterIsolationAsync(task, intent, "映射在校验期间发生变化，校验结果作废，转为独立下载。", ct);
                return;
            }

            if (!mapping.Selected) continue;
            var complete                       = file.Size <= 0 || remote.Progress >= 1.0;
            if (!complete) allSelectedComplete = false;
            if (mapping.IsCandidate && !complete)
                candidatesFailed.Add(mapping.VerificationPath);
            sampleParts.Add($"{mapping.FileIndex}:{remote.Progress:R}");
        }

        // 首次确认要求回读停止态；已持久化 ReadyToRun 时允许授权运行态，但仍须核对根和文件证据。
        var stable = (await session.FindTorrentsAsync([task.InfoHash], ct)).SingleOrDefault();
        if (stable is not { IsMoving: false } ||
            (readyToRun
                ? stable.Category is not (QbittorrentTorrentCategory.Stopped or QbittorrentTorrentCategory.Downloading
                 or QbittorrentTorrentCategory.Seeding)
                : stable.Category != QbittorrentTorrentCategory.Stopped) ||
            !MatchesIsolationPath(stable.SavePath, intent.CandidateRoot))
        {
            switch (stable)
            {
                // 回读发现移动：与入口观察同样处理——本轮校验后的移动可能已改变目标，整轮证据作废、
                // 回到等待重新校验；移动结束后由补发路径重新发起，不得沿用移动前的 Checking 与文件结果。
                // 作废保留移动等待起点：这里首次看到的移动同样按首次观察时刻独立计时。
                case { IsMoving: true } :
                    await SaveVerificationIntentAsync(task.Id, task.Status,
                                                      ResetRoundEvidence(intent, DownloadVerificationStep.Requested)
                                                          with
                                                          {
                                                              MovingObservedAtUtc = intent.MovingObservedAtUtc ?? now
                                                          }, ct);
                    return;
                // 只在这里看到的快速起止校验同样作废旧稳定序列：中断后的首个同内容样本
                // 不得沿用中断前的样本数直接凑满下结论。
                case { Category: QbittorrentTorrentCategory.Checking } when intent.StabilitySample is not null :
                    await SaveVerificationIntentAsync(task.Id, task.Status, VoidStabilitySamples(intent), ct);
                    break;
            }

            // 其余回读不一致（意外运行态或根变化）按采样间隔继续观察，下一轮由对应分支处理。
            await DeferObservationAsync(task.Id, task.Status, now + timing.VerificationSampleInterval, ct);
            return;
        }

        // 结果稳定窗口：任务状态与逐文件清单不是原子快照，退出校验后先跨采样间隔核对一致再下结论——
        // 刚退出的低进度可能只是文件结果滞后。ReadyToRun 的证据在登记前已经稳定过，恢复只重新核对布局。
        if (!readyToRun)
        {
            var sample = $"{stable.Category}|{string.Join(";", sampleParts)}";
            // 观察中断（重启、长期停采）不是结果不稳定：序列重新积累、稳定观察上限锚点随中断重开；
            // 内容变化才是不稳定——锚点不重置，结果始终不稳定时按「结果无法确认」受阻。
            var interrupted = intent.StabilitySampleAtUtc is { } previousAt &&
                              now - previousAt > timing.ResultStabilityWindow;
            intent = interrupted || sample != intent.StabilitySample
                ? intent with
                {
                    StabilitySample = sample, StabilityStartedAtUtc = now, StabilitySampleCount = 1,
                    StabilityObservedAtUtc = interrupted ? now : intent.StabilityObservedAtUtc ?? now
                }
                : intent with { StabilitySampleCount = intent.StabilitySampleCount + 1 };
            intent = intent with { StabilitySampleAtUtc = now };

            var stabilityStartedAt = intent.StabilityStartedAtUtc ?? now;
            if (intent.StabilitySampleCount < timing.ResultStabilitySamples ||
                now - stabilityStartedAt    < timing.ResultStabilityWindow)
            {
                if (now - (intent.StabilityObservedAtUtc ?? now) >= timing.ResultStabilityMaxWindow)
                {
                    await EnterVerificationUnconfirmedAsync(task, intent,
                                                            "校验后的文件结果长时间无法稳定确认，未改动任何文件，可重新校验或核对下载器。", ct);
                    return;
                }

                await SaveVerificationIntentAsync(task.Id, task.Status, intent, ct);
                // 稳定观察同步本轮内容进度与观察时间：退出校验后进度语义已从校验进度切回所选
                // 内容进度，数值与时间必须同源更新，不保留上一轮校验进度伪装为新观察。
                var observed = await WithSelectedContentProgressAsync(session, task, stable, ct);
                await UpdateTaskAsync(task.Id, task.Status, update =>
                {
                    update.Progress             = observed.Progress;
                    update.TotalSize            = observed.TotalSize;
                    update.LastSyncedAtUtc      = now;
                    update.NextObservationAtUtc = now + timing.VerificationSampleInterval;
                }, ct);
                return;
            }
        }

        // 候选数据在本轮稳定样本中确认缺失（与「结果无法确认」分别记录）：转隔离保护旧数据。
        if (candidatesFailed.Count > 0)
        {
            await EnterIsolationAsync(task, intent,
                                      $"候选路径数据未通过校验（本轮校验已执行且结果稳定，确认缺失）：{string.Join("、", candidatesFailed)}。",
                                      ct);
            return;
        }

        // 候选全部通过：登记 ReusedVerified（绑定路径）并按完成情况收尾。
        if (!readyToRun)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var tracked = await db.DownloadTasks.Include(value => value.Files)
                                  .SingleAsync(value => value.Id == task.Id, ct);
            foreach (var mapping in intent.Files.Where(item => item.IsCandidate))
            {
                var file = tracked.Files.FirstOrDefault(item => item.FileIndex == mapping.FileIndex);
                if (file is null || !file.IsSelectedForDownload) continue;
                file.VerificationOutcome = DownloadFileVerificationOutcomes.ReusedVerified;
                file.VerificationPath    = mapping.VerificationPath;
                file.UpdatedAtUtc        = timeProvider.GetUtcNow();
                // 只发布已证实的新位置；同包未完成的文件继续沿已发布完整路径指向旧成片。
                DownloadRoundLayout.PublishFile(tracked, file);
            }

            // 包含承接的提前切换（设计第四阶段）：候选数据已在校验目标路径（即归档终名）证实完整，
            // 本任务登记的待完成优胜不必等整包完成即可承接现任——大包新集仍在下载时旧包职责即可
            // 退出。切换后评估旧占用者职责是否全部耗尽，与证据同一事务落库。
            var retiredOwnerIds = new List<Guid>();
            if (tracked.SubjectId is { } verificationSubjectId)
            {
                // 复用校验只切换登记期明确挂了本任务 PendingVideoFileId 的行（含跨源升级旧版行）。
                var verifiedFileIds = tracked.Files
                                             .Where(file => file.VerificationOutcome ==
                                                            DownloadFileVerificationOutcomes.ReusedVerified)
                                             .Select(file => file.Id)
                                             .ToHashSet();
                var pendingRows = await db.EpisodeVersions
                                          .Where(row => row.SubjectId          == verificationSubjectId &&
                                                        row.PendingVideoFileId != null)
                                          .ToListAsync(ct);
                var previousIncumbents = new List<Guid>();
                foreach (var row in pendingRows.Where(row => verifiedFileIds.Contains(row.PendingVideoFileId!.Value)))
                {
                    if (row.IncumbentVideoFileId is { } previous && previous != row.PendingVideoFileId)
                    {
                        row.SupersededVideoFileId = previous;
                        previousIncumbents.Add(previous);
                    }

                    row.IncumbentVideoFileId = row.PendingVideoFileId;
                    row.PendingVideoFileId   = null;
                    row.UpdatedAtUtc         = timeProvider.GetUtcNow();
                }

                if (previousIncumbents.Count > 0)
                    retiredOwnerIds = await db.DownloadTaskFiles
                                              .Where(file => previousIncumbents.Contains(file.Id))
                                              .Select(file => file.DownloadTaskId)
                                              .Distinct()
                                              .ToListAsync(ct);
                await TryRetireExhaustedOwnersAsync(db, retiredOwnerIds, task.Id, ct);
            }

            // 校验成功：本轮根成为可信布局；逐文件路径只发布已证实的文件，未完成文件仍指向旧成片。
            DownloadRoundLayout.PublishRoot(tracked);

            // 启动意图与证据一起落库。Resume 响应丢失时仍走本状态机，不能落回普通提前命名。
            tracked.VerificationIntent = (intent with { Step = DownloadVerificationStep.ReadyToRun }).ToJson();
            await db.SaveChangesAsync(ct);
            changeNotifier?.Notify([task.Id, .. retiredOwnerIds], true);
        }

        // 完整任务：数据完整性已确认，停止态下恢复做种一次并 best-effort 通告 tracker，
        // 随后进入完成归档；分享限制造成的再次停止由完成状态吸收，不会形成重复启动。
        // 部分完成：只在仍停止时启动独立缺集的下载。
        // ReadyToRun 已在运行表示上次 Resume 已生效（可能丢响应），不能先暂停再重复启动。
        // 数据完整性已确认；启动（做种/继续下载）交由启动恢复 journal-first 推进：
        // 先落库恢复记录再发 Resume，任何失败都不改写状态、FailedStep/Error 与完成证据。
        var recovery = await StartRunWithRecoveryAsync(session, task, stable,
                                                       allSelectedComplete
                                                           ? DownloadRunPurposes.Seeding
                                                           : DownloadRunPurposes.ContinueDownload, ct);
        if (recovery is { State: DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown } &&
            !allSelectedComplete)
        {
            // 部分完成的启动待确认：保持系统暂停与观察窗口，确认运行后清记录回到普通下载跟踪；
            // 绝不落回用户暂停语义。
            await UpdateTaskAsync(task.Id, task.Status, update =>
            {
                update.VerificationIntent = null;
                update.PreparationStage   = DownloadPreparationStage.ReadyToRun;
                update.LastSyncedAtUtc    = timeProvider.GetUtcNow();
            }, ct);
            logger.LogInformation("任务 {TaskId} 复用校验候选全部通过；下载启动待确认（{RunState}），保持系统暂停继续观察。",
                                  task.Id, recovery.State);
            return;
        }

        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.IsSystemPaused     = false;
            update.VerificationIntent = null;
            update.PreparationStage   = DownloadPreparationStage.ReadyToRun;
            update.LastSyncedAtUtc    = timeProvider.GetUtcNow();
            // 启动待确认的完整任务保留 StartRun 写入的观察时间：完成与做种恢复分离推进。
            if (recovery?.State is not (DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown))
                update.NextObservationAtUtc = null;
        }, ct);
        if (allSelectedComplete)
            await ApplySnapshotAsync(task.Id, task.Status, stable with { Progress = 1.0 }, session, ct);
        // 完成收尾（归档/改名）可能清掉调度：启动待确认的完整任务重新落上观察时间，
        // 保证 Completed+RunRecovery 进入对账任务集继续观察做种是否恢复。
        if (allSelectedComplete && recovery is
                { State: DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown })
            await UpdateTaskAsync(task.Id, DownloadTaskStatus.Completed,
                                  update => update.NextObservationAtUtc =
                                      timeProvider.GetUtcNow() + timing.ControlConfirmationWindow, ct);
        logger.LogInformation(allSelectedComplete
                                  ? "任务 {TaskId} 复用校验全部通过，已确认内容完整并进入完成归档。"
                                  : "任务 {TaskId} 复用校验候选全部通过，其余文件继续下载。", task.Id);
    }

    private static IReadOnlyDictionary<int, string> CandidateRelativePaths(DownloadVerificationIntent intent)
    {
        var result = new Dictionary<int, string>();
        var root   = intent.CandidateRoot.TrimEnd('/');
        foreach (var mapping in intent.Files.Where(item => item.IsCandidate))
        {
            var path = DownloadPathNamePolicy.NormalizePath(mapping.VerificationPath);
            if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) continue;
            result[mapping.FileIndex] = path[(root.Length + 1)..];
        }

        return result;
    }

    private static string JoinAbsolute(string root, string relative) =>
        DownloadPathNamePolicy.NormalizePath(root.TrimEnd('/') + "/" + relative.Replace('\\', '/').TrimStart('/'));

    private async Task<bool> AnyCandidateHasProgressAsync(
        IQbittorrentSession session, DownloadTask task, IReadOnlyDictionary<int, string> candidateRelatives,
        CancellationToken   ct)
    {
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile)) return true;
        var indexByPath = TorrentDownloadPolicy.GetQbittorrentFileIndices(task.TorrentFile, torrent, isMultiFile);
        var remoteByIndex = (await session.ListFilesAsync(task.InfoHash, ct))
                           .GroupBy(file => file.Index)
                           .ToDictionary(group => group.Key, group => group.First());
        foreach (var file in task.Files)
        {
            if (!candidateRelatives.ContainsKey(file.FileIndex)) continue;
            if (!indexByPath.TryGetValue(file.OriginalRelativePath, out var qbIndex) ||
                !remoteByIndex.TryGetValue(qbIndex, out var remote)) return true;
            if (remote.Progress > 0 && !EqualVerificationPath(JoinAbsolute(DownloadRoundLayout.Root(task), remote.Path),
                                                              JoinAbsolute(DownloadRoundLayout.Root(task),
                                                                           candidateRelatives[file.FileIndex])))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     进入隔离（放弃复用、转为任务专属根独立下载）：与关闭校验意图在同一事务原子完成；
    ///     候选文件登记 ReuseUnverified 并绑定路径——该结果只表示「该次映射未通过或不能确认」，
    ///     不证明字节内容不同。删除重加适用于任何任务（含已接管、有独有数据者）：旧 extents 的数据
    ///     保留在磁盘（deleteFiles=false），代价是独有数据需要重下载。
    /// </summary>
    private async Task EnterIsolationAsync(
        DownloadTask task, DownloadVerificationIntent intent, string reason, CancellationToken ct)
    {
        logger.LogInformation("任务 {TaskId} 放弃复用校验并转入隔离独立下载：{Reason}", task.Id, reason);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var tracked = await db.DownloadTasks.Include(value => value.Files)
                              .SingleAsync(value => value.Id == task.Id, ct);
        DownloadRoundLayout.CapturePublished(tracked, tracked.Files);
        foreach (var mapping in intent.Files.Where(item => item.IsCandidate))
        {
            var file = tracked.Files.FirstOrDefault(item => item.FileIndex == mapping.FileIndex);
            if (file is null) continue;
            file.VerificationOutcome = DownloadFileVerificationOutcomes.ReuseUnverified;
            file.VerificationPath    = mapping.VerificationPath;
            file.UpdatedAtUtc        = timeProvider.GetUtcNow();
        }

        tracked.VerificationIntent = null;
        tracked.IsolationState     = DownloadIsolationState.Deleting;
        tracked.IsSystemPaused     = true;
        tracked.PendingSavePath    = null;
        // 隔离会清空布局计划，对应的请求台账一并作废；路径决策记录隔离原因供诊断。
        tracked.PathDecision = new DownloadPathDecision
        {
            Kind         = DownloadPathDecisionKinds.IsolationRecovery,
            Reason       = $"复用校验放弃：{reason}",
            Root         = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath ?? task.SavePath, task.Id),
            DecidedAtUtc = timeProvider.GetUtcNow()
        }.ToJson();
        tracked.NextObservationAtUtc        = null;
        tracked.PendingLayoutRequestedAtUtc = null;
        tracked.PendingLayoutRequestCount   = 0;
        foreach (var file in tracked.Files)
        {
            file.PendingRenameFrom = null;
            file.PendingRenameTo   = null;
        }

        await db.SaveChangesAsync(ct);
        changeNotifier?.Notify(task.Id, true);
    }

    /// <summary>
    ///     隔离状态机：Deleting（确认停止 → 保留文件删除）→ Readding（确认 hash 已消失 → 任务专属根停止重加）
    ///     → Confirming（布局与原始身份一致 → 清旧改名日志、重确认选择、回 Planned）。每步以下载器
    ///     真实状态确认后推进，绝不凭请求成功推进；崩溃后按当前状态与真实状态重导出。
    /// </summary>
    private async Task AdvanceIsolationAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot? snapshot, CancellationToken ct)
    {
        if (task.Status is not (DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)) return;
        // 对账入口的任务列表不带 Files；隔离确认需要文件身份，按需加载。
        task = await LoadTaskWithFilesAsync(task.Id, ct) ?? task;
        var targetRoot = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath ??
                                                        throw new InvalidOperationException("隔离任务缺少下载根记录。"), task.Id);

        switch (task.IsolationState)
        {
            case DownloadIsolationState.Deleting :
                if (snapshot is null)
                {
                    // 查无从不能证明删除请求已发出：种子表里的旧 Add 仍可能生效，
                    // 这里只推进状态，绝不释放更早未决 Add 的实际写入路径。
                    await UpdateTaskAsync(task.Id, task.Status,
                                          update => update.IsolationState = DownloadIsolationState.Readding, ct);
                    return;
                }

                if (snapshot.Category != QbittorrentTorrentCategory.Stopped)
                {
                    // 停止确认窗口内不重复暂停，窗口到期仍未停止下一轮再补发。
                    await session.PauseAsync(task.InfoHash, ct);
                    await DeferObservationAsync(task.Id, task.Status,
                                                timeProvider.GetUtcNow() + timing.ControlConfirmationWindow, ct);
                    return;
                }

                await session.DeleteTorrentAsync(task.InfoHash, false, ct);
                // 旧种子删除请求已发出：先前 Add 不再由本轮负责，首条隔离重加按新请求处理。
                await UpdateTaskAsync(task.Id, task.Status,
                                      update =>
                                      {
                                          update.IsolationState        = DownloadIsolationState.Readding;
                                          update.PendingSubmitSavePath = null;
                                          DownloadRoundLayout.ClearPendingAdds(update);
                                      }, ct);
                return;

            case DownloadIsolationState.Readding :
                if (snapshot is null)
                {
                    // 重加请求受可见性窗口门控：上一次 Add 可能仍在下载器处理中，不每轮重发。
                    if (task.LastSubmitAttemptAtUtc is { } attemptedAt &&
                        timeProvider.GetUtcNow() < attemptedAt + timing.AddVisibilityWindow)
                    {
                        await DeferObservationAsync(task.Id, task.Status,
                                                    timeProvider.GetUtcNow() + timing.PreparationSampleInterval, ct);
                        return;
                    }

                    // 本轮之前是否已有未决 Add：预检失败、HTTP 明确拒绝与 Fails. 都只否定本次
                    // 请求，不能清掉先前结果未知的实际写入根。
                    var hadUnconfirmedReadd     = DownloadRoundLayout.PendingAddRoots(task).Count > 0;
                    // 本次目标根此前是否已有未决请求：同根补发被拒时那一次仍可能生效，占用必须保留；
                    // 目标根是本次新登记时，明确拒绝只撤销这一次的占用，其他根上的先前请求不受影响。
                    var targetRootHadPendingAdd = DownloadRoundLayout.HasPendingAddRoot(task, targetRoot);

                    // 任务专属暂存同样受提交门禁：不能因为是隔离重加就绕过前置布局检查，
                    // 否则两个不同 hash 的种子会指向同一个完整文件路径。
                    var originalRoot = task.SavePath;
                    task.SavePath = targetRoot;
                    try
                    {
                        if (await InitialLayoutConflictsAsync(session, task, targetRoot, ct) ||
                            !string.IsNullOrWhiteSpace(task.DownloadRootPath))
                            await (submissionPathValidator ?? new DownloadSubmissionPathValidator(dbContextFactory))
                               .ValidateAsync(task, ct);
                    }
                    catch (InvalidOperationException exception)
                    {
                        // 预检只挡住这次尚未发出的重加；先前 Add 结果未知时继续占用其写入根。
                        await MarkFailedAsync(task.Id, task.Status, "提交路径", exception.Message, ct,
                                              !hadUnconfirmedReadd);
                        return;
                    }
                    finally
                    {
                        task.SavePath = originalRoot;
                    }

                    // 每条 Add 发出前先持久化实际提交根：响应未知、人工重试或重启都继续占用该路径；
                    // 旧成片根留在 SavePath，只有归档成功才发布新位置。
                    var readdAt = timeProvider.GetUtcNow();
                    await UpdateTaskAsync(task.Id, task.Status,
                                          update =>
                                          {
                                              update.LastSubmitAttemptAtUtc = readdAt;
                                              if (!hadUnconfirmedReadd) update.PendingSubmitSavePath = targetRoot;
                                              DownloadRoundLayout.RegisterPendingAdd(update, targetRoot);
                                          }, ct);
                    string response;
                    try
                    {
                        response = await session.AddTorrentAsync(task.TorrentFile, targetRoot, TagsFor(task),
                                                                 task.NamingSnapshot, true, ct);
                        if (string.Equals(response.Trim(), "Ok.", StringComparison.OrdinalIgnoreCase))
                            await ApplyConfiguredShareLimitAsync(session, task.InfoHash, task.SiteSource, ct);
                    }
                    catch (Exception exception) when (exception is QbittorrentBadRequestException
                                                                or QbittorrentNotFoundException)
                    {
                        // HTTP 400/404 明确拒绝本次请求：只撤销这一次请求所写根，其他根上的先前
                        // 请求仍可能生效。同根补发被拒同样不能否定更早的那次请求。
                        if (targetRootHadPendingAdd)
                        {
                            logger.LogWarning(exception,
                                              "任务 {TaskId} 的隔离补发被下载器拒绝，先前请求结果未知，继续保留路径占用。",
                                              task.Id);
                            await DeferObservationAsync(task.Id, task.Status,
                                                        timeProvider.GetUtcNow() + timing.AddVisibilityWindow, ct);
                            return;
                        }

                        // 本次是全新请求：撤销该根占用；若其他根仍有未决请求，任务继续等待它们收敛。
                        await MarkFailedAsync(task.Id, task.Status, "提交",
                                              $"下载器拒绝重新添加该种子：{exception.Message}", ct,
                                              !hadUnconfirmedReadd,
                                              targetRoot);
                        return;
                    }

                    if ((response ?? string.Empty).Trim().Contains("fail", StringComparison.OrdinalIgnoreCase))
                    {
                        if (targetRootHadPendingAdd)
                        {
                            // 同根的更早请求可能已被接受但暂时查不到：保留该根占用，按窗口等待。
                            logger.LogWarning("任务 {TaskId} 的隔离补发被拒绝，先前请求结果未知，继续保留路径占用。",
                                              task.Id);
                            await DeferObservationAsync(task.Id, task.Status,
                                                        timeProvider.GetUtcNow() + timing.AddVisibilityWindow, ct);
                            return;
                        }

                        // 本次是全新请求：明确拒绝（"Fails."）即撤销任务专属根占用并终止，
                        // 不能停在 Readding 无限重加；更早其他根的占用继续保留。
                        await MarkFailedAsync(task.Id, task.Status, "提交",
                                              $"下载器拒绝重新添加该种子：{(response ?? string.Empty).Trim()}", ct,
                                              !hadUnconfirmedReadd,
                                              targetRoot);
                    }
                    else
                        // Ok. 只证明请求被接受，种子可能暂时查不到：新根继续作为本轮布局根占用，
                        // 旧根在先前 Add 收敛前仍留在台账，等 hash 可见后由对账统一清理。
                    {
                        await UpdateTaskAsync(task.Id, task.Status,
                                              update =>
                                              {
                                                  update.PendingSubmitSavePath = targetRoot;
                                                  DownloadRoundLayout.RegisterPendingAdd(update, targetRoot);
                                              }, ct);
                    }

                    return;
                }

                if (!MatchesIsolationPath(snapshot.SavePath, targetRoot))
                {
                    // 外部在旧根重新出现的同 hash 种子：不是我们的重加，移除后下轮重试。
                    await session.DeleteTorrentAsync(task.InfoHash, false, ct);
                    return;
                }

                await UpdateTaskAsync(task.Id, task.Status,
                                      update => update.IsolationState = DownloadIsolationState.Confirming, ct);
                return;

            case DownloadIsolationState.Confirming :
                if (snapshot is null)
                {
                    await UpdateTaskAsync(task.Id, task.Status,
                                          update => update.IsolationState = DownloadIsolationState.Readding, ct);
                    return;
                }

                if (snapshot.IsMoving) return;
                if (!MatchesIsolationPath(snapshot.SavePath, targetRoot))
                {
                    await UpdateTaskAsync(task.Id, task.Status,
                                          update => update.IsolationState = DownloadIsolationState.Deleting, ct);
                    return;
                }

                if (snapshot.Category != QbittorrentTorrentCategory.Stopped)
                {
                    // 停止确认窗口内不重复暂停，窗口到期仍未停止下一轮再补发。
                    await session.PauseAsync(task.InfoHash, ct);
                    await DeferObservationAsync(task.Id, task.Status,
                                                timeProvider.GetUtcNow() + timing.ControlConfirmationWindow, ct);
                    return;
                }

                var remote = await session.ListFilesAsync(task.InfoHash, ct);
                if (remote.Count != task.Files.Count ||
                    task.Files.Any(file => remote.All(item =>
                                                          !string.Equals(item.Path.Replace('\\', '/'),
                                                                         file.OriginalRelativePath,
                                                                         StringComparison.Ordinal) ||
                                                          item.Size != file.Size)))
                {
                    // 重加后文件清单可能尚未就绪：宽限窗口内等待，窗口过后才是布局不一致。
                    if (WithinManifestGraceWindow(task)) return;
                    await MarkFailedAsync(task.Id, task.Status, "文件整理", "隔离重加后的布局与原始身份不一致，请人工核对。", ct);
                    return;
                }

                await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
                {
                    var tracked = await db.DownloadTasks.Include(value => value.Files)
                                          .SingleAsync(value => value.Id == task.Id, ct);
                    DownloadRoundLayout.CapturePublished(tracked, tracked.Files);
                    foreach (var file in tracked.Files)
                    {
                        file.CurrentRelativePath = file.OriginalRelativePath;
                        file.PendingRenameFrom   = null;
                        file.PendingRenameTo     = null;
                        file.UpdatedAtUtc        = timeProvider.GetUtcNow();
                    }

                    // 有旧成片时任务专属根只是本轮下载器布局：成片根继续指向旧成片，归档成功后才发布。
                    if (tracked.CompletedAtUtc is not null)
                    {
                        tracked.PendingSubmitSavePath = targetRoot;
                    }
                    else
                    {
                        tracked.SavePath              = targetRoot;
                        tracked.PendingSubmitSavePath = null;
                    }

                    tracked.PendingSavePath = null;
                    tracked.IsolationState  = DownloadIsolationState.None;
                    DownloadRoundLayout.ClearPendingAdds(tracked);
                    tracked.IsSystemPaused   = true;
                    tracked.PreparationStage = DownloadPreparationStage.Planned;
                    await db.SaveChangesAsync(ct);
                    changeNotifier?.Notify(task.Id, false);
                }

                logger.LogInformation("任务 {TaskId} 隔离完成，已在任务专属根重新添加并回到准备流程。", task.Id);
                return;
        }
    }

    /// <summary>
    ///     接管下载器中已存在的同 hash 种子：补打来源 tag、把本地隔离路径对齐为种子真实位置，再按快照推进。
    ///     存量任务在补建替换关系前已提交到正式根时，先恢复任务专属暂存布局，避免接管后整理再次撞占用。
    /// </summary>
    private async Task AdoptExistingAsync(
        DownloadTask task, TorrentSnapshot snapshot, IQbittorrentSession session, CancellationToken cancellationToken)
    {
        var siteSource = task.SiteSource;
        if (!string.IsNullOrWhiteSpace(siteSource) &&
            !snapshot.Tags.Contains(siteSource, StringComparer.OrdinalIgnoreCase))
            await session.AddTagAsync(task.InfoHash, siteSource, cancellationToken);

        await ApplyConfiguredShareLimitAsync(session, task.InfoHash, siteSource, cancellationToken);

        var staging = false;
        if (downloadFileService is not null)
            try
            {
                staging = await downloadFileService.EnsureReplacementStagingUnderGateAsync(session, task.Id,
                    cancellationToken);
                if (staging) task = await LoadTaskAsync(task.Id, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
                return;
            }

        // 接管即路径决策：对齐为种子真实位置，原因落库可追溯。恢复替换暂存时记录暂存根，
        // 移动请求由随后的接管流程执行或续做，这里不猜种子此刻的真实位置。
        var stagingRoot = staging ? DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, task.Id) : null;
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.PathDecision = new DownloadPathDecision
            {
                Kind = staging
                    ? DownloadPathDecisionKinds.ReplacementStaging
                    : DownloadPathDecisionKinds.SameHashAdoption,
                Reason = staging
                    ? "存量任务已提交在正式根且存在待替换旧版：接管后迁入任务专属根暂存，完成后再迁让旧文件并归档。"
                    : "下载器已存在同 Hash 种子，接管其真实保存位置。",
                Root         = stagingRoot ?? DownloadPathNamePolicy.NormalizePath(snapshot.SavePath),
                DecidedAtUtc = timeProvider.GetUtcNow()
            }.ToJson();
            // 已归档任务重下的接管只对齐待确认根，旧成片路径继续有效；纯新任务按原有语义发布真实位置。
            if (task.CompletedAtUtc is not null && DownloadRoundLayout.IsPending(task))
            {
                update.PendingSubmitSavePath = DownloadPathNamePolicy.NormalizePath(snapshot.SavePath);
            }
            else
            {
                if (!staging && !MatchesIsolationPath(snapshot.SavePath, task.SavePath))
                    // 隔离路径让位于种子的真实位置：接管外部种子时它通常不在本应用约定的目录里，
                    // 后续对账与完成改名都以真实位置为准。
                    update.SavePath = DownloadPathNamePolicy.NormalizePath(snapshot.SavePath);
                update.PendingSubmitSavePath = null;
            }

            DownloadRoundLayout.ClearPendingAdds(update);
        }, cancellationToken);

        if (snapshot.Category == QbittorrentTorrentCategory.Stopped && task.IsSystemPaused)
            // 接管的停止种子无法区分是“本应用停止添加后响应丢失”还是“用户/外部工具添加并暂停”：
            // 按人工暂停呈现，对账绝不自动启动；系统准备意图保留——用户显式恢复时
            // 由准备流程先补齐文件选择再启动，而不是绕过计划直接下载。
            // 停止来源同样是外部观察，记录时间供界面区分解释。
            await UpdateTaskAsync(task.Id, task.Status,
                                  update =>
                                  {
                                      update.IsUserPaused              = true;
                                      update.ExternalStopObservedAtUtc = timeProvider.GetUtcNow();
                                  },
                                  cancellationToken);

        if (downloadFileService is not null && !string.IsNullOrWhiteSpace(task.DownloadRootPath) &&
            snapshot.Category is QbittorrentTorrentCategory.Downloading or QbittorrentTorrentCategory.Stopped or
                                 QbittorrentTorrentCategory.Seeding)
            try
            {
                if (!await downloadFileService.AdoptExistingUnderGateAsync(session, task.Id, snapshot,
                                                                           cancellationToken))
                    return;
            }
            catch (InvalidOperationException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
                return;
            }

        // 与普通对账共用完成判定：接管的快照同样要经过所选文件进度核验——
        // 总进度按下载器当前选择加权，远端取消选择会让计划文件从分母里消失，不能直接据此判定完成。
        snapshot = await WithSelectedContentProgressAsync(session, task, snapshot, cancellationToken);
        await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
    }

    /// <summary>提交时随种子设置的下载器 tag：即来源站点标签（mikan/anibt/u2/byrbt/tjupt 等）。</summary>
    private static IReadOnlyList<string> TagsFor(DownloadTask task) =>
        string.IsNullOrWhiteSpace(task.SiteSource) ? [] : [task.SiteSource];

    private async Task ReconcileTrackedAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot? snapshot, CancellationToken cancellationToken)
    {
        // 种子重新可见即证明先前 Add 已生效，台账任务由 qB 自身参与占用检查；清除未确认记录，
        // 但不能改当前布局根——已归档任务仍要等归档成功才发布成片路径。
        if (snapshot is not null && DownloadRoundLayout.PendingAddRoots(task).Count > 0)
            await UpdateTaskAsync(task.Id, task.Status, DownloadRoundLayout.ClearPendingAdds, cancellationToken);

        // 路由优先级：启动恢复 → 隔离恢复 → 活跃校验 → 普通缺失/接管/命名/完成处理。
        // 隔离中 hash 暂时消失是正常中间态，绝不标 Lost；校验意图活跃期间不得进入普通
        // 命名、完成判定或现任切换——候选路径上的共享数据未经证实不得写入。
        // 启动恢复路由：已完成任务的做种恢复观察，与部分完成任务的下载启动确认；
        // 这些任务不适用 ApplySnapshotAsync 的普通状态推进，也绝不写 FailedStep/Error。
        if (DownloadRunRecovery.FromJson(task.RunRecovery) is { } runRecovery &&
            (task is { Status: DownloadTaskStatus.Completed, NextObservationAtUtc: not null } ||
             (task is { Status: DownloadTaskStatus.Downloading, IsSystemPaused: true } &&
              runRecovery.Purpose == DownloadRunPurposes.ContinueDownload)))
        {
            await AdvanceRunRecoveryAsync(session, task, snapshot, runRecovery, cancellationToken);
            return;
        }

        if (task.IsolationState != DownloadIsolationState.None)
        {
            await AdvanceIsolationAsync(session, task, snapshot, cancellationToken);
            return;
        }

        if (DownloadVerificationIntent.FromJson(task.VerificationIntent) is { } activeIntent)
        {
            await AdvanceVerificationAsync(session, task, snapshot, activeIntent, cancellationToken);
            return;
        }

        if (task.VerificationIntent is not null)
        {
            await MarkFailedAsync(task.Id, task.Status, "复用校验", "持久化校验意图无法解析，禁止按普通任务推进。", cancellationToken);
            return;
        }

        if (task.SelectionSyncPending && !DownloadTaskPlacement.IsTaskScoped(task))
        {
            var withFiles = await LoadTaskWithFilesAsync(task.Id, cancellationToken);
            if (withFiles is not null && withFiles.Files.Any(file => file is
                                                                     {
                                                                         IsSelectedForDownload: true,
                                                                         VerificationOutcome:
                                                                         DownloadFileVerificationOutcomes
                                                                            .ReuseUnverified
                                                                     } &&
                                                                     EqualVerificationPath(DownloadRoundLayout
                                                                            .CurrentAbsolute(task, file),
                                                                         file.VerificationPath ?? string.Empty)))
            {
                await EnterIsolationAsync(task, new DownloadVerificationIntent(),
                                          "文件选择发生变化，旧复用证据失效，重新下载前先隔离共享布局。", cancellationToken);
                return;
            }
        }

        if (snapshot is null)
        {
            // 添加可见性窗口内短暂查不到不标 Lost：请求可能仍被下载器处理，窗口内继续等待；
            // 曾同步过的任务缺失是真实删除，按原语义处理。
            if (WithinAddVisibilityWindow(task))
            {
                await DeferObservationAsync(task.Id, task.Status,
                                            timeProvider.GetUtcNow() + timing.PreparationSampleInterval,
                                            cancellationToken);
                return;
            }

            await UpdateTaskAsync(task.Id, task.Status, update =>
            {
                update.Status     = DownloadTaskStatus.Lost;
                update.FailedStep = "对账";
            }, cancellationToken);
            return;
        }

        // 保存位置变更未确认时，两端路径均可能被下载器返回；新一轮未发布时按待确认根核对。
        if (!MatchesIsolationPath(snapshot.SavePath, DownloadRoundLayout.Root(task)) &&
            (task.PendingSavePath is null || !MatchesIsolationPath(snapshot.SavePath, task.PendingSavePath)))
        {
            await MarkFailedAsync(task.Id, task.Status, "对账",
                                  "下载器中同 hash 任务的保存路径与本任务的隔离目录不一致，未接管，请人工检查下载器。",
                                  cancellationToken);
            return;
        }

        // 系统准备暂停中的任务优先推进准备流程（文件选择 → 命名 → 启动），每轮至多发出一批副作用；
        // 正常推进（含部分完成）后由下一轮以新快照同步，推进失败（如目标路径占用）时本轮仍同步进度。
        if (task is { Status      : DownloadTaskStatus.Downloading, IsSystemPaused: true } &&
            snapshot is { Category: QbittorrentTorrentCategory.Stopped, IsMoving  : false })
        {
            try
            {
                await AdvancePreparationAsync(session, task, snapshot, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadFileIdentityException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "下载任务 {TaskId} 准备流程推进失败，本轮仍同步进度，下一轮重试。",
                                  task.Id);
            }

            await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
            return;
        }

        // 登记侧逐集择优改动了已进入下载（或人工暂停）任务的文件选择：把本地计划与下载器实际
        // 选择比对下推。发出变更或存在无法寻址的文件时本轮到此为止，只同步进度，下一轮确认后再清旗。
        if (task.SelectionSyncPending            &&
            task is not { IsSystemPaused: true } &&
            snapshot is
            {
                IsMoving: false,
                Category: QbittorrentTorrentCategory.Downloading
                       or QbittorrentTorrentCategory.Stopped
                       or QbittorrentTorrentCategory.Seeding
            })
            try
            {
                var selectionTask = await LoadTaskWithFilesAsync(task.Id, cancellationToken);
                if (selectionTask is not null &&
                    await TryApplyFileSelectionAsync(session, selectionTask, cancellationToken, false))
                {
                    await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
                    return;
                }

                await UpdateTaskAsync(task.Id, task.Status,
                                      update => update.SelectionSyncPending = false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadFileIdentityException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "下载任务 {TaskId} 的文件选择同步失败，下一轮重试。", task.Id);
                return;
            }

        // 首轮扫到即改名：下载中（含人工暂停）任务一旦文件仍保持登记原始路径，立即执行命名计划，
        // 不等下载完成；失败只留待下一轮重试，不阻塞本轮进度推进。已完成快照走完成链的归档校验。
        if (snapshot is
            { Progress: < 1, Category: QbittorrentTorrentCategory.Downloading or QbittorrentTorrentCategory.Stopped })
            try
            {
                await SettleNamingEarlyAsync(task, snapshot, session, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DownloadFileIdentityException exception)
            {
                await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                // 文件规划/改名失败应由后续对账重试；本轮仍要把已经取得的下载器进度写入数据库。
                logger.LogWarning(exception, "下载任务 {TaskId} 提前整理文件失败，本轮仍继续同步进度，后续对账重试文件操作。",
                                  task.Id);
            }

        // 存在跳过文件的任务按所选内容计算进度：选中文件全部完成即满足本次下载目标，
        // 不为整包进度强制下载落选文件。整包 100% 时同样必须核对——总进度是按下载器当前
        // 选择加权的，远端取消选择会使未下载的计划文件不出现在分母里。
        if (snapshot is
            {
                Category: QbittorrentTorrentCategory.Downloading
                       or QbittorrentTorrentCategory.Stopped
                       or QbittorrentTorrentCategory.Seeding
            })
            snapshot = await WithSelectedContentProgressAsync(session, task, snapshot, cancellationToken);

        await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
    }

    /// <summary>
    ///     推进停止添加任务的准备流程：先核对文件下载选择与计划一致（不一致只发出变更，下一轮确认），
    ///     再应用提前命名计划，全部确认且用户未暂停后恢复下载。每一步都幂等，按下载器真实状态比对补做缺项。
    /// </summary>
    private async Task AdvancePreparationAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot snapshot, CancellationToken cancellationToken)
    {
        var loaded = await LoadTaskWithFilesAsync(task.Id, cancellationToken);
        if (loaded is not { Status: DownloadTaskStatus.Downloading, IsSystemPaused: true }) return;
        task = loaded;

        // 没有任何选中文件的候选不启动：保留可解释的落选或被覆盖结果，等待人工处理。
        if (task.Files.Count > 0 && task.Files.All(file => !file.IsSelectedForDownload))
        {
            logger.LogInformation("任务 {TaskId} 的全部文件均已落选，保持停止不启动下载。", task.Id);
            return;
        }

        // 1. 文件下载选择与计划比对；不一致时发出变更即返回，请求成功不代表已生效，下一轮核对后再继续。
        //    无法在下载器文件清单中确认身份的文件同样阻塞推进：缺项不代表选择已确认。
        if (await TryApplyFileSelectionAsync(session, task, cancellationToken)) return;

        // 选择已确认与计划一致：登记侧承接或删除回退置起的同步旗标在准备路径内一并清掉，
        // 免得任务启动后再空跑一轮对账；发出变更的轮次仍由下一轮确认后清理。
        if (task.SelectionSyncPending)
            await UpdateTaskAsync(task.Id, task.Status,
                                  update => update.SelectionSyncPending = false, cancellationToken);

        // 2. 命名计划：仍是登记原始路径的文件交由文件服务改名；改名日志由恢复链在后续轮次确认完成。
        //    目标路径占用不再回退「原始命名继续下载」：原始路径同样未经证实可写，带着未证实布局启动
        //    会改写未知数据。任务内命名目标位于按构造安全的命名空间，命中该异常说明布局异常或目标
        //    被占用，保持停止等待下一轮重试；身份校验失败等其他异常向外抛出并阻塞推进。
        try
        {
            await SettleNamingEarlyAsync(task, snapshot, session, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TargetPathOccupiedException exception)
        {
            await MarkFailedAsync(task.Id, task.Status, "文件整理", exception.Message, cancellationToken);
            return;
        }

        bool hasPending;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            hasPending = await db.DownloadTasks.AsNoTracking()
                                 .AnyAsync(value => value.Id == task.Id &&
                                                    (value.PendingSavePath != null ||
                                                     value.Files.Any(file => file.PendingRenameTo != null)),
                                           cancellationToken);
        }

        if (hasPending) return;

        // 3. 准备完成：恢复下载并清除系统暂停意图。启动前重读任务状态，
        // 准备推进期间用户可能已暂停（或任务被替换）——人工意图优先，绝不带着过期意图启动。
        var latest = await LoadTaskAsync(task.Id, cancellationToken);
        if (latest is not { Status: DownloadTaskStatus.Downloading, IsUserPaused: false, IsSystemPaused: true })
            return;

        await session.ResumeAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(task.Id, DownloadTaskStatus.Downloading, update =>
        {
            update.IsSystemPaused       = false;
            update.PreparationStage     = DownloadPreparationStage.ReadyToRun;
            update.LastSyncedAtUtc      = timeProvider.GetUtcNow();
            update.NextObservationAtUtc = null;
        }, cancellationToken);
        logger.LogInformation("任务 {TaskId} 准备完成（文件选择与命名已确认），已恢复下载。", task.Id);
    }

    /// <summary>
    ///     核对下载器实际文件选择与计划的一致性，不一致时发出补做请求。返回 true 表示本轮不应推进
    ///     后续步骤（已发出变更等待确认，或有文件身份无法确认——缺项不代表选择已确认）。
    ///     markStage 为 false 时供准备外（逐集承接下推）复用，不改写解释用的准备阶段。
    /// </summary>
    private async Task<bool> TryApplyFileSelectionAsync(
        IQbittorrentSession session, DownloadTask task, CancellationToken cancellationToken, bool markStage = true)
    {
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile)) return false;

        var indexByPath = TorrentDownloadPolicy.GetQbittorrentFileIndices(task.TorrentFile, torrent, isMultiFile);
        var remoteFiles = (await session.ListFilesAsync(task.InfoHash, cancellationToken))
                         .GroupBy(file => file.Index)
                         .ToDictionary(group => group.Key, group => group.First());

        var toSkip      = new List<int>();
        var toDownload  = new List<int>();
        var unconfirmed = 0;
        foreach (var file in task.Files)
        {
            if (!indexByPath.TryGetValue(file.OriginalRelativePath, out var qbIndex) ||
                !remoteFiles.TryGetValue(qbIndex, out var remote)                    || remote.Size != file.Size)
            {
                // 计划内文件在下载器清单中无法寻址：选择状态无法确认，不得当作已确认推进。
                unconfirmed++;
                continue;
            }

            switch (file.IsSelectedForDownload)
            {
                case true when !remote.IsSelectedForDownload :
                    toDownload.Add(qbIndex);
                    break;
                case false when remote.IsSelectedForDownload :
                    toSkip.Add(qbIndex);
                    break;
            }
        }

        if (unconfirmed > 0)
        {
            // 刚添加的种子 hash 可能已可见而逐文件清单尚未就绪：宽限窗口内只等待，
            // 区分清单滞后与真实身份冲突；窗口过后仍无法寻址才按身份冲突失败。
            if (WithinManifestGraceWindow(task))
            {
                await DeferObservationAsync(task.Id, task.Status,
                                            timeProvider.GetUtcNow() + timing.PreparationSampleInterval,
                                            cancellationToken);
                return true;
            }

            throw new DownloadFileIdentityException($"有 {unconfirmed} 个计划文件无法在下载器清单中确认身份，请核对后人工重试。");
        }

        if (toSkip.Count == 0 && toDownload.Count == 0)
        {
            // 选择已确认：清掉等待期留下的到期时间与请求预算，worker 恢复按空闲节奏调度
            // （残留的过期时间会让 worker 持续按最短间隔全量轮询）。
            if (task.NextObservationAtUtc is not null || task.SelectionRequestCount != 0)
                await UpdateTaskAsync(task.Id, task.Status, update =>
                {
                    update.NextObservationAtUtc  = null;
                    update.SelectionRequestCount = 0;
                }, cancellationToken);
            return false;
        }

        // 选择变更确认窗口内不重复下发：请求可能仍被下载器处理；窗口到期仍未生效下一轮再补发。
        // 查询轮次与 pulse 不构成补发依据；补发按持久化预算有界，耗尽即受阻待人工核对。
        var requestedAt = timeProvider.GetUtcNow();
        if (task.PendingLayoutRequestedAtUtc is { } lastRequestedAt &&
            requestedAt < lastRequestedAt + timing.SelectionConfirmationWindow)
        {
            await DeferObservationAsync(task.Id, task.Status,
                                        requestedAt + timing.PreparationSampleInterval, cancellationToken);
            return true;
        }

        if (task.SelectionRequestCount >= timing.MaxSelectionRequestIssues)
        {
            await MarkFailedAsync(task.Id, task.Status, "文件整理",
                                  "文件选择变更多次发出后仍未生效，为避免请求风暴已停止推进，请核对下载器后人工重试。",
                                  cancellationToken);
            return true;
        }

        // 副作用前登记请求台账：请求被下载器接受而响应丢失时，预算与确认窗口仍然生效，
        // 不因异常跳过登记而在下一轮同一时刻无界重发。
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.PendingLayoutRequestedAtUtc =  requestedAt;
            update.SelectionRequestCount       += 1;
        }, cancellationToken);

        if (toSkip.Count > 0)
            await session.SetTorrentFileSelectionAsync(task.InfoHash, toSkip, false, cancellationToken);
        if (toDownload.Count > 0)
            await session.SetTorrentFileSelectionAsync(task.InfoHash, toDownload, true, cancellationToken);

        if (markStage)
            await UpdateTaskAsync(task.Id, DownloadTaskStatus.Downloading,
                                  update => update.PreparationStage = DownloadPreparationStage.Applying,
                                  cancellationToken);
        logger.LogInformation("任务 {TaskId} 已发出文件选择变更（跳过 {SkipCount} 个、恢复下载 {DownloadCount} 个），等待下载器确认。",
                              task.Id, toSkip.Count, toDownload.Count);
        return true;
    }

    /// <summary>
    ///     计算所选内容进度：把整包进度替换为选中文件的加权进度；选中文件全部完成时返回进度 1，
    ///     由调用方沿用统一完成判定。没有跳过文件或无法取得文件级信息时原样返回整包进度。
    /// </summary>
    private async Task<TorrentSnapshot> WithSelectedContentProgressAsync(
        IQbittorrentSession session, DownloadTask task, TorrentSnapshot snapshot, CancellationToken cancellationToken)
    {
        List<DownloadTaskFile> files;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            files = await db.DownloadTaskFiles.AsNoTracking()
                            .Where(file => file.DownloadTaskId == task.Id)
                            .ToListAsync(cancellationToken);
        }

        if (files.Count == 0) return snapshot;

        // 本地计划有跳过文件，或整包进度已报告完成时，都必须核对文件级进度：
        // qB 的总进度按下载器当前选择加权，远端取消选择会让未下载的计划文件从分母里消失。
        if (files.All(file => file.IsSelectedForDownload) && snapshot.Progress < 1.0) return snapshot;

        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile)) return snapshot;

        var indexByPath = TorrentDownloadPolicy.GetQbittorrentFileIndices(task.TorrentFile, torrent, isMultiFile);
        var remoteFiles = (await session.ListFilesAsync(task.InfoHash, cancellationToken))
                         .GroupBy(file => file.Index)
                         .ToDictionary(group => group.Key, group => group.First());

        long selectedTotal = 0, selectedDone = 0;
        var  allComplete   = true;
        foreach (var file in files.Where(file => file.IsSelectedForDownload))
        {
            // 零字节文件不参与除法：能取得下载器确认即视为满足，拿不到也按完成口径保守放行其存在性。
            if (file.Size > 0) selectedTotal += file.Size;
            if (file.Size <= 0) continue;

            if (indexByPath.TryGetValue(file.OriginalRelativePath, out var qbIndex) &&
                remoteFiles.TryGetValue(qbIndex, out var remote))
            {
                selectedDone += (long)(remote.Progress * file.Size);
                if (remote.Progress < 1.0) allComplete = false;
            }
            else
            {
                // 无法确认该文件的下载器进度：保守视为未完成，不用整包进度冒充所选进度。
                allComplete = false;
            }
        }

        if (allComplete) return snapshot with { Progress = 1.0 };
        if (snapshot.Progress >= 1.0)
            // 整包进度 100% 但计划选中文件未全部完成：通常是下载器端选择与本地计划不一致
            // （如远端取消了计划要求下载的文件），如实保留所选进度，不宣称完成。
            logger.LogWarning("任务 {TaskId} 整包进度已完成，但计划选中的文件未全部完成（所选进度 {Progress:P1}），不判定完成。",
                              task.Id, selectedTotal > 0 ? (double)selectedDone / selectedTotal : 0);
        if (selectedTotal <= 0) return snapshot;
        return snapshot with { Progress = (double)selectedDone / selectedTotal };
    }

    /// <summary>任一文件仍是登记原始路径的活动任务视为尚未执行命名计划，交由文件服务改名；已改名或 legacy 布局跳过。</summary>
    private async Task SettleNamingEarlyAsync(
        DownloadTask task, TorrentSnapshot snapshot, IQbittorrentSession session, CancellationToken cancellationToken)
    {
        if (downloadFileService is null || string.IsNullOrWhiteSpace(task.DownloadRootPath)) return;
        if (task.SubmittedAtUtc is null && !DownloadTaskPlacement.IsTaskScoped(task)) return;
        bool untouched;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // 复用校验已布置过的任务（存在校验结果）不经提前命名：候选文件已在最终路径、
            // 其余文件停靠在命名空间，命名计划的最终布局只经完成归档链执行。
            var files = db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == task.Id);
            untouched = !await files.AnyAsync(file => file.VerificationOutcome ==
                                                      DownloadFileVerificationOutcomes.ReusedVerified,
                                              cancellationToken) &&
                        await files.AnyAsync(file => file.CurrentRelativePath == file.OriginalRelativePath,
                                             cancellationToken);
        }

        if (!untouched) return;
        await SaveTorrentFileTitleMappingsAsync(task.TorrentFile, task.SubjectId, cancellationToken);
        await downloadFileService.SettleNamingUnderGateAsync(session, task.Id, snapshot, cancellationToken);
    }

    /// <summary>按下载器快照推进本地状态：完成改名、区分人工暂停与系统准备暂停、记录进度。</summary>
    private async Task ApplySnapshotAsync(Guid taskId, DownloadTaskStatus currentStatus, TorrentSnapshot snapshot,
                                          IQbittorrentSession session, CancellationToken cancellationToken)
    {
        switch (snapshot.Category)
        {
            case QbittorrentTorrentCategory.Error :
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status               = DownloadTaskStatus.Failed;
                    update.FailedStep           = "下载器";
                    update.Error                = "下载器报告该任务出错或文件缺失，请检查下载器。";
                    update.NextObservationAtUtc = null;
                    update.LastObservedCategory = snapshot.Category;
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Checking :
                // 校验是异步阶段，进度可能临时波动：只同步进度，不推进完成、暂停或准备流程，
                // 待校验结束回到稳定状态后再继续。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Progress             = snapshot.Progress;
                    update.TotalSize            = snapshot.TotalSize;
                    update.LastSyncedAtUtc      = timeProvider.GetUtcNow();
                    update.LastObservedCategory = snapshot.Category;
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Seeding when snapshot.Progress     >= 1.0 :
            case QbittorrentTorrentCategory.Downloading when snapshot.Progress >= 1.0 :
            case QbittorrentTorrentCategory.Stopped when snapshot.Progress     >= 1.0 :
                // 先持久化下载器已报告的完成进度；若随后文件整理失败，页面仍能区分“下载已完成、整理失败”。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Progress             = snapshot.Progress;
                    update.TotalSize            = snapshot.TotalSize;
                    update.LastSyncedAtUtc      = timeProvider.GetUtcNow();
                    update.LastObservedCategory = snapshot.Category;
                }, cancellationToken);
                await CompleteAsync(session, taskId, snapshot, currentStatus, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Stopped :
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    if (update is { IsSystemPaused: true, IsUserPaused: false })
                    {
                        // 系统以停止状态添加/接管且准备未完成：保持待准备语义（含提交中断后的接管），
                        // 准备推进由对账的准备分支负责，不得把准备暂停误记为人工暂停。
                        update.Status = DownloadTaskStatus.Downloading;
                    }
                    else
                    {
                        // 人工在下载器或页面暂停的任务必须保留暂停状态，对账绝不自动恢复；
                        // 外部停止且系统未声明准备时同样按人工暂停保守处理。首次观察到该停止时
                        // 记录来源时间：页面暂停已置 Paused 不再改写，界面据此区分解释而非统一归为人工。
                        if (update.Status != DownloadTaskStatus.Paused)
                        {
                            update.Status                    = DownloadTaskStatus.Paused;
                            update.IsUserPaused              = true;
                            update.ExternalStopObservedAtUtc = timeProvider.GetUtcNow();
                        }
                    }

                    update.Progress             = snapshot.Progress;
                    update.TotalSize            = snapshot.TotalSize;
                    update.LastSyncedAtUtc      = timeProvider.GetUtcNow();
                    update.LastObservedCategory = snapshot.Category;

                    // 注意：不得清理 NextObservationAtUtc——选择/布局等待的到期时间可能尚未到期，
                    // 普通进度同步无权取消尚未收敛的等待（过期残留由 worker 只取未来时间兜底）。
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Downloading or QbittorrentTorrentCategory.Seeding :
                // 进度未满的做种快照（接管/校验后确认内容不完整）与下载中同等处理：保持等待，不判完成。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status       = DownloadTaskStatus.Downloading;
                    update.IsUserPaused = false;

                    // 下载已实际发生（可能是准备完成后恢复，也可能是用户在下载器内手动启动）：
                    // 系统准备暂停语义结束，用户手动启动优先。同样不清理未到期的观察时间。
                    update.IsSystemPaused            = false;
                    update.Progress                  = snapshot.Progress;
                    update.TotalSize                 = snapshot.TotalSize;
                    update.LastSyncedAtUtc           = timeProvider.GetUtcNow();
                    update.LastObservedCategory      = snapshot.Category;
                    // 恢复运行即推翻此前观察到的外部停止；再次外部停止会重新记录。
                    update.ExternalStopObservedAtUtc = null;
                }, cancellationToken);
                return;

            default :
                // Unknown 状态不推进，等下一轮确认。
                return;
        }
    }

    /// <summary>
    ///     完成收尾：按命名要素改名（单文件改文件名；多文件逐文件按各自集数改文件名、解析不出集数的
    ///     附属文件挪进 Seed/），种子名称模板只命名种子、不参与内容目录，并按当前状态重新选择替换对象。
    ///     登记期同集裁决顶替掉的下载中低优先任务在此停删（连文件），已完成的旧任务沿用版本守卫语义
    ///     仅标记、文件保留。没有命名要素的存量任务回退按命名快照改种子名。
    /// </summary>
    private async Task CompleteAsync(IQbittorrentSession session,       Guid taskId, TorrentSnapshot snapshot,
                                     DownloadTaskStatus  currentStatus, CancellationToken cancellationToken)
    {
        string                    targetName;
        int?                      subjectId;
        decimal?                  taskEpisode;
        int                       taskVersion;
        TorrentNamingIngredients? ingredients;
        byte[]                    torrentFile;

        IReadOnlyList<string> fileNames;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var task = await db.DownloadTasks.AsNoTracking()
                               .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
            if (task is null || task.Status.IsFinal()) return;

            fileNames   = await session.ListFileNamesAsync(snapshot.Hash, cancellationToken);
            // 种子名不带扩展名，与添加时 rename 参数设置的名称同形；扩展名只属于内容文件。
            ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
            if (ingredients is null ||
                string.IsNullOrWhiteSpace(ingredients.Title) ||
                string.IsNullOrWhiteSpace(ingredients.EpisodeText))
                ingredients = null;
            targetName  = await RenderLiveTorrentNameAsync(ingredients, task.NamingSnapshot, cancellationToken);
            subjectId   = task.SubjectId;
            taskEpisode = task.Episode;
            taskVersion = task.Version;
            torrentFile = task.TorrentFile;
        }

        if (fileNames.Count == 0)
        {
            await MarkFailedAsync(taskId, currentStatus, "改名", "无法读取种子内文件列表，无法按命名快照改名，请人工处理。", cancellationToken);
            return;
        }

        await SaveTorrentFileTitleMappingsAsync(torrentFile, subjectId, cancellationToken);

        var useFileArchive = downloadFileService is not null &&
                             !string.IsNullOrWhiteSpace((await LoadTaskAsync(taskId, cancellationToken))
                                                       .DownloadRootPath);
        if (useFileArchive)
            try
            {
                if (!await downloadFileService!.ArchiveCompletedUnderGateAsync(session, taskId, snapshot,
                                                                               cancellationToken))
                    return;
                await session.RenameTorrentAsync(snapshot.Hash, targetName, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                await MarkFailedAsync(taskId, currentStatus, "文件整理", exception.Message, cancellationToken);
                return;
            }
            catch (QbittorrentNotFoundException)
            {
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status     = DownloadTaskStatus.Lost;
                    update.FailedStep = "对账";
                    update.Error      = "完成对账时下载器中已不存在该任务。";
                }, cancellationToken);
                return;
            }
        else
            // 存量任务保持旧的收尾方式：只改文件名与种子名，不使用种子名称模板改内容目录。
            try
            {
                var fileTemplate = namingTemplateStore is null
                    ? null
                    : await namingTemplateStore.GetFileTemplateAsync(cancellationToken);
                if (fileNames.Count == 1)
                {
                    // 单文件也按文件名实际集数应用 Subject 修正规则；无可解析集数时保留任务快照命名。
                    var extension = Path.GetExtension(fileNames[0]);
                    var singleIngredients = ingredients is null
                        ? null
                        : CorrectIngredientsForFile(ingredients, Path.GetFileName(fileNames[0]), null, subjectId,
                                                    taskVersion);
                    var singleName = singleIngredients is null
                        ? targetName
                        : namingPolicy.Create(fileTemplate is null
                                                  ? singleIngredients
                                                  : singleIngredients with { FileTemplate = fileTemplate }, true);
                    await session.RenameTorrentFileAsync(snapshot.Hash, fileNames[0],
                                                         AppendExtension(singleName, extension), cancellationToken);
                }
                else if (ingredients is not null)
                {
                    // 多文件种子：逐文件按各自集数改名，附属文件挪进 Seed/。
                    var contentIngredients = fileTemplate is null
                        ? ingredients
                        : ingredients with { FileTemplate = fileTemplate };
                    await RenameContentFilesAsync(session, snapshot.Hash, fileNames, contentIngredients, subjectId,
                                                  taskEpisode, taskVersion, cancellationToken);
                }

                await session.RenameTorrentAsync(snapshot.Hash, targetName, cancellationToken);
            }
            catch (QbittorrentNotFoundException)
            {
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status     = DownloadTaskStatus.Lost;
                    update.FailedStep = "对账";
                    update.Error      = "完成对账时下载器中已不存在该任务。";
                }, cancellationToken);
                return;
            }

        await using var completionDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trackedTask = await completionDb.DownloadTasks
                                            .SingleAsync(value => value.Id == taskId, cancellationToken);
        if (trackedTask.Status.IsFinal()) return;

        trackedTask.Status               = DownloadTaskStatus.Completed;
        trackedTask.NamingSnapshot       = targetName;
        trackedTask.TorrentRenamePending = false;
        trackedTask.Progress             = 1;
        trackedTask.CompletedAtUtc  = timeProvider.GetUtcNow();
        trackedTask.LastSyncedAtUtc = trackedTask.CompletedAtUtc;
        trackedTask.FailedStep      = null;
        trackedTask.Error           = null;

        // 登记期替换对象的兜底收敛（常规翻转见 ApplyWinningEpisodesAsync 与 TryRetireExhaustedOwnersAsync）：
        // 职责耗尽才翻转，不再按完成状态无条件顶替；远端清理由对账清理环统一保留内容移除，此处不直接删种。
        if (trackedTask.ReplacesTaskId is { } arbitrationTargetId)
            await TryRetireExhaustedOwnersAsync(completionDb, [arbitrationTargetId], taskId, cancellationToken);

        // 逐集切换现任：本任务的选中正片按文件级集数独立登记为该集可播放版本，并清掉自己此前
        // 登记的待完成优胜。已完成旧任务不再整任务翻 Replaced——旧合集可能仍有其他现任集，
        // 任务级替换字段自此只保留登记期摘要，现任关系以每集登记表为准。
        await SwitchEpisodeIncumbentsAsync(completionDb, trackedTask, cancellationToken);

        await completionDb.SaveChangesAsync(cancellationToken);
        IReadOnlyCollection<Guid> completedAffected = trackedTask.ReplacesTaskId is { } targetId
            ? [taskId, targetId]
            : [taskId];
        changeNotifier?.Notify(completedAffected, true);
        logger.LogInformation("下载任务 {TaskId} 已完成并改名为 {TargetName}。", taskId, targetName);
    }


    /// <summary>保存路径归一化分隔符后精确比较：大小写必须一致，避免在大小写敏感的 Linux 文件系统上把不同目录判成同一目录。</summary>
    private static bool MatchesIsolationPath(string snapshotPath, string taskPath) =>
        string.Equals(DownloadPathNamePolicy.NormalizePath(snapshotPath),
                      DownloadPathNamePolicy.NormalizePath(taskPath), StringComparison.Ordinal);

    /// <summary>所有文件都位于同一顶层目录时返回该目录；顶层散文件或多目录布局返回 false。</summary>
    private static bool TryGetCommonRootFolder(IReadOnlyList<string> fileNames, out string rootFolder)
    {
        rootFolder = string.Empty;
        if (fileNames.Count == 0) return false;

        var firstPath      = fileNames[0].Replace('\\', '/');
        var separatorIndex = firstPath.IndexOf('/');
        if (separatorIndex <= 0) return false;

        var candidate = firstPath[..separatorIndex];
        if (fileNames.Any(name => !name.Replace('\\', '/').StartsWith(candidate + "/", StringComparison.Ordinal)))
            return false;

        rootFolder = candidate;
        return true;
    }

    /// <summary>目标名已带同名扩展名时原样返回，否则追加；RSS/上传回退的原始标题可能自带 .mp4 后缀。</summary>
    private static string AppendExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;

    /// <summary>
    ///     逐文件整理种子内容（ByrTorrent 同源方法）：文件名能解析出集数的视频/字幕按命名要素改名为
    ///     各自集数（标题、字幕组、来源沿用任务级令牌）；解析不出的附属文件挪进 Seed/，不混入成片命名。
    ///     同一种子内两文件解析出相同目标名时跳过改名保留原名，等待人工处理。
    /// </summary>
    private TorrentNamingIngredients? CorrectIngredientsForFile(TorrentNamingIngredients ingredients,   string fileName,
                                                                ParseResult?             alreadyParsed, int? subjectId,
                                                                int                      taskVersion)
    {
        var parsed = alreadyParsed ?? aniParser.Parse(fileName);
        if (parsed is null) return null;

        string? correctedText;
        if (parsed.Episode is { } episode)
        {
            correctedText =
                ReleaseNamePreviewPolicy.FormatEpisodeText(episodeCorrectionPolicy.Apply(subjectId, episode).Episode);
        }
        else if (parsed is { StartEpisode: { } start, EndEpisode: { } end })
        {
            var correctedStart = episodeCorrectionPolicy.Apply(subjectId, start).Episode;
            var correctedEnd   = episodeCorrectionPolicy.Apply(subjectId, end).Episode;
            correctedText = correctedStart == correctedEnd
                ? ReleaseNamePreviewPolicy.FormatEpisodeText(correctedStart)
                : $"{ReleaseNamePreviewPolicy.FormatEpisodeText(correctedStart)}-E{ReleaseNamePreviewPolicy.FormatEpisodeText(correctedEnd)}";
        }
        else
        {
            correctedText = null;
        }

        if (correctedText is null) return null;
        return ingredients with
        {
            EpisodeText = correctedText,
            Version = parsed.Version > 1 ? parsed.Version : taskVersion > 1 ? taskVersion : ingredients.Version,
            AniMediaType = parsed.MediaType.ToString(),
            AniTitle = parsed.Title,
            AniGroup = parsed.Group,
            AniGroupType = parsed.GroupType.ToString(),
            AniSubtitleType = parsed.SubtitleType.ToString(),
            AniSource = parsed.Source.ToString(),
            AniWebSource = parsed.WebSource,
            AniResolution = parsed.Resolution.ToString(),
            AniVideoCodec = parsed.VideoCodec,
            AniAudioCodec = parsed.AudioCodec,
            AniColorBitDepth = parsed.ColorBitDepth,
            Language = parsed.Language.ToString(),
            Resolution = parsed.Resolution.ToString()
        };
    }

    private async Task RenameContentFilesAsync(IQbittorrentSession      session, string infoHash,
                                               IReadOnlyList<string>    fileNames,
                                               TorrentNamingIngredients ingredients, int? subjectId,
                                               decimal?                 taskEpisode, int  taskVersion,
                                               CancellationToken        cancellationToken)
    {
        var prefix      = TryGetCommonRootFolder(fileNames, out var rootFolder) ? $"{rootFolder}/" : string.Empty;
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in fileNames)
        {
            var    path        = rawPath.Replace('\\', '/');
            var    extension   = Path.GetExtension(path);
            var    parsed      = aniParser.Parse(Path.GetFileName(path));
            var    episodeText = parsed is null ? null : ReleaseNamePreviewPolicy.FormatEpisodeText(parsed);
            string newPath;
            if (episodeText is not null &&
                ContentFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                var fileIngredients =
                    CorrectIngredientsForFile(ingredients, Path.GetFileName(path), parsed, subjectId, taskVersion);
                if (fileIngredients is null)
                {
                    newPath = path;
                    continue;
                }

                var candidate = prefix + namingPolicy.Create(fileIngredients, true) + extension;
                if (!usedTargets.Add(candidate))
                {
                    logger.LogWarning("种子 {InfoHash} 内 {Path} 的目标命名与其他文件冲突，保留原名等待人工处理。",
                                      infoHash, path);
                    continue;
                }

                newPath = candidate;
            }
            else
            {
                // 解析不出集数的附属文件（NCOP/ED、菜单、Extras 等）挪进 Seed/，不混入成片命名。
                var relativePath = prefix.Length > 0 ? path[rootFolder.Length..] : $"/{path}";
                newPath = $"{prefix}Seed{relativePath}";
            }

            if (!string.Equals(newPath, path, StringComparison.Ordinal))
                await session.RenameTorrentFileAsync(infoHash, path, newPath, cancellationToken);
        }
    }

    /// <summary>
    ///     文件整理前按种子内原始文件名解析标题，并把可识别正片的 AniParser 标题关联到任务已绑定的
    ///     Bangumi Subject，供后续只提供种子的上传流程命中标题映射。
    /// </summary>
    private async Task SaveTorrentFileTitleMappingsAsync(
        byte[] torrentFile, int? subjectId, CancellationToken cancellationToken)
    {
        if (subjectId is not { } boundSubjectId ||
            !TorrentDownloadPolicy.TryLoad(torrentFile, out var torrent, out var isMultiFile)) return;

        var normalizedTitles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in torrent.Files)
        {
            var path      = TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile);
            var extension = Path.GetExtension(path);
            if (!ContentFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

            var parsed = aniParser.Parse(Path.GetFileName(path));
            if (parsed is null || ReleaseNamePreviewPolicy.FormatEpisodeText(parsed) is null ||
                string.IsNullOrWhiteSpace(parsed.Title)) continue;

            var normalizedTitle = TitleNormalizer.Normalize(parsed.Title);
            if (normalizedTitle.Length > 0) normalizedTitles.Add(normalizedTitle);
        }

        foreach (var normalizedTitle in normalizedTitles)
            await subjectCache.SaveTitleMappingAsync(normalizedTitle, boundSubjectId, cancellationToken);
    }


    /// <summary>
    ///     墓碑清理确认：只在成功查无、或删除后按 hash 复查确认消失时落标记（deleteFiles=false 不变）；
    ///     标记绑定实际下载器与本次承接关系，查询/删除失败与结果未知都不写，下一轮保留清理资格。
    /// </summary>
    private Task ConfirmMissingCleanupAsync(
        DownloadTask task, string configKey, CancellationToken cancellationToken) =>
        UpdateTaskAsync(task.Id, DownloadTaskStatus.Replaced, update =>
        {
            update.MissingCleanupConfirmedAtUtc = timeProvider.GetUtcNow();
            update.MissingCleanupScopeKey       = BuildMissingCleanupScopeKey(configKey, update.ReplacedByTaskId);
            update.MissingCleanupReplacedById   = update.ReplacedByTaskId;
        }, cancellationToken);

    private static string BuildMissingCleanupScopeKey(string configKey, Guid? replacedById) =>
        $"{configKey}:{replacedById?.ToString("D") ?? string.Empty}";

    private async Task<bool> IsActiveTaskForHashAsync(
        string infoHash, Guid exceptTaskId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadTasks.AsNoTracking().AnyAsync(task => task.Id       != exceptTaskId &&
                                                                      task.InfoHash == infoHash     &&
                                                                      (task.Status ==
                                                                       DownloadTaskStatus.PendingSubmit              ||
                                                                       task.Status == DownloadTaskStatus.Submitting  ||
                                                                       task.Status == DownloadTaskStatus.Downloading ||
                                                                       task.Status == DownloadTaskStatus.Paused),
                                                              cancellationToken);
    }

    private async Task<DownloadTask> LoadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadTasks.AsNoTracking()
                       .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到指定下载任务。");
    }

    /// <summary>加载任务及其文件计划（准备流程需要文件选择与命名状态）。</summary>
    private async Task<DownloadTask?> LoadTaskWithFilesAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadTasks.AsNoTracking()
                       .Include(task => task.Files)
                       .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
    }

    /// <summary>
    ///     带状态保护的字段更新：只更新仍处于预期状态的任务，避免与人工操作交错。
    ///     onApplied 在状态变更生效后、与同一次 SaveChangesAsync 落库前执行，用于把通知投递意图写入同一本地事务。
    /// </summary>
    private async Task<bool> UpdateTaskAsync(Guid taskId, DownloadTaskStatus expectedStatus,
                                             Action<DownloadTask> update, CancellationToken cancellationToken,
                                             Func<ApplicationDbContext, DownloadTask, Task>? onApplied = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.DownloadTasks.Include(value => value.Subject)
                           .SingleAsync(value => value.Id == taskId, cancellationToken);
        if (task.Status != expectedStatus) return false;

        var prevStatus            = task.Status;
        var prevUserPaused        = task.IsUserPaused;
        var prevSystemPaused      = task.IsSystemPaused;
        var prevCategory          = task.LastObservedCategory;
        var prevVerification      = task.VerificationIntent;
        var prevMissingAt         = task.TorrentMissingAtUtc;
        var prevNamingSnapshot    = task.NamingSnapshot;
        var prevNamingIngredients = task.NamingIngredients;
        var prevEpisode           = task.Episode;
        var prevVersion           = task.Version;

        update(task);
        if (onApplied is not null) await onApplied(db, task);
        await db.SaveChangesAsync(cancellationToken);

        var affectsListOrderOrMembership =
            task.Status != prevStatus                                                                     ||
            task.IsUserPaused != prevUserPaused                                                           ||
            task.IsSystemPaused != prevSystemPaused                                                       ||
            (task.LastObservedCategory == QbittorrentTorrentCategory.Checking) !=
            (prevCategory == QbittorrentTorrentCategory.Checking)                                         ||
            !string.Equals(task.VerificationIntent, prevVerification, StringComparison.Ordinal)           ||
            task.TorrentMissingAtUtc != prevMissingAt                                                     ||
            !string.Equals(task.NamingSnapshot, prevNamingSnapshot, StringComparison.Ordinal)             ||
            !string.Equals(task.NamingIngredients, prevNamingIngredients, StringComparison.Ordinal)       ||
            task.Episode != prevEpisode                                                                   ||
            task.Version != prevVersion;
        changeNotifier?.Notify(taskId, affectsListOrderOrMembership);
        return true;
    }

    /// <summary>Add 已生效：把逐文件引用切到种子原始文件名，与待确认根一起描述下载器真实布局。</summary>
    private static async Task StartNewRoundFilesAsync(
        ApplicationDbContext db, Guid taskId, CancellationToken cancellationToken)
    {
        var owner = await db.DownloadTasks.AsNoTracking()
                            .SingleAsync(value => value.Id == taskId, cancellationToken);
        var files = await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == taskId)
                            .ToListAsync(cancellationToken);
        DownloadRoundLayout.StartRound(owner, files);
    }

    private async Task MarkFailedAsync(Guid taskId, DownloadTaskStatus expectedStatus, string step, string error,
                                       CancellationToken cancellationToken, bool releaseSubmission = false,
                                       string? revokeAddRoot = null)
    {
        logger.LogWarning("下载任务 {TaskId} 在步骤 {Step} 失败：{Error}", taskId, step, error);
        await UpdateTaskAsync(taskId, expectedStatus, update =>
                              {
                                  update.Status               = DownloadTaskStatus.Failed;
                                  update.FailedStep           = step;
                                  update.Error                = error.Length > 1000 ? error[..1000] : error;
                                  // 终态任务不再参与到期调度，清掉残留的观察时间。
                                  update.NextObservationAtUtc = null;
                                  // 明确拒绝才收尾释放占用；响应未知时继续占用，直到接管或人工确认。
                                  if (releaseSubmission)
                                  {
                                      update.PendingSubmitSavePath = null;
                                      DownloadRoundLayout.ClearPendingAdds(update);
                                  }
                                  // 只有本次请求被明确拒绝时撤销该根；其他根上的先前请求仍可能生效。
                                  else if (revokeAddRoot is not null)
                                  {
                                      DownloadRoundLayout.RevokePendingAddRoot(update, revokeAddRoot);
                                  }
                              }, cancellationToken,
                              (db, task) =>
                                  notificationEnqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadFailed, "下载失败",
                                                                       $"{DescribeTask(task)}：在「{step}」步骤失败。{error}",
                                                                       cancellationToken));
    }

    /// <summary>通知内容里对任务的描述：优先作品名（中文名优先），缺失时退回命名快照。</summary>
    private static string DescribeTask(DownloadTask task)
    {
        var subjectName = task.Subject is null
            ? null
            : string.IsNullOrWhiteSpace(task.Subject.NameCn)
                ? task.Subject.Name
                : task.Subject.NameCn;
        var episode = task.Episode is null ? string.Empty : $" 第{task.Episode.Value:0.###}集";
        var version = task.Version > 1 ? $"（v{task.Version}）" : string.Empty;
        return $"{subjectName ?? task.NamingSnapshot}{episode}{version}";
    }
}
