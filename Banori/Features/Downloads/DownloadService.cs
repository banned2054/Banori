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
    DownloadSubmissionPathValidator?        submissionPathValidator = null)
{
    /// <summary>响应丢失后的自动重新提交上限；达到上限视为提交失败，等待人工重试。</summary>
    public const int MaxAutoSubmitAttempts = 5;

    /// <summary>所有下载登记的前置门禁：解析 Bangumi 绑定，未绑定返回 null（调用方拒绝下载并要求补 ID）。</summary>
    private const string SubjectUnboundMessage = "未能绑定 Bangumi Subject，请在表单补充 Bangumi ID 后重试。";

    /// <summary>能解析出集数的内容文件扩展名：视频与字幕；其余一律视为附属文件。</summary>
    private static readonly string[] ContentFileExtensions =
        [".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".mov", ".flv", ".ass", ".ssa", ".srt", ".sup"];


    /// <summary>一轮对账：提交待提交任务、核对未确认与下载中任务。下载器未启用或未配置时静默跳过。</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var       settings          = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return;
        List<DownloadTask> tasks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                           task.Status == DownloadTaskStatus.Submitting    ||
                                           task.Status == DownloadTaskStatus.Downloading   ||
                                           task.Status == DownloadTaskStatus.Paused)
                            .ToListAsync(cancellationToken);
        }

        // SQLite 不支持 DateTimeOffset 服务端排序；先创建先处理，在内存中排序。
        tasks = tasks.OrderBy(task => task.CreatedAtUtc).ToList();

        List<(Guid Id, string InfoHash)> pendingFileTasks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            pendingFileTasks = downloadFileService is null
                ? []
                : await db.DownloadTasks.AsNoTracking()
                          .Where(task => task.PendingSavePath != null ||
                                         task.Files.Any(file => file.PendingRenameTo != null))
                          .Select(task => new ValueTuple<Guid, string>(task.Id, task.InfoHash))
                          .ToListAsync(cancellationToken);
        }

        if (tasks.Count == 0 && pendingFileTasks.Count == 0) return;

        await using var session = await sessionFactory.CreateAsync(cancellationToken);

        // 校验或移动中的种子不得执行冲突计划（改名/移动）：恢复链先看快照状态，
        // 处于 Checking 或 IsMoving 的任务本轮跳过，待其稳定后再恢复。
        Dictionary<string, TorrentSnapshot> pendingSnapshots;
        if (pendingFileTasks.Count == 0)
            pendingSnapshots = new Dictionary<string, TorrentSnapshot>(StringComparer.OrdinalIgnoreCase);
        else
            pendingSnapshots = (await session.FindTorrentsAsync(pendingFileTasks.Select(task => task.InfoHash).ToList(),
                                                                cancellationToken))
                              .GroupBy(snapshot => snapshot.Hash, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(group => group.Key, group => group.First(),
                                            StringComparer.OrdinalIgnoreCase);

        foreach (var (taskId, infoHash) in pendingFileTasks)
            try
            {
                if (pendingSnapshots.GetValueOrDefault(infoHash) is
                    { Category: QbittorrentTorrentCategory.Checking } or { IsMoving: true })
                    continue;

                await downloadFileService!.RecoverPendingUnderGateAsync(session, taskId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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

        // 登记期替换只落库，不在登记事务内等待 qB；在全局协调门内清理被替换且未完成的资源。
        // 单项清理失败只记录日志，下一轮按同样的未完成记录继续，不阻塞本轮提交与核对。
        List<DownloadTask> superseded;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            superseded = await db.DownloadTasks.AsNoTracking()
                                 .Where(task => task.Status           == DownloadTaskStatus.Replaced &&
                                                task.ReplacedByTaskId != null                        &&
                                                task.CompletedAtUtc   == null)
                                 .ToListAsync(cancellationToken);
        }

        foreach (var oldTask in superseded)
            try
            {
                var stillOwned = await IsActiveTaskForHashAsync(oldTask.InfoHash, oldTask.Id, cancellationToken);
                if (stillOwned) continue;
                var foundOld = await session.FindTorrentsAsync([oldTask.InfoHash], cancellationToken);
                if (foundOld.Count > 0)
                    await session.DeleteTorrentAsync(oldTask.InfoHash, true, cancellationToken);
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
                await ReconcileTrackedAsync(session, task, foundByHash.GetValueOrDefault(task.InfoHash),
                                            cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status != DownloadTaskStatus.Downloading) throw new InvalidOperationException("只有下载中的任务可以暂停。");

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.PauseAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.Paused;
            update.IsUserPaused = true;
        }, cancellationToken);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);
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
            }, cancellationToken);
            return;
        }

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.ResumeAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.Downloading;
            update.IsUserPaused = false;
        }, cancellationToken);
    }

    /// <summary>人工重试：失败或丢失的任务重新进入待提交队列，沿用原决策与命名快照。</summary>
    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status is not (DownloadTaskStatus.Failed or DownloadTaskStatus.Lost))
            throw new InvalidOperationException("只有失败或丢失的任务可以重试。");

        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.PendingSubmit;
            update.AttemptCount = 0;
            update.FailedStep   = null;
            update.Error        = null;
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

    /// <summary>清理指向待删任务的替换引用：替换者被删的旧任务回退为已完成，其余任务对待删任务的替换声明置空。</summary>
    private static async Task ClearReplacementReferencesAsync(
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
                if (other.Status == DownloadTaskStatus.Replaced && other.CompletedAtUtc is not null)
                    other.Status = DownloadTaskStatus.Completed;
            }

            if (other.ReplacesTaskId is { } replaces && deletedIds.Contains(replaces))
                other.ReplacesTaskId = null;
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

        if (!string.IsNullOrWhiteSpace(task.DownloadRootPath))
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
            update.Status           =   DownloadTaskStatus.Submitting;
            update.AttemptCount     +=  1;
            update.SubmittedAtUtc   ??= timeProvider.GetUtcNow();
            update.IsSystemPaused   =   true;
            update.PreparationStage =   DownloadPreparationStage.Planned;
        }, cancellationToken);

        string response;
        try
        {
            response = await session.AddTorrentAsync(task.TorrentFile, task.SavePath,
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

    /// <summary>接管下载器中已存在的同 hash 种子：补打来源 tag、把本地隔离路径对齐为种子真实位置，再按快照推进。</summary>
    private async Task AdoptExistingAsync(
        DownloadTask task, TorrentSnapshot snapshot, IQbittorrentSession session, CancellationToken cancellationToken)
    {
        var siteSource = task.SiteSource;
        if (!string.IsNullOrWhiteSpace(siteSource) &&
            !snapshot.Tags.Contains(siteSource, StringComparer.OrdinalIgnoreCase))
            await session.AddTagAsync(task.InfoHash, siteSource, cancellationToken);

        if (!MatchesIsolationPath(snapshot.SavePath, task.SavePath))
            // 隔离路径让位于种子的真实位置：接管外部种子时它通常不在本应用约定的目录里，
            // 后续对账与完成改名都以真实位置为准。
            await UpdateTaskAsync(task.Id, task.Status,
                                  update =>
                                  {
                                      update.SavePath = DownloadPathNamePolicy.NormalizePath(snapshot.SavePath);
                                  },
                                  cancellationToken);

        if (snapshot.Category == QbittorrentTorrentCategory.Stopped && task.IsSystemPaused)
            // 接管的停止种子无法区分是“本应用停止添加后响应丢失”还是“用户/外部工具添加并暂停”：
            // 按人工暂停呈现，对账绝不自动启动；系统准备意图保留——用户显式恢复时
            // 由准备流程先补齐文件选择再启动，而不是绕过计划直接下载。
            await UpdateTaskAsync(task.Id, task.Status,
                                  update => { update.IsUserPaused = true; },
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
        if (snapshot is null)
        {
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
            catch (Exception exception)
            {
                logger.LogWarning(exception, "下载任务 {TaskId} 准备流程推进失败，本轮仍同步进度，下一轮重试。",
                                  task.Id);
            }

            await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
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
        task = await LoadTaskWithFilesAsync(task.Id, cancellationToken);
        if (task is not { Status: DownloadTaskStatus.Downloading, IsSystemPaused: true }) return;

        // 没有任何选中文件的候选不启动：保留可解释的落选或被覆盖结果，等待人工处理。
        if (task.Files.Count > 0 && task.Files.All(file => !file.IsSelectedForDownload))
        {
            logger.LogInformation("任务 {TaskId} 的全部文件均已落选，保持停止不启动下载。", task.Id);
            return;
        }

        // 1. 文件下载选择与计划比对；不一致时发出变更即返回，请求成功不代表已生效，下一轮核对后再继续。
        //    无法在下载器文件清单中确认身份的文件同样阻塞推进：缺项不代表选择已确认。
        if (await TryApplyFileSelectionAsync(session, task, cancellationToken)) return;

        // 2. 命名计划：仍是登记原始路径的文件交由文件服务改名；改名日志由恢复链在后续轮次确认完成。
        //    只有明确的路径占用可安全回退——以原始命名继续下载，完成链的备份移动（.replaced/）
        //    保护旧成片并完成替换。身份校验失败、未知路径布局等其他异常必须阻塞启动，
        //    由外层捕获记录并等待人工或远端恢复，不能带着无法核实的文件布局开始下载。
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
            logger.LogWarning(exception,
                              "任务 {TaskId} 准备阶段命名被目标路径占用拦截，以原始命名继续下载，完成时再替换。",
                              task.Id);
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
            update.IsSystemPaused   = false;
            update.PreparationStage = DownloadPreparationStage.ReadyToRun;
            update.LastSyncedAtUtc  = timeProvider.GetUtcNow();
        }, cancellationToken);
        logger.LogInformation("任务 {TaskId} 准备完成（文件选择与命名已确认），已恢复下载。", task.Id);
    }

    /// <summary>
    ///     核对下载器实际文件选择与计划的一致性，不一致时发出补做请求。返回 true 表示本轮不应推进
    ///     后续步骤（已发出变更等待确认，或有文件身份无法确认——缺项不代表选择已确认）。
    /// </summary>
    private async Task<bool> TryApplyFileSelectionAsync(
        IQbittorrentSession session, DownloadTask task, CancellationToken cancellationToken)
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
                !remoteFiles.TryGetValue(qbIndex, out var remote))
            {
                // 计划内文件在下载器清单中无法寻址：选择状态无法确认，不得当作已确认推进。
                unconfirmed++;
                continue;
            }

            if (file.IsSelectedForDownload       && !remote.IsSelectedForDownload) toDownload.Add(qbIndex);
            else if (!file.IsSelectedForDownload && remote.IsSelectedForDownload) toSkip.Add(qbIndex);
        }

        if (unconfirmed > 0)
        {
            logger.LogWarning("任务 {TaskId} 有 {Count} 个计划文件无法在下载器文件清单中确认身份，暂缓准备推进。",
                              task.Id, unconfirmed);
            return true;
        }

        if (toSkip.Count == 0 && toDownload.Count == 0) return false;

        if (toSkip.Count > 0)
            await session.SetTorrentFileSelectionAsync(task.InfoHash, toSkip, false, cancellationToken);
        if (toDownload.Count > 0)
            await session.SetTorrentFileSelectionAsync(task.InfoHash, toDownload, true, cancellationToken);

        await UpdateTaskAsync(task.Id, DownloadTaskStatus.Downloading,
                              update => update.PreparationStage = DownloadPreparationStage.Applying, cancellationToken);
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
    private async Task SettleNamingEarlyAsync(DownloadTask        task,    TorrentSnapshot   snapshot,
                                              IQbittorrentSession session, CancellationToken cancellationToken)
    {
        if (downloadFileService is null || string.IsNullOrWhiteSpace(task.DownloadRootPath)) return;
        bool untouched;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            untouched = await db.DownloadTaskFiles.AsNoTracking()
                                .AnyAsync(file => file.DownloadTaskId      == task.Id &&
                                                  file.CurrentRelativePath == file.OriginalRelativePath,
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
                    update.Status     = DownloadTaskStatus.Failed;
                    update.FailedStep = "下载器";
                    update.Error      = "下载器报告该任务出错或文件缺失，请检查下载器。";
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Checking :
                // 校验是异步阶段，进度可能临时波动：只同步进度，不推进完成、暂停或准备流程，
                // 待校验结束回到稳定状态后再继续。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Seeding when snapshot.Progress     >= 1.0 :
            case QbittorrentTorrentCategory.Downloading when snapshot.Progress >= 1.0 :
            case QbittorrentTorrentCategory.Stopped when snapshot.Progress     >= 1.0 :
                // 先持久化下载器已报告的完成进度；若随后文件整理失败，页面仍能区分“下载已完成、整理失败”。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
                }, cancellationToken);
                await CompleteAsync(session, taskId, snapshot, currentStatus, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Stopped :
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    if (update.IsSystemPaused && !update.IsUserPaused)
                    {
                        // 系统以停止状态添加/接管且准备未完成：保持待准备语义（含提交中断后的接管），
                        // 准备推进由对账的准备分支负责，不得把准备暂停误记为人工暂停。
                        update.Status = DownloadTaskStatus.Downloading;
                    }
                    else
                    {
                        // 人工在下载器或页面暂停的任务必须保留暂停状态，对账绝不自动恢复；
                        // 外部停止且系统未声明准备时同样按人工暂停保守处理。
                        update.Status       = DownloadTaskStatus.Paused;
                        update.IsUserPaused = true;
                    }

                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Downloading or QbittorrentTorrentCategory.Seeding :
                // 进度未满的做种快照（接管/校验后确认内容不完整）与下载中同等处理：保持等待，不判完成。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status       = DownloadTaskStatus.Downloading;
                    update.IsUserPaused = false;

                    // 下载已实际发生（可能是准备完成后恢复，也可能是用户在下载器内手动启动）：
                    // 系统准备暂停语义结束，用户手动启动优先。
                    update.IsSystemPaused  = false;
                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
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

            fileNames = await session.ListFileNamesAsync(snapshot.Hash, cancellationToken);
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

        // 登记期只翻转现任状态（见 SupersedeIncumbentAsync），远端清理由对账承接；此处是兜底：
        // 清理对账间隙仍残留的未完成替换对象——未下完的连种子带文件删除（不留半成品冗余数据），
        // 已下完的沿用版本守卫语义仅标记、保留文件。删除下载器种子失败不阻断完成，留待人工处理。
        var supersededHashes = new List<string>();
        if (trackedTask.ReplacesTaskId is { } arbitrationTargetId)
        {
            var arbitrationTarget = await completionDb.DownloadTasks
                                                      .SingleOrDefaultAsync(value => value.Id == arbitrationTargetId,
                                                                            cancellationToken);
            if (arbitrationTarget is not null                                     &&
                arbitrationTarget.ReplacedByTaskId == null                        &&
                arbitrationTarget.Status           != DownloadTaskStatus.Replaced &&
                arbitrationTarget.Status           != DownloadTaskStatus.Failed   &&
                arbitrationTarget.Status           != DownloadTaskStatus.Lost)
            {
                var stillDownloading = arbitrationTarget.Status != DownloadTaskStatus.Completed;
                arbitrationTarget.Status           = DownloadTaskStatus.Replaced;
                arbitrationTarget.ReplacedByTaskId = taskId;
                if (stillDownloading) supersededHashes.Add(arbitrationTarget.InfoHash);
            }
        }

        // 创建时记录的替换对象可能已过期：完成时按当前状态重新选择并直接赋值；
        // 查不到候选（旧任务已被更晚完成的任务替换、或仅剩更高版本任务）时清除声明，避免保留过期关系。
        // 遵守版本守卫：新任务只替换不高于自身版本的已完成项。若登记期的旧目标已被
        // 更高版本取代，清除过期声明而不跳过守卫去替换更高版本。
        trackedTask.ReplacesTaskId = await DownloadTaskLookups.FindReplacementTargetAsync(completionDb,
            trackedTask.SubjectId,
            trackedTask.Episode, trackedTask.InfoHash,
            trackedTask.Version, cancellationToken);

        if (trackedTask.ReplacesTaskId is { } oldTaskId)
        {
            var oldTask = await completionDb.DownloadTasks
                                            .SingleOrDefaultAsync(value => value.Id == oldTaskId, cancellationToken);
            if (oldTask is not null && oldTask.Status != DownloadTaskStatus.Replaced)
            {
                oldTask.Status           = DownloadTaskStatus.Replaced;
                oldTask.ReplacedByTaskId = taskId;
            }
        }

        await completionDb.SaveChangesAsync(cancellationToken);
        foreach (var supersededHash in supersededHashes)
            try
            {
                await session.DeleteTorrentAsync(supersededHash, true, cancellationToken);
                logger.LogInformation("高优先任务 {TaskId} 完成，已顶替删除下载器中的低优先种子 {Hash}。", taskId,
                                      supersededHash);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                                  "顶替删除下载器中的低优先种子 {Hash} 失败，任务已标记被替换，请人工删除种子。", supersededHash);
            }

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
        if (fileNames.Any(name => !name.Replace('\\', '/')
                                       .StartsWith(candidate + "/", StringComparison.Ordinal)))
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
                                  update.Status     = DownloadTaskStatus.Failed;
                                  update.FailedStep = step;
                                  update.Error      = error.Length > 1000 ? error[..1000] : error;
                              }, cancellationToken,
                              (db, task) => notificationEnqueuer.TryEnqueueAsync(
                               db, NotificationEventType.DownloadFailed, "下载失败",
                               $"{DescribeTask(task)}：在「{step}」步骤失败。{error}", cancellationToken));
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
