using Banned.AniParser;
using Banned.AniParser.Models;
using Banned.Qbittorrent.Exceptions;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     下载闭环：创建意图、提交下载器、对账至完成。
///     提交前后都按 info-hash 对账：下载器中已存在的同 hash 种子（自己上次提交未确认，或用户/外部工具预先添加）
///     直接接管推进，不重复提交；接管时补打来源 tag 并把隔离路径对齐为种子真实位置。
///     提交时随种子设置来源 tag 与命名快照名；数据库唯一约束兜底并发。单条任务失败不影响其他任务，更不影响既有资源。
/// </summary>
public sealed class DownloadService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    DownloadCoordinationGate                coordinationGate,
    ITorrentFileSource                      torrentFileSource,
    IFeedSourceReader                       feedSourceReader,
    IQbittorrentSessionFactory              sessionFactory,
    ManageQbittorrentSettings               qbittorrentSettings,
    NotificationEnqueuer                    notificationEnqueuer,
    AniParser                               aniParser,
    NamingTemplateStore                     namingTemplateStore,
    ReleaseNamePreviewPolicy                namingPolicy,
    CandidateEligibilityPolicy              eligibilityPolicy,
    CandidateRankingPolicy                  rankingPolicy,
    ISelectionRuleProvider                  ruleProvider,
    SubjectMetadataCache                    subjectCache,
    ISubjectLinkResolver                    subjectLinkResolver,
    TimeProvider                            timeProvider,
    ILogger<DownloadService>                logger,
    SubjectEpisodeCorrectionPolicy          episodeCorrectionPolicy,
    DownloadFileService?                    downloadFileService     = null,
    DownloadSubmissionPathValidator?        submissionPathValidator = null,
    DownloadEpisodeResolver?                episodeResolver         = null,
    DownloadTimingPolicy?                   timingPolicy            = null)
{
    /// <summary>响应丢失后的自动重新提交上限；达到上限视为提交失败，等待人工重试。</summary>
    public const int MaxAutoSubmitAttempts = 5;

    /// <summary>所有下载登记的前置门禁：解析 Bangumi 绑定，未绑定返回 null（调用方拒绝下载并要求补 ID）。</summary>
    private const string SubjectUnboundMessage = "未能绑定 Bangumi Subject，请在表单补充 Bangumi ID 后重试。";

    /// <summary>同集择优落选墓碑的终止步骤标记（与登记入口同口径）；该类记录从未提交或接管过下载器资源。</summary>
    private const string SelectionLossStep = "择优";

    /// <summary>能解析出集数的内容文件扩展名：视频与字幕；其余一律视为附属文件。</summary>
    private static readonly string[] ContentFileExtensions =
        [".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".mov", ".flv", ".ass", ".ssa", ".srt", ".sup"];

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
        var       settings          = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return;
        List<DownloadTask> tasks;
        bool               hasRetiredCleanup;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                           task.Status == DownloadTaskStatus.Submitting    ||
                                           task.Status == DownloadTaskStatus.Downloading   ||
                                           task.Status == DownloadTaskStatus.Paused)
                            .ToListAsync(cancellationToken);
            // 存在被替换墓碑时清理环仍需执行：全部任务静默（完成/被替换）不能让冗余种子无限滞留。
            hasRetiredCleanup = await db.DownloadTasks.AsNoTracking()
                                        .AnyAsync(task => task.Status           == DownloadTaskStatus.Replaced &&
                                                          task.ReplacedByTaskId != null, cancellationToken);
        }

        // SQLite 不支持 DateTimeOffset 服务端排序；先创建先处理，在内存中排序。
        tasks = tasks.OrderBy(task => task.CreatedAtUtc).ToList();

        List<(Guid Id, string InfoHash, DateTimeOffset? NextObservationAtUtc)> pendingFileTasks;
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
        }

        if (tasks.Count == 0 && pendingFileTasks.Count == 0 && !hasRetiredCleanup) return;

        await using var session = await sessionFactory.CreateAsync(cancellationToken);

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
                                                task.ReplacedByTaskId != null)
                                 .ToListAsync(cancellationToken);
        }

        foreach (var oldTask in superseded)
            try
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
                if (oldTask.SubmittedAtUtc is null &&
                    oldTask is { AttemptCount: 0, LastSyncedAtUtc: null, CompletedAtUtc: null })
                    continue;
                var stillOwned = await IsActiveTaskForHashAsync(oldTask.InfoHash, oldTask.Id, cancellationToken);
                if (stillOwned) continue;
                var foundOld = await session.FindTorrentsAsync([oldTask.InfoHash], cancellationToken);
                if (foundOld.Count == 0) continue;
                // 对所有待清理任务核对实际根与确认根（统一规范化比较）：受管种子被外部移动、
                // 或同 hash 外部种子位于别处时一律保守保留，路径异常交由人工或后续轮次处理。
                if (foundOld.Any(snapshot => !MatchesIsolationPath(snapshot.SavePath, oldTask.SavePath)))
                    continue;
                await session.DeleteTorrentAsync(oldTask.InfoHash, false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "清理被顶替任务 {TaskId} 的下载器资源失败，下一轮重试。", oldTask.Id);
            }

        var reconcileHashes = tasks
                             .Where(task => task.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)
                             .Select(task => task.InfoHash)
                             .ToList();
        var foundByHash = (await session.FindTorrentsAsync(reconcileHashes, cancellationToken))
                         .GroupBy(snapshot => snapshot.Hash)
                         .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks.Where(task => task.Status is DownloadTaskStatus.Downloading
                                                             or DownloadTaskStatus.Paused))
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
            update.Status       = DownloadTaskStatus.Paused;
            update.IsUserPaused = true;
            // 页面发起的人工暂停来源明确，覆盖此前可能存在的外部停止观察记录。
            update.ExternalStopObservedAtUtc = null;
        }, cancellationToken);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status != DownloadTaskStatus.Paused || !task.IsUserPaused)
            throw new InvalidOperationException("只有人工暂停的任务可以恢复。");

        // 准备流程未完成时，恢复只解除人工暂停：不直接启动下载器——否则落选文件会绕过
        // 文件选择计划直接开始下载。任务回到待准备状态，由对账继续推进并在准备完成后启动。
        if (task.IsSystemPaused)
        {
            await UpdateTaskAsync(taskId, task.Status, update =>
            {
                update.Status       = DownloadTaskStatus.Downloading;
                update.IsUserPaused = false;
                update.ExternalStopObservedAtUtc = null;
            }, cancellationToken);
            return;
        }

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.ResumeAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.Downloading;
            update.IsUserPaused = false;
            update.ExternalStopObservedAtUtc = null;
        }, cancellationToken);
    }

    /// <summary>人工重试：失败或丢失的任务重新进入待提交队列，沿用原决策与命名快照。</summary>
    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status is not (DownloadTaskStatus.Failed or DownloadTaskStatus.Lost))
            throw new InvalidOperationException("只有失败或丢失的任务可以重试。");

        if (DownloadVerificationIntent.FromJson(task.VerificationIntent) is { } intent)
            await EnterIsolationAsync(task, intent, "人工重试校验失败的任务，废弃旧证据并转为独立下载。", cancellationToken);

        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status                      = DownloadTaskStatus.PendingSubmit;
            update.AttemptCount                = 0;
            update.FailedStep                  = null;
            update.Error                       = null;
            // 人工重试开启新的受控轮次：布局与选择请求台账重置（含预算耗尽的任务），
            // 未收敛的改名/选择计划按确认窗口与全新预算重新推进。
            update.PendingLayoutRequestedAtUtc = null;
            update.PendingLayoutRequestCount   = 0;
            update.SelectionRequestCount       = 0;
            update.NextObservationAtUtc        = null;
        }, cancellationToken);
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
    public async Task DeleteAsync(Guid              taskId, bool deleteFromDownloader, bool deleteFiles,
                                  CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       task              = await LoadTaskAsync(taskId, cancellationToken);

        if (deleteFromDownloader)
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            await session.DeleteTorrentAsync(task.InfoHash, deleteFiles, cancellationToken);
        }

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var tracked = await db.DownloadTasks.SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
            if (tracked is null) return;

            await ClearReplacementReferencesAsync(db, [taskId], cancellationToken);
            db.DownloadTasks.Remove(tracked);
            await db.SaveChangesAsync(cancellationToken);
        }

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

        int deletedCount;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var ids     = tasks.Select(task => task.Id).ToList();
            var tracked = await db.DownloadTasks.Where(task => ids.Contains(task.Id)).ToListAsync(cancellationToken);
            deletedCount = tracked.Count;

            await ClearReplacementReferencesAsync(db, ids, cancellationToken);
            db.DownloadTasks.RemoveRange(tracked);
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("已批量删除 {Count} 个下载任务（删除器种子：{DeleteTorrent}，连带文件：{DeleteFiles}）。",
                              deletedCount, deleteFromDownloader, deleteFiles);
        return deletedCount;
    }

    /// <summary>清理指向待删任务的替换引用：替换者被删的旧任务回退为已完成，其余任务对待删任务的替换声明置空；逐集承接的文件在所属任务仍活动时恢复下载职责；每集登记表回退待完成与现任引用。</summary>
    private async Task ClearReplacementReferencesAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> deletedIds, CancellationToken cancellationToken)
    {
        var referencing = await db.DownloadTasks
                                  .Where(other => (other.ReplacesTaskId != null &&
                                                   deletedIds.Contains(other.ReplacesTaskId.Value)) ||
                                                  (other.ReplacedByTaskId != null &&
                                                   deletedIds.Contains(other.ReplacedByTaskId.Value)))
                                  .ToListAsync(cancellationToken);
        foreach (var other in referencing)
        {
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
        if (restoreCandidates.Count == 0) return;
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
        }
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
            if (episode is not { } key || byEpisode.ContainsKey(key)) continue;
            byEpisode[key] = video;
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
        var incumbentGuards = new Dictionary<Guid, (int Version, DownloadTaskStatus Status, Guid? ReplacedByTaskId)>();
        foreach (var row in rows.Where(row => row.IncumbentVideoFileId is not null))
        {
            var fileId = row.IncumbentVideoFileId!.Value;
            var owner  = incumbentOwners.FirstOrDefault(candidate => candidate.Files.Any(file => file.Id == fileId));
            var file   = owner?.Files.FirstOrDefault(candidate => candidate.Id == fileId);
            if (owner is null || file is null) continue;
            incumbentGuards[fileId] = (DownloadTaskLookups.EpisodeFileVersion(aniParser, owner, file, row.Episode),
                                       owner.Status, owner.ReplacedByTaskId);
        }

        var retiredOwnerIds = new List<Guid>();
        foreach (var (episode, video) in byEpisode)
        {
            var row = rows.FirstOrDefault(row => row.Episode == episode);
            if (row is null)
            {
                row = new EpisodeVersion { SubjectId = subjectId, Episode = episode };
                db.EpisodeVersions.Add(row);
                rows.Add(row);
            }
            else if (row.IncumbentVideoFileId is { } incumbent &&
                     (row.PendingVideoFileId is not { } pending || !fileIds.Contains(pending)))
            {
                // 本任务不是登记在案的待完成优胜（未在对决中胜出，或属于不互相竞争的来源）。
                // 例外沿用条目链完成期替换的版本守卫（判定集中在 DownloadTaskLookups，与登记期
                // 同一实现）：现任所属任务已完成且版本不高于本集正片文件时仍允许切换——覆盖两个订阅
                // 条目任务先后完成、登记时互相豁免的场景。
                if (!incumbentGuards.TryGetValue(incumbent, out var guard) ||
                    !DownloadTaskLookups.IsTakeoverEligibleIncumbent(guard.Status, guard.ReplacedByTaskId,
                                                                     guard.Version,
                                                                     DownloadTaskLookups.EpisodeFileVersion(aniParser,
                                                                         task, video,
                                                                         episode, taskFiles)))
                    continue;
            }

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

        // 冗余退出评估：被顶替的旧现任职责可能就此全部耗尽——已完成旧任务不再豁免（设计
        // 第四阶段），最后一项职责退出后即翻转为被替换，由对账清理环统一保留内容移除。
        await TryRetireExhaustedOwnersAsync(db, retiredOwnerIds, task.Id, cancellationToken);
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

            var rows = await db.EpisodeVersions.Where(row => row.SubjectId == subjectId).ToListAsync(ct);
            if (DownloadTaskLookups.HasResidualDuties(aniParser, owner, owner.Files.ToList(), rows,
                                                      episodeResolver.EpisodeOf))
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
        if (task.Status is not (DownloadTaskStatus.PendingSubmit or DownloadTaskStatus.Submitting)) return;

        // 提交前先按 hash 对账：下载器中已存在的同 hash 种子直接接管——既覆盖“自己上次提交已生效
        // 但未收到确认”，也覆盖“用户或其他工具预先添加的种子”。数据库活动唯一索引保证同 hash
        // 至多一个活动任务，接管不会与自身其他任务互相抢占。
        var existing = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken))
           .FirstOrDefault();
        // 响应丢失或人工重试仍须恢复原状态机，不能按普通接管改名、移动或完成共享候选。
        if (task.IsolationState != DownloadIsolationState.None || task.VerificationIntent is not null)
        {
            await UpdateTaskAsync(task.Id, task.Status, update => update.Status = DownloadTaskStatus.Downloading,
                                  cancellationToken);
            await ReconcileTrackedAsync(session, await LoadTaskAsync(task.Id, cancellationToken), existing,
                                        cancellationToken);
            return;
        }

        if (existing is not null)
        {
            await AdoptExistingAsync(task, existing, session, cancellationToken);
            return;
        }

        if (task is { Status: DownloadTaskStatus.Submitting, AttemptCount: >= MaxAutoSubmitAttempts })
        {
            await MarkFailedAsync(task.Id, task.Status, "提交", "多次提交后仍未在下载器中发现该任务，请检查下载器后人工重试。",
                                  cancellationToken);
            return;
        }

        // 添加可见性窗口内不重复提交：上次 Add 请求可能仍被下载器处理中（或响应结果未知），
        // hash 对账未命中先等待窗口到期；查询轮次与 pulse 不消耗提交预算。
        if (task is { Status: DownloadTaskStatus.Submitting, LastSubmitAttemptAtUtc: { } attemptedAt } &&
            timeProvider.GetUtcNow() < attemptedAt + timing.AddVisibilityWindow)
        {
            await DeferObservationAsync(task.Id, task.Status,
                                        timeProvider.GetUtcNow() + timing.PreparationSampleInterval, cancellationToken);
            return;
        }

        // 一个规划流程：已有候选先校验；需要替换旧版时暂存；其余任务直接在正式根下载。
        (string CandidateRoot, string IntentJson)? verificationAdd;
        string                                     addRoot;
        var                                        replacing = false;
        try
        {
            verificationAdd = await TryPlanVerificationAddAsync(session, task, cancellationToken);
            replacing = task.ReplacesTaskId is not null || (downloadFileService is not null &&
                                                            await downloadFileService
                                                               .HasReplacementAsync(task.Id, cancellationToken));
            addRoot = verificationAdd?.CandidateRoot ??
                      (string.IsNullOrWhiteSpace(task.DownloadRootPath) ? task.SavePath :
                          replacing ? DownloadTaskPlacement.TaskRoot(task.DownloadRootPath, task.Id) :
                                      task.DownloadRootPath);
            task.SavePath = addRoot;
            if (verificationAdd is null                   && !string.IsNullOrWhiteSpace(task.DownloadRootPath) &&
                !DownloadTaskPlacement.IsTaskScoped(task) && downloadFileService is not null                   &&
                await InitialLayoutConflictsAsync(session,
                                                  await LoadTaskWithFilesAsync(task.Id, cancellationToken) ?? task,
                                                  addRoot, cancellationToken))
                throw new TargetPathOccupiedException("种子的原始布局被其他任务占用，不能在该目录直接添加，请先处理冲突。");
        }
        catch (InvalidOperationException exception)
        {
            await MarkFailedAsync(task.Id, task.Status, "提交路径", exception.Message, cancellationToken);
            return;
        }

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
                    ? "存在待替换旧版，先在 .tasks 任务目录暂存，完成后统一归档替换。"
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
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.Status                 =   DownloadTaskStatus.Submitting;
            update.AttemptCount           +=  1;
            update.SubmittedAtUtc         ??= timeProvider.GetUtcNow();
            update.LastSubmitAttemptAtUtc =   timeProvider.GetUtcNow();
            update.IsSystemPaused         =   true;
            update.PreparationStage       =   DownloadPreparationStage.Planned;
            update.SavePath               =   addRoot;
            update.PathDecision           =   pathDecision.ToJson();
            if (verificationAdd is not null)
            {
                update.SavePath           = verificationAdd.Value.CandidateRoot;
                update.VerificationIntent = verificationAdd.Value.IntentJson;
            }
        }, cancellationToken);

        string response;
        try
        {
            response = await session.AddTorrentAsync(task.TorrentFile, addRoot,
                                                     TagsFor(task), task.NamingSnapshot,
                                                     true, cancellationToken);
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
            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝了提交：{exception.Message}",
                                  cancellationToken);
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

            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝该种子：{responseText}",
                                  cancellationToken);
            return;
        }

        await UpdateTaskAsync(task.Id, DownloadTaskStatus.Submitting,
                              update => { update.Status = DownloadTaskStatus.Downloading; },
                              cancellationToken,
                              (db, updated) =>
                                  notificationEnqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted,
                                                                       "下载开始",
                                                                       $"{DescribeTask(updated)}：已提交到下载器开始下载。命名：{updated.NamingSnapshot}",
                                                                       cancellationToken));

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
        if (candidates.Count == 0) return null;

        var candidateRoot = DownloadPathNamePolicy.NormalizePath(task.DownloadRootPath ?? task.SavePath);

        var withFiles = await LoadTaskWithFilesAsync(task.Id, ct);
        if (withFiles is null) return null;
        if (await InitialLayoutConflictsAsync(session, withFiles, candidateRoot, ct,
                                              candidates.GroupBy(item => item.AbsolutePath,
                                                                 StringComparer.OrdinalIgnoreCase)
                                                        .ToDictionary(group => group.Key, group => group.First().Size,
                                                                      StringComparer.OrdinalIgnoreCase)))
            throw new TargetPathOccupiedException("校验任务的原始布局存在非候选占用，无法安全添加。");
        var replacing = await downloadFileService.HasReplacementAsync(task.Id, ct);

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
            Round         = 1, Step = DownloadVerificationStep.Mapping, CreatedAtUtc = timeProvider.GetUtcNow(),
            CandidateRoot = candidateRoot, Files = files
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
                                     DownloadPathNamePolicy.JoinSavePath(candidateRoot, relative),
                                     DownloadPathNamePolicy.JoinSavePath(candidateRoot,
                                                                         $".tasks-{task.Id:N}/{relative}")
                                 };
                             })
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<DownloadTask> others;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            others = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .Where(value => value.Id != task.Id).ToListAsync(ct);
        }

        foreach (var other in others)
        {
            var occupiedPaths = new List<string>();
            if (other.Files.Count == 0) occupiedPaths.Add(other.SavePath);
            foreach (var file in other.Files)
            {
                occupiedPaths.Add(DownloadPathNamePolicy.JoinSavePath(other.SavePath, file.CurrentRelativePath));
                foreach (var pending in new[] { file.PendingRenameFrom, file.PendingRenameTo }
                            .Where(value => value is not null))
                    occupiedPaths.Add(DownloadPathNamePolicy.JoinSavePath(other.SavePath, pending!));
                if (other.PendingSavePath is null) continue;
                occupiedPaths.Add(DownloadPathNamePolicy.JoinSavePath(other.PendingSavePath, file.CurrentRelativePath));
                occupiedPaths.Add(DownloadPathNamePolicy.JoinSavePath(other.PendingSavePath,
                                                                      file.OriginalRelativePath));
            }

            if (occupiedPaths.Any(path => layout.Any(item => PathsOverlapAbsolute(item, path)) &&
                                          (candidates is null || !candidates.ContainsKey(path)))) return true;
        }

        foreach (var other in await session.ListTorrentsAsync(null, ct))
        {
            if (string.Equals(other.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var root = DownloadPathNamePolicy.NormalizePath(other.SavePath).TrimEnd('/');
                foreach (var file in await session.ListFilesAsync(other.Hash, ct))
                {
                    var occupied = root + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                    if (layout.Any(item => PathsOverlapAbsolute(item, occupied) &&
                                           !(EqualVerificationPath(item, occupied)          && candidates is not null &&
                                             candidates.TryGetValue(occupied, out var size) && size == file.Size)))
                        return true;
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
        Guid taskId, DownloadTaskStatus expectedStatus, DownloadVerificationIntent intent, CancellationToken ct) =>
        await UpdateTaskAsync(taskId, expectedStatus,
                              update => update.VerificationIntent = intent.ToJson(), ct);

    /// <summary>
    ///     推迟本任务的下一次观察：等待确认窗口或采样间隔到期。到期时间持久化，重启后仍按此调度；
    ///     到期前的对账轮次跳过本任务，不影响其他任务。
    /// </summary>
    private Task DeferObservationAsync(Guid taskId, DownloadTaskStatus expectedStatus, DateTimeOffset dueUtc,
                                       CancellationToken ct) =>
        UpdateTaskAsync(taskId, expectedStatus, update => update.NextObservationAtUtc = dueUtc, ct);

    /// <summary>
    ///     添加可见性窗口判断：请求已发出（或响应结果未知）且从未在下载器观察到该种子时，短暂查不到
    ///     只推迟观察，不触发 Lost/隔离；窗口过后仍缺失按原有缺失语义处理。窗口按最近一次 Add 请求计算，
    ///     重试成功后获得全新的窗口，不沿用首次提交时间。
    /// </summary>
    private bool WithinAddVisibilityWindow(DownloadTask task)
    {
        if (task.LastSyncedAtUtc is not null) return false;
        return LatestAddRequestAt(task) is { } at && timeProvider.GetUtcNow() < at + timing.AddVisibilityWindow;
    }

    /// <summary>最近一次 Add 请求时间（首次提交或之后的重试/隔离重加）；null 表示从未发起。</summary>
    private static DateTimeOffset? LatestAddRequestAt(DownloadTask task) =>
        task.SubmittedAtUtc is { } submitted && task.LastSubmitAttemptAtUtc is { } attempted
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
    private static DownloadVerificationIntent VoidStabilitySamples(
        DownloadVerificationIntent intent) => intent with
    {
        StabilitySample        = null,
        StabilityStartedAtUtc  = null,
        StabilityObservedAtUtc = null,
        StabilitySampleAtUtc   = null,
        StabilitySampleCount   = 0
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
        Step                      = step,
        ObservedChecking          = false,
        RecheckRequestedAtUtc      = null,
        DeadlineUtc               = null,
        MovingObservedAtUtc        = null,
        CheckStartedAtUtc          = null,
        LastCheckProgressAtUtc     = null,
        LastCheckProgress          = null,
        StabilitySample           = null,
        StabilityStartedAtUtc      = null,
        StabilityObservedAtUtc     = null,
        StabilitySampleAtUtc       = null,
        StabilitySampleCount       = 0,
        TimeoutStopRequestedAtUtc  = null,
        TimeoutStopAttempts        = 0
    };

    /// <summary>
    ///     活跃校验的推进驱动。快照无法区分「谁启动了运行」，校验期出现无法解释的运行状态一律先暂停；
    ///     人工暂停意图保留（IsUserPaused 全程不动）。完成证据：当前轮次观察到 Checking 且结束、
    ///     映射未变、无移动、无错误，并在停止状态读取文件结果。仅做种收尾的暂停不单独作废证据；
    ///     未确认共享数据时出现下载则保留风险，即使已停止也不得沿用旧证据。已落库的启动意图单独恢复。
    /// </summary>
    private async Task AdvanceVerificationAsync(IQbittorrentSession session,  DownloadTask               task,
                                                TorrentSnapshot?    snapshot, DownloadVerificationIntent intent,
                                                CancellationToken   ct)
    {
        var loaded = await LoadTaskWithFilesAsync(task.Id, ct);
        if (loaded?.Status is not (DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)) return;
        task = loaded;
        var readyToRun = intent.Step == DownloadVerificationStep.ReadyToRun;

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
            !MatchesIsolationPath(task.SavePath, intent.CandidateRoot)                           ||
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
        if (snapshot is { Category: QbittorrentTorrentCategory.Checking } or { IsMoving: true })
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
                    CheckStartedAtUtc      = now,
                    LastCheckProgressAtUtc = intent.LastCheckProgressAtUtc ?? now,
                    LastCheckProgress      = intent.LastCheckProgress ?? snapshot.Progress
                };

            // 已作废轮次的残留校验不可归属任何请求（作废未伴随停止请求记录时首遇即发出）：
            // 停止确认的期限与预算接管——窗口内等待、有界补发、耗尽受阻；停稳后由补发路径
            // 重新发起可信校验，不得无限等待一个已作废的校验自然结束。
            if (intent is { Step: DownloadVerificationStep.Requested, RecheckRequestedAtUtc: null } &&
                intent.TimeoutStopRequestedAtUtc is null)
            {
                await IssueTimeoutStopAsync();
                return;
            }

            // 请求前观察到的外部校验不能充当本轮请求的完成证据：只归属本轮请求的 Checking 记为证据。
            if (intent.Step != DownloadVerificationStep.Mapping &&
                intent is { IssuedCount: > 0, RecheckRequestedAtUtc: not null, ObservedChecking: false })
                intent = intent with
                {
                    ObservedChecking       = true,
                    Step                   = DownloadVerificationStep.Observing,
                    CheckStartedAtUtc      = intent.CheckStartedAtUtc ?? now,
                    LastCheckProgressAtUtc = intent.LastCheckProgressAtUtc ?? now,
                    LastCheckProgress      = intent.LastCheckProgress ?? snapshot.Progress
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
        if (snapshot is { Category: QbittorrentTorrentCategory.Error })
        {
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
                                                      PauseAttempts       = intent.PauseAttempts + 1,
                                                      PauseRequestedAtUtc = pausedAt,
                                                      UnverifiedWriteRisk = NextUnverifiedWriteRisk(intent, snapshot.Category)
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

            // 映射确认完成：先落库本轮请求记录（作废上一轮观察与稳定样本），再发出 recheck。
            var requested = ResetRoundEvidence(intent, DownloadVerificationStep.Requested) with
            {
                IssuedCount           = intent.IssuedCount + 1,
                RecheckRequestedAtUtc = timeProvider.GetUtcNow(),
                DeadlineUtc           = timeProvider.GetUtcNow() + timing.CheckingStartWindow
            };
            await SaveVerificationIntentAsync(task.Id, task.Status, requested, ct);
            await UpdateTaskAsync(task.Id, task.Status,
                                  update => update.PreparationStage = DownloadPreparationStage.AwaitingVerification,
                                  ct);
            await session.RecheckTorrentAsync(task.InfoHash, ct);
            return;
        }

        if (!intent.ObservedChecking)
        {
            // 尚未观察到 Checking：校验启动窗口内持续快速观察（不因非 Checking 立即下结论，也不睡到窗口末）；
            // 窗口过期且有界预算未耗尽时补发一次并重置本轮证据；仍无则保留「校验执行未确认」——
            // 不凭重复 100% 证明校验执行，也不因漏采 Checking 断言数据损坏、删除数据或自动重新下载。
            if (intent.DeadlineUtc is { } deadline && now < deadline)
            {
                await DeferObservationAsync(task.Id, task.Status, now + timing.PreparationSampleInterval, ct);
                return;
            }

            if (intent.IssuedCount < timing.MaxRecheckIssues)
            {
                var reissued = ResetRoundEvidence(intent, DownloadVerificationStep.Requested) with
                {
                    IssuedCount           = intent.IssuedCount + 1,
                    RecheckRequestedAtUtc = now,
                    DeadlineUtc           = now + timing.CheckingStartWindow
                };
                await SaveVerificationIntentAsync(task.Id, task.Status, reissued, ct);
                await session.RecheckTorrentAsync(task.InfoHash, ct);
                return;
            }

            await MarkFailedAsync(task.Id, task.Status, "复用校验",
                                  "多次请求校验后仍未观察到校验阶段，本轮校验执行未确认，已停止自动推进；请核对下载器后人工重试（重试将放弃复用并转为独立下载）。",
                                  ct);
            return;
        }

        // 本轮校验曾被主动停止（无进展或单轮上限到期后发出的停止请求）：校验执行情况已不可确认——
        // 停止后读到的文件结果可能是中断时刻的部分结果或旧快照，既不能登记复用，也不能确认缺失。
        if (!readyToRun && intent is { ObservedChecking: true, TimeoutStopRequestedAtUtc: not null })
        {
            await MarkFailedAsync(task.Id, task.Status, "复用校验",
                                  "校验因到期被主动停止，本轮校验执行未确认，已停止自动推进；请核对下载器后人工重试（重试将放弃复用并转为独立下载）。",
                                  ct);
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
                !remoteByIndex.TryGetValue(qbIndex, out var remote) ||
                remote.Size != mapping.Size ||
                remote.IsSelectedForDownload != mapping.Selected ||
                (readyToRun && mapping.IsCandidate &&
                 (file.VerificationOutcome != DownloadFileVerificationOutcomes.ReusedVerified ||
                  !EqualVerificationPath(file.VerificationPath ?? string.Empty, mapping.VerificationPath))) ||
                !EqualVerificationPath(JoinAbsolute(task.SavePath, file.CurrentRelativePath),
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
            // 回读发现移动：与入口观察同样处理——本轮校验后的移动可能已改变目标，整轮证据作废、
            // 回到等待重新校验；移动结束后由补发路径重新发起，不得沿用移动前的 Checking 与文件结果。
            // 作废保留移动等待起点：这里首次看到的移动同样按首次观察时刻独立计时。
            if (stable is { IsMoving: true })
            {
                await SaveVerificationIntentAsync(task.Id, task.Status,
                                                  ResetRoundEvidence(intent, DownloadVerificationStep.Requested) with
                                                  {
                                                      MovingObservedAtUtc = intent.MovingObservedAtUtc ?? now
                                                  }, ct);
                return;
            }

            // 只在这里看到的快速起止校验同样作废旧稳定序列：中断后的首个同内容样本
            // 不得沿用中断前的样本数直接凑满下结论。
            if (stable is { Category: QbittorrentTorrentCategory.Checking } && intent.StabilitySample is not null)
                await SaveVerificationIntentAsync(task.Id, task.Status, VoidStabilitySamples(intent), ct);

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
                    await MarkFailedAsync(task.Id, task.Status, "复用校验",
                                          "校验后的文件结果长时间无法稳定确认，已停止自动推进，请核对下载器后人工重试。", ct);
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
            }

            // 包含承接的提前切换（设计第四阶段）：候选数据已在校验目标路径（即归档终名）证实完整，
            // 本任务登记的待完成优胜不必等整包完成即可承接现任——大包新集仍在下载时旧包职责即可
            // 退出。切换后评估旧占用者职责是否全部耗尽，与证据同一事务落库。
            var retiredOwnerIds = new List<Guid>();
            if (tracked.SubjectId is { } verificationSubjectId)
            {
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

            // 启动意图与证据一起落库。Resume 响应丢失时仍走本状态机，不能落回普通提前命名。
            tracked.VerificationIntent = (intent with { Step = DownloadVerificationStep.ReadyToRun }).ToJson();
            await db.SaveChangesAsync(ct);
        }

        // 完整任务直接完成，保留分享限制或外部停止；部分完成只在仍停止时启动独立缺集。
        // ReadyToRun 已在运行表示上次 Resume 已生效（可能丢响应），不能先暂停再重复启动。
        if (!allSelectedComplete && stable.Category == QbittorrentTorrentCategory.Stopped)
            await session.ResumeAsync(task.InfoHash, ct);
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.IsSystemPaused       = false;
            update.VerificationIntent   = null;
            update.PreparationStage     = DownloadPreparationStage.ReadyToRun;
            update.LastSyncedAtUtc      = timeProvider.GetUtcNow();
            update.NextObservationAtUtc = null;
        }, ct);
        if (allSelectedComplete)
            await ApplySnapshotAsync(task.Id, task.Status, stable with { Progress = 1.0 }, session, ct);
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
            if (remote.Progress > 0 && !EqualVerificationPath(JoinAbsolute(task.SavePath, remote.Path),
                                                              JoinAbsolute(task.SavePath,
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
                    // 删除已生效（或从未存在）：前进到重加。
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
                await UpdateTaskAsync(task.Id, task.Status,
                                      update => update.IsolationState = DownloadIsolationState.Readding, ct);
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

                    // 请求登记先落库，再发出重加。
                    var readdAt = timeProvider.GetUtcNow();
                    await UpdateTaskAsync(task.Id, task.Status,
                                          update => update.LastSubmitAttemptAtUtc = readdAt, ct);
                    await session.AddTorrentAsync(task.TorrentFile, targetRoot, TagsFor(task), task.NamingSnapshot,
                                                  true, ct);
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
                    foreach (var file in tracked.Files)
                    {
                        file.CurrentRelativePath = file.OriginalRelativePath;
                        file.PendingRenameFrom   = null;
                        file.PendingRenameTo     = null;
                        file.UpdatedAtUtc        = timeProvider.GetUtcNow();
                    }

                    tracked.SavePath         = targetRoot;
                    tracked.PendingSavePath  = null;
                    tracked.IsolationState   = DownloadIsolationState.None;
                    tracked.IsSystemPaused   = true;
                    tracked.PreparationStage = DownloadPreparationStage.Planned;
                    await db.SaveChangesAsync(ct);
                }

                logger.LogInformation("任务 {TaskId} 隔离完成，已在任务专属根重新添加并回到准备流程。", task.Id);
                return;
        }
    }

    /// <summary>接管下载器中已存在的同 hash 种子：补打来源 tag、把本地隔离路径对齐为种子真实位置，再按快照推进。</summary>
    private async Task AdoptExistingAsync(
        DownloadTask task, TorrentSnapshot snapshot, IQbittorrentSession session, CancellationToken cancellationToken)
    {
        var siteSource = task.SiteSource;
        if (!string.IsNullOrWhiteSpace(siteSource) &&
            !snapshot.Tags.Contains(siteSource, StringComparer.OrdinalIgnoreCase))
            await session.AddTagAsync(task.InfoHash, siteSource, cancellationToken);

        // 接管即路径决策：对齐为种子真实位置，原因落库可追溯。
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.PathDecision = new DownloadPathDecision
            {
                Kind         = DownloadPathDecisionKinds.SameHashAdoption,
                Reason       = "下载器已存在同 Hash 种子，接管其真实保存位置。",
                Root         = DownloadPathNamePolicy.NormalizePath(snapshot.SavePath),
                DecidedAtUtc = timeProvider.GetUtcNow()
            }.ToJson();
            if (!MatchesIsolationPath(snapshot.SavePath, task.SavePath))
                // 隔离路径让位于种子的真实位置：接管外部种子时它通常不在本应用约定的目录里，
                // 后续对账与完成改名都以真实位置为准。
                update.SavePath = DownloadPathNamePolicy.NormalizePath(snapshot.SavePath);
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
        // 路由优先级：隔离恢复 → 活跃校验 → 普通缺失/接管/命名/完成处理。
        // 隔离中 hash 暂时消失是正常中间态，绝不标 Lost；校验意图活跃期间不得进入普通
        // 命名、完成判定或现任切换——候选路径上的共享数据未经证实不得写入。
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
                                                                     EqualVerificationPath(JoinAbsolute(task.SavePath,
                                                                             file.CurrentRelativePath),
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
                update.Error      = "下载器中不存在该任务，可能已被手动删除。";
            }, cancellationToken);
            return;
        }

        // 保存位置变更未确认时，两端路径均可能被下载器返回。
        if (!MatchesIsolationPath(snapshot.SavePath, task.SavePath) &&
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
                Category: QbittorrentTorrentCategory.Downloading or QbittorrentTorrentCategory.Stopped
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
                Category: QbittorrentTorrentCategory.Downloading or QbittorrentTorrentCategory.Stopped
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
                    update.IsSystemPaused         = false;
                    update.Progress               = snapshot.Progress;
                    update.TotalSize              = snapshot.TotalSize;
                    update.LastSyncedAtUtc        = timeProvider.GetUtcNow();
                    update.LastObservedCategory   = snapshot.Category;
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
            if (string.IsNullOrWhiteSpace(ingredients?.EpisodeText)) ingredients = null;
            targetName  = ingredients is not null ? namingPolicy.Create(ingredients) : task.NamingSnapshot;
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
                        : namingPolicy.Create(singleIngredients, true);
                    await session.RenameTorrentFileAsync(snapshot.Hash, fileNames[0],
                                                         AppendExtension(singleName, extension), cancellationToken);
                }
                else if (ingredients is not null)
                {
                    // 多文件种子：逐文件按各自集数改名，附属文件挪进 Seed/。
                    await RenameContentFilesAsync(session, snapshot.Hash, fileNames, ingredients, subjectId,
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

        trackedTask.Status          = DownloadTaskStatus.Completed;
        trackedTask.Progress        = 1;
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

        update(task);
        if (onApplied is not null) await onApplied(db, task);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task MarkFailedAsync(Guid taskId, DownloadTaskStatus expectedStatus, string step, string error,
                                       CancellationToken cancellationToken)
    {
        logger.LogWarning("下载任务 {TaskId} 在步骤 {Step} 失败：{Error}", taskId, step, error);
        await UpdateTaskAsync(taskId, expectedStatus, update =>
                              {
                                  update.Status               = DownloadTaskStatus.Failed;
                                  update.FailedStep           = step;
                                  update.Error                = error.Length > 1000 ? error[..1000] : error;
                                  // 终态任务不再参与到期调度，清掉残留的观察时间。
                                  update.NextObservationAtUtc = null;
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
