using Banned.AniParser.Models.Enums;
using MediaManager.Core.Rules.Selection;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Features.Settings;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Subscriptions;

/// <summary>同集分组键：修正后的 Subject + 解析出的集类型 + 修正后集数。</summary>
public sealed record SelectionGroupKey(int SubjectId, string MediaType, decimal Episode);

/// <summary>一组候选的计算结果（未落库）。Winner 为空表示组内没有合格候选或全部被排除。</summary>
public sealed record GroupSelectionPlan(
    SelectionGroupKey            Key,
    EpisodeCandidate?            Winner,
    string                       WinnerBasis,
    int                          RuleVersion,
    IReadOnlyList<CandidateRank> Ranking);

/// <summary>规则变更预览的单组结果：当前已保存的优胜与草拟规则下的优胜对比。</summary>
public sealed record SelectionPreviewGroup(
    SelectionGroupKey Key,
    string            SubjectName, // MediaSubject.NameCn 非空用之，否则 Name；查不到用 "#SubjectId"
    Guid?             CurrentWinnerFeedItemId,
    string?           CurrentWinnerTitle, // 当前选择记录优胜条目的 FeedItem.Title，无选择记录为 null
    EpisodeCandidate? DraftWinner,        // 草拟规则下的优胜；组内无合格候选为 null
    string?           DraftWinnerTitle,   // 草拟优胜条目的 FeedItem.Title，无草拟优胜为 null
    string            DraftWinnerBasis,
    bool              WinnerChanged); // 当前优胜 FeedItemId 与草拟优胜不同（含一边为 null）

/// <summary>规则变更预览的整体结果。</summary>
public sealed record SelectionPreviewResult(
    int                                  GroupCount,
    int                                  ChangedCount,
    IReadOnlyList<SelectionPreviewGroup> Groups);

/// <summary>
///     同集批量择优：读取组内全部合格候选（含历史与跨订阅条目），按规则快照选出优胜，
///     并把选择记录与下载意图在同一本地短事务中落库。种子下载等网络请求在事务外完成。
///     同一组的计划与提交在共享的组级门闸（单例）内相邻执行，防止并发的刷新 / 人工重评 /
///     规则重选（各自持有 Scoped 服务实例）用过期快照互相覆盖。
///     单组失败不拖垮整批；中断窗口由 RecoverPendingSelectionsAsync 兜底恢复。
/// </summary>
public sealed class CandidateSelectionService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ISelectionRuleProvider                  ruleProvider,
    SelectionGroupGate                      groupGate,
    MediaManager.Features.Downloads.DownloadCoordinationGate downloadGate,
    CandidateEligibilityPolicy              eligibilityPolicy,
    CandidateRankingPolicy                  rankingPolicy,
    ManageQbittorrentSettings               qbittorrentSettings,
    DownloadService                         downloadService,
    TimeProvider                            timeProvider,
    ILogger<CandidateSelectionService>      logger)
{
    /// <summary>计算若干组的优胜计划；纯读操作，不写数据库。</summary>
    public async Task<List<GroupSelectionPlan>> PlanSelectionsAsync(
        IReadOnlyCollection<SelectionGroupKey> groupKeys, CancellationToken cancellationToken = default)
    {
        var             config = await ruleProvider.GetAsync(cancellationToken);
        var             plans  = new List<GroupSelectionPlan>();
        await using var db     = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        foreach (var key in groupKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await TryPlanSingleGroupAsync(db, key, config, cancellationToken);
            if (plan is not null) plans.Add(plan);
        }

        return plans;
    }

    /// <summary>
    ///     逐组“计算计划 + 落库”并返回成功计算的组计划（含落库失败者）。
    ///     每组的计划与提交在同一组级门闸内相邻执行：并发的刷新 / 人工重评 / 规则重选
    ///     在同组上串行化，先算完的过期快照无法在更新的提交之后再写入。
    ///     自动路径（刷新批次 / 恢复扫描）默认不重试已失败的种子登记；人工重评入口传
    ///     resetFailedIntent: true，才允许对同一优胜重试登记。
    /// </summary>
    public async Task<List<GroupSelectionPlan>> SelectAndRegisterAsync(
        IReadOnlyCollection<SelectionGroupKey> groupKeys, bool resetFailedIntent = false,
        CancellationToken                      cancellationToken = default)
    {
        var             config = await ruleProvider.GetAsync(cancellationToken);
        var             plans  = new List<GroupSelectionPlan>();
        await using var db     = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        foreach (var key in groupKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await groupGate.WaitAsync(key, cancellationToken);
            try
            {
                var plan = await TryPlanSingleGroupAsync(db, key, config, cancellationToken);
                if (plan is null) continue;

                plans.Add(plan);
                // 登记事务与 qB 提交/删除共用同一进程门禁，防止跨 scope 竞态。
                using (await downloadGate.EnterAsync(cancellationToken))
                    await CommitSelectionAsync(plan, resetFailedIntent, cancellationToken);
            }
            finally
            {
                groupGate.Release(key);
            }
        }

        return plans;
    }

    /// <summary>计算单组计划并吞掉非取消异常（记日志返回 null）；供批量路径共享。</summary>
    private async Task<GroupSelectionPlan?> TryPlanSingleGroupAsync(
        ApplicationDbContext db, SelectionGroupKey key, SelectionRuleConfig config,
        CancellationToken    cancellationToken)
    {
        try
        {
            return await PlanSingleGroupAsync(db, key, config, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "同集组 {SubjectId}/{MediaType}/{Episode} 择优计算失败。", key.SubjectId,
                            key.MediaType, key.Episode);
            return null;
        }
    }

    /// <summary>
    ///     中断恢复，补齐三类窗口——已合格但所在组尚无选择记录的条目（择优计算或种子下载后、
    ///     事务提交前中断，即使原条目已不在最新 RSS 中也能恢复）、选择记录存在但意图尚未
    ///     登记且无失败原因的选择，以及选择记录的规则版本与当前规则不一致的选择（重选被
    ///     中断、只处理了部分组时，剩余组在此按新版本补齐，不必再次保存规则）。前两类窗口
    ///     依赖下载器就绪；版本对账窗口不受下载器状态约束——选择记录本身应始终收敛到当前
    ///     生效规则，意图留待下载器配置完成后由未登记窗口补上。幂等，可由后台周期调用。
    /// </summary>
    public async Task RecoverPendingSelectionsAsync(CancellationToken cancellationToken = default)
    {
        // 意图登记窗口依赖下载器就绪，未就绪时登记必然留空；版本对账窗口仍需执行。
        var downloaderReady = await qbittorrentSettings.GetAsync(cancellationToken) is
            { Enabled: true, IsConfigured: true };

        List<SelectionGroupKey> pendingKeys;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // 刷新批次评估进行中时必须跳过：逐条评估会陆续产生合格条目，此时扫描会把
            // “先评估完的低优先级候选”提前登记下载，重新引入 P4 要消除的先到先下问题；
            // 批次完成时自会统一择优。恢复扫描只处理上次进程遗留的中断窗口。
            var hasRunningRefresh = await db.RefreshWorkItems
                                            .AnyAsync(item => item.Status == RefreshWorkStatus.Running,
                                                      cancellationToken);
            if (hasRunningRefresh) return;

            var scan = await ScanGuardedGroupsAsync(db, cancellationToken);
            pendingKeys = [];

            if (downloaderReady)
            {
                var selections = await db.CandidateSelections.AsNoTracking()
                                         .Select(item => new { item.SubjectId, item.MediaType, item.Episode })
                                         .ToListAsync(cancellationToken);
                var knownKeys = selections.Select(item => (item.SubjectId, item.MediaType, item.Episode)).ToHashSet();

                pendingKeys.AddRange(scan.GroupKeys
                                         .Where(key => !knownKeys.Contains((key.SubjectId, key.MediaType,
                                                                            key.Episode))));

                var unregistered = await db.CandidateSelections.AsNoTracking()
                                           .Where(item => item.IntentTaskId       == null &&
                                                          item.IntentFailedReason == null)
                                           .Select(item => new { item.SubjectId, item.MediaType, item.Episode })
                                           .ToListAsync(cancellationToken);
                // 同一等待规则也适用于“有选择记录但意图未登记”的组（如下载器后配置）：批次未完成时
                // 不得按旧候选登记下载意图，等批次完成后的统一择优或下一轮恢复。
                pendingKeys.AddRange(unregistered
                                    .Where(item => !scan.WaitingKeys.Contains(
                                                                              (item.SubjectId, item.MediaType,
                                                                               item.Episode)))
                                    .Select(item =>
                                                new SelectionGroupKey(item.SubjectId, item.MediaType, item.Episode)));
            }

            // 版本对账窗口：选择记录的规则版本与当前生效规则不一致即重选，不只看落后——
            // 配置损坏回退默认会把版本号重置回 1，高于当前版本的旧记录同样要对账到当前规则
            // （提交守卫按“当前生效规则优先”允许这类提交）。同样受批次等待守卫约束。
            var currentRuleVersion = (await ruleProvider.GetAsync(cancellationToken)).Version;
            var versionMismatched = await db.CandidateSelections.AsNoTracking()
                                            .Where(item => item.RuleVersion != currentRuleVersion)
                                            .Select(item => new { item.SubjectId, item.MediaType, item.Episode })
                                            .ToListAsync(cancellationToken);
            pendingKeys.AddRange(versionMismatched
                                .Where(item => !scan.WaitingKeys.Contains(
                                                                          (item.SubjectId, item.MediaType,
                                                                           item.Episode)))
                                .Select(item =>
                                            new SelectionGroupKey(item.SubjectId, item.MediaType, item.Episode)));
            pendingKeys = pendingKeys.Distinct().ToList();
        }

        if (pendingKeys.Count == 0) return;

        logger.LogWarning("发现 {Count} 个待恢复的同集选择，重新择优并登记下载意图。", pendingKeys.Count);
        await SelectAndRegisterAsync(pendingKeys, cancellationToken : cancellationToken);
    }

    /// <summary>
    ///     规则版本变更后的整库重选：对全部合格组（含已有选择记录的组）按新规则重算并落库。
    ///     不做恢复扫描的“已知选择 / 未登记意图”窗口判定，只保留组级 Pending 守卫；
    ///     自动路径语义（不重试已失败的种子登记）。返回处理（尝试重选）的组数。
    /// </summary>
    public async Task<int> ReselectAllGroupsAsync(CancellationToken cancellationToken = default)
    {
        List<SelectionGroupKey> groupKeys;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            groupKeys = (await ScanGuardedGroupsAsync(db, cancellationToken)).GroupKeys;
        }

        if (groupKeys.Count == 0) return 0;

        await SelectAndRegisterAsync(groupKeys, false, cancellationToken);
        logger.LogInformation("规则版本变更后整库重选完成，共处理 {Count} 个同集组。", groupKeys.Count);
        return groupKeys.Count;
    }

    /// <summary>
    ///     保存前比较预览：对当前全部“可参与重选”的合格组用草拟规则重算优胜（复用
    ///     PlanSingleGroupAsync，与执行同一条计算路径），与已保存选择比较。
    ///     纯读操作：不落库、不下载种子、不创建任务。单组失败不拖垮整批。
    /// </summary>
    public async Task<SelectionPreviewResult> PreviewRuleChangeAsync(
        SelectionRuleConfig draftConfig, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var groupKeys = (await ScanGuardedGroupsAsync(db, cancellationToken)).GroupKeys;

        var subjectNames = (await db.MediaSubjects.AsNoTracking()
                                    .Where(subject => groupKeys.Select(key => key.SubjectId)
                                                               .Distinct()
                                                               .Contains(subject.Id))
                                    .Select(subject => new { subject.Id, subject.Name, subject.NameCn })
                                    .ToListAsync(cancellationToken))
           .ToDictionary(subject => subject.Id, subject =>
                             string.IsNullOrWhiteSpace(subject.NameCn) ? subject.Name : subject.NameCn);

        var currentWinners = (await db.CandidateSelections.AsNoTracking()
                                      .Select(item => new
                                       {
                                           item.SubjectId, item.MediaType, item.Episode,
                                           item.WinnerFeedItemId
                                       })
                                      .ToListAsync(cancellationToken))
           .ToDictionary(item => (item.SubjectId, item.MediaType, item.Episode),
                         item => item.WinnerFeedItemId);

        var plans = new List<GroupSelectionPlan>();
        foreach (var key in groupKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                plans.Add(await PlanSingleGroupAsync(db, key, draftConfig, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "同集组 {SubjectId}/{MediaType}/{Episode} 规则变更预览失败。",
                                key.SubjectId, key.MediaType, key.Episode);
            }
        }

        // 当前优胜与草拟优胜涉及的条目标题一次性批量取出，避免逐组查询。
        var involvedItemIds = plans.Select(plan => plan.Winner?.FeedItemId)
                                   .Where(itemId => itemId is not null)
                                   .Select(itemId => itemId!.Value)
                                   .Concat(currentWinners.Values)
                                   .Distinct()
                                   .ToList();
        var titleById = (await db.FeedItems.AsNoTracking()
                                 .Where(item => involvedItemIds.Contains(item.Id))
                                 .Select(item => new { item.Id, item.Title })
                                 .ToListAsync(cancellationToken))
           .ToDictionary(item => item.Id, item => item.Title);

        var groups = plans.Select(plan =>
        {
            var currentWinnerId = currentWinners.TryGetValue(
                                                             (plan.Key.SubjectId, plan.Key.MediaType, plan.Key.Episode),
                                                             out var winnerId)
                ? (Guid?)winnerId
                : null;
            var draftWinnerId = plan.Winner?.FeedItemId;
            return new SelectionPreviewGroup(
                                             plan.Key,
                                             subjectNames.TryGetValue(plan.Key.SubjectId, out var name)
                                                 ? name
                                                 : $"#{plan.Key.SubjectId}",
                                             currentWinnerId,
                                             currentWinnerId is not null &&
                                             titleById.TryGetValue(currentWinnerId.Value, out var currentTitle)
                                                 ? currentTitle
                                                 : null,
                                             plan.Winner,
                                             draftWinnerId is not null &&
                                             titleById.TryGetValue(draftWinnerId.Value, out var draftTitle)
                                                 ? draftTitle
                                                 : null,
                                             plan.WinnerBasis,
                                             currentWinnerId != draftWinnerId);
        }).ToList();

        return new SelectionPreviewResult(groups.Count, groups.Count(group => group.WinnerChanged), groups);
    }

    /// <summary>
    ///     合格条目按同集分组并应用组级 Pending 守卫：订阅仍有待评估条目 = 该订阅的刷新批次
    ///     尚未完成解析（含启动恢复把中断工作转为 Pending 后尚未消费的窗口），此时按部分候选
    ///     提前择优会先下载低优先级版本，必须等批次完成；组内任一候选所属订阅仍有 Pending 条目
    ///     即整组等待（跨订阅成组时只看单个候选，会漏掉另一订阅尚未评估的同集条目）。
    ///     同时返回仍被待评估条目阻塞的组键，供恢复扫描判定“有选择记录但意图未登记”的组。
    /// </summary>
    private static async Task<GuardedGroupScan> ScanGuardedGroupsAsync(
        ApplicationDbContext db, CancellationToken cancellationToken)
    {
        // SQLite 不支持 DateTimeOffset 服务端排序与复杂集合比较，候选先取回内存再求差。
        var accepted = await db.FeedItems.AsNoTracking()
                               .Where(item => item.Status    == FeedItemStatus.Accepted &&
                                              item.SubjectId != null                    &&
                                              item.Episode   != null                    &&
                                              item.MediaType != null                    &&
                                              item.MediaType != "MultipleEpisode")
                               .Select(item => new
                                {
                                    item.SubscriptionId, item.SubjectId, item.MediaType,
                                    item.Episode
                                })
                               .ToListAsync(cancellationToken);
        var subscriptionsWithPending = (await db.FeedItems.AsNoTracking()
                                                .Where(item => item.Status == FeedItemStatus.Pending)
                                                .Select(item => item.SubscriptionId)
                                                .Distinct()
                                                .ToListAsync(cancellationToken))
           .ToHashSet();

        var waitingKeys = accepted
                         .Where(item => subscriptionsWithPending.Contains(item.SubscriptionId))
                         .Select(item => (item.SubjectId!.Value, item.MediaType!, item.Episode!.Value))
                         .ToHashSet();

        var groupKeys = accepted
                       .GroupBy(item => (item.SubjectId!.Value, item.MediaType!, item.Episode!.Value))
                       .Where(group => group.All(item =>
                                                     !subscriptionsWithPending.Contains(item.SubscriptionId)))
                       .Select(group => new SelectionGroupKey(group.Key.Item1, group.Key.Item2,
                                                              group.Key.Item3))
                       .ToList();

        return new GuardedGroupScan(groupKeys, waitingKeys);
    }

    private async Task<GroupSelectionPlan> PlanSingleGroupAsync(
        ApplicationDbContext db, SelectionGroupKey key, SelectionRuleConfig config,
        CancellationToken    cancellationToken)
    {
        var items = await db.FeedItems.AsNoTracking()
                            .Where(item => item.Status    == FeedItemStatus.Accepted &&
                                           item.SubjectId == key.SubjectId           &&
                                           item.MediaType == key.MediaType           &&
                                           item.Episode   == key.Episode)
                            .ToListAsync(cancellationToken);

        var candidates = items.Select(BuildCandidate)
                              .Where(candidate => candidate is not null)
                              .Select(candidate => candidate!)
                              .ToList();

        var currentWinnerId = await db.CandidateSelections.AsNoTracking()
                                      .Where(item => item.SubjectId == key.SubjectId &&
                                                     item.MediaType == key.MediaType &&
                                                     item.Episode   == key.Episode)
                                      .Select(item => (Guid?)item.WinnerFeedItemId)
                                      .SingleOrDefaultAsync(cancellationToken);

        if (candidates.Count == 0)
            return new GroupSelectionPlan(key, null, "组内没有合格候选。", config.Version, []);

        // 历史候选可能在新规则下不再满足硬性过滤；只排除出竞争，不改写已保存的条目状态。
        var eligible = candidates
                      .Where(candidate =>
                                 eligibilityPolicy.Evaluate(candidate, config).Status == EligibilityStatus.Eligible)
                      .ToList();
        if (eligible.Count == 0)
            return new GroupSelectionPlan(key, null, "组内没有满足硬性过滤的候选。", config.Version, []);

        var result = rankingPolicy.Select(eligible, config, currentWinnerId);
        return new GroupSelectionPlan(key, result.Winner, result.WinnerBasis, config.Version, result.Ranking);
    }

    /// <summary>
    ///     把一组选择计划落库：组内条目 SelectionOutcome 与选择记录在同一事务更新；
    ///     有优胜时在事务外先完成种子下载，再在同一事务内登记下载意图。
    ///     同一优胜且意图已有着落（已登记任务或已记录失败）时不重试种子下载：失败等待人工
    ///     处理，仅人工重评入口（resetFailedIntent）显式要求时重试；优胜变化则重置意图字段
    ///     后重新登记。种子获取失败时保留选择记录并写入失败原因，不自动反复重试。
    /// </summary>
    private async Task CommitSelectionAsync(GroupSelectionPlan plan, bool resetFailedIntent,
                                            CancellationToken  cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 先读现有选择记录，用于判断意图是否需要（重新）登记；写入仍在下方同一事务完成。
            var selection = await db.CandidateSelections
                                    .SingleOrDefaultAsync(item => item.SubjectId == plan.Key.SubjectId &&
                                                                  item.MediaType == plan.Key.MediaType &&
                                                                  item.Episode   == plan.Key.Episode,
                                                          cancellationToken);

            // 提交前版本守卫：计划版本与当前生效规则双向对账。高于当前生效版本说明计划读取
            // 快照后规则发生了回退/重置（如配置损坏回退默认会把版本号重置回 1），过期计划
            // 不得覆盖回退后的选择或登记旧下载意图，跳过并交由恢复扫描按当前规则补齐。
            // 低于当前生效版本时维持双条件：已落库选择与当前生效规则都比本计划新才跳过
            // （规则的版本只会递增；若已落库版本不低于当前生效规则，说明它来自回退前的
            // 旧版本，按当前规则正常提交正是恢复扫描期望的对账行为）。
            var currentVersion = (await ruleProvider.GetAsync(cancellationToken)).Version;
            if (plan.RuleVersion > currentVersion)
            {
                logger.LogInformation(
                                      "同集组 {Key} 当前生效规则版本 {CurrentVersion} 低于计划版本 {PlanVersion}（规则已回退或重置），跳过过期计划，待恢复扫描按当前规则补齐。",
                                      plan.Key, currentVersion, plan.RuleVersion);
                return;
            }

            if (selection is { RuleVersion: var stored } && stored > plan.RuleVersion &&
                plan.RuleVersion                                   < currentVersion)
            {
                logger.LogInformation(
                                      "同集组 {Key} 已有规则版本 {StoredVersion} 的选择，跳过按版本 {PlanVersion} 计算的过期计划。",
                                      plan.Key, stored, plan.RuleVersion);
                return;
            }

            FeedItem? winnerItem   = null;
            byte[]?   torrentBytes = null;
            string?   infoHash     = null;
            string?   intentError  = null;
            // 下载器未启用或未配置时只落库选择，不登记意图；配置好后由恢复扫描自动补齐。
            var downloaderReady = await qbittorrentSettings.GetAsync(cancellationToken) is
                { Enabled: true, IsConfigured: true };
            var winnerChanged = plan.Winner is not null && selection is not null &&
                                selection.WinnerFeedItemId != plan.Winner.FeedItemId;
            var intentSettled = selection is not null &&
                                (selection.IntentTaskId is not null || selection.IntentFailedReason is not null);
            var retryFailedIntent = resetFailedIntent              && selection is not null &&
                                    selection.IntentTaskId is null &&
                                    selection.IntentFailedReason is not null;
            var shouldAttemptIntent = plan.Winner is not null && downloaderReady &&
                                      (winnerChanged || !intentSettled || retryFailedIntent);
            if (plan.Winner is { } winner)
            {
                winnerItem = await db.FeedItems
                                     .Include(item => item.Subject)
                                     .Include(item => item.Subscription)
                                     .SingleOrDefaultAsync(item => item.Id == winner.FeedItemId, cancellationToken);
                if (winnerItem is null)
                    logger.LogWarning("同集组 {Key} 的优胜条目 {ItemId} 已不存在，按无优胜处理。", plan.Key,
                                      winner.FeedItemId);
                else if (shouldAttemptIntent)
                    try
                    {
                        // 种子下载跟随所属订阅的代理配置，与 RSS 抓取保持同一出口。
                        (torrentBytes, infoHash) =
                            await downloadService.DownloadTorrentAsync(winnerItem.TorrentUrl!,
                                                                       winnerItem.Subscription.ProxyId,
                                                                       cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        intentError = DescribeIntentError(exception);
                        logger.LogWarning(exception, "同集组 {Key} 的优胜候选获取种子失败。", plan.Key);
                    }
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (plan.Winner is null || winnerItem is null)
            {
                // 组内不再有有效优胜：复位全组旧的胜负标记（现任优胜与残留 RunnerUp，含已被
                // 新规则硬性过滤排除的历史候选）并移除选择记录；已创建的下载任务保持不动。
                if (selection is not null)
                {
                    var groupItems = await db.FeedItems
                                             .Where(item => item.SubjectId        == plan.Key.SubjectId &&
                                                            item.MediaType        == plan.Key.MediaType &&
                                                            item.Episode          == plan.Key.Episode   &&
                                                            item.SelectionOutcome != SelectionOutcome.None)
                                             .ToListAsync(cancellationToken);
                    foreach (var item in groupItems) item.SelectionOutcome = SelectionOutcome.None;
                    db.CandidateSelections.Remove(selection);
                }
            }
            else
            {
                foreach (var rank in plan.Ranking)
                {
                    var tracked = await db.FeedItems
                                          .SingleAsync(item => item.Id == rank.Candidate.FeedItemId,
                                                       cancellationToken);
                    // 未命中规则进入待确认的候选不参与胜负，不得标成组内落选。
                    tracked.SelectionOutcome = rank.IsExcluded
                        ? SelectionOutcome.None
                        : rank.Candidate.FeedItemId == plan.Winner.FeedItemId
                            ? SelectionOutcome.Winner
                            : SelectionOutcome.RunnerUp;
                }

                if (selection is null)
                {
                    selection = new CandidateSelection
                    {
                        Id        = Guid.NewGuid(),
                        SubjectId = plan.Key.SubjectId,
                        MediaType = plan.Key.MediaType,
                        Episode   = plan.Key.Episode
                    };
                    db.CandidateSelections.Add(selection);
                }
                else if (winnerChanged)
                {
                    // 旧优胜不在本轮参与名单（如已被人工改组）时复位；在名单内者由上面的循环处理。
                    var previousWinner = await db.FeedItems
                                                 .SingleOrDefaultAsync(item =>
                                                                           item.Id == selection.WinnerFeedItemId,
                                                                       cancellationToken);
                    if (previousWinner is not null &&
                        plan.Ranking.All(rank => rank.Candidate.FeedItemId != previousWinner.Id))
                        previousWinner.SelectionOutcome = SelectionOutcome.None;

                    // 优胜变化后旧任务 Id / 旧失败原因不再代表当前选择，必须重置：留下旧值会让
                    // 恢复扫描（只处理两者皆空的记录）永远错过为新优胜补登记。
                    selection.IntentTaskId       = null;
                    selection.IntentFailedReason = null;
                }

                selection.WinnerFeedItemId = plan.Winner.FeedItemId;
                selection.RuleVersion      = plan.RuleVersion;
                selection.RankingBasis     = plan.WinnerBasis;
                selection.NamingSnapshot   = winnerItem.NamingPreview ?? string.Empty;
                selection.SelectedAtUtc    = timeProvider.GetUtcNow();

                if (intentError is not null)
                {
                    selection.IntentFailedReason = intentError;
                }
                else if (!downloaderReady)
                {
                    // 下载器未就绪：只落库选择记录，意图留待恢复扫描在配置完成后补齐。
                }
                else if (!shouldAttemptIntent)
                {
                    // 同一优胜且意图已有着落：保留现有任务引用或失败原因，等待人工处理。
                }
                else
                {
                    try
                    {
                        // 同一资源已有任意任务（含失败与被替换）时不重复登记：重新下载同一 hash 没有增益，
                        // 失败任务等待人工重试；同条目活动任务直接复用。
                        var tasks = await db.DownloadTasks.AsNoTracking()
                                            .Where(task => task.InfoHash   == infoHash ||
                                                           task.FeedItemId == winnerItem.Id)
                                            .Select(task => new { task.Id, task.InfoHash, task.Status })
                                            .ToListAsync(cancellationToken);
                        var duplicate = tasks.FirstOrDefault(task => task.InfoHash == infoHash) ??
                                        tasks.FirstOrDefault(task => task.Status.IsActive());
                        if (duplicate is not null)
                        {
                            selection.IntentTaskId       = duplicate.Id;
                            selection.IntentFailedReason = null;
                        }
                        else
                        {
                            selection.IntentTaskId = await downloadService.CreateTaskWithinContextAsync(
                                 db, winnerItem, torrentBytes!, infoHash!, cancellationToken);
                            selection.IntentFailedReason = null;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        selection.IntentTaskId       = null;
                        selection.IntentFailedReason = DescribeIntentError(exception);
                        logger.LogWarning(exception, "同集组 {Key} 登记下载意图失败。", plan.Key);
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "同集组 {SubjectId}/{MediaType}/{Episode} 的选择结果落库失败，等待下轮恢复。",
                            plan.Key.SubjectId, plan.Key.MediaType, plan.Key.Episode);
        }
    }

    /// <summary>把条目快照转换为择优候选；解析字段缺失或为多集合集时返回 null。</summary>
    private static EpisodeCandidate? BuildCandidate(FeedItem item)
    {
        if (item.SubjectId is null || item.Episode is null || item.MediaType is null) return null;
        if (string.Equals(item.MediaType, "MultipleEpisode", StringComparison.Ordinal)) return null;

        EnumLanguage? language = null;
        if (!string.IsNullOrWhiteSpace(item.Language) &&
            Enum.TryParse(item.Language, true, out EnumLanguage parsedLanguage))
            language = parsedLanguage;

        var resolution = Enum.TryParse(item.Resolution, true, out EnumResolution parsedResolution)
            ? parsedResolution
            : EnumResolution.Unknown;

        return new EpisodeCandidate
        {
            FeedItemId     = item.Id,
            SubjectId      = item.SubjectId.Value,
            MediaType      = item.MediaType,
            Episode        = item.Episode.Value,
            Group          = item.Group ?? string.Empty,
            Language       = language,
            Resolution     = resolution,
            Version        = item.Version,
            PublishedAtUtc = item.PublishedAtUtc,
            StableId       = item.StableId
        };
    }

    private static string DescribeIntentError(Exception exception)
    {
        return exception switch
        {
            InvalidOperationException => exception.Message,
            HttpRequestException      => "获取种子文件失败。",
            _                         => "登记下载意图时发生意外错误。"
        };
    }

    /// <summary>组级守卫扫描结果：通过守卫的组键，以及仍被待评估条目阻塞的组键集合。</summary>
    private sealed record GuardedGroupScan(
        List<SelectionGroupKey>                                     GroupKeys,
        HashSet<(int SubjectId, string MediaType, decimal Episode)> WaitingKeys);
}
