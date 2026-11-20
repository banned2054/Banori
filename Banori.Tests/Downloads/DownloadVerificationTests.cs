using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>
///     第三阶段验收：已有数据校验、缺失文件直接下载、旧版保护与完成后替换，以及隔离恢复。
///     fake 的 recheck 默认 RecordOnly——未显式配置的用例绝不能因 fake 凭空推进校验而通过。
/// </summary>
public sealed partial class DownloadVerificationTests
{
    private const string DownloadRoot = "/downloads";
    private const string MediaRoot    = DownloadRoot + "/Media/Anime";

    private static string TaskRootOf(VerificationHarness harness, Guid taskId) =>
        DownloadTaskPlacement.TaskRoot(DownloadRoot, taskId);

    // ───────────────────────── 统一下载流程 ─────────────────────────

    [Fact]
    public async Task SingleFlow_NoExistingVersion_DownloadsDirectly()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadRoot, task.SavePath); // 登记仍在全局根；提交时才决定落到媒体目录。
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Equal(MediaRoot, Assert.Single(harness.Store.AddCalls).SavePath);
        Assert.Equal(MediaRoot, harness.Store.Torrents[task.InfoHash].SavePath);
        Assert.Contains(task.InfoHash, harness.Store.ResumeCalls);
        Assert.All(harness.Store.RenameFileCalls,
                   call => Assert.DoesNotContain("Media/Anime/", call.NewPath, StringComparison.Ordinal));
        Assert.Empty(harness.Store.RecheckCalls);
        Assert.Empty(harness.Store.SetLocationCalls);
    }

    [Fact]
    public async Task SingleFlow_ExistingCandidate_AlwaysChecksBeforeRunning()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var (externalHash, externalFile) =
            SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Single(harness.Store.RecheckCalls);
        Assert.Empty(harness.Store.ResumeCalls);
        Assert.Equal(externalFile, harness.Store.FileIdentities[externalHash][0]);
        Assert.NotNull((await harness.GetTaskAsync(taskId)).VerificationIntent);
    }

    [Fact]
    public async Task SingleFlow_NewDownload_CompletesWithoutApproval()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        for (var i = 0; i < 2; i++) await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(MediaRoot, completed.SavePath);
        Assert.Null(completed.ArchiveReview);
        Assert.Empty(harness.Store.SetLocationCalls);
        Assert.Equal(await FirstAnimeFileIdAsync(harness, taskId), await IncumbentVideoFileIdAsync(harness, taskId));
    }

    [Fact]
    public async Task Archive_SameSizeDifferentContent_ManagedIncumbentMovedAsideNotSkipped()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var incumbentId  = await CompleteIncumbentAsync(harness, "繁日双语");
        var incumbent    = await harness.GetTaskAsync(incumbentId);
        var challengerId = await harness.CreateUploadAsync(SingleFileTorrent());
        var challenger   = await harness.GetTaskAsync(challengerId);
        await PrepareReplacementAfterFailedCheckAsync(harness, challengerId);
        Assert.Equal(TaskRootOf(harness, challengerId), (await harness.GetTaskAsync(challengerId)).SavePath);
        Assert.DoesNotContain(harness.Store.RenameFileCalls,
                              call => call.Hash == incumbent.InfoHash && call.NewPath.StartsWith(".replaced/"));
        CompleteTorrent(harness, challenger.InfoHash);
        await harness.ReconcileAsync();
        Assert.Contains(harness.Store.RenameFileCalls, call => call.Hash == incumbent.InfoHash &&
                                                               call.NewPath.StartsWith(".replaced/",
                                                                   StringComparison.Ordinal));
        var archived = await harness.GetTaskAsync(challengerId);
        Assert.Equal(MediaRoot, archived.SavePath);
        Assert.Null(archived.ArchiveReview);
        Assert.Equal(await FirstAnimeFileIdAsync(harness, challengerId),
                     await IncumbentVideoFileIdAsync(harness, challengerId));
    }

    [Fact]
    public async Task Archive_IncumbentCrossReferencedByExternalSeed_BlocksAsideBeforeMoving()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var incumbentId = await CompleteIncumbentAsync(harness, "繁日双语");
        var incumbent   = await harness.GetTaskAsync(incumbentId);
        var target      = await ArchivalTargetAsync(harness, incumbentId);
        var (externalHash, _) = SeedExternalOccupant(harness, target, 1024);
        var renameCount  = harness.Store.RenameFileCalls.Count(call => call.Hash == incumbent.InfoHash);
        var challengerId = await harness.CreateUploadAsync(SingleFileTorrent());
        var challenger   = await harness.GetTaskAsync(challengerId);
        await PrepareReplacementAfterFailedCheckAsync(harness, challengerId);
        CompleteTorrent(harness, challenger.InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count(call => call.Hash == incumbent.InfoHash));
        Assert.Equal(target, harness.Store.Torrents[externalHash].SavePath + "/" +
                             harness.Store.FileIdentities[externalHash][0].Path);
        var held = await harness.GetTaskAsync(challengerId);
        Assert.Contains("引用", DownloadArchiveReview.FromJson(held.ArchiveReview)!.Reason);
        Assert.Equal(TaskRootOf(harness, challengerId), held.SavePath);
    }

    // ───────────────────────── 隔离与路由 ─────────────────────────

    [Fact]
    public async Task Isolation_MissingHashIsIntermediateState_NeverMarkedLost()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);

        // 构造「删除已生效、重加未发生」的崩溃中段：种子不在下载器，隔离状态 Deleting。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            tracked.Status         = DownloadTaskStatus.Downloading;
            tracked.IsSystemPaused = true;
            tracked.IsolationState = DownloadIsolationState.Deleting;
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync(); // Deleting：hash 缺失 → 前进 Readding（不是 Lost）。
        var readding = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.Readding, readding.IsolationState);
        Assert.Equal(DownloadTaskStatus.Downloading, readding.Status);

        await harness.ReconcileAsync(); // Readding：任务专属根停止重加。
        Assert.Contains(harness.Store.AddCalls, call => call.SavePath == TaskRootOf(harness, taskId));

        await harness.ReconcileAsync(); // Confirming。
        await harness.ReconcileAsync(); // 布局确认 → 回到准备。
        var restored = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, restored.IsolationState);
        Assert.Equal(TaskRootOf(harness, taskId), restored.SavePath);
        Assert.Contains(task.InfoHash, harness.Store.StoppedAdds);
    }

    [Fact]
    public async Task IdentityMismatch_BlocksPreparation_NeverResumes()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);

        await harness.ReconcileAsync(); // 提交（停止添加）。

        // 破坏远端文件身份：持久化大小对不上，身份校验必须阻塞推进，绝不进入可启动回退。
        var original = harness.Store.FileIdentities[task.InfoHash][0];
        harness.Store.FileIdentities[task.InfoHash] = [original with { Size = 999 }];

        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();

        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        var blocked = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, blocked.Status);
        Assert.Equal("文件整理", blocked.FailedStep);
        Assert.True(blocked.IsSystemPaused);
    }

    // ───────────────────────── 已有数据校验 ─────────────────────────

    [Fact]
    public async Task Verification_IntentPersistedBeforeAdd_AndFullPass_ReusesSeedsAndSwitchesIncumbent()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var       target  = await ArchivalTargetAsync(harness, taskId);
        var (externalHash, externalFile) = SeedExternalOccupant(harness, target, 1024);
        var candidateRoot = DownloadRoot;

        // 意图先于添加请求落库（持久化意图先于远端请求约束）。
        harness.Factory.SessionConfigurator = session =>
        {
            session.BeforeAddTorrentAsync = async () =>
            {
                await using var db = await harness.CreateDbContextAsync();
                var pending = await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
                Assert.NotNull(pending.VerificationIntent);
                Assert.Equal(candidateRoot, pending.SavePath);
            };
        };

        await harness.ReconcileAsync(); // 提交：候选根停止添加，意图已先行落库。
        var add = Assert.Single(harness.Store.AddCalls);
        Assert.Equal(candidateRoot, add.SavePath);

        // 校验阶段进入 Checking：观察到 Checking 后才允许读取结果（fake 显式推进）。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 映射：候选文件改名 onto 候选路径并发出 recheck。
        Assert.Contains(task.InfoHash, harness.Store.RecheckCalls);
        var mapping = await harness.GetTaskAsync(taskId);
        var intent  = DownloadVerificationIntent.FromJson(mapping.VerificationIntent);
        Assert.NotNull(intent);
        // 请求发出后立即观察：快速校验可能在下一个采样间隔前就结束，不能等空闲对账的第一采。
        Assert.Equal(DownloadVerificationStep.Observing, intent.Step);
        Assert.Equal(1, intent.IssuedCount);
        Assert.True(intent.ObservedChecking);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, mapping.PreparationStage);
        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);

        await harness.ReconcileAsync(); // 观察位：校验持续中，不做任何决策。
        var observing = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(observing);
        Assert.True(observing.ObservedChecking);

        // 候选数据全部通过：结束校验 → 直接完成 + ReusedVerified，不启动已完整的停止任务。
        harness.Store.FileIdentities[task.InfoHash] =
            harness.Store.FileIdentities[task.InfoHash]
                   .Select(file => file with { Progress = 1.0 })
                   .ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync(); // 结论轮：全部通过 → 完成归档。

        var reused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, reused.Status);
        Assert.Null(reused.VerificationIntent);
        Assert.False(reused.IsSystemPaused);
        // 数据完整性确认后继续做种：完整且停止时恢复一次并重新通告 tracker（旧实现完成后不启动）。
        Assert.Equal(1, harness.Store.ResumeCalls.Count(hash => hash     == task.InfoHash));
        Assert.Equal(1, harness.Store.ReannounceCalls.Count(hash => hash == task.InfoHash));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
            Assert.StartsWith(candidateRoot, file.VerificationPath, StringComparison.OrdinalIgnoreCase);
        }

        await harness.ReconcileAsync(); // 完成轮：候选已在最终路径——无需迁移、无需 .replaced、无归档待核对。
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(DownloadArchiveReview.FromJson(completed.ArchiveReview));
        Assert.Equal(externalFile.Path, harness.Store.FileIdentities[externalHash][0].Path);
        Assert.Equal(await FirstAnimeFileIdAsync(harness, taskId),
                     await IncumbentVideoFileIdAsync(harness, taskId));
    }

    [Fact]
    public async Task Verification_SharedCandidateFails_IsolationReDownloadsInTaskRootWithoutTouchingExternal()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var       target  = await ArchivalTargetAsync(harness, taskId);
        var (externalHash, externalFile) = SeedExternalOccupant(harness, target, 1024);

        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck（进入 Checking）。
        await harness.ReconcileAsync(); // 观察位。

        // 候选数据未通过（该次映射未通过或不能确认）。
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync(); // 结论轮：进入隔离。

        var isolating = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.Deleting, isolating.IsolationState);
        Assert.Null(isolating.VerificationIntent);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }

        await harness.ReconcileAsync(); // Deleting：确认停止 → 保留文件删除。
        Assert.Contains(harness.Store.DeleteCalls, call => call.Hash == task.InfoHash && !call.DeleteFiles);

        await harness.ReconcileAsync(); // Readding：hash 已消失 → 任务专属根停止重加。
        Assert.Contains(harness.Store.AddCalls, call => call.SavePath == TaskRootOf(harness, taskId));

        await harness.ReconcileAsync(); // Confirming。
        await harness.ReconcileAsync(); // 布局确认 → 回 Planned。

        var restored = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, restored.IsolationState);
        Assert.Equal(DownloadPreparationStage.Planned, restored.PreparationStage);
        Assert.Equal(TaskRootOf(harness, taskId), restored.SavePath);

        // 独立下载完成：归档目标被未登记种子占用 → 待核对保留，绝不覆盖外部数据。
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(TaskRootOf(harness, taskId), completed.SavePath);
        Assert.Equal(externalFile.Path, harness.Store.FileIdentities[externalHash][0].Path);
    }

    /// <summary>
    ///     快速校验（qB 5.3 实测路径）：校验在两次采样之间完成、观察不到 Checking 时，凭请求前基线
    ///     未完成 → 请求后停止态完成的完成见证自动完成并登记复用——不隔离、不重下、不补发。
    /// </summary>
    [Fact]
    public async Task Verification_FastRecheckCompletesBetweenSamples_AutoCompletesAndRegistersReuse()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);

        // 快速校验：请求即按磁盘上的完整数据应用真实结果，两次采样之间永远观察不到 Checking。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.CompleteImmediately;
        harness.Factory.SessionConfigurator = session => session.BeforeRecheckAsync = () =>
        {
            // 数据早已完整但 qB 仍按 0% 处理；只能在请求时刻播种进度，映射前候选零进度检查不得看到它。
            harness.Store.FileIdentities[task.InfoHash] =
                harness.Store.FileIdentities[task.InfoHash].Select(file => file with { Progress = 1.0 }).ToList();
            return Task.CompletedTask;
        };

        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + 首次 recheck（结果在两次采样之间应用）。
        Assert.Equal(1, harness.Store.RecheckCalls.Count(hash => hash == task.InfoHash));
        await harness.ReconcileAsync(); // 完成见证 → 结果登记 → 收尾完成（Immediate 策略单样本稳定）。

        // 不再要求「永远未确认」：漏采 Checking 不代表校验未执行，基线未完成 → 停止态完成即可确认。
        var completedTask = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completedTask.Status);
        Assert.Null(completedTask.VerificationIntent);
        Assert.Equal(DownloadIsolationState.None, completedTask.IsolationState);
        Assert.Equal(1, harness.Store.RecheckCalls.Count(hash => hash == task.InfoHash)); // 见证先于补发。
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Single(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    [Fact]
    public async Task Verification_ErrorAfterChecking_IsNotAPass_AndIsolates()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var       target  = await ArchivalTargetAsync(harness, taskId);
        SeedExternalOccupant(harness, target, 1024);

        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck。
        await harness.ReconcileAsync(); // 观察位。

        // 校验结束后进入错误状态：结果不可解释，不算通过。
        harness.Store.FileIdentities[task.InfoHash] =
            harness.Store.FileIdentities[task.InfoHash]
                   .Select(file => file with { Progress = 1.0 })
                   .ToList();
        harness.Store.FinishRecheck(task.InfoHash, true);
        await harness.ReconcileAsync();

        var isolated = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.Deleting, isolated.IsolationState);
        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }
    }

    [Fact]
    public async Task Verification_UnexplainedRunningState_PausesWithoutCompletion()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var       target  = await ArchivalTargetAsync(harness, taskId);
        SeedExternalOccupant(harness, target, 1024);

        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck。

        // 无法解释的运行状态（校验后自动恢复或外部启动，快照无法区分）：统一先暂停，不进普通完成。
        harness.Store.StoppedAdds.Remove(task.InfoHash);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { Progress = 1.0 };
        await harness.ReconcileAsync();

        Assert.Contains(task.InfoHash, harness.Store.PauseCalls);
        var held = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, held.Status);
        Assert.Null(held.CompletedAtUtc);
        Assert.False(held.IsUserPaused);
        Assert.NotNull(DownloadVerificationIntent.FromJson(held.VerificationIntent));
    }

    [Fact]
    public async Task Verification_SeedingThenStoppedTorrent_PassesStopGuardAndReuses()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var       target  = await ArchivalTargetAsync(harness, taskId);
        SeedExternalOccupant(harness, target, 1024);

        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck（进入 Checking）。
        await harness.ReconcileAsync(); // 观察位。

        // 校验结束时任务已完成且被停止（分享率达标或外部停止 → qB 的 stoppedUP）：
        // 「做种后停止」是停止而不是运行中，停止守卫必须直接通过，不得反复发暂停或判「暂停不生效」。
        harness.Store.FileIdentities[task.InfoHash] =
            harness.Store.FileIdentities[task.InfoHash]
                   .Select(file => file with { Progress = 1.0 })
                   .ToList();
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
        };
        harness.Store.Paused.Add(task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync(); // 结论轮。

        Assert.DoesNotContain(task.InfoHash, harness.Store.PauseCalls);
        // 数据已确认完整：停止态下恢复一次做种并通告 tracker，而不是把完成等同于停止。
        Assert.Equal(1, harness.Store.ResumeCalls.Count(hash => hash     == task.InfoHash));
        Assert.Equal(1, harness.Store.ReannounceCalls.Count(hash => hash == task.InfoHash));
        var reused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, reused.Status);
        Assert.Null(reused.VerificationIntent);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    [Fact]
    public async Task Verification_PauseTakesEffect_EvidenceSurvivesAndReuses()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        var       target  = await ArchivalTargetAsync(harness, taskId);
        SeedExternalOccupant(harness, target, 1024);

        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck（进入 Checking）。
        await harness.ReconcileAsync(); // 观察位。

        // 校验后完整内容进入做种：暂停后保留证据，不与未确认候选发生下载混为一谈。
        harness.Store.FinishRecheck(task.InfoHash);
        CompleteTorrent(harness, task.InfoHash);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Seeding
        };
        await harness.ReconcileAsync();
        Assert.Contains(task.InfoHash, harness.Store.PauseCalls);

        // 暂停已生效（回到停止）：发过暂停本身不作废本轮校验证据——
        // 此处只观察到完整内容做种，没有未确认的共享下载，应继续完成归档。
        await harness.ReconcileAsync(); // 结论轮。

        var reused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, reused.IsolationState);
        Assert.Equal(DownloadTaskStatus.Completed, reused.Status);
        Assert.Null(reused.VerificationIntent);
        // 校验通过后继续做种：停止态下恢复一次并重新通告 tracker。
        Assert.Equal(1, harness.Store.ResumeCalls.Count(hash => hash     == task.InfoHash));
        Assert.Equal(1, harness.Store.ReannounceCalls.Count(hash => hash == task.InfoHash));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    [Fact]
    public async Task SingleFlow_ResumeThenImmediateShareLimitStop_CompletesWithoutRestarting()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Contains(task.InfoHash, harness.Store.ResumeCalls);

        // 启动后立即因分享率达标被 qB 停止（stoppedUP）：内容已完整即直接完成归档，
        // 不得再次认定启动失败或循环 Resume 绕过分享限制。
        harness.Store.FileIdentities[task.InfoHash] =
            harness.Store.FileIdentities[task.InfoHash]
                   .Select(file => file with { Progress = 1.0 })
                   .ToList();
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
        };
        harness.Store.Paused.Add(task.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(1, harness.Store.ResumeCalls.Count(hash => hash == task.InfoHash));
    }

    // ───────────────────────── 助手 ─────────────────────────

    private static async Task<string> ArchivalTargetAsync(VerificationHarness harness, Guid taskId)
    {
        var service = harness.Provider.GetRequiredService<DownloadFileService>();
        var targets = await service.ComputeArchivalTargetsAsync(taskId);
        return Assert.Single(targets).TargetPath;
    }

    private static (string Hash, TorrentFileSnapshot File) SeedExternalOccupant(
        VerificationHarness harness, string target, long size)
    {
        var hash   = new string('e', 40);
        var parent = ParentDirectory(target);
        var name   = target[(parent.Length + 1)..];
        var file   = new TorrentFileSnapshot(0, name, size, 1.0);
        harness.Store.AddTorrent(hash, "External Seed", QbittorrentTorrentCategory.Seeding, 1.0, parent);
        harness.Store.FileIdentities[hash] = [file];
        return (hash, file);
    }

    private static string ParentDirectory(string path) => path[..path.LastIndexOf('/')];

    private static void CompleteTorrent(VerificationHarness harness, string hash)
    {
        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 1.0 };
        // 完成判定按文件级进度核验：文件身份也置为已完成。
        if (harness.Store.FileIdentities.TryGetValue(hash, out var files))
            harness.Store.FileIdentities[hash] = files.Select(file => file with { Progress = 1.0 }).ToList();
    }

    private static async Task ExpireVerificationDeadlineAsync(VerificationHarness harness, Guid taskId)
    {
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        var intent = DownloadVerificationIntent.FromJson(task.VerificationIntent) ??
                     throw new InvalidOperationException("校验意图缺失。");
        task.VerificationIntent = (intent with { DeadlineUtc = DateTimeOffset.UnixEpoch }).ToJson();
        await db.SaveChangesAsync();
    }

    private static async Task<Guid?> IncumbentVideoFileIdAsync(VerificationHarness harness, Guid taskId)
    {
        await using var db = await harness.CreateDbContextAsync();
        var task = await db.DownloadTasks.Where(value => value.Id == taskId)
                           .Select(value => new { value.SubjectId, value.SiteSource }).FirstAsync();
        if (task.SubjectId is not { } subjectId) return null;
        var sourceScope = SourceScopes.FromSiteSource(task.SiteSource);
        var row = await db.EpisodeVersions.AsNoTracking()
                          .Where(value => value.SubjectId            == subjectId   &&
                                          value.SourceScope          == sourceScope &&
                                          value.IncumbentVideoFileId != null)
                          .ToListAsync();
        return row.SingleOrDefault()?.IncumbentVideoFileId;
    }

    private static async Task<Guid> FirstAnimeFileIdAsync(VerificationHarness harness, Guid taskId)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.DownloadTaskFiles.AsNoTracking()
                       .Where(value => value.DownloadTaskId == taskId && value.Kind == DownloadFileKind.Anime)
                       .OrderBy(value => value.FileIndex)
                       .Select(value => value.Id)
                       .FirstAsync();
    }

    /// <summary>完成一个现任任务（登记 + 完成轮 + 确认归档），返回任务 Id。</summary>
    private static async Task<Guid> CompleteIncumbentAsync(VerificationHarness harness, string language)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent(language));
        var task   = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync(); // 完成轮：归档待核对。
        return taskId;
    }

    private static async Task PrepareReplacementAfterFailedCheckAsync(VerificationHarness harness, Guid taskId)
    {
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        var task = await harness.GetTaskAsync(taskId);
        if (task.VerificationIntent is not null)
        {
            harness.Store.FinishRecheck(task.InfoHash);
            await harness.ReconcileAsync();
        }

        for (var i = 0; i < 7; i++) await harness.ReconcileAsync();
    }

    [Fact]
    public async Task SingleFlow_DifferentSizeUpgradeDownloadsTemporarilyAndReplacesOnlyAfterCompletion()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId   = await CompleteIncumbentAsync(harness, "繁日双语");
        var old     = await harness.GetTaskAsync(oldId);
        var oldPath = Assert.Single(harness.Store.FileIdentities[old.InfoHash]).Path;
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(SingleFileTorrent())
                                                   .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                            StringComparison.Ordinal));
        var newId    = await harness.CreateUploadAsync(bytes);
        var incoming = await harness.GetTaskAsync(newId);
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Equal(TaskRootOf(harness, newId), (await harness.GetTaskAsync(newId)).SavePath);
        Assert.Empty(harness.Store.RecheckCalls);
        Assert.Equal(oldPath, Assert.Single(harness.Store.FileIdentities[old.InfoHash]).Path);
        Assert.Equal(await FirstAnimeFileIdAsync(harness, oldId), await IncumbentVideoFileIdAsync(harness, newId));
        CompleteTorrent(harness, incoming.InfoHash);
        await harness.ReconcileAsync();
        var completed = await harness.GetTaskAsync(newId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(MediaRoot, completed.SavePath);
        Assert.Null(completed.ArchiveReview);
        Assert.StartsWith(".replaced/", Assert.Single(harness.Store.FileIdentities[old.InfoHash]).Path);
        Assert.Equal(await FirstAnimeFileIdAsync(harness, newId), await IncumbentVideoFileIdAsync(harness, newId));
    }

    /// <summary>喵萌式发布名：与既有用例完全同形态，确保 AniParser 解析出集数并分类为正片。</summary>
    private static byte[] SingleFileTorrent(string language = "简日双语") =>
        TestTorrentBytes.Create($"【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][01][1080p][{language}].mp4");

    /// <summary>简日（JpSc）优先于繁日的规则，与 DownloadServiceTests.NekoRules 同构造。</summary>
    private static SelectionRuleConfig NekoRules() =>
        SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["喵萌奶茶屋"], LanguageOrder = [EnumLanguage.JpSc, EnumLanguage.Sc]
        };

    /// <summary>第三阶段专用测试 harness：文件服务恒注册、默认命名模板与下载根，独立于其它测试文件。</summary>
    private sealed class VerificationHarness(
        ServiceProvider               provider,
        DirectoryInfo                 directory,
        FakeTorrentStore              store,
        FakeQbittorrentSessionFactory factory,
        DownloadService               service,
        DownloadIntakeService         intake) : IDisposable
    {
        public IServiceProvider              Provider { get; } = provider;
        public FakeTorrentStore              Store    { get; } = store;
        public FakeQbittorrentSessionFactory Factory  { get; } = factory;

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

        public static async Task<VerificationHarness> CreateAsync()
        {
            var directory = Directory.CreateTempSubdirectory("Banori.P3V.Tests.");
            var services  = new ServiceCollection();
            services.AddLogging();
            // 登记服务按 Data Protection 解密订阅 RSS 地址后才判定来源；测试用临时密钥即可。
            services.AddDataProtection();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                                   options
                                                                      .UseSqlite($"Data Source={Path.Combine(directory.FullName, "p3v.db")};Pooling=False"));
            services.AddSingleton<TimeProvider>(new FixedVerificationTimeProvider());
            services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
            var store = new FakeTorrentStore();
            services.AddSingleton(store);
            var factory = new FakeQbittorrentSessionFactory(store);
            services.AddSingleton(factory);
            services.AddSingleton<IQbittorrentSessionFactory>(factory);
            services.AddSingleton<IFeedSourceReader>(new FakeFeedSourceReader());
            services.AddSingleton<ITorrentFileSource>(new FakeTorrentFileSource(_ => TestTorrentBytes.Create()));
            var subjectProvider = new FakeVerificationSubjectProvider();
            services.AddSingleton<ISubjectMetadataProvider>(subjectProvider);
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
            services.AddScoped<NamingTemplateStore>();
            services.AddSingleton<DownloadCoordinationGate>();
            services.AddScoped<DownloadDirectorySettingsStore>();
            services.AddSingleton<DownloadFileClassificationPolicy>();
            services.AddScoped<DownloadFileService>();
            services.AddScoped<DownloadService>();
            services.AddScoped<DownloadIntakeService>();
            services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                          IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();

            var harness = new VerificationHarness(provider, directory, store, factory,
                                                  provider.GetRequiredService<DownloadService>(),
                                                  provider.GetRequiredService<DownloadIntakeService>());
            await using var db = await harness.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            db.MediaSubjects.Add(new MediaSubject
            {
                Id          = 42, Name = "Verification Subject", NameCn = "验证作品", Platform = "TV",
                CachedAtUtc = DateTimeOffset.Parse("2026-09-25T04:00:00Z")
            });
            await db.SaveChangesAsync();
            await harness.SaveDownloaderSettingsAsync();
            await harness.SaveNamingTemplateAsync("{title}");
            await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
            return harness;
        }

        public Task ReconcileAsync() => service.ReconcileAsync();

        public Task<Guid> CreateUploadAsync(byte[] torrentBytes) =>
            intake.CreateFromUploadAsync(torrentBytes, null, 42);

        public async Task<DownloadTask> GetTaskAsync(Guid taskId)
        {
            await using var db = await CreateDbContextAsync();
            return await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        }

        public Task RetryArchiveAsync(Guid taskId) =>
            Provider.GetRequiredService<DownloadFileService>().RetryArchiveAsync(taskId);


        public Task SaveDownloaderSettingsAsync()
        {
            var settingsService = new ManageQbittorrentSettings(Provider.GetRequiredService<IApplicationSettingStore>(),
                                                                Factory);
            return settingsService.SaveAsync(
                                             new QbittorrentSettings("http://qb.test:8080", "admin", DownloadRoot,
                                                                     true), "secret");
        }

        public async Task SaveNamingTemplateAsync(string template) =>
            await new NamingTemplateStore(Provider.GetRequiredService<IApplicationSettingStore>())
               .SaveAsync(template);

        public async Task SaveFileNamingTemplateAsync(string template) =>
            await new NamingTemplateStore(Provider.GetRequiredService<IApplicationSettingStore>())
               .SaveFileTemplateAsync(template);

        public async Task SaveSelectionRulesAsync(SelectionRuleConfig config)
        {
            var ruleSettings = new ManageSelectionRuleSettings(Provider.GetRequiredService<IApplicationSettingStore>(),
                                                               NullLogger<ManageSelectionRuleSettings>.Instance);
            await ruleSettings.SaveAsync(config);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync() =>
            Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();

        private sealed class FixedVerificationTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-25T04:00:00Z");
        }

        private sealed class FakeVerificationSubjectProvider : ISubjectMetadataProvider
        {
            public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new SubjectMetadata(subjectId, "Verification Subject", "验证作品", "TV",
                                                    "2026-01-01", null,
                                                    DateTimeOffset.Parse("2026-09-25T04:00:00Z")));
        }
    }
}
