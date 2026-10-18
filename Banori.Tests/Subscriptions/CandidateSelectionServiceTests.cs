using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Downloads;
using Banori.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Banori.Tests.Subscriptions;

/// <summary>
///     同集批量择优与下载意图登记的 SQLite 集成回归：组内胜负、重复保护、
///     跨轮升级与中断恢复。普通测试只用替身下载器与临时数据库。
/// </summary>
public sealed class CandidateSelectionServiceTests
{
    private const int     SubjectId = 42;
    private const string  MediaType = "SingleEpisode";
    private const decimal Episode   = 12m;

    [Fact]
    public async Task Select_HigherPriorityGroupWins_RegardlessOfInsertOrder()
    {
        foreach (var order in new[] { new[] { "LoliHouse", "ANi" }, new[] { "ANi", "LoliHouse" } })
        {
            using var harness = await CreateHarnessAsync();
            // 按给定顺序依次入库，验证数据库中的条目先后不影响择优结果。
            var itemIds = new Dictionary<string, Guid>();
            foreach (var group in order)
                itemIds[group] =
                    await harness.AddItemAsync($"guid:{group.ToLowerInvariant()}", group, "JpSc", 1);

            var plans = await harness.SelectionService
                                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

            var plan = Assert.Single(plans);
            Assert.NotNull(plan.Winner);
            Assert.Contains("LoliHouse", plan.WinnerBasis, StringComparison.Ordinal);

            await using var db        = await harness.CreateDbContextAsync();
            var             selection = Assert.Single(await db.CandidateSelections.ToListAsync());
            Assert.Equal(itemIds["LoliHouse"], selection.WinnerFeedItemId);
            Assert.Equal(SubjectId, selection.SubjectId);
            Assert.Equal(MediaType, selection.MediaType);
            Assert.Equal(Episode, selection.Episode);
            Assert.NotNull(selection.IntentTaskId);
            Assert.Null(selection.IntentFailedReason);
            Assert.Equal(SelectionOutcome.Winner,
                         (await db.FeedItems.SingleAsync(item => item.Id == itemIds["LoliHouse"]))
                        .SelectionOutcome);
            Assert.Equal(SelectionOutcome.RunnerUp,
                         (await db.FeedItems.SingleAsync(item => item.Id == itemIds["ANi"]))
                        .SelectionOutcome);
            var task = Assert.Single(await db.DownloadTasks.Include(value => value.Files).ToListAsync());
            Assert.Equal(selection.IntentTaskId, task.Id);
            Assert.Equal("/downloads", task.DownloadRootPath);
            Assert.Equal(DownloadDirectorySettings.DefaultMediaDirectory, task.MediaDirectory);
            Assert.Equal(DownloadDirectorySettings.DefaultSeedDirectory, task.SeedDirectory);
            Assert.Equal("/downloads/Media/Anime", task.SavePath);
            var file = Assert.Single(task.Files);
            Assert.Equal(task.Id, file.DownloadTaskId);
            Assert.Equal(0, file.FileIndex);
            Assert.Equal(SelectionTorrentFileName("guid:lolihouse"), file.OriginalRelativePath);
            Assert.Equal(file.OriginalRelativePath, file.CurrentRelativePath);
            Assert.Equal(1024L, file.Size);
            Assert.Equal(DownloadFileKind.Anime, file.Kind);
            Assert.False(file.IsManualOverride);
            Assert.Equal(harness.Timestamp, file.UpdatedAtUtc);
        }
    }

    [Fact]
    public async Task Select_LaterHigherPriorityCandidate_RegistersUpgradeAndKeepsOldTask()
    {
        using var harness = await CreateHarnessAsync();
        var       aniItem = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);

        var firstPlans =
            await harness.SelectionService
                         .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);
        var firstTaskId = (await harness.GetSelectionAsync()).IntentTaskId;

        var loliHouseItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliHouseItem, selection.WinnerFeedItemId);
        Assert.NotEqual(firstTaskId, selection.IntentTaskId);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(2, await db.DownloadTasks.CountAsync());
        // 候选选择服务自身重选只更新选择意图；订阅 DownloadService 登记优胜时才依据跨路径裁决终止旧任务。
        var oldTask = await db.DownloadTasks.SingleAsync(task => task.Id == firstTaskId);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, oldTask.Status);
        Assert.Null(oldTask.ReplacedByTaskId);
        Assert.Equal(SelectionOutcome.RunnerUp,
                     (await db.FeedItems.SingleAsync(item => item.Id == aniItem)).SelectionOutcome);
    }

    [Fact]
    public async Task Select_SameHashCompletedTask_IsReusedNotRecreated()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await harness.AddItemAsync("guid:item", "ANi", "JpSc", 1);
        await harness.AddCompletedTaskForAsync(itemId);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var             selection = await harness.GetSelectionAsync();
        await using var db        = await harness.CreateDbContextAsync();
        Assert.Equal(1, await db.DownloadTasks.CountAsync());
        Assert.Equal(await db.DownloadTasks.Select(task => task.Id).SingleAsync(), selection.IntentTaskId);
    }

    [Fact]
    public async Task CreateForFeedItem_NonWinnerAcceptedItem_IsRejected()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await harness.AddItemAsync("guid:accepted-only", "ANi", "JpSc", 1);

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    harness.IntakeService
                                                                           .CreateForFeedItemAsync(itemId));

        Assert.Contains("优胜候选", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_RunningRefreshWorkInProgress_SkipsRecovery()
    {
        using var harness = await CreateHarnessAsync();
        // 批次评估进行中（存在执行中刷新工作）：合格条目即使还没有选择记录，
        // 也不得被恢复扫描提前择优登记，否则先评估完的低优先级候选会先被下载。
        await harness.AddItemAsync("guid:evaluating", "ANi", "JpSc", 1);
        await harness.AddRunningWorkAsync();

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(0, await db.CandidateSelections.CountAsync());
        Assert.Equal(0, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Recover_MissingSelectionForAcceptedGroup_IsRestoredWithoutFeed()
    {
        using var harness = await CreateHarnessAsync();
        // 模拟"择优计算后、选择落库前"中断：条目已合格但组内没有任何选择记录。
        await harness.AddItemAsync("guid:orphan", "ANi", "JpSc", 1);

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.NotNull(selection);
        Assert.NotNull(selection.IntentTaskId);
        Assert.Null(selection.IntentFailedReason);
    }

    [Fact]
    public async Task Recover_SubscriptionStillHasPendingItems_SkipsGroupUntilBatchCompletes()
    {
        using var harness = await CreateHarnessAsync();
        // 批次评估中途中断（工作已被启动恢复转为 Pending）：已评估的部分候选成组，
        // 但订阅内仍有待评估条目——此时择优会用不完整候选集，先到先下问题重现。
        await harness.AddItemAsync("guid:partially-evaluated", "ANi", "JpSc", 1);
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.FeedItems.Add(new FeedItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = (await db.MediaSubscriptions.SingleAsync()).Id,
                StableId       = "guid:not-yet-evaluated",
                Title          = "[ANi] 测试作品 - 13",
                TorrentUrl     = "https://mikanani.me/Download/guid:not-yet-evaluated.torrent",
                FirstSeenAtUtc = harness.Timestamp,
                Status         = FeedItemStatus.Pending,
                Reason         = "等待处理。"
            });
            await db.SaveChangesAsync();
        }

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        await using (var db = await harness.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.CandidateSelections.CountAsync());
            Assert.Equal(0, await db.DownloadTasks.CountAsync());
        }

        // 批次完成后（待评估条目已处理），中断窗口由恢复扫描正常补齐。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var pending = await db.FeedItems.SingleAsync(item => item.StableId == "guid:not-yet-evaluated");
            pending.Status = FeedItemStatus.Rejected;
            await db.SaveChangesAsync();
        }

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.NotNull(selection.IntentTaskId);
    }

    [Fact]
    public async Task Recover_UnregisteredSelectionWithPendingSubscription_SkipsGroupUntilBatchCompletes()
    {
        using var harness = await CreateHarnessAsync();
        // 批次评估中途中断：组内已有选择记录（如下载器未配置时落库）但意图未登记，且同订阅
        // 仍有待评估条目——与“无选择记录”分支同一等待规则，不得按旧候选提前登记下载意图。
        var itemId = await harness.AddItemAsync("guid:unregistered", "ANi", "JpSc", 1);
        await harness.AddSelectionRecordAsync(itemId, null, null);
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.FeedItems.Add(new FeedItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = (await db.MediaSubscriptions.SingleAsync()).Id,
                StableId       = "guid:not-yet-evaluated",
                Title          = "[ANi] 测试作品 - 13",
                TorrentUrl     = "https://mikanani.me/Download/guid:not-yet-evaluated.torrent",
                FirstSeenAtUtc = harness.Timestamp,
                Status         = FeedItemStatus.Pending,
                Reason         = "等待处理。"
            });
            await db.SaveChangesAsync();
        }

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.Null(selection.IntentTaskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.DownloadTasks.CountAsync());
        }

        // 批次完成后（待评估条目已处理），中断窗口由恢复扫描正常补齐。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var pending = await db.FeedItems.SingleAsync(item => item.StableId == "guid:not-yet-evaluated");
            pending.Status = FeedItemStatus.Rejected;
            await db.SaveChangesAsync();
        }

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        selection = await harness.GetSelectionAsync();
        Assert.NotNull(selection.IntentTaskId);
    }

    [Fact]
    public async Task Select_WinnerChangesWhileDownloaderNotReady_RecoveryRegistersNewWinner()
    {
        using var harness = await CreateHarnessAsync(false);
        var       aniItem = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var loliHouseItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        // 下载器未就绪时换了优胜：意图字段必须重置，不得继续指向旧优胜的任务。
        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliHouseItem, selection.WinnerFeedItemId);
        Assert.Null(selection.IntentTaskId);
        Assert.Null(selection.IntentFailedReason);

        await harness.SaveDownloaderSettingsAsync();
        await harness.SelectionService.RecoverPendingSelectionsAsync();

        selection = await harness.GetSelectionAsync();
        Assert.NotNull(selection.IntentTaskId);
        await using var db = await harness.CreateDbContextAsync();
        var task = await db.DownloadTasks.AsNoTracking()
                           .SingleAsync(item => item.Id == selection.IntentTaskId);
        Assert.Equal(loliHouseItem, task.FeedItemId);
    }

    [Fact]
    public async Task Select_WinnerChangesThenTorrentFails_IntentFieldsResetForNewWinner()
    {
        using var harness = await CreateHarnessAsync();
        var       aniItem = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);
        var oldTaskId = (await harness.GetSelectionAsync()).IntentTaskId;

        // 新优胜的种子下载必然失败（未注册的 stableId 触发测试下载器异常）。
        var broken = await harness.AddItemAsync("guid:broken", "LoliHouse", "JpSc", 1);
        harness.MarkTorrentBroken("guid:broken");
        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(broken, selection.WinnerFeedItemId);
        // 旧任务引用与旧记录已被重置，失败原因描述的是新优胜，恢复与人工处理以此为准。
        Assert.NotEqual(oldTaskId, selection.IntentTaskId);
        Assert.Null(selection.IntentTaskId);
        Assert.NotNull(selection.IntentFailedReason);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(DownloadTaskStatus.PendingSubmit,
                     (await db.DownloadTasks.AsNoTracking().SingleAsync(item => item.Id == oldTaskId)).Status);
    }

    [Fact]
    public async Task Select_SameWinnerAfterTorrentFailure_DoesNotRetryUntilRequested()
    {
        using var harness = await CreateHarnessAsync();
        var       aniItem = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        harness.MarkTorrentBroken("guid:ani");

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);
        Assert.Equal("获取种子文件失败。", (await harness.GetSelectionAsync()).IntentFailedReason);

        // 低位新候选落选触发同一优胜重算：不得自动重试已失败的种子下载，失败记录保持等待人工处理。
        var runnerUp = await harness.AddItemAsync("guid:ani-b", "ANi", "JpSc", 1);
        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(aniItem, selection.WinnerFeedItemId);
        Assert.Null(selection.IntentTaskId);
        Assert.Equal("获取种子文件失败。", selection.IntentFailedReason);
        // 种子只拉取过一次（首次失败），同优胜重算没有重新下载。
        Assert.Equal(1, harness.TorrentFetchCount);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(0, await db.DownloadTasks.CountAsync());
        Assert.Equal(SelectionOutcome.RunnerUp,
                     (await db.FeedItems.SingleAsync(item => item.Id == runnerUp)).SelectionOutcome);

        // 人工重评路径显式要求重试（resetFailedIntent）时，修复种子来源后重试成功。
        harness.HealTorrent("guid:ani");
        await harness.SelectionService.SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)],
                                                              true);

        selection = await harness.GetSelectionAsync();
        Assert.NotNull(selection.IntentTaskId);
        Assert.Null(selection.IntentFailedReason);
        Assert.Equal(2, harness.TorrentFetchCount);
        Assert.Equal(1, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Select_SameWinnerRecompute_DoesNotRefetchTorrent()
    {
        using var harness = await CreateHarnessAsync();
        await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);
        var firstTaskId = (await harness.GetSelectionAsync()).IntentTaskId;
        Assert.NotNull(firstTaskId);

        // 同一优胜且意图已登记：新落选候选触发重算时不重新下载种子，也不新建任务。
        await harness.AddItemAsync("guid:ani-b", "ANi", "JpSc", 1);
        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        Assert.Equal(1, harness.TorrentFetchCount);
        var selection = await harness.GetSelectionAsync();
        Assert.Equal(firstTaskId, selection.IntentTaskId);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(1, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Select_NeedsReviewExcludedCandidate_IsNotMarkedRunnerUp()
    {
        // 未命中规则进入待确认的候选不参与胜负：同组另有优胜时不得标成组内落选。
        var config = new SelectionRuleConfig
        {
            Mode               = SelectionMode.Independent,
            GroupOrder         = ["ANi"],
            LanguageOrder      = [EnumLanguage.JpSc],
            DimensionOrder     = SelectionDimensionOrder.GroupFirst,
            CombinedEntries    = [],
            LanguageMatch      = LanguageMatchMode.Exact,
            Unmatched          = UnmatchedPolicy.NeedsReview,
            AllowedResolutions = [EnumResolution.R1080P]
        };
        using var harness      = await CreateHarnessAsync(ruleConfig : config);
        var       aniItem      = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       sakuratoItem = await harness.AddItemAsync("guid:sakurato", "Sakurato", "JpSc", 1);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(SelectionOutcome.Winner,
                     (await db.FeedItems.SingleAsync(item => item.Id == aniItem)).SelectionOutcome);
        Assert.Equal(SelectionOutcome.None,
                     (await db.FeedItems.SingleAsync(item => item.Id == sakuratoItem)).SelectionOutcome);
    }

    [Fact]
    public async Task Recover_UnregisteredSelection_CompletesIntentRegistration()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await harness.AddItemAsync("guid:unregistered", "ANi", "JpSc", 1);
        await harness.AddSelectionRecordAsync(itemId, null, null);

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.NotNull(selection.IntentTaskId);
        Assert.Null(selection.IntentFailedReason);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(1, await db.DownloadTasks.CountAsync(task => task.FeedItemId == itemId));
    }

    [Fact]
    public async Task Recover_SelectionWithFailedReason_IsNotRetriedAutomatically()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await harness.AddItemAsync("guid:failed", "ANi", "JpSc", 1);
        await harness.AddSelectionRecordAsync(itemId, null, "获取种子文件失败。");

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.Null(selection.IntentTaskId);
        Assert.Equal("获取种子文件失败。", selection.IntentFailedReason);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(0, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Preview_DraftRuleChangesWinner_MatchesExecutionResult()
    {
        using var harness  = await CreateHarnessAsync();
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        // 草拟规则把 ANi 排到 LoliHouse 之前：预览应给出与执行同一路径的草拟优胜。
        var draft   = SelectionRuleConfig.CreateDefault() with { GroupOrder = ["ANi", "LoliHouse"] };
        var preview = await harness.SelectionService.PreviewRuleChangeAsync(draft);

        Assert.Equal(1, preview.GroupCount);
        Assert.Equal(1, preview.ChangedCount);
        var group = Assert.Single(preview.Groups);
        Assert.Equal(SubjectId, group.Key.SubjectId);
        Assert.Equal(MediaType, group.Key.MediaType);
        Assert.Equal(Episode, group.Key.Episode);
        Assert.Equal("测试作品", group.SubjectName);
        Assert.Equal(loliItem, group.CurrentWinnerFeedItemId);
        Assert.Equal("[LoliHouse] 测试作品 - 12", group.CurrentWinnerTitle);
        Assert.NotNull(group.DraftWinner);
        Assert.Equal(aniItem, group.DraftWinner!.FeedItemId);
        Assert.Equal("[ANi] 测试作品 - 12", group.DraftWinnerTitle);
        Assert.True(group.WinnerChanged);

        // 保存草拟规则后整库重选：落库结果与预览一致（优胜、版本与优胜依据）。
        var saved = await harness.RuleSettings.SaveAsync(draft);
        Assert.True(saved.Changed);
        Assert.Equal(2, saved.Config.Version);

        await harness.SelectionService.ReselectAllGroupsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(aniItem, selection.WinnerFeedItemId);
        Assert.Equal(2, selection.RuleVersion);
        Assert.Equal(group.DraftWinnerBasis, selection.RankingBasis);
    }

    [Fact]
    public async Task Preview_SubscriptionStillHasPendingItems_GroupExcluded()
    {
        using var harness = await CreateHarnessAsync();
        await harness.AddItemAsync("guid:pending-group", "ANi", "JpSc", 1);
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.FeedItems.Add(new FeedItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = (await db.MediaSubscriptions.SingleAsync()).Id,
                StableId       = "guid:not-yet-evaluated",
                Title          = "[ANi] 测试作品 - 13",
                TorrentUrl     = "https://mikanani.me/Download/guid:not-yet-evaluated.torrent",
                FirstSeenAtUtc = harness.Timestamp,
                Status         = FeedItemStatus.Pending,
                Reason         = "等待处理。"
            });
            await db.SaveChangesAsync();
        }

        var preview = await harness.SelectionService
                                   .PreviewRuleChangeAsync(SelectionRuleConfig.CreateDefault());

        Assert.Equal(0, preview.GroupCount);
        Assert.Empty(preview.Groups);
    }

    [Fact]
    public async Task ReselectAll_ExistingSelectionRecomputed_ReturnsGroupCountAndKeepsVersion()
    {
        using var harness  = await CreateHarnessAsync();
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);

        await harness.SelectionService
                     .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);
        Assert.Equal(1, (await harness.GetSelectionAsync()).RuleVersion);

        // provider 规则未变化：重选按同一规则执行，优胜与版本保持不变。
        var processed = await harness.SelectionService.ReselectAllGroupsAsync();

        Assert.True(processed >= 1);
        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliItem, selection.WinnerFeedItemId);
        Assert.Equal(1, selection.RuleVersion);
    }

    [Fact]
    public async Task ReselectAll_SameWinnerWithFailedIntent_DoesNotRetry()
    {
        using var harness  = await CreateHarnessAsync();
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        await harness.AddSelectionRecordAsync(loliItem, null, "获取种子文件失败。");

        var processed = await harness.SelectionService.ReselectAllGroupsAsync();

        Assert.True(processed >= 1);
        // 同一优胜且失败已有记录：自动路径不重试种子登记，失败原因保留等待人工处理。
        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliItem, selection.WinnerFeedItemId);
        Assert.Null(selection.IntentTaskId);
        Assert.Equal("获取种子文件失败。", selection.IntentFailedReason);
        Assert.Equal(0, harness.TorrentFetchCount);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(0, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task ReselectAll_SubscriptionStillHasPendingItems_SkipsGroup()
    {
        using var harness = await CreateHarnessAsync();
        await harness.AddItemAsync("guid:guarded", "ANi", "JpSc", 1);
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.FeedItems.Add(new FeedItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = (await db.MediaSubscriptions.SingleAsync()).Id,
                StableId       = "guid:not-yet-evaluated",
                Title          = "[ANi] 测试作品 - 13",
                TorrentUrl     = "https://mikanani.me/Download/guid:not-yet-evaluated.torrent",
                FirstSeenAtUtc = harness.Timestamp,
                Status         = FeedItemStatus.Pending,
                Reason         = "等待处理。"
            });
            await db.SaveChangesAsync();
        }

        var processed = await harness.SelectionService.ReselectAllGroupsAsync();

        Assert.Equal(0, processed);
        await using var verify = await harness.CreateDbContextAsync();
        Assert.Equal(0, await verify.CandidateSelections.CountAsync());
        Assert.Equal(0, await verify.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Select_NoEligibleCandidateUnderNewRules_ClearsGroupOutcomes()
    {
        // 回归：新规则下全组无合格候选时，不仅要删选择记录，还要复位组内残留的
        // 优胜 / RunnerUp 标记，否则条目详情继续显示已不成立的落选状态。
        using var harness  = await CreateHarnessAsync();
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        await harness.SelectionService.SelectAndRegisterAsync([key]);
        Assert.Equal(SelectionOutcome.RunnerUp,
                     await harness.GetOutcomeAsync(aniItem));

        // 新规则只允许 720P：候选全是 1080P，全组无合格候选。
        await harness.RuleSettings.SaveAsync(SelectionRuleConfig.CreateDefault() with
        {
            AllowedResolutions = [EnumResolution.R720P]
        });

        await harness.SelectionService.SelectAndRegisterAsync([key]);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(0, await db.CandidateSelections.CountAsync());
        Assert.Equal(SelectionOutcome.None, await harness.GetOutcomeAsync(aniItem));
        Assert.Equal(SelectionOutcome.None, await harness.GetOutcomeAsync(loliItem));
    }

    [Fact]
    public async Task Recover_SelectionBehindCurrentRuleVersion_IsReselectedUnderNewRules()
    {
        // 回归：规则保存后的整库重选被打断（只处理了部分组）时，恢复扫描应把
        // 选择记录规则版本落后的组按当前规则补齐重选，不必再次保存规则。
        using var harness  = await CreateHarnessAsync();
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        await harness.SelectionService.SelectAndRegisterAsync([key]);
        // 模拟重选中断：规则已保存为 v2（ANi 优先），但不执行整库重选。
        await harness.RuleSettings.SaveAsync(SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["ANi", "LoliHouse"]
        });

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(aniItem, selection.WinnerFeedItemId);
        Assert.Equal(2, selection.RuleVersion);
        Assert.NotNull(selection.IntentTaskId);
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.SingleAsync(value => value.Id == selection.IntentTaskId);
        Assert.Equal(aniItem, task.FeedItemId);
    }

    [Fact]
    public async Task Select_ConcurrentBatchesOnSameGroup_SerializeToOneFetchAndTask()
    {
        // 回归：同组并发批次必须串行化；后到批次看到同一优胜且意图已落定，
        // 不再重复拉取种子或登记任务。
        using var harness  = await CreateHarnessAsync();
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        var first  = harness.SelectionService.SelectAndRegisterAsync([key]);
        var second = harness.SelectionService.SelectAndRegisterAsync([key]);
        await Task.WhenAll(first, second);

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliItem, selection.WinnerFeedItemId);
        Assert.Equal(1, harness.TorrentFetchCount);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(1, await db.CandidateSelections.CountAsync());
        Assert.Equal(1, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Select_PlanBehindNewerRuleVersion_IsSkippedAtCommit()
    {
        // 回归：批次启动后规则升版且其他路径已完成该组重选时，旧版本的过期计划
        // 不得在提交时覆盖较新的胜者与下载意图。
        var provider = new SwitchableRuleProvider
        {
            Config        = SelectionRuleConfig.CreateDefault(),
            HoldFirstRead = true
        };
        using var harness  = await CreateHarnessAsync(switchableProvider : provider);
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        // 慢批次在入口读取规则时挂起（快照为 v1，优胜 LoliHouse）。
        var slow = harness.SelectionService.SelectAndRegisterAsync([key]);
        await provider.FirstReadArrived;

        // 挂起期间规则升到 v2（ANi 优先），另一批次按 v2 完成整组选择。
        provider.Config = SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["ANi", "LoliHouse"],
            Version = 2
        };
        await harness.SelectionService.SelectAndRegisterAsync([key]);

        // 放行慢批次：它按 v1 计划出 LoliHouse，但提交必须被版本守卫跳过。
        provider.ReleaseFirstRead();
        await slow;

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(aniItem, selection.WinnerFeedItemId);
        Assert.Equal(2, selection.RuleVersion);
        // 只有 v2 优胜 ANi 的种子被拉取，v1 计划的 LoliHouse 不触发下载。
        Assert.Equal(1, harness.TorrentFetchCount);
    }

    [Fact]
    public async Task Select_PlanAheadOfRolledBackRuleVersion_IsSkippedAtCommit()
    {
        // 回归：规则版本回退（如配置损坏回退默认、版本号重置回 1）后，回退前读取快照的
        // 在途高版本计划不得在提交时覆盖回退后的选择，也不得为其优胜登记下载意图；
        // 提交守卫按“当前生效规则优先”双向对账，记录交由恢复扫描补齐。
        var provider = new SwitchableRuleProvider
        {
            Config = SelectionRuleConfig.CreateDefault() with
            {
                GroupOrder = ["ANi", "LoliHouse"],
                Version = 2
            },
            HoldFirstRead = true
        };
        using var harness  = await CreateHarnessAsync(switchableProvider : provider);
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        // 慢批次在入口读取规则时挂起（快照为 v2，优胜 ANi）。
        var slow = harness.SelectionService.SelectAndRegisterAsync([key]);
        await provider.FirstReadArrived;

        // 挂起期间配置损坏回退默认规则（LoliHouse 优先，版本重置为 1），重选按 v1 提交。
        provider.Config = SelectionRuleConfig.CreateDefault();
        await harness.SelectionService.SelectAndRegisterAsync([key]);

        // 放行慢批次：它按 v2 计划出 ANi，但提交必须被版本守卫跳过，不得覆盖 v1 选择，
        // 也不得再为 ANi 拉取种子登记任务。
        provider.ReleaseFirstRead();
        await slow;

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliItem, selection.WinnerFeedItemId);
        Assert.Equal(1, selection.RuleVersion);
        Assert.Equal(1, harness.TorrentFetchCount);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(1, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Select_ConcurrentScopesOnSameGroup_SharedGateSerializesToSingleIntent()
    {
        // 回归：门闸必须进程级共享——刷新 worker 与 Blazor 电路各持一个 Scoped 服务实例，
        // 实例级门闸无法互斥，同规则版本下旧候选快照仍可能覆盖较新的选择。
        // 编排：批次一停在提交中途（种子获取处），批次二从另一作用域进入同组；
        // 共享门闸时批次二必须等批次一完整提交后才开始规划，不再拉取种子。
        using var harness  = await CreateHarnessAsync();
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        // 批次一必须在线程池线程上启动：其同步前缀会一路执行到种子获取处的挂起，
        // 若在测试线程上启动会把自己阻塞在放行之前，形成自死锁。
        var (arrived, release) = harness.HoldNextTorrentFetch();
        var first = Task.Run(() => harness.SelectionService.SelectAndRegisterAsync([key]));
        await arrived;

        // 批次二从另一 DI 作用域进入同组：共享门闸时它必须等批次一完整提交后才开始规划，
        // 看到已落定的优胜与意图，不再拉取种子。等待窗口保证批次二确实已到达门闸。
        var second = harness.ParallelSelectionService.SelectAndRegisterAsync([key]);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        release();
        await Task.WhenAll(first, second);

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliItem, selection.WinnerFeedItemId);
        Assert.Equal(1, harness.TorrentFetchCount);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(1, await db.CandidateSelections.CountAsync());
        Assert.Equal(1, await db.DownloadTasks.CountAsync());
    }

    [Fact]
    public async Task Recover_SelectionAheadOfFallbackRuleVersion_IsReselectedUnderCurrentRules()
    {
        // 回归：配置损坏回退默认（版本号重置回 1）后，版本号高于当前的选择记录同样要
        // 对账到当前生效规则；版本对账用 != 而不是 < 才能覆盖该场景。
        using var harness  = await CreateHarnessAsync();
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        // ANi 优先规则（v2）下完成选择：优胜 ANi。
        await harness.RuleSettings.SaveAsync(SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["ANi", "LoliHouse"]
        });
        await harness.SelectionService.SelectAndRegisterAsync([key]);
        Assert.Equal(2, (await harness.GetSelectionAsync()).RuleVersion);

        // 配置损坏：读取回退默认规则（LoliHouse 优先，版本 1）。
        await harness.CorruptRuleSettingsAsync();

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(loliItem, selection.WinnerFeedItemId);
        Assert.Equal(1, selection.RuleVersion);
        Assert.NotNull(selection.IntentTaskId);
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.SingleAsync(value => value.Id == selection.IntentTaskId);
        Assert.Equal(loliItem, task.FeedItemId);
    }

    [Fact]
    public async Task Recover_DownloaderNotReady_StillFixesVersionMismatchedSelections()
    {
        // 回归：下载器未就绪只应跳过意图登记窗口；规则版本对账窗口不受下载器约束，
        // 否则下载器长期未配置时陈旧选择记录永远不会被修正。
        using var harness  = await CreateHarnessAsync(false);
        var       aniItem  = await harness.AddItemAsync("guid:ani", "ANi", "JpSc", 1);
        var       loliItem = await harness.AddItemAsync("guid:loli", "LoliHouse", "JpSc", 1);
        var       key      = new SelectionGroupKey(SubjectId, MediaType, Episode);

        await harness.SelectionService.SelectAndRegisterAsync([key]);
        await harness.RuleSettings.SaveAsync(SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["ANi", "LoliHouse"]
        });

        await harness.SelectionService.RecoverPendingSelectionsAsync();

        var selection = await harness.GetSelectionAsync();
        Assert.Equal(aniItem, selection.WinnerFeedItemId);
        Assert.Equal(2, selection.RuleVersion);
        // 下载器未就绪：只对账选择，意图仍留空，待配置完成后由未登记窗口补上。
        Assert.Null(selection.IntentTaskId);
        Assert.Equal(0, harness.TorrentFetchCount);
    }

    /// <summary>合法 BEP-3 单文件种子：稳定标识只用于派生安全文件名，不直接成为路径。</summary>
    private static byte[] CreateSelectionTorrentBytes(string stableId)
    {
        var name = SelectionTorrentFileName(stableId);
        // 1024 字节的确定性文件只占一个 piece，pieces 必须包含对应的 20 字节 SHA-1。
        var header = Encoding.UTF8.GetBytes(
                                            "d8:announce31:http://tracker.example/announce4:infod6:lengthi1024e" +
                                            $"4:name{Encoding.UTF8.GetByteCount(name)}:{name}"                   +
                                            "12:piece lengthi16384e6:pieces20:");
        using var torrent = new MemoryStream();
        torrent.Write(header);
        torrent.Write(SHA1.HashData(new byte[1024]));
        torrent.Write("ee"u8);
        return torrent.ToArray();
    }

    private static string SelectionTorrentFileName(string stableId)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableId)));
        return $"【喵萌奶茶屋】[Test Anime {identity}][12][1080p][简日双语].mkv";
    }

    private static async Task<SelectionHarness> CreateHarnessAsync(bool                    configureDownloader = true,
                                                                   SelectionRuleConfig?    ruleConfig          = null,
                                                                   SwitchableRuleProvider? switchableProvider  = null)
    {
        var directory = Directory.CreateTempSubdirectory("Banori.P4.Selection.Tests.");
        var timestamp = new DateTimeOffset(2026, 9, 26, 4, 0, 0, TimeSpan.Zero);
        var services  = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "p4.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
        var brokenStableIds = new HashSet<string>(StringComparer.Ordinal);
        var fetchCounter    = new TorrentFetchCounter();
        services.AddSingleton<ITorrentFileSource>(new FakeTorrentFileSource(uri =>
        {
            fetchCounter.Count++;
            var stableId = Path.GetFileNameWithoutExtension(uri.Segments[^1]);
            if (brokenStableIds.Contains(stableId))
                throw new HttpRequestException("测试模拟的种子下载失败。");
            var hold = fetchCounter.HoldFirst;
            if (hold is not null && hold.TryAcquire())
            {
                hold.Arrived.TrySetResult();
                hold.Release.Task.Wait();
            }

            return CreateSelectionTorrentBytes(stableId);
        }));
        var sessionFactory = new FakeQbittorrentSessionFactory(new FakeTorrentStore());
        services.AddSingleton(sessionFactory);
        services.AddSingleton<IQbittorrentSessionFactory>(sessionFactory);
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddNotificationTestFakes();
        services.AddScoped<NamingTemplateStore>();
        services.AddSingleton<DownloadCoordinationGate>();
        services.AddScoped<DownloadService>();
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：Identity 服务注册影响
        // ApplicationDbContext 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var qbSettings = new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(),
                                                       sessionFactory);
        var settingsStore = provider.GetRequiredService<IApplicationSettingStore>();
        // 与应用注册一致：默认走设置存储提供者（未保存规则时回退内置默认规则），行为与
        // 旧的 DefaultSelectionRuleProvider 相同，同时支撑规则保存 / 预览 / 整库重选的回归。
        var ruleSettings = new ManageSelectionRuleSettings(provider.GetRequiredService<IApplicationSettingStore>(),
                                                           NullLogger<ManageSelectionRuleSettings>.Instance);
        var selectionRuleProvider = switchableProvider ??
                                    (ISelectionRuleProvider)(ruleConfig is null
                                        ? new SettingsSelectionRuleProvider(ruleSettings)
                                        : new FixedRuleProvider(ruleConfig));
        var downloadService = new DownloadService(dbFactory,
                                                  provider.GetRequiredService<DownloadCoordinationGate>(),
                                                  provider.GetRequiredService<ITorrentFileSource>(),
                                                  new FakeFeedSourceReader(),
                                                  sessionFactory, qbSettings,
                                                  provider.GetRequiredService<NotificationEnqueuer>(),
                                                  new AniParser(), new NamingTemplateStore(settingsStore),
                                                  new ReleaseNamePreviewPolicy(),
                                                  new CandidateEligibilityPolicy(),
                                                  new CandidateRankingPolicy(),
                                                  selectionRuleProvider,
                                                  new SubjectMetadataCache(dbFactory, UnusedSubjectProvider.Instance,
                                                                           provider.GetRequiredService<TimeProvider>()),
                                                  UnusedSubjectLinkResolver.Instance,
                                                  provider.GetRequiredService<TimeProvider>(),
                                                  NullLogger<DownloadService>.Instance,
                                                  new SubjectEpisodeCorrectionPolicy());
        var intakeService = new DownloadIntakeService(dbFactory,
                                                      provider.GetRequiredService<DownloadCoordinationGate>(),
                                                      provider.GetRequiredService<ITorrentFileSource>(),
                                                      new FakeFeedSourceReader(),
                                                      qbSettings,
                                                      new AniParser(), new NamingTemplateStore(settingsStore),
                                                      new ReleaseNamePreviewPolicy(),
                                                      new CandidateEligibilityPolicy(),
                                                      new CandidateRankingPolicy(),
                                                      selectionRuleProvider,
                                                      new SubjectMetadataCache(dbFactory,
                                                                               UnusedSubjectProvider.Instance,
                                                                               provider
                                                                                  .GetRequiredService<TimeProvider>()),
                                                      UnusedSubjectLinkResolver.Instance,
                                                      new SubjectEpisodeCorrectionPolicy(),
                                                      provider.GetRequiredService<TimeProvider>(),
                                                      NullLogger<DownloadIntakeService>.Instance);
        // 门闸必须进程级共享：与应用的单例注册一致，两个服务实例（模拟刷新 worker 与
        // Blazor 电路各自的 Scoped 实例）通过同一门闸互斥同组并发。
        var gate      = new SelectionGroupGate();
        var fixedTime = provider.GetRequiredService<TimeProvider>();

        CandidateSelectionService CreateService() =>
            new(dbFactory, selectionRuleProvider, gate,
                provider.GetRequiredService<DownloadCoordinationGate>(),
                new CandidateEligibilityPolicy(), new CandidateRankingPolicy(),
                qbSettings, new TestHostApplicationLifetime(), intakeService,
                fixedTime, NullLogger<CandidateSelectionService>.Instance);

        var harness = new SelectionHarness(directory, timestamp, provider, downloadService, intakeService,
                                           CreateService(), CreateService(), ruleSettings, brokenStableIds,
                                           fetchCounter);
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.MediaSubjects.Add(new MediaSubject
        {
            Id = SubjectId, Name = "Test Subject", NameCn = "测试作品", Platform = "TV", CachedAtUtc = timestamp
        });
        await db.SaveChangesAsync();

        if (configureDownloader) await harness.SaveDownloaderSettingsAsync();

        return harness;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class TorrentFetchCounter
    {
        public int Count;

        /// <summary>非空时把第一次种子获取挂起（阻塞在种子解析回调里），用于编排提交中途的交错。</summary>
        public FirstFetchHold? HoldFirst;
    }

    /// <summary>第一次种子获取的挂起点：Arrived 表示获取已到达并停住，Release 放行。</summary>
    private sealed class FirstFetchHold
    {
        private  int                  _acquired;
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryAcquire() => Interlocked.CompareExchange(ref _acquired, 1, 0) == 0;
    }

    /// <summary>固定返回指定规则配置的提供者，用于测试 NeedsReview 等非默认政策。</summary>
    private sealed class FixedRuleProvider(SelectionRuleConfig config) : ISelectionRuleProvider
    {
        public ValueTask<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(config);
    }

    /// <summary>
    ///     可在测试中途切换配置的规则提供者；可把第一次读取挂起，用于编排
    ///     “批次启动后规则才变更”的并发交错。每次读取返回读取时刻的配置快照。
    /// </summary>
    private sealed class SwitchableRuleProvider : ISelectionRuleProvider
    {
        private readonly TaskCompletionSource _firstReadArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseFirstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _reads;

        public required SelectionRuleConfig Config { get; set; }

        public bool HoldFirstRead { get; set; }

        /// <summary>第一次读取已到达且被挂起；释放用 <see cref="ReleaseFirstRead" />。</summary>
        public Task FirstReadArrived => _firstReadArrived.Task;

        public async ValueTask<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = Config;
            if (HoldFirstRead && Interlocked.Increment(ref _reads) == 1)
            {
                _firstReadArrived.TrySetResult();
                await _releaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }

        public void ReleaseFirstRead()
        {
            _releaseFirstRead.TrySetResult();
        }
    }

    private sealed class SelectionHarness(
        DirectoryInfo               directory,
        DateTimeOffset              timestamp,
        ServiceProvider             provider,
        DownloadService             downloadService,
        DownloadIntakeService       intakeService,
        CandidateSelectionService   selectionService,
        CandidateSelectionService   parallelSelectionService,
        ManageSelectionRuleSettings ruleSettings,
        HashSet<string>             brokenStableIds,
        TorrentFetchCounter         fetchCounter) : IDisposable
    {
        public DirectoryInfo             Directory        { get; } = directory;
        public DateTimeOffset            Timestamp        { get; } = timestamp;
        public DownloadService           DownloadService  { get; } = downloadService;
        public DownloadIntakeService     IntakeService    { get; } = intakeService;
        public CandidateSelectionService SelectionService { get; } = selectionService;

        /// <summary>共享同一门闸的第二个服务实例，模拟另一 DI 作用域（如 Blazor 电路）。</summary>
        public CandidateSelectionService ParallelSelectionService { get; } = parallelSelectionService;

        /// <summary>规则设置服务：与 SelectionService 的提供者共享同一存储，用于规则保存回归。</summary>
        public ManageSelectionRuleSettings RuleSettings { get; } = ruleSettings;

        /// <summary>把下载器的种子拉取次数，用于断言同一优胜不重复拉取种子。</summary>
        public int TorrentFetchCount => fetchCounter.Count;

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                Directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>把存储的规则配置改成非法 JSON，模拟配置损坏回退内置默认规则。</summary>
        public Task CorruptRuleSettingsAsync()
        {
            var store = (MemoryApplicationSettingStore)provider.GetRequiredService<IApplicationSettingStore>();
            return store.SetAsync(ManageSelectionRuleSettings.SettingKey, "{corrupt", CancellationToken.None);
        }

        /// <summary>
        ///     把下一次种子获取挂起：返回的 Arrived 在获取到达并停住时完成，调用 Release 放行。
        ///     用于把一个批次精确停在提交中途（已读选择记录、未开事务），编排并发交错。
        /// </summary>
        public (Task Arrived, Action Release) HoldNextTorrentFetch()
        {
            var hold = new FirstFetchHold();
            fetchCounter.HoldFirst = hold;
            return (hold.Arrived.Task, () => hold.Release.TrySetResult());
        }

        /// <summary>把条目的种子地址标记为下载必然失败，用于模拟网络故障。</summary>
        public void MarkTorrentBroken(string stableId)
        {
            brokenStableIds.Add(stableId);
        }

        /// <summary>解除种子故障标记，模拟网络恢复后的人工重试。</summary>
        public void HealTorrent(string stableId)
        {
            brokenStableIds.Remove(stableId);
        }

        /// <summary>插入一条已通过硬性过滤的合格候选条目。</summary>
        public async Task<Guid> AddItemAsync(string stableId, string group, string language, int version)
        {
            await using var db     = await CreateDbContextAsync();
            var             itemId = Guid.NewGuid();
            db.FeedItems.Add(new FeedItem
            {
                Id             = itemId,
                SubscriptionId = await EnsureSubscriptionAsync(db),
                StableId       = stableId,
                Title          = $"[{group}] 测试作品 - 12",
                TorrentUrl     = $"https://mikanani.me/Download/{stableId}.torrent",
                FirstSeenAtUtc = Timestamp,
                Status         = FeedItemStatus.Accepted,
                DecisionStatus = ReleaseDecisionStatus.Accepted,
                ParsedTitle    = $"[{group}] 测试作品 - 12",
                MediaType      = MediaType,
                Episode        = Episode,
                Version        = version,
                Group          = group,
                Language       = language,
                Resolution     = "R1080P",
                SubjectId      = SubjectId,
                Reason         = "通过硬性过滤。",
                NamingPreview  = $"测试作品 E{Episode:00} v{version} [{group}]",
                EvaluatedAtUtc = Timestamp
            });
            await db.SaveChangesAsync();
            return itemId;
        }

        /// <summary>插入该条目资源（按 stableId 确定性 hash）的已完成任务。</summary>
        public async Task AddCompletedTaskForAsync(Guid itemId)
        {
            await using var db           = await CreateDbContextAsync();
            var             item         = await db.FeedItems.SingleAsync(value => value.Id == itemId);
            var             torrentBytes = CreateSelectionTorrentBytes(item.StableId);
            Assert.True(TorrentDownloadPolicy.TryLoad(torrentBytes, out var torrent, out _));
            db.DownloadTasks.Add(new DownloadTask
            {
                Id             = Guid.NewGuid(),
                FeedItemId     = itemId,
                SubjectId      = SubjectId,
                InfoHash       = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant(),
                TorrentFile    = torrentBytes,
                TorrentUrl     = item.TorrentUrl!,
                SavePath       = "/downloads/test",
                NamingSnapshot = item.NamingPreview!,
                Episode        = Episode,
                Version        = item.Version,
                Status         = DownloadTaskStatus.Completed,
                CreatedAtUtc   = Timestamp,
                CompletedAtUtc = Timestamp
            });
            await db.SaveChangesAsync();
        }

        /// <summary>插入一条执行中的刷新工作，模拟刷新批次正在进行。</summary>
        public async Task AddRunningWorkAsync()
        {
            await using var db = await CreateDbContextAsync();
            db.RefreshWorkItems.Add(new RefreshWorkItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = await EnsureSubscriptionAsync(db),
                Status         = RefreshWorkStatus.Running,
                RequestedAtUtc = Timestamp,
                StartedAtUtc   = Timestamp
            });
            await db.SaveChangesAsync();
        }

        public async Task AddSelectionRecordAsync(Guid winnerItemId, Guid? intentTaskId, string? intentFailedReason)
        {
            await using var db = await CreateDbContextAsync();
            db.CandidateSelections.Add(new CandidateSelection
            {
                Id                 = Guid.NewGuid(),
                SubjectId          = SubjectId,
                MediaType          = MediaType,
                Episode            = Episode,
                WinnerFeedItemId   = winnerItemId,
                RuleVersion        = 1,
                RankingBasis       = "测试优胜依据",
                NamingSnapshot     = "测试作品 E12 v1",
                IntentTaskId       = intentTaskId,
                IntentFailedReason = intentFailedReason,
                SelectedAtUtc      = Timestamp
            });
            await db.SaveChangesAsync();
        }

        public async Task<CandidateSelection> GetSelectionAsync()
        {
            await using var db = await CreateDbContextAsync();
            return await db.CandidateSelections.AsNoTracking()
                           .SingleAsync(selection => selection.SubjectId == SubjectId &&
                                                     selection.MediaType == MediaType &&
                                                     selection.Episode   == Episode);
        }

        /// <summary>读取条目当前的同集择优标记（Winner / RunnerUp / None）。</summary>
        public async Task<SelectionOutcome> GetOutcomeAsync(Guid itemId)
        {
            await using var db = await CreateDbContextAsync();
            return (await db.FeedItems.AsNoTracking().SingleAsync(item => item.Id == itemId)).SelectionOutcome;
        }

        public Task SaveDownloaderSettingsAsync()
        {
            var settingsService = new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(),
                                                                provider
                                                                   .GetRequiredService<IQbittorrentSessionFactory>());
            return settingsService.SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", "/downloads",
                                                                     true), "secret");
        }

        public Task<ApplicationDbContext> CreateDbContextAsync()
        {
            var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            return factory.CreateDbContextAsync();
        }

        private async Task<Guid> EnsureSubscriptionAsync(ApplicationDbContext db)
        {
            var existing = await db.MediaSubscriptions.FirstOrDefaultAsync();
            if (existing is not null) return existing.Id;

            var subscription = new MediaSubscription
            {
                Id                     = Guid.NewGuid(), Name = "测试订阅", FeedUrlProtected = "unused",
                RefreshIntervalMinutes = 30
            };
            db.MediaSubscriptions.Add(subscription);
            await db.SaveChangesAsync();
            return subscription.Id;
        }
    }

    /// <summary>订阅登记路径不触达 Subject 绑定解析，这两个替身只满足 DownloadService 构造签名。</summary>
    private sealed class UnusedSubjectProvider : ISubjectMetadataProvider
    {
        public static readonly UnusedSubjectProvider Instance = new();

        public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("订阅路径不应读取 Bangumi 元数据。");
    }

    private sealed class UnusedSubjectLinkResolver : ISubjectLinkResolver
    {
        public static readonly UnusedSubjectLinkResolver Instance = new();

        public Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = null,
                                                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("订阅路径不应解析条目链接。");
    }
}
