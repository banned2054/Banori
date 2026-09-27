using Banned.Qbittorrent.Exceptions;
using MediaManager.Core.Rules;
using MediaManager.Core.Torrents;
using MediaManager.Data;
using MediaManager.Features.Notifications;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Downloads;

/// <summary>
///     下载闭环：创建意图、提交下载器、对账至完成。
///     首次提交请求发出前先持久化 Submitting；结果未确认或崩溃恢复时按 info-hash 对账后决定继续下载还是重试；
///     数据库唯一约束与外部对账共同避免重复提交。单条任务失败不影响其他任务，更不影响既有资源。
/// </summary>
public sealed class DownloadService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ITorrentFileSource                      torrentFileSource,
    IQbittorrentSessionFactory              sessionFactory,
    ManageQbittorrentSettings               qbittorrentSettings,
    NotificationEnqueuer                    notificationEnqueuer,
    TimeProvider                            timeProvider,
    ILogger<DownloadService>                logger)
{
    /// <summary>响应丢失后的自动重新提交上限；达到上限视为提交失败，等待人工重试。</summary>
    public const int MaxAutoSubmitAttempts = 5;

    public async Task<Guid> CreateForFeedItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            throw new InvalidOperationException("请先在设置页启用并配置 qBittorrent 下载器。");

        FeedItem item;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            item = await db.FeedItems.AsNoTracking()
                           .Include(value => value.Subject)
                           .SingleOrDefaultAsync(value => value.Id == itemId, cancellationToken)
                ?? throw new KeyNotFoundException("找不到指定条目。");

            // P4 起手动创建与自动登记共用同一门槛：条目必须是所在同集组的当前优胜候选，
            // 历史 Accepted 条目需经人工重评触发组内重选后才能创建任务。
            var isWinner = await db.CandidateSelections.AsNoTracking()
                                   .AnyAsync(selection => selection.WinnerFeedItemId == itemId, cancellationToken);
            if (!isWinner)
                throw new InvalidOperationException("该条目不是所在集的当前优胜候选，请先重新评估或调整选择规则。");
        }

        AssertDownloadable(item);

        var (torrentBytes, infoHash) = await DownloadTorrentAsync(item.TorrentUrl!, cancellationToken);

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            return await CreateTaskWithinContextAsync(db, item, torrentBytes, infoHash, cancellationToken);
        }
    }

    /// <summary>下载种子文件并提取 info-hash；网络请求不置于数据库事务内。</summary>
    public async Task<(byte[] TorrentBytes, string InfoHash)> DownloadTorrentAsync(
        string torrentUrl, CancellationToken cancellationToken)
    {
        var torrentBytes = await torrentFileSource.DownloadAsync(new Uri(torrentUrl), cancellationToken);
        if (!TorrentInfoHashExtractor.TryExtractInfoHash(torrentBytes, out var infoHash))
            throw new InvalidOperationException("无法从种子文件中提取 info-hash，文件可能无效或已损坏。");

        return (torrentBytes, infoHash);
    }

    /// <summary>
    ///     在给定上下文内核验重复、决定替换对象并落库任务，由调用方控制事务提交。
    ///     自动登记与手动创建共用该核心，保证查重与替换判定一致。
    /// </summary>
    public async Task<Guid> CreateTaskWithinContextAsync(
        ApplicationDbContext db, FeedItem item, byte[] torrentBytes, string infoHash,
        CancellationToken    cancellationToken)
    {
        var activeDuplicate = await db.DownloadTasks.AsNoTracking()
                                      .Where(task => task.FeedItemId == item.Id || task.InfoHash == infoHash)
                                      .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                                     task.Status == DownloadTaskStatus.Submitting    ||
                                                     task.Status == DownloadTaskStatus.Downloading   ||
                                                     task.Status == DownloadTaskStatus.Paused)
                                      .Select(task => (Guid?)task.Id)
                                      .FirstOrDefaultAsync(cancellationToken);
        if (activeDuplicate is not null) throw new InvalidOperationException("该条目或同一资源已有进行中的下载任务。");

        // 有效完成的同资源不自动重新下载；完成记录不因不属于活动唯一索引而被重复登记绕过。
        var completedDuplicate = await db.DownloadTasks.AsNoTracking()
                                         .Where(task => task.InfoHash == infoHash &&
                                                        task.Status   == DownloadTaskStatus.Completed)
                                         .Select(task => (Guid?)task.Id)
                                         .FirstOrDefaultAsync(cancellationToken);
        if (completedDuplicate is not null)
            throw new InvalidOperationException("同一资源已有已完成的下载任务。");

        // 替换对象在创建与完成两处共用同一规则，见 FindReplacementTargetAsync。
        var replacesTaskId =
            await FindReplacementTargetAsync(db, item.SubjectId, item.Episode, infoHash, item.Version,
                                             cancellationToken);

        var task = new DownloadTask
        {
            Id          = Guid.NewGuid(),
            FeedItemId  = item.Id,
            SubjectId   = item.SubjectId,
            InfoHash    = infoHash,
            TorrentFile = torrentBytes,
            TorrentUrl  = item.TorrentUrl!,
            SavePath = BuildSavePath((await qbittorrentSettings.GetAsync(cancellationToken)).SavePath,
                                     item.Subject?.NameCn ?? item.Subject?.Name,
                                     item.NamingPreview!, infoHash),
            NamingSnapshot = item.NamingPreview!,
            Episode        = item.Episode,
            Version        = item.Version,
            Status         = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc   = timeProvider.GetUtcNow(),
            ReplacesTaskId = replacesTaskId
        };
        db.DownloadTasks.Add(task);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发下绕过前置查重时由数据库活动唯一索引兜底。
            throw new InvalidOperationException("同一资源已有进行中的下载任务，重复提交已被数据库约束阻止。");
        }

        logger.LogInformation("已为条目 {ItemId} 创建下载任务 {TaskId}（hash {InfoHash}）。", item.Id, task.Id,
                              infoHash);
        return task.Id;
    }

    /// <summary>一轮对账：提交待提交任务、核对未确认与下载中任务。下载器未启用或未配置时静默跳过。</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
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

        if (tasks.Count == 0) return;

        await using var session = await sessionFactory.CreateAsync(cancellationToken);

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
                await SubmitAsync(session, task, cancellationToken);
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

    public async Task<IReadOnlyList<DownloadTaskView>> ListAsync(
        int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.DownloadTasks.AsNoTracking()
                            .Include(task => task.FeedItem)
                            .Include(task => task.Subject)
                            .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序并截取。
        tasks = tasks.OrderByDescending(task => task.CreatedAtUtc)
                     .Take(Math.Clamp(take, 1, 500))
                     .ToList();

        var relatedIds = tasks.SelectMany(task => new[] { task.ReplacesTaskId, task.ReplacedByTaskId })
                              .Where(value => value is not null)
                              .Select(value => value!.Value)
                              .Distinct()
                              .ToList();
        var relatedNames = relatedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.DownloadTasks.AsNoTracking()
                      .Where(task => relatedIds.Contains(task.Id))
                      .ToDictionaryAsync(task => task.Id, task => task.NamingSnapshot, cancellationToken);
        return tasks.Select(task => ToView(task, relatedNames)).ToList();
    }

    private async Task SubmitAsync(IQbittorrentSession session, DownloadTask task, CancellationToken cancellationToken)
    {
        if (task.Status == DownloadTaskStatus.Submitting)
        {
            // 响应丢失不等于提交失败：先按 Hash 对账，再决定是否重新提交。
            var existing = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken))
               .FirstOrDefault();
            if (existing is not null)
            {
                // 同 hash 不代表同一次提交：隔离路径不一致时不接管，避免误动其他目录里的同资源任务。
                if (!MatchesIsolationPath(existing.SavePath, task.SavePath))
                {
                    await MarkFailedAsync(task.Id, task.Status, "提交",
                                          "下载器中已存在同 hash 任务，但保存路径与本任务的隔离目录不一致，未接管，请人工检查下载器。",
                                          cancellationToken);
                    return;
                }

                await ApplySnapshotAsync(task.Id, task.Status, existing, session, cancellationToken);
                return;
            }

            if (task.AttemptCount >= MaxAutoSubmitAttempts)
            {
                await MarkFailedAsync(task.Id, task.Status, "提交", "多次提交后仍未在下载器中发现该任务，请检查下载器后人工重试。",
                                      cancellationToken);
                return;
            }
        }

        // 请求发出前先落库 Submitting：下载器已接收而应用在保存结果前退出时，重启按 Hash 对账而不是重复提交。
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.Status         =   DownloadTaskStatus.Submitting;
            update.AttemptCount   +=  1;
            update.SubmittedAtUtc ??= timeProvider.GetUtcNow();
        }, cancellationToken);

        string response;
        try
        {
            response = await session.AddTorrentAsync(task.TorrentFile, task.SavePath,
                                                     QbittorrentSettings.DefaultCategory, cancellationToken);
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

        var responseText = (response ?? string.Empty).Trim();
        if (responseText.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝该种子：{responseText}",
                                  cancellationToken);
            return;
        }

        await UpdateTaskAsync(task.Id, DownloadTaskStatus.Submitting,
                              update => { update.Status = DownloadTaskStatus.Downloading; }, cancellationToken,
                              (db, updated) =>
                                  notificationEnqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted,
                                                                       "下载开始",
                                                                       $"{DescribeTask(updated)}：已提交到下载器开始下载。命名：{updated.NamingSnapshot}",
                                                                       cancellationToken));
    }

    private async Task ReconcileTrackedAsync(IQbittorrentSession session, DownloadTask task, TorrentSnapshot? snapshot,
                                             CancellationToken   cancellationToken)
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

        // 同 hash 不代表同一次提交：隔离路径不一致时不接管，避免误动其他目录里的同资源任务。
        if (!MatchesIsolationPath(snapshot.SavePath, task.SavePath))
        {
            await MarkFailedAsync(task.Id, task.Status, "对账",
                                  "下载器中同 hash 任务的保存路径与本任务的隔离目录不一致，未接管，请人工检查下载器。",
                                  cancellationToken);
            return;
        }

        await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
    }

    /// <summary>按下载器快照推进本地状态：完成改名、识别人工暂停、记录进度。</summary>
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

            case QbittorrentTorrentCategory.Seeding when snapshot.Progress     >= 1.0 :
            case QbittorrentTorrentCategory.Downloading when snapshot.Progress >= 1.0 :
            case QbittorrentTorrentCategory.Stopped when snapshot.Progress     >= 1.0 :
                await CompleteAsync(session, taskId, snapshot, currentStatus, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Stopped :
                // 人工在下载器或页面暂停的任务必须保留暂停状态，对账绝不自动恢复。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status          = DownloadTaskStatus.Paused;
                    update.IsUserPaused    = true;
                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Downloading :
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status          = DownloadTaskStatus.Downloading;
                    update.IsUserPaused    = false;
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

    /// <summary>完成收尾：按命名快照改名实际文件（单文件改文件名、多文件改根目录名，无法唯一确定目标的布局如实失败），并按当前状态重新选择替换对象。旧任务与文件保留。</summary>
    private async Task CompleteAsync(
        IQbittorrentSession session, Guid taskId, TorrentSnapshot snapshot, DownloadTaskStatus currentStatus,
        CancellationToken   cancellationToken)
    {
        string targetName;
        string namingSnapshot;
        string savePath;

        IReadOnlyList<string> fileNames;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var task = await db.DownloadTasks.AsNoTracking()
                               .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
            if (task is null || task.Status.IsFinal()) return;

            fileNames      = await session.ListFileNamesAsync(snapshot.Hash, cancellationToken);
            namingSnapshot = task.NamingSnapshot;
            savePath       = task.SavePath;
            var extension = fileNames.Count == 1 ? Path.GetExtension(fileNames[0]) : string.Empty;
            targetName = namingSnapshot + extension;
        }

        if (fileNames.Count == 0)
        {
            await MarkFailedAsync(taskId, currentStatus, "改名", "无法读取种子内文件列表，无法按命名快照改名，请人工处理。", cancellationToken);
            return;
        }

        // 冲突按完整目标路径精确判断：不同隔离目录中的同名资源互不影响，只有同目录同名才算冲突。
        var siblings   = await session.ListTorrentsAsync(QbittorrentSettings.DefaultCategory, cancellationToken);
        var targetPath = JoinSavePath(savePath, targetName);
        if (siblings.Any(other => other.Hash != snapshot.Hash &&
                                  string.Equals(JoinSavePath(other.SavePath, other.Name), targetPath,
                                                StringComparison.Ordinal)))
        {
            await MarkFailedAsync(taskId, currentStatus, "改名", "目标路径与下载器中其他任务冲突，请人工处理。",
                                  cancellationToken);
            return;
        }

        try
        {
            if (fileNames.Count == 1)
            {
                // 单文件种子：直接改实际文件名，保证磁盘文件与命名快照一致。
                await session.RenameTorrentFileAsync(snapshot.Hash, fileNames[0], targetName, cancellationToken);
            }
            else if (TryGetCommonRootFolder(fileNames, out var rootFolder))
            {
                // 多文件种子：改公共根目录名，保证内容目录与命名快照一致。
                await session.RenameTorrentFolderAsync(snapshot.Hash, rootFolder, namingSnapshot, cancellationToken);
            }
            else
            {
                // 多个顶层散文件无法唯一映射到命名快照：如实失败等待人工处理，不标记完成。
                await MarkFailedAsync(taskId, currentStatus, "改名",
                                      "种子包含多个顶层文件，无法自动按命名快照改名，请人工处理。", cancellationToken);
                return;
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
        // 创建时记录的替换对象可能已过期：完成时按当前状态重新选择并直接赋值；
        // 查不到候选（旧任务已被更晚完成的任务替换、或仅剩更高版本任务）时清除声明，避免保留过期关系。
        trackedTask.ReplacesTaskId = await FindReplacementTargetAsync(completionDb, trackedTask.SubjectId,
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
        logger.LogInformation("下载任务 {TaskId} 已完成并改名为 {TargetName}。", taskId, targetName);
    }

    internal static void AssertDownloadable(FeedItem item)
    {
        if (item.Status != FeedItemStatus.Accepted || item.DecisionStatus != ReleaseDecisionStatus.Accepted)
            throw new InvalidOperationException("只有筛选通过（已选择）的条目才能创建下载任务。");

        if (string.IsNullOrWhiteSpace(item.TorrentUrl) ||
            !Uri.TryCreate(item.TorrentUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("该条目没有可用的种子地址。");

        if (item.Episode is null || item.SubjectId is null)
            throw new InvalidOperationException("该条目缺少 Subject 或集数，无法创建下载任务。");

        if (string.IsNullOrWhiteSpace(item.NamingPreview)) throw new InvalidOperationException("该条目缺少命名预览，无法创建下载任务。");
    }

    private string BuildSavePath(string basePath, string? subjectName, string namingPreview, string infoHash)
    {
        // 基础路径按用户输入原样使用（可能是 Windows 或 Linux 路径），只净化追加的目录段。
        var directory      = basePath.Trim().TrimEnd('/', '\\');
        var subjectSegment = DownloadPathNamePolicy.SanitizePathSegment(subjectName, 100);
        var namingSegment  = DownloadPathNamePolicy.SanitizePathSegment(namingPreview);
        var hashSegment    = infoHash.Length >= 8 ? infoHash[..8] : infoHash;

        var middle = string.IsNullOrWhiteSpace(subjectSegment) ? string.Empty : $"/{subjectSegment}";
        return $"{directory}{middle}/{namingSegment}-{hashSegment}";
    }

    /// <summary>保存路径归一化分隔符后精确比较：大小写必须一致，避免在大小写敏感的 Linux 文件系统上把不同目录判成同一目录。</summary>
    private static bool MatchesIsolationPath(string snapshotPath, string taskPath)
    {
        return string.Equals(NormalizePath(snapshotPath), NormalizePath(taskPath), StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/').TrimEnd('/');
    }

    private static string JoinSavePath(string directory, string name)
    {
        return $"{NormalizePath(directory)}/{name.Trim().Replace('\\', '/')}";
    }

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

    /// <summary>
    ///     查找本任务完成后应标记为被替换的旧任务：同 Subject + Episode、不同 hash、未被他任务替换的已完成任务，
    ///     优先更高版本。失败任务没有可供替代的旧文件；不越过比自己更高的版本，避免旧版本反向替换新资源。
    /// </summary>
    private static async Task<Guid?> FindReplacementTargetAsync(ApplicationDbContext db, int? subjectId,
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

    private async Task<DownloadTask> LoadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadTasks.AsNoTracking()
                       .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到指定下载任务。");
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

    private static DownloadTaskView ToView(DownloadTask task, IReadOnlyDictionary<Guid, string> relatedNames)
    {
        return new DownloadTaskView
        {
            Id          = task.Id,
            FeedItemId  = task.FeedItemId,
            TorrentName = task.FeedItem?.Title,
            SubjectId   = task.SubjectId,
            SubjectName = task.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(task.Subject.NameCn) ? task.Subject.Name : task.Subject.NameCn,
            Episode        = task.Episode,
            Version        = task.Version,
            Status         = task.Status,
            IsUserPaused   = task.IsUserPaused,
            Progress       = task.Progress,
            TotalSize      = task.TotalSize,
            NamingSnapshot = task.NamingSnapshot,
            FailedStep     = task.FailedStep,
            Error          = task.Error,
            CreatedAtUtc   = task.CreatedAtUtc,
            SubmittedAtUtc = task.SubmittedAtUtc,
            CompletedAtUtc = task.CompletedAtUtc,
            ReplacesTaskId = task.ReplacesTaskId,
            ReplacesNaming = task.ReplacesTaskId is { } replacedId
                ? relatedNames.GetValueOrDefault(replacedId)
                : null,
            ReplacedByTaskId = task.ReplacedByTaskId,
            ReplacedByNaming = task.ReplacedByTaskId is { } replacedById
                ? relatedNames.GetValueOrDefault(replacedById)
                : null
        };
    }
}
