using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banned.Qbittorrent.Exceptions;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Notifications;
using Banori.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>
///     第二阶段验收：时间策略、可恢复等待与调度。使用可步进时钟与生产默认窗口——
///     窗口内只观察不补发、查询轮次与 pulse 不消耗预算、到期前任务被跳过、路径决策可追溯。
/// </summary>
public sealed class DownloadSchedulingTests
{
    private const string DownloadRoot = "/downloads";

    // ───────────────────────── 暂停确认窗口 ─────────────────────────

    [Fact]
    public async Task PauseConfirm_PulseFloodWithinWindow_DoesNotExhaustBudget()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterRunningAfterRecheckAsync(harness);
        var       hash    = (await harness.GetTaskAsync(taskId)).InfoHash;

        // 第一次暂停：预算扣 1，记录请求时间（先跨过校验采样 2 秒的推迟）。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        Assert.Single(harness.Store.PauseCalls);
        var afterFirst = await harness.GetTaskAsync(taskId);
        Assert.True(afterFirst.Status.IsActive());

        // 同一时刻连续 10 轮（模拟钩子脉冲风暴）：不补发、不扣预算、不失败。
        for (var i = 0; i < 10; i++)
        {
            ForceRunning(harness, hash);
            await harness.ReconcileNowAsync();
        }

        Assert.Single(harness.Store.PauseCalls);
        var flooded = await harness.GetTaskAsync(taskId);
        Assert.True(flooded.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, flooded.IsolationState);

        // 窗口（15 秒）过期后仍未停止：允许补发一次。
        ForceRunning(harness, hash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        Assert.Equal(2, harness.Store.PauseCalls.Count);

        // 再过期一次：第 3 次耗尽预算；下一次窗口过期仍运行才判失败。
        ForceRunning(harness, hash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        Assert.Equal(3, harness.Store.PauseCalls.Count);
        ForceRunning(harness, hash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Contains("暂停不生效", failed.Error);
    }

    [Fact]
    public async Task PauseConfirm_LegacyIntentWithoutRequestTimestamp_TreatsWindowElapsed()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterRunningAfterRecheckAsync(harness);
        var       hash    = (await harness.GetTaskAsync(taskId)).InfoHash;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        Assert.Single(harness.Store.PauseCalls);

        // 旧格式意图没有 pauseRequestedAtUtc：窗口视为已过期，立即允许补发保护共享数据。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            var intent = DownloadVerificationIntent.FromJson(tracked.VerificationIntent) ??
                         throw new InvalidOperationException("校验意图缺失。");
            tracked.VerificationIntent = (intent with { PauseRequestedAtUtc = null }).ToJson();
            await db.SaveChangesAsync();
        }

        ForceRunning(harness, hash);
        await harness.ReconcileNowAsync();
        Assert.Equal(2, harness.Store.PauseCalls.Count);
    }

    // ───────────────────────── 布局确认窗口 ─────────────────────────

    [Fact]
    public async Task LayoutConfirm_RenameDeferred_NoReissueStormWithinWindow()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        harness.Store.DeferRenames = true;

        await harness.RoundAsync(); // 提交（停止添加）。
        await harness.RoundAsync(); // 准备：发出改名（第 1 轮），登记请求台账并推迟观察。
        var issued = harness.Store.RenameFileCalls.Count;
        Assert.True(issued > 0);
        var ledger = await harness.GetTaskAsync(taskId);
        Assert.Equal(1, ledger.PendingLayoutRequestCount);
        Assert.NotNull(ledger.PendingLayoutRequestedAtUtc);
        Assert.NotNull(ledger.NextObservationAtUtc);

        // 窗口内连续轮询不重发（读结果仍为旧路径也只观察）。
        for (var i = 0; i < 5; i++) await harness.ReconcileNowAsync();
        Assert.Equal(issued, harness.Store.RenameFileCalls.Count);

        // 窗口（30 秒）过期仍未生效：允许一次有界补发。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(issued + 1, harness.Store.RenameFileCalls.Count);

        // 请求在窗口内生效：到期前即可确认推进（窗口只挡补发，不挡确认），也不重发。
        harness.Store.CompleteRenames();
        await harness.RoundAsync();
        Assert.Equal(issued + 1, harness.Store.RenameFileCalls.Count);
        var task = await harness.GetTaskAsync(taskId);
        Assert.True(task.Status.IsActive());
        Assert.Equal(DownloadPreparationStage.ReadyToRun, task.PreparationStage);
        Assert.Contains(task.InfoHash, harness.Store.ResumeCalls);
    }

    [Fact]
    public async Task LayoutConfirm_MoveDeferred_NoLocationStormWithinWindow()
    {
        using var harness      = await SchedulingHarness.CreateAsync();
        var       incumbentId  = await CompleteIncumbentAsync(harness, "繁日双语");
        var       incumbent    = await harness.GetTaskAsync(incumbentId);
        var       challengerId = await harness.CreateUploadAsync(DifferentSizeTorrent());
        var       challenger   = await harness.GetTaskAsync(challengerId);

        // 驱动到下载中（.tasks 任务根），完成下载后归档需要迁移到正式根。
        for (var i = 0; i < 4; i++) await harness.RoundAsync();
        Assert.Equal(TaskRootOf(challengerId), (await harness.GetTaskAsync(challengerId)).SavePath);
        Assert.Equal(DownloadPathDecisionKinds.ReplacementStaging,
                     DecisionOf(await harness.GetTaskAsync(challengerId)).Kind);
        harness.Store.DeferLocation = true;
        CompleteTorrent(harness, challenger.InfoHash);

        await harness.RoundAsync(); // 完成轮：改名即时生效，发出迁移（第 1 轮）。
        Assert.Single(harness.Store.SetLocationCalls);
        var moving = await harness.GetTaskAsync(challengerId);
        Assert.True(moving.PendingLayoutRequestCount >= 1);

        // 窗口内连续轮询不重发 SetLocation（fake 的 IsMoving 先挡，台账语义由补发断言覆盖）。
        for (var i = 0; i < 5; i++) await harness.ReconcileNowAsync();
        Assert.Single(harness.Store.SetLocationCalls);

        // 模拟移动未生效：IsMoving 已结束但保存根仍在原处（请求被吞/移动失败）。
        // 窗口（30 秒）过期后才允许一次有界补发。
        harness.Store.PendingLocations.Clear();
        harness.Store.Torrents[challenger.InfoHash] =
            harness.Store.Torrents[challenger.InfoHash] with { IsMoving = false };
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(2, harness.Store.SetLocationCalls.Count);

        // 补发后移动真实完成：确认生效并归档，不再重发。
        harness.Store.CompleteMoves();
        await harness.RoundAsync();
        Assert.Equal(2, harness.Store.SetLocationCalls.Count);
        var completed = await harness.GetTaskAsync(challengerId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(DownloadRoot, completed.SavePath);
        Assert.Null(completed.ArchiveReview);
        // 旧版已被迁让，不再占据正式根。
        Assert.StartsWith(".replaced/", Assert.Single(harness.Store.FileIdentities[incumbent.InfoHash]).Path);
    }

    [Fact]
    public async Task SelectionConfirm_DeferredChange_NoReissueWithinWindow()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        await harness.RoundAsync();
        await harness.RoundAsync(); // 准备完成，已启动下载。
        var task = await harness.GetTaskAsync(taskId);
        Assert.Contains(task.InfoHash, harness.Store.ResumeCalls);

        // 登记侧改动文件选择（计划改为跳过），下载器侧仍选中：需要下发 skip。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.Include(value => value.Files)
                                  .SingleAsync(value => value.Id == taskId);
            tracked.Files.Single().IsSelectedForDownload = false;
            tracked.SelectionSyncPending                 = true;
            await db.SaveChangesAsync();
        }

        harness.Store.DeferSelections = true;
        await harness.RoundAsync();
        Assert.Single(harness.Store.SelectionCalls);

        // 窗口内不重发。
        for (var i = 0; i < 5; i++) await harness.ReconcileNowAsync();
        Assert.Single(harness.Store.SelectionCalls);

        // 窗口过期仍未生效：补发一次。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(2, harness.Store.SelectionCalls.Count);

        // 生效后确认并清除同步旗标。
        harness.Store.CompleteSelections();
        await harness.RoundAsync();
        Assert.Equal(2, harness.Store.SelectionCalls.Count);
        Assert.False((await harness.GetTaskAsync(taskId)).SelectionSyncPending);
    }

    // ───────────────────────── 添加可见性窗口 ─────────────────────────

    [Fact]
    public async Task AddVisibility_HiddenRightAfterAdd_NeitherLostNorReAdded()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.RoundAsync();
        Assert.Single(harness.Store.AddCalls);

        // 下载器已接收但短暂查不到：窗口内不标 Lost、不重复提交。
        harness.Store.HiddenTorrents.Add(task.InfoHash);
        for (var i = 0; i < 5; i++) await harness.RoundAsync();
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, waiting.Status);
        Assert.Single(harness.Store.AddCalls);

        // 种子出现后正常推进（同步过一次，此后缺失才是 Lost）。
        harness.Store.HiddenTorrents.Remove(task.InfoHash);
        await harness.RoundAsync();
        var synced = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, synced.Status);
        Assert.NotNull(synced.LastSyncedAtUtc);

        // 已同步过的任务再缺失：可见性窗口不再保护。
        harness.Store.HiddenTorrents.Add(task.InfoHash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        var lost = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Lost, lost.Status);
    }

    [Fact]
    public async Task AddVisibility_HiddenBeyondWindow_MarksLost()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.RoundAsync();
        harness.Store.HiddenTorrents.Add(task.InfoHash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        var lost = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Lost, lost.Status);
    }

    [Fact]
    public async Task AddVisibility_VerificationAddHidden_NoIsolationChurn()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.RoundAsync(); // 候选根提交。
        var staged = await harness.GetTaskAsync(taskId);
        Assert.NotNull(staged.VerificationIntent);

        // 校验种子短暂不可见：不转隔离（隔离会删除重加），意图保留。
        harness.Store.HiddenTorrents.Add(task.InfoHash);
        await harness.RoundAsync();
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, waiting.IsolationState);
        Assert.NotNull(waiting.VerificationIntent);

        harness.Store.HiddenTorrents.Remove(task.InfoHash);
        await harness.RoundAsync();
        var resumed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, resumed.IsolationState);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, resumed.PreparationStage);
    }

    [Fact]
    public async Task SubmitRetry_AddException_WindowGatesReAdd()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        harness.Factory.SessionConfigurator = session =>
            session.AddException = new QbittorrentException("模拟响应丢失");
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());

        await harness.RoundAsync(); // 第 1 次提交：请求失败，落库 Submitting + 请求时间。
        var first = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Submitting, first.Status);
        Assert.Equal(1, first.AttemptCount);
        Assert.NotNull(first.LastSubmitAttemptAtUtc);
        Assert.Empty(harness.Store.AddCalls);

        // 窗口内（30 秒）不重复提交。
        for (var i = 0; i < 5; i++) await harness.ReconcileNowAsync();
        Assert.Empty(harness.Store.AddCalls);
        Assert.Equal(1, (await harness.GetTaskAsync(taskId)).AttemptCount);

        // 窗口过期：允许一次重试。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        var second = await harness.GetTaskAsync(taskId);
        Assert.Equal(2, second.AttemptCount);
        Assert.Empty(harness.Store.AddCalls);
    }

    // ───────────────────────── worker 调度 ─────────────────────────

    [Fact]
    public void ComputeWakeDelay_RespectsEarliestDueAndPulseThrottle()
    {
        var timing = DownloadTimingPolicy.Default;
        var now    = DateTimeOffset.Parse("2026-10-20T08:00:00Z");

        // 无到期任务：空闲间隔。
        Assert.Equal(timing.IdleReconcileInterval,
                     DownloadReconcileWorker.ComputeWakeDelay(now, null, now - TimeSpan.FromSeconds(14), timing));

        // 有 2 秒后到期的任务：等待到到期时刻。
        Assert.Equal(TimeSpan.FromSeconds(2),
                     DownloadReconcileWorker.ComputeWakeDelay(now, now + TimeSpan.FromSeconds(2),
                                                              now      - TimeSpan.FromSeconds(14), timing));

        // 已过期任务：立即唤醒。
        Assert.Equal(TimeSpan.Zero,
                     DownloadReconcileWorker.ComputeWakeDelay(now, now - TimeSpan.FromSeconds(1),
                                                              now      - TimeSpan.FromSeconds(14), timing));

        // 上一轮刚结束（脉冲风暴）：即使有任务到期，也不得早于最短轮间隔。
        var throttled = DownloadReconcileWorker.ComputeWakeDelay(now, now - TimeSpan.FromSeconds(1),
                                                                 now      - TimeSpan.FromMilliseconds(200), timing);
        Assert.True(throttled >= timing.MinReconcileRoundInterval - TimeSpan.FromMilliseconds(200));
        Assert.True(throttled <= timing.IdleReconcileInterval);
    }

    [Fact]
    public async Task Worker_SkipsDeferredTaskAndWakesAtDueTime()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterCheckingVerificationAsync(harness);
        var       before  = (await harness.GetTaskAsync(taskId)).LastSyncedAtUtc;
        Assert.NotNull(before);

        var pulse = new ReconcilePulse();
        var worker =
            new DownloadReconcileWorker(harness.Provider.GetRequiredService<IServiceScopeFactory>(), pulse, harness.Time,
                                        harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                                        DownloadTimingPolicy.Default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(cts.Token);
        try
        {
            // 校验采样推迟 2 秒：worker 的第一轮不推进该任务。
            await Task.Delay(200);
            Assert.Equal(before, (await harness.GetTaskAsync(taskId)).LastSyncedAtUtc);

            // 到期后 worker 被唤醒并推进（进度同步写入新的观察时间）。
            harness.Time.Advance(TimeSpan.FromSeconds(2.5));
            var             deadline = Task.Delay(TimeSpan.FromSeconds(5));
            DateTimeOffset? after    = null;
            while (after is null || after <= before)
            {
                Assert.False(deadline.IsCompleted, "worker 未在到期后被唤醒。");
                await Task.Delay(25);
                after = (await harness.GetTaskAsync(taskId)).LastSyncedAtUtc;
            }

            Assert.True(after > before);
        }
        finally
        {
            await cts.CancelAsync();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ───────────────────────── 路径决策 ─────────────────────────

    [Fact]
    public async Task PathDecision_PlainTask_NoOldVersion_GoesStraightToFinalRoot()
    {
        using var harness = await SchedulingHarness.CreateAsync(DownloadTimingPolicy.Immediate);
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        await harness.ReconcileNowAsync();

        var task = await harness.GetTaskAsync(taskId);
        // 回归：无候选、无待替换版本的缺集直接在正式路径下载，不进 .tasks。
        Assert.Equal(DownloadRoot, task.SavePath);
        Assert.DoesNotContain(".tasks", task.SavePath, StringComparison.Ordinal);
        Assert.Equal(DownloadRoot, Assert.Single(harness.Store.AddCalls).SavePath);
        var decision = DecisionOf(task);
        Assert.Equal(DownloadPathDecisionKinds.FinalRoot, decision.Kind);
        Assert.Equal(DownloadRoot, decision.Root);
    }

    [Fact]
    public async Task PathDecision_ReplacementTask_RecordsStagingReasonWithRelatedTask()
    {
        using var harness     = await SchedulingHarness.CreateAsync(DownloadTimingPolicy.Immediate);
        // 下载中的繁日现任被简日挑战者整任务承接：ReplacesTaskId 指向旧任务。
        var       incumbentId = await harness.CreateUploadAsync(SingleFileTorrent("繁日双语"));
        await harness.ReconcileNowAsync();
        await harness.ReconcileNowAsync();
        Assert.True((await harness.GetTaskAsync(incumbentId)).Status.IsActive());

        var challengerId = await harness.CreateUploadAsync(DifferentSizeTorrent());
        var challenger   = await harness.GetTaskAsync(challengerId);
        Assert.Equal(incumbentId, challenger.ReplacesTaskId);
        await harness.ReconcileNowAsync();

        var task     = await harness.GetTaskAsync(challengerId);
        var decision = DecisionOf(task);
        Assert.Equal(DownloadPathDecisionKinds.ReplacementStaging, decision.Kind);
        Assert.Equal(TaskRootOf(challengerId), decision.Root);
        Assert.Equal(incumbentId, decision.RelatedTaskId);
        Assert.Contains(".tasks", decision.Root, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathDecision_CandidateTask_RecordsReuseVerification()
    {
        using var harness = await SchedulingHarness.CreateAsync(DownloadTimingPolicy.Immediate);
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        await harness.ReconcileNowAsync();

        var decision = DecisionOf(await harness.GetTaskAsync(taskId));
        Assert.Equal(DownloadPathDecisionKinds.ReuseVerification, decision.Kind);
        Assert.Equal(DownloadRoot, decision.Root);
        Assert.Contains("候选", decision.Reason);
    }

    [Fact]
    public async Task PathDecision_FailedCheckEntersIsolation_RecordsRecoveryRoot()
    {
        using var harness = await SchedulingHarness.CreateAsync(DownloadTimingPolicy.Immediate);
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileNowAsync();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileNowAsync(); // 候选未通过（零进度）→ 隔离。

        var isolated = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState);
        var decision = DecisionOf(isolated);
        Assert.Equal(DownloadPathDecisionKinds.IsolationRecovery, decision.Kind);
        Assert.Equal(TaskRootOf(taskId), decision.Root);
        Assert.Contains("复用校验放弃", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathDecision_SameHashAdoption_RecordsRealLocation()
    {
        using var harness  = await SchedulingHarness.CreateAsync(DownloadTimingPolicy.Immediate);
        var       taskId   = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task     = await harness.GetTaskAsync(taskId);
        var       realPath = "/external/downloads";
        harness.Store.AddTorrent(task.InfoHash, task.NamingSnapshot, QbittorrentTorrentCategory.Seeding,
                                 1.0, realPath);
        await harness.ReconcileNowAsync();

        var adopted  = await harness.GetTaskAsync(taskId);
        var decision = DecisionOf(adopted);
        Assert.Equal(DownloadPathDecisionKinds.SameHashAdoption, decision.Kind);
        Assert.Equal(realPath, decision.Root);
        Assert.Empty(harness.Store.AddCalls); // 未重复添加，直接接管。
    }

    // ───────────────────────── 复审回归（2026-10-20 第二阶段审查修复） ─────────────────────────

    [Fact]
    public async Task PauseConfirm_DownloadingDuringWindow_PersistsWriteRiskAndBlocksReuse()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterCheckingVerificationAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.FinishRecheck(task.InfoHash);
        CompleteTorrent(harness, task.InfoHash);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Seeding
        };

        // 做种触发暂停#1：做种收尾不算写入风险，risk 记为 false。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        Assert.Single(harness.Store.PauseCalls);

        // 确认窗口内观察到 50% 下载：窗口只限制补发暂停，不限制风险观察，必须记入风险。
        ForceRunning(harness, task.InfoHash);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { Progress = 0.5 };
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 0.5 }).ToList();
        await harness.ReconcileNowAsync();
        var observed = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.True(observed?.UnverifiedWriteRisk, "确认窗口内的下载状态必须记入共享写入风险。");

        // 暂停生效且回到 100%：带风险的旧证据不得沿用，不得登记 ReusedVerified。
        CompleteTorrent(harness, task.InfoHash);
        harness.Store.Paused.Add(task.InfoHash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        await using var db = await harness.CreateDbContextAsync();
        var outcome =
            (await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == taskId)).VerificationOutcome;
        Assert.NotEqual(DownloadFileVerificationOutcomes.ReusedVerified, outcome);
    }

    [Fact]
    public async Task AddVisibility_RetryAdd_GetsFreshVisibilityWindow()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        harness.Factory.SessionConfigurator = session =>
            session.AddException = new QbittorrentException("模拟响应丢失");
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        await harness.RoundAsync(); // 首次提交失败，落库 Submitting + 请求时间。

        harness.Factory.SessionConfigurator = null;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 窗口过期：重试成功。
        Assert.Single(harness.Store.AddCalls);

        // 重试成功后短暂查不到：可见性窗口按最近一次 Add 请求计算，不沿用首次提交时间。
        var task = await harness.GetTaskAsync(taskId);
        harness.Store.HiddenTorrents.Add(task.InfoHash);
        await harness.RoundAsync();
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, waiting.Status);
        Assert.Single(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Worker_PreArrivedPulse_StillHonorsMinimumRoundInterval()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       pulse   = new ReconcilePulse();
        var worker =
            new DownloadReconcileWorker(harness.Provider.GetRequiredService<IServiceScopeFactory>(), pulse, harness.Time,
                                        harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                                        DownloadTimingPolicy.Default);

        // 白盒驱动 WaitForNextTickAsync：上一轮刚结束，预先到达的 pulse 不得越过最短轮间隔。
        typeof(DownloadReconcileWorker)
           .GetField("_lastRoundAtUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
           .SetValue(worker, harness.Time.GetUtcNow());
        pulse.Pulse();
        var wait = (Task)typeof(DownloadReconcileWorker)
                        .GetMethod("WaitForNextTickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(worker, [CancellationToken.None])!;
        var premature = await Task.WhenAny(wait, Task.Delay(300));
        Assert.NotSame(wait, premature);

        // 最短间隔过后必须放行（不因节流永久卡住）。
        harness.Time.Advance(TimeSpan.FromSeconds(1.1));
        var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(wait, finished);
    }

    [Fact]
    public async Task SelectionConfirm_Convergence_ClearsExpiredObservationTime()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreatePendingSelectionDownloadAsync(harness);
        await harness.RoundAsync(); // 窗口内等待。
        harness.Store.CompleteSelections();
        await harness.RoundAsync(); // 确认收敛。

        // 确认成功必须清掉等待期的到期时间与请求预算：残留过期时间会让 worker 持续最短间隔轮询。
        var task = await harness.GetTaskAsync(taskId);
        Assert.False(task.SelectionSyncPending);
        Assert.Null(task.NextObservationAtUtc);
        Assert.Equal(0, task.SelectionRequestCount);
    }

    [Fact]
    public async Task SelectionConfirm_DeferredForever_ReissuesAreBounded()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreatePendingSelectionDownloadAsync(harness);

        // 持续延迟生效：补发有持久化预算（初次 + 2 次补发），耗尽转可解释的失败，不无限下发。
        for (var i = 0; i < 6; i++) await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.True(harness.Store.SelectionCalls.Count <= 3,
                    $"selection issues={harness.Store.SelectionCalls.Count}");
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal(3, failed.SelectionRequestCount);

        // 人工重试重置预算后恢复推进（接管轮 + 选择确认轮）。
        harness.Store.CompleteSelections();
        await harness.Provider.GetRequiredService<DownloadService>().RetryAsync(taskId);
        await harness.RoundAsync();
        await harness.RoundAsync();
        var recovered = await harness.GetTaskAsync(taskId);
        Assert.True(recovered.Status.IsActive());
        Assert.False(recovered.SelectionSyncPending);
    }

    [Fact]
    public async Task LayoutConfirm_BudgetExhausted_ManualRetryResetsLedger()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        harness.Store.DeferRenames = true;
        await harness.RoundAsync();
        await harness.RoundAsync(); // 改名下发（第 1 轮）。

        // 窗口反复过期 + 永不生效：预算耗尽转失败。
        for (var i = 0; i < 3; i++) await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        var before = harness.Store.RenameFileCalls.Count;

        // 修正下载器行为后人工重试：请求台账重置，计划以全新预算恢复。
        harness.Store.DeferRenames = false;
        await harness.Provider.GetRequiredService<DownloadService>().RetryAsync(taskId);
        await harness.RoundAsync();
        Assert.True(harness.Store.RenameFileCalls.Count > before,
                    $"人工重试未恢复布局请求：status={(await harness.GetTaskAsync(taskId)).Status}");
        var recovered = await harness.GetTaskAsync(taskId);
        Assert.True(recovered.Status.IsActive());
        Assert.Equal(0, recovered.PendingLayoutRequestCount);
    }

    [Fact]
    public async Task AddVisibility_ManifestNotReady_WaitsWithinWindowThenFailsAsConflict()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        await harness.RoundAsync(); // 提交成功。
        var task = await harness.GetTaskAsync(taskId);

        // hash 已可见而文件清单尚未就绪：宽限窗口内等待，不按身份冲突失败。
        harness.Store.FileIdentities[task.InfoHash] = [];
        await harness.RoundAsync();
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadTaskStatus.Failed, waiting.Status);

        // 窗口过后清单仍不完整：才是真实身份冲突。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Contains("身份", failed.Error);
    }

    /// <summary>构造「登记侧要求下载、下载器侧仍是跳过且延迟生效」的选择同步等待场景；返回任务 Id。</summary>
    private static async Task<Guid> CreatePendingSelectionDownloadAsync(SchedulingHarness harness)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        await harness.RoundAsync();
        await harness.RoundAsync(); // 提交 + 准备完成，已启动下载。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.Include(value => value.Files)
                                  .SingleAsync(value => value.Id == taskId);
            tracked.Files.Single().IsSelectedForDownload = true;
            tracked.SelectionSyncPending                 = true;
            await db.SaveChangesAsync();
        }

        harness.Store.FileIdentities[(await harness.GetTaskAsync(taskId)).InfoHash] =
            harness.Store.FileIdentities[(await harness.GetTaskAsync(taskId)).InfoHash]
                   .Select(file => file with { IsSelectedForDownload = false }).ToList();
        harness.Store.DeferSelections = true;
        await harness.RoundAsync(); // 下发第 1 轮并登记台账。
        return taskId;
    }

    // ───────────────────────── 复审追查回归（窗口中途风险采样 / 响应丢失台账 / 等待不被进度同步清除） ─────────────────────────

    [Fact]
    public async Task PauseConfirm_BenignSampleThenDownloading_MidWindowRiskIsObserved()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterCheckingVerificationAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.FinishRecheck(task.InfoHash);
        CompleteTorrent(harness, task.InfoHash);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Seeding
        };

        // 做种触发暂停#1；暂停未生效，窗口内又采样到一次做种（良性样本）。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        Assert.Single(harness.Store.PauseCalls);
        harness.Store.Paused.Remove(task.InfoHash);
        await harness.ReconcileNowAsync();

        // 窗口中途转为 50% 下载：等待必须按采样间隔继续（而不是睡到窗口末），
        // 中途的下载快照要被观察到并记入风险（推进两个采样拍：第一拍仍在采样推迟内）。
        ForceRunning(harness, task.InfoHash);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { Progress = 0.5 };
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 0.5 }).ToList();
        await harness.RoundAsync();
        await harness.RoundAsync();
        var risk =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent)!.UnverifiedWriteRisk;
        Assert.True(risk, "暂停窗口中途出现的下载快照必须被采样到并记入共享写入风险。");

        // 最终停止回到 100%：带风险的证据不得登记复用。
        CompleteTorrent(harness, task.InfoHash);
        harness.Store.Paused.Add(task.InfoHash);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        await using var db = await harness.CreateDbContextAsync();
        var outcome =
            (await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == taskId)).VerificationOutcome;
        Assert.NotEqual(DownloadFileVerificationOutcomes.ReusedVerified, outcome);
    }

    [Fact]
    public async Task SelectionConfirm_ResponseLoss_ConsumesBudgetAndStartsWindow()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreatePendingSelectionDownloadAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(31)); // 首轮窗口过期，可补发。

        // 选择请求被下载器接受但响应丢失：台账已在副作用前登记，预算扣减、确认窗口启动。
        var responseLossFactory = new ResponseLossSessionFactory(harness.Factory);
        var service             = BuildServiceWith(harness, responseLossFactory);
        for (var i = 0; i < 6; i++) await service.ReconcileAsync();

        Assert.True(harness.Store.SelectionCalls.Count <= 3,
                    $"同一时刻无界重发：issues={harness.Store.SelectionCalls.Count}");
        var after = await harness.GetTaskAsync(taskId);
        Assert.Equal(2, after.SelectionRequestCount);
        Assert.NotNull(after.PendingLayoutRequestedAtUtc);
    }

    [Fact]
    public async Task SelectionConfirm_WaitKeepsObservationSchedule_AgainstProgressSync()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreatePendingSelectionDownloadAsync(harness);

        // 等待期的下一轮：选择仍未确认，进度同步不得清除尚未到期的观察时间。
        await harness.RoundAsync();
        var after = await harness.GetTaskAsync(taskId);
        Assert.True(after.SelectionSyncPending);
        Assert.NotNull(after.NextObservationAtUtc);
        Assert.True(after.NextObservationAtUtc > harness.Time.GetUtcNow(),
                    $"next={after.NextObservationAtUtc:O}, now={harness.Time.GetUtcNow():O}");
    }

    /// <summary>用指定会话工厂构造 DownloadService（其余依赖取自 harness 容器），用于注入响应丢失代理。</summary>
    private static DownloadService BuildServiceWith(SchedulingHarness harness, IQbittorrentSessionFactory factory)
    {
        var ctor = typeof(DownloadService).GetConstructors().Single();
        var args = ctor.GetParameters()
                       .Select(parameter => parameter.ParameterType == typeof(IQbittorrentSessionFactory)
                                    ? (object?)factory
                                   : harness.Provider.GetService(parameter.ParameterType))
                       .ToArray();
        return (DownloadService)ctor.Invoke(args)!;
    }

    /// <summary>选择请求已在远端执行但响应丢失的会话代理：SetTorrentFileSelectionAsync 完成后抛传输异常。</summary>
    private class SelectionResponseLossProxy : DispatchProxy
    {
        public IQbittorrentSession Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(Inner, args);
            return targetMethod.Name == nameof(IQbittorrentSession.SetTorrentFileSelectionAsync)
                ? LoseResponseAsync((Task)result!)
                : result;
        }

        private static async Task LoseResponseAsync(Task remote)
        {
            await remote;
            throw new QbittorrentException("请求已被下载器接受，响应丢失。");
        }
    }

    private sealed class ResponseLossSessionFactory(IQbittorrentSessionFactory inner) : IQbittorrentSessionFactory
    {
        public async Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default) =>
            Wrap(await inner.CreateAsync(cancellationToken));

        public async Task<IQbittorrentSession> CreateAsync(QbittorrentSettings settings, string? password,
                                                           CancellationToken   cancellationToken = default) =>
            Wrap(await inner.CreateAsync(settings, password, cancellationToken));

        private static IQbittorrentSession Wrap(IQbittorrentSession innerSession)
        {
            var proxy = DispatchProxy.Create<IQbittorrentSession, SelectionResponseLossProxy>();
            ((SelectionResponseLossProxy)(object)proxy).Inner = innerSession;
            return proxy;
        }
    }

    // ───────────────────────── 助手 ─────────────────────────

    private static string TaskRootOf(Guid taskId) => DownloadTaskPlacement.TaskRoot(DownloadRoot, taskId);

    private static DownloadPathDecision DecisionOf(DownloadTask task) =>
        DownloadPathDecision.FromJson(task.PathDecision) ?? throw new InvalidOperationException("路径决策缺失。");

    private static byte[] SingleFileTorrent(string language = "简日双语") =>
        TestTorrentBytes.Create($"【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][01][1080p][{language}].mp4");

    private static byte[] DifferentSizeTorrent() =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SingleFileTorrent())
                                       .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                StringComparison.Ordinal));

    private static async Task<string> ArchivalTargetAsync(SchedulingHarness harness, Guid taskId)
    {
        var service = harness.Provider.GetRequiredService<DownloadFileService>();
        return Assert.Single(await service.ComputeArchivalTargetsAsync(taskId)).TargetPath;
    }

    private static (string Hash, TorrentFileSnapshot File) SeedExternalOccupant(
        SchedulingHarness harness, string target, long size)
    {
        var hash   = new string('e', 40);
        var parent = target[..target.LastIndexOf('/')];
        var file   = new TorrentFileSnapshot(0, target[(parent.Length + 1)..], size, 1.0);
        harness.Store.AddTorrent(hash, "External Seed", QbittorrentTorrentCategory.Seeding, 1.0, parent);
        harness.Store.FileIdentities[hash] = [file];
        return (hash, file);
    }

    private static void CompleteTorrent(SchedulingHarness harness, string hash)
    {
        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 1.0 };
        if (harness.Store.FileIdentities.TryGetValue(hash, out var files))
            harness.Store.FileIdentities[hash] = files.Select(file => file with { Progress = 1.0 }).ToList();
    }

    private static async Task<Guid> CompleteIncumbentAsync(SchedulingHarness harness, string language)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent(language));
        var task   = await harness.GetTaskAsync(taskId);
        await harness.ReconcileNowAsync();
        await harness.ReconcileNowAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileNowAsync();
        return taskId;
    }

    /// <summary>候选任务推进到「已发 recheck、进入校验阶段」；返回任务 Id。</summary>
    private static async Task<Guid> EnterCheckingVerificationAsync(SchedulingHarness harness)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        var task   = await harness.GetTaskAsync(taskId);
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck。
        await harness.RoundAsync(); // 观察 Checking（推迟 2 秒采样）。
        var checking = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, checking.PreparationStage);
        return taskId;
    }

    /// <summary>候选任务推进到「校验结束、种子处于运行状态」——暂停分支的入口；返回任务 Id。</summary>
    private static async Task<Guid> EnterRunningAfterRecheckAsync(SchedulingHarness harness)
    {
        var taskId = await EnterCheckingVerificationAsync(harness);
        var task   = await harness.GetTaskAsync(taskId);
        harness.Store.FinishRecheck(task.InfoHash);
        ForceRunning(harness, task.InfoHash);
        return taskId;
    }

    /// <summary>模拟暂停不生效：暂停请求被吞，种子保持运行。</summary>
    private static void ForceRunning(SchedulingHarness harness, string hash)
    {
        harness.Store.Paused.Remove(hash);
        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Downloading
        };
    }

    /// <summary>
    ///     第二阶段专用 harness：可步进时钟（Advance 触发到期计时器）+ 生产默认时间策略。
    ///     RoundAsync = 推进 1 秒再对账（正常采样节奏）；ReconcileNowAsync = 同一时刻立即对账（模拟脉冲风暴）。
    /// </summary>
    private sealed class SchedulingHarness(
        ServiceProvider               provider,
        DirectoryInfo                 directory,
        FakeTorrentStore              store,
        FakeQbittorrentSessionFactory factory,
        DownloadService               service,
        DownloadIntakeService         intake,
        StepTimeProvider              time) : IDisposable
    {
        public IServiceProvider              Provider { get; } = provider;
        public FakeTorrentStore              Store    { get; } = store;
        public FakeQbittorrentSessionFactory Factory  { get; } = factory;
        public StepTimeProvider              Time     { get; } = time;

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        public static async Task<SchedulingHarness> CreateAsync(DownloadTimingPolicy? timing = null)
        {
            timing ??= DownloadTimingPolicy.Default;
            var directory = Directory.CreateTempSubdirectory("Banori.P4S.Tests.");
            var services  = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                                   options.UseSqlite(
                                                                    $"Data Source={Path.Combine(directory.FullName, "p4s.db")};Pooling=False"));
            var time = new StepTimeProvider();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton(timing);
            services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
            var store = new FakeTorrentStore();
            services.AddSingleton(store);
            var factory = new FakeQbittorrentSessionFactory(store);
            services.AddSingleton(factory);
            services.AddSingleton<IQbittorrentSessionFactory>(factory);
            services.AddSingleton<IFeedSourceReader>(new FakeFeedSourceReader());
            services.AddSingleton<ITorrentFileSource>(new FakeTorrentFileSource(_ => TestTorrentBytes.Create()));
            services.AddSingleton<ISubjectMetadataProvider>(new FixedSubjectProvider());
            services.AddScoped<SubjectMetadataCache>();
            services.AddSingleton<ISubjectLinkResolver, NullSubjectLinkResolver>();
            services.AddScoped<ManageQbittorrentSettings>();
            services.AddNotificationTestFakes();
            services.AddSingleton<AniParser>();
            services.AddSingleton<ReleaseNamePreviewPolicy>();
            services.AddSingleton<CandidateEligibilityPolicy>();
            services.AddSingleton<CandidateRankingPolicy>();
            services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
            services.AddScoped<ManageSelectionRuleSettings>();
            services.AddScoped<ISelectionRuleProvider, SettingsSelectionRuleProvider>();
            // worker 每轮先恢复同集选择，其依赖与下载服务一并注册。
            services.AddSingleton<SelectionGroupGate>();
            services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime());
            services.AddScoped<CandidateSelectionService>();
            services.AddScoped<NamingTemplateStore>();
            services.AddSingleton<DownloadCoordinationGate>();
            services.AddScoped<DownloadDirectorySettingsStore>();
            services.AddSingleton<DownloadFileClassificationPolicy>();
            services.AddScoped<DownloadFileService>();
            services.AddScoped<DownloadService>();
            services.AddScoped<DownloadIntakeService>();
            services.AddIdentityCore<ApplicationUser>(options =>
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();

            var harness = new SchedulingHarness(provider, directory, store, factory,
                                                provider.GetRequiredService<DownloadService>(),
                                                provider.GetRequiredService<DownloadIntakeService>(), time);
            await using var db = await harness.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            db.MediaSubjects.Add(new MediaSubject
            {
                Id          = 42, Name = "Scheduling Subject", NameCn = "调度作品", Platform = "TV",
                CachedAtUtc = DateTimeOffset.Parse("2026-10-20T08:00:00Z")
            });
            await db.SaveChangesAsync();
            var settingsService =
                new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(), factory);
            await settingsService.SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", DownloadRoot,
                                                                    true), "secret");
            await new NamingTemplateStore(provider.GetRequiredService<IApplicationSettingStore>()).SaveAsync("{title}");
            await new NamingTemplateStore(provider.GetRequiredService<IApplicationSettingStore>())
               .SaveFileTemplateAsync("{title} E{episode}");
            // 简日优先规则：让同集上传的简日挑战者能在登记裁决中胜出（替换路径测试依赖）。
            await new ManageSelectionRuleSettings(provider.GetRequiredService<IApplicationSettingStore>(),
                                                  NullLogger<ManageSelectionRuleSettings>.Instance)
               .SaveAsync(SelectionRuleConfig.CreateDefault() with
                {
                    GroupOrder = ["喵萌奶茶屋"], LanguageOrder = [EnumLanguage.JpSc, EnumLanguage.Sc]
                });
            return harness;
        }

        public Task ReconcileNowAsync() => service.ReconcileAsync();

        /// <summary>推进一个采样间隔（1 秒）再对账：驱动需要时间流逝的正常流程。</summary>
        public async Task RoundAsync()
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await service.ReconcileAsync();
        }

        public async Task AdvanceAsync(TimeSpan by)
        {
            time.Advance(by);
            await service.ReconcileAsync();
        }

        public Task<Guid> CreateUploadAsync(byte[] torrentBytes) =>
            intake.CreateFromUploadAsync(torrentBytes, null, 42);

        public async Task<DownloadTask> GetTaskAsync(Guid taskId)
        {
            await using var db = await CreateDbContextAsync();
            return await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync() =>
            Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();

        private sealed class FixedSubjectProvider : ISubjectMetadataProvider
        {
            public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new SubjectMetadata(subjectId, "Scheduling Subject", "调度作品", "TV",
                                                    "2026-01-01", null,
                                                    DateTimeOffset.Parse("2026-10-20T08:00:00Z")));
        }
    }
}

/// <summary>
///     可步进时钟：Advance 推进当前时间并同步触发所有到期计时器（Task.Delay 依赖 CreateTimer）。
///     不推进时间时一切等待挂起——测试据此在同一时刻重复对账（模拟脉冲风暴）而不引入真实睡眠。
/// </summary>
internal sealed class StepTimeProvider : TimeProvider
{
    private readonly object          _lock   = new();
    private readonly List<StepTimer> _timers = [];
    private          DateTimeOffset  _now    = DateTimeOffset.Parse("2026-10-20T08:00:00Z");

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public void Advance(TimeSpan by)
    {
        List<StepTimer> due;
        lock (_lock)
        {
            _now += by;
            due  =  _timers.Where(timer => timer.DueAt is { } at && at <= _now).ToList();
            foreach (var timer in due) _timers.Remove(timer);
            foreach (var timer in due.Where(timer => timer.Period != Timeout.InfiniteTimeSpan))
                timer.DueAt = _now + timer.Period;
        }

        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new StepTimer(this, callback, state);
        lock (_lock)
        {
            timer.Period = period;
            timer.DueAt  = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class StepTimer(
        StepTimeProvider owner,
        TimerCallback    callback,
        object?          state) : ITimer
    {
        public DateTimeOffset? DueAt;
        public TimeSpan        Period;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                DueAt  = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
