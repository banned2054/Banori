using MediaManager.Data;
using MediaManager.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Xml;

namespace MediaManager.Features.Subscriptions;

/// <summary>
///     订阅刷新的单次执行主线：领取工作 → 读取 RSS → 新条目入库 → 评估（含数据库遗留
///     待评估条目恢复）→ 保存工作结果与下次刷新时间。限定单实例、单个消费者串行执行。
/// </summary>
public sealed class MikanRefreshService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IDataProtectionProvider                 dataProtectionProvider,
    IFeedSourceReader                       feedSourceReader,
    FeedItemEvaluator                       evaluator,
    CandidateSelectionService               selectionService,
    TimeProvider                            timeProvider,
    ILogger<MikanRefreshService>            logger) : IRefreshWorkConsumer
{
    private readonly IDataProtector _urlProtector = FeedUrlProtection.CreateProtector(dataProtectionProvider);

    /// <summary>
    ///     启动恢复：消费开始前，把上次进程遗留的执行中工作恢复为待执行。
    ///     依赖单实例假设，正常运行中不因工作耗时而重领。
    /// </summary>
    public async Task RecoverInterruptedWorksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var interrupted = await db.RefreshWorkItems
                                  .Where(item => item.Status == RefreshWorkStatus.Running)
                                  .ToListAsync(cancellationToken);
        if (interrupted.Count == 0) return;

        foreach (var work in interrupted) work.Status = RefreshWorkStatus.Pending;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("已将上次进程遗留的 {Count} 个执行中刷新工作恢复为待执行。", interrupted.Count);
    }

    /// <summary>领取并执行一项刷新工作；没有待执行工作时返回 null。</summary>
    public async Task<RefreshResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var work = await ClaimNextAsync(timeProvider.GetUtcNow(), cancellationToken);
        if (work is null) return null;

        try
        {
            return await ExecuteWorkAsync(work, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAsync(work.Id, work.SubscriptionId, "刷新被取消。", timeProvider.GetUtcNow(), null,
                            CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "刷新工作 {WorkId} 执行失败。", work.Id);
            const string message = "处理刷新工作时发生意外错误。";
            await FailAsync(work.Id, work.SubscriptionId, message, timeProvider.GetUtcNow(), null, cancellationToken);
            return new RefreshResult(work.Id, 0, 0, 1, message);
        }
    }

    private async Task<RefreshResult> ExecuteWorkAsync(RefreshWork work, CancellationToken cancellationToken)
    {
        var subscription = await LoadSubscriptionAsync(work.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            await FailAsync(work.Id, work.SubscriptionId, "订阅已不存在。", timeProvider.GetUtcNow(), null,
                            cancellationToken);
            return new RefreshResult(work.Id, 0, 0, 1, "订阅已不存在。");
        }

        var (entries, fetchError) = await ReadFeedAsync(subscription, cancellationToken);
        var (added, duplicates)   = await AddNewItemsAsync(subscription, entries, cancellationToken);
        var (failedItems, evaluatedIds) =
            await EvaluatePendingItemsAsync(subscription, CollectSourceSubjectHints(entries), cancellationToken);

        // 整批解析完成后按固定批次统一择优：本批全部候选与数据库历史候选一起比较，
        // RSS 顺序不影响结果；随后为需要新增或升级的优胜登记下载意图。
        var groupKeys = await CollectGroupKeysAsync(evaluatedIds, cancellationToken);
        // 刷新批次是自动路径：同一优胜已记录失败或已登记任务时不重试种子登记。
        await selectionService.SelectAndRegisterAsync(groupKeys, cancellationToken: cancellationToken);

        if (fetchError is null)
        {
            await CompleteAsync(work.Id, subscription.Id, subscription.RefreshIntervalMinutes, cancellationToken);
            return new RefreshResult(work.Id, added, duplicates, failedItems);
        }

        await FailAsync(work.Id, subscription.Id, fetchError, timeProvider.GetUtcNow(),
                        subscription.RefreshIntervalMinutes, cancellationToken);
        return new RefreshResult(work.Id, added, duplicates, failedItems, fetchError);
    }

    private async Task<RefreshWork?> ClaimNextAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // SQLite 不支持按 DateTimeOffset 服务端排序，先取回候选再在内存中排序。
        var workId = (await db.RefreshWorkItems
                              .Where(item => item.Status == RefreshWorkStatus.Pending)
                              .Select(item => new { item.Id, item.RequestedAtUtc })
                              .ToListAsync(cancellationToken))
                    .OrderBy(item => item.RequestedAtUtc)
                    .Select(item => item.Id)
                    .FirstOrDefault();
        if (workId == Guid.Empty) return null;

        var work = await db.RefreshWorkItems.SingleAsync(item => item.Id == workId, cancellationToken);

        work.Status       = RefreshWorkStatus.Running;
        work.StartedAtUtc = nowUtc;
        work.AttemptCount++;
        var subscription = await db.MediaSubscriptions.SingleAsync(item => item.Id == work.SubscriptionId,
                                                                   cancellationToken);
        subscription.LastRefreshStartedAtUtc = nowUtc;
        subscription.LastError               = null;
        await db.SaveChangesAsync(cancellationToken);
        return new RefreshWork(work.Id, work.SubscriptionId, work.AttemptCount);
    }

    private async Task<RefreshSubscription?> LoadSubscriptionAsync(
        Guid subscriptionId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MediaSubscriptions.AsNoTracking()
                           .SingleOrDefaultAsync(value => value.Id == subscriptionId, cancellationToken);
        return item is null ? null : new RefreshSubscription(item, _urlProtector.Unprotect(item.FeedUrlProtected));
    }

    /// <summary>
    ///     读取 RSS。失败时返回空列表与安全错误信息，本次工作仍会继续恢复遗留条目，
    ///     最后如实以抓取失败收尾；恢复不因本次抓取失败而永久阻塞。
    /// </summary>
    private async Task<(List<FeedSourceItem> Entries, string? Error)> ReadFeedAsync(
        RefreshSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await feedSourceReader.ReadAsync(new Uri(subscription.FeedUrl), subscription.ProxyId,
                                                           cancellationToken);
            return ([.. entries], null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = GetSafeFeedError(exception);
            logger.LogWarning(exception, "订阅 {SubscriptionName} 的 RSS 抓取失败：{Error}", subscription.Name,
                              message);
            return ([], message);
        }
    }

    private async Task<(int Added, int Duplicates)> AddNewItemsAsync(
        RefreshSubscription subscription, List<FeedSourceItem> entries, CancellationToken cancellationToken)
    {
        var             added      = 0;
        var             duplicates = 0;
        await using var db         = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var entry in entries.OrderBy(item => item.PublishedAtUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryAddFeedItemAsync(db, subscription.Id, entry, timeProvider.GetUtcNow(), cancellationToken))
                added++;
            else
                duplicates++;
        }

        return (added, duplicates);
    }

    /// <summary>
    ///     评估数据库中仍处于待处理状态的条目，覆盖本次新增条目和“已入库但评估前中断”的遗留条目，
    ///     后者不要求仍出现在最新 RSS 中。sourceSubjectHints 按 StableId 提供来源 feed 直接给出的
    ///     Subject，本次 feed 仍包含的遗留条目同样受益。单条失败不终止整批。返回失败数与评估成功的条目 ID。
    /// </summary>
    private async Task<(int Failures, List<Guid> EvaluatedIds)> EvaluatePendingItemsAsync(
        RefreshSubscription subscription, IReadOnlyDictionary<string, int?> sourceSubjectHints,
        CancellationToken   cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // SQLite 不支持按 DateTimeOffset 服务端排序，先取回候选再在内存中排序。
        var pendingItems = (await db.FeedItems.AsNoTracking()
                                    .Where(item => item.SubscriptionId == subscription.Id &&
                                                   item.Status         == FeedItemStatus.Pending)
                                    .Select(item => new { item.Id, item.StableId, item.FirstSeenAtUtc })
                                    .ToListAsync(cancellationToken))
                          .OrderBy(item => item.FirstSeenAtUtc)
                          .ToList();

        var failures     = 0;
        var evaluatedIds = new List<Guid>();
        foreach (var item in pendingItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await evaluator.EvaluateAsync(item.Id, subscription.FixedSubjectId, cancellationToken,
                                              sourceSubjectHints.GetValueOrDefault(item.StableId));
                evaluatedIds.Add(item.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                logger.LogWarning(exception, "条目 {ItemId} 评估失败。", item.Id);
                await MarkItemFailedAsync(item.Id, cancellationToken);
            }
        }

        return (failures, evaluatedIds);
    }

    /// <summary>按 StableId 汇总来源 feed 直接给出的 Subject（如 AniBT 的 bgmId）；同 StableId 重复时取先出现者。值为可空：未提供的条目不得注入占位值。</summary>
    private static IReadOnlyDictionary<string, int?> CollectSourceSubjectHints(List<FeedSourceItem> entries)
    {
        var hints = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry.SourceSubjectId is { } subjectId && !hints.ContainsKey(entry.StableId))
                hints[entry.StableId] = subjectId;

        return hints;
    }

    /// <summary>收集本批评估成功条目所属的同集分组键，供批量择优使用。</summary>
    private async Task<List<SelectionGroupKey>> CollectGroupKeysAsync(
        List<Guid> evaluatedIds, CancellationToken cancellationToken)
    {
        if (evaluatedIds.Count == 0) return [];

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.FeedItems.AsNoTracking()
                            .Where(item => evaluatedIds.Contains(item.Id) &&
                                           item.Status    == FeedItemStatus.Accepted &&
                                           item.SubjectId != null &&
                                           item.Episode   != null &&
                                           item.MediaType != null)
                            .Select(item => new { item.SubjectId, item.MediaType, item.Episode })
                            .ToListAsync(cancellationToken);

        return items
               .Where(item => item.MediaType != "MultipleEpisode")
               .Select(item => new SelectionGroupKey(item.SubjectId!.Value, item.MediaType!, item.Episode!.Value))
               .Distinct()
               .ToList();
    }

    private async Task MarkItemFailedAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             item = await db.FeedItems.SingleOrDefaultAsync(value => value.Id == itemId, cancellationToken);
        if (item is null) return;
        item.Status           = FeedItemStatus.Failed;
        item.Reason           = "处理条目时发生意外错误，可稍后重新评估。";
        item.EvaluatedAtUtc   = timeProvider.GetUtcNow();
        item.SelectionOutcome = SelectionOutcome.None;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> TryAddFeedItemAsync(ApplicationDbContext db,   Guid           subscriptionId,
                                                        FeedSourceItem       item, DateTimeOffset seenAtUtc,
                                                        CancellationToken    cancellationToken)
    {
        var existing = await db.FeedItems.SingleOrDefaultAsync(value => value.SubscriptionId == subscriptionId &&
                                                                        value.StableId       == item.StableId,
                                                               cancellationToken);
        if (existing is not null)
        {
            // 历史条目可能因旧版解析不到发布时间而缺失（Mikan 把 pubDate 藏在 torrent 扩展元素内），刷新时用 feed 值回填。
            if (existing.PublishedAtUtc is null && item.PublishedAtUtc is not null)
            {
                existing.PublishedAtUtc = item.PublishedAtUtc;
                await db.SaveChangesAsync(cancellationToken);
            }

            return false;
        }

        var entity = new FeedItem
        {
            Id             = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            StableId       = item.StableId,
            SourceItemId   = item.SourceItemId,
            Title          = item.Title,
            Link           = item.Link?.AbsoluteUri,
            TorrentUrl     = item.TorrentUri?.AbsoluteUri,
            PublishedAtUtc = item.PublishedAtUtc,
            FirstSeenAtUtc = seenAtUtc,
            Status         = FeedItemStatus.Pending
        };
        db.FeedItems.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    private async Task CompleteAsync(
        Guid workId, Guid subscriptionId, int refreshIntervalMinutes, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // 订阅可能已被删除并级联删除工作，此时无需收尾。
        var work = await db.RefreshWorkItems.SingleOrDefaultAsync(item => item.Id == workId, cancellationToken);
        if (work is null) return;
        var now = timeProvider.GetUtcNow();
        work.Status         = RefreshWorkStatus.Succeeded;
        work.CompletedAtUtc = now;
        work.Error          = null;
        var subscription = await db.MediaSubscriptions.SingleOrDefaultAsync(item => item.Id == subscriptionId,
                                                                            cancellationToken);
        if (subscription is not null)
        {
            subscription.LastRefreshCompletedAtUtc = now;
            subscription.LastError                 = null;
            // 一次性订阅不参与定时调度：完成后不排下次刷新时间。
            if (subscription.IsEnabled && !subscription.IsOneShot)
                subscription.NextRefreshAtUtc = now.AddMinutes(refreshIntervalMinutes);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(Guid workId, Guid subscriptionId, string error, DateTimeOffset failedAtUtc,
                                 int? refreshIntervalMinutes, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // 订阅可能已被删除并级联删除工作，此时无需收尾。
        var work = await db.RefreshWorkItems.SingleOrDefaultAsync(item => item.Id == workId, cancellationToken);
        if (work is null) return;
        work.Status         = RefreshWorkStatus.Failed;
        work.CompletedAtUtc = failedAtUtc;
        work.Error          = error;
        var subscription = await db.MediaSubscriptions.SingleOrDefaultAsync(item => item.Id == subscriptionId,
                                                                            cancellationToken);
        if (subscription is not null)
        {
            subscription.LastRefreshCompletedAtUtc = failedAtUtc;
            subscription.LastError                 = error;
            // 一次性订阅不参与定时调度：失败后同样不排下次刷新，等待手动重试。
            if (subscription.IsEnabled && !subscription.IsOneShot)
                subscription.NextRefreshAtUtc =
                    failedAtUtc.AddMinutes(refreshIntervalMinutes ?? subscription.RefreshIntervalMinutes);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string GetSafeFeedError(Exception exception)
    {
        return exception switch
        {
            InvalidOperationException => exception.Message,
            XmlException xmlException => $"RSS 源 XML 格式错误（第 {xmlException.LineNumber} 行）。",
            HttpRequestException      => "无法连接 RSS 源。",
            _                         => "读取或解析 RSS 源失败。"
        };
    }

    /// <summary>刷新执行所需的订阅数据；FeedUrl 为解密后的完整地址，仅在服务内部使用。</summary>
    private sealed record RefreshSubscription(
        Guid   Id,
        string Name,
        string FeedUrl,
        int    RefreshIntervalMinutes,
        int?   FixedSubjectId,
        Guid?  ProxyId)
    {
        public RefreshSubscription(MediaSubscription entity, string feedUrl) : this(entity.Id, entity.Name, feedUrl,
            entity.RefreshIntervalMinutes, entity.FixedSubjectId, entity.ProxyId)
        {
        }
    }
}
