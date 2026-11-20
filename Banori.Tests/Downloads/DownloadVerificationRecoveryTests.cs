using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadVerificationTests
{
    [Fact]
    public async Task Verification_AddResponseLost_RecoversIntentWithoutOrdinaryAdoption()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Submitting, (await harness.GetTaskAsync(taskId)).Status);
        harness.Factory.SessionConfigurator = null;
        harness.Store.RecheckBehavior       = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();

        Assert.Single(harness.Store.AddCalls);
        Assert.Contains(task.InfoHash, harness.Store.RecheckCalls);
        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        Assert.False((await harness.GetTaskAsync(taskId)).IsUserPaused);
        Assert.Empty(harness.Store.SetLocationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_OriginalAndParkingConflicts_BlockSubmission(bool parking)
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       bytes   = MixedTorrent();
        var       taskId  = await harness.CreateUploadAsync(bytes);
        var       task    = await harness.GetTaskAsync(taskId);
        var targets = await harness.Provider.GetRequiredService<DownloadFileService>()
                                   .ComputeArchivalTargetsAsync(taskId);
        SeedExternalOccupant(harness, targets[0].TargetPath, 1024);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var multi));
        var original = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files.Last(), multi);
        var root     = DownloadRoot;
        var hash     = new string('d', 40);
        harness.Store.AddTorrent(hash, "Initial layout occupant", QbittorrentTorrentCategory.Seeding, 1, root);
        harness.Store.FileIdentities[hash] =
            [new TorrentFileSnapshot(0, parking ? $".tasks-{taskId:N}/{original}" : original, 17)];

        await harness.ReconcileAsync();

        Assert.Empty(harness.Store.AddCalls);
        Assert.Equal(DownloadTaskStatus.Failed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Null((await harness.GetTaskAsync(taskId)).VerificationIntent);
    }

    [Fact]
    public async Task Verification_PartialReuse_DownloadsMissingFilesAtTargetAndParksUnselected()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(MixedTorrent());
        await using (var db = await harness.CreateDbContextAsync())
        {
            var attachment =
                await db.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == taskId &&
                                                            f.Kind           == DownloadFileKind.Seed);
            attachment.IsSelectedForDownload = false;
            await db.SaveChangesAsync();
        }

        var targets = await harness.Provider.GetRequiredService<DownloadFileService>()
                                   .ComputeArchivalTargetsAsync(taskId);
        SeedExternalOccupant(harness, targets[0].TargetPath, 1024);
        var task = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 5; i++) await harness.ReconcileAsync();
        Assert.Contains(task.InfoHash, harness.Store.RecheckCalls);
        var intent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent)!;
        Assert.Contains(intent.Files, f => !f.Selected);
        Assert.All(harness.Store.FileIdentities[task.InfoHash].Where(f => !f.IsSelectedForDownload),
                   f => Assert.StartsWith($".tasks-{taskId:N}/", f.Path));
        Assert.All(harness.Store.FileIdentities[task.InfoHash].Where(f => f.IsSelectedForDownload),
                   f => Assert.StartsWith("Media/Anime/", f.Path));
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(f => f with
                                                              {
                                                                  Progress = intent.Files
                                                                     .Single(m => m.IsCandidate)
                                                                     .VerificationPath.EndsWith('/' + f.Path)
                                                                      ? 1
                                                                      : 0
                                                              })
                                                             .ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync();
        var renameCount = harness.Store.RenameFileCalls.Count;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Empty(harness.Store.SetLocationCalls);
        // 混合布局无法在同一保存根下安全归档时，保留共享候选，不能先移动候选再搬根。
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Null((await harness.GetTaskAsync(taskId)).ArchiveReview);
        Assert.Empty(harness.Store.SetLocationCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("root")]
    [InlineData("size")]
    [InlineData("selection")]
    public async Task Verification_ChangedEvidenceIsNeverAccepted(string change)
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(f => f with
                                                              {
                                                                  Progress = 1, Size = change == "size" ? 999 : f.Size,
                                                                  IsSelectedForDownload = change != "selection"
                                                              }).ToList();
        if (change == "missing") harness.Store.Torrents.Remove(task.InfoHash);
        if (change == "root")
            harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { SavePath = "/moved" };

        await harness.ReconcileAsync();

        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        var held = await harness.GetTaskAsync(taskId);
        Assert.True(held.IsolationState != DownloadIsolationState.None || held.Status == DownloadTaskStatus.Failed);
        if (change == "missing") Assert.Empty(harness.Store.PauseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_MovingOrUnrequestedCheckingNeverCountsAsRequestedRecheck(bool checking)
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        if (checking) harness.Store.Rechecking.Add(task.InfoHash);
        else harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { IsMoving = true };

        await harness.ReconcileAsync();

        var intent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent)!;
        Assert.False(intent.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Mapping, intent.Step);
        Assert.Empty(harness.Store.ResumeCalls);
    }

    [Fact]
    public async Task Verification_CheckingNoProgress_StopsThenFailsWhenStopIneffective()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Contains(task.InfoHash, harness.Store.Rechecking);

        // 无进展期限到期（固定时钟下把有效进展与停止台账改写为过去）：到期退出先停止确认，
        // 停止请求在确认窗口内不重复发；窗口反复过期且停止不生效（fake 保持 Checking）才判失败。
        for (var round = 0; round < 5; round++)
        {
            await AgeOutCheckProgressAsync(harness, taskId);
            await harness.ReconcileAsync();
        }

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.True(harness.Store.PauseCalls.Count(hash => hash == task.InfoHash) is >= 1 and <= 3);
        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        Assert.All(harness.Store.DeleteCalls, call => Assert.NotEqual(task.InfoHash, call.Hash));
    }

    /// <summary>把本轮校验的有效进展时间与超时停止台账改写为窗口前：模拟无进展期限与停止确认窗口均已到期。</summary>
    private static async Task AgeOutCheckProgressAsync(VerificationHarness harness, Guid taskId)
    {
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        var intent = DownloadVerificationIntent.FromJson(task.VerificationIntent) ??
                     throw new InvalidOperationException("校验意图缺失。");
        task.VerificationIntent = (intent with
        {
            LastCheckProgressAtUtc = DateTimeOffset.UnixEpoch,
            TimeoutStopRequestedAtUtc = DateTimeOffset.UnixEpoch
        }).ToJson();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Isolation_DiscardsOldRenameJournalBeforeAnyRecovery()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.Include(t => t.Files).SingleAsync(t => t.Id == taskId);
            tracked.IsolationState  = DownloadIsolationState.Deleting;
            tracked.PendingSavePath = tracked.SavePath;
            foreach (var file in tracked.Files)
            {
                file.PendingRenameFrom = file.CurrentRelativePath;
                file.PendingRenameTo   = "obsolete.mkv";
            }

            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Contains(harness.Store.DeleteCalls, c => c.Hash == task.InfoHash && !c.DeleteFiles);
    }

    [Fact]
    public async Task Verification_ManualFileEditCannotBypassMapping()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        await harness.ReconcileAsync();
        var files = harness.Provider.GetRequiredService<DownloadFileService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.ApplyAsync(taskId, 0,
                                                                     new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                                              Episode : 3,
                                                                              AnimeRelativePath : "changed.mp4")));
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.SetLocationCalls);
    }

    [Fact]
    public async Task Verification_ManagedCandidateCanFinishWithoutMovingIncumbent()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var incumbentId = await CompleteIncumbentAsync(harness, "繁日双语");
        var incumbent   = await harness.GetTaskAsync(incumbentId);
        var taskId      = await harness.CreateUploadAsync(SingleFileTorrent());
        var task        = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 1 }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        var renameCount = harness.Store.RenameFileCalls.Count(c => c.Hash == incumbent.InfoHash);
        await harness.ReconcileAsync();
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.ArchiveReview);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count(c => c.Hash == incumbent.InfoHash));
    }

    private static async Task<Guid> CreateCandidateAsync(VerificationHarness harness)
    {
        var id = await harness.CreateUploadAsync(SingleFileTorrent());
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, id), 1024);
        return id;
    }

    [Fact]
    public async Task Archive_SelectedFilesComplete_CompletesWithoutApproval()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(MixedTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var attachment =
                await db.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == taskId &&
                                                            f.Kind           == DownloadFileKind.Seed);
            attachment.IsSelectedForDownload = false;
            await db.SaveChangesAsync();
        }

        for (var i = 0; i < 4; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(f => f with
                                                              {
                                                                  Progress = f.IsSelectedForDownload ? 1 : 0
                                                              }).ToList();
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { Progress = 0.5 };
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Null((await harness.GetTaskAsync(taskId)).ArchiveReview);
        Assert.Equal(DownloadRoot, (await harness.GetTaskAsync(taskId)).SavePath);
    }

    [Fact]
    public async Task Archive_DormantSamePathWithoutVerificationCannotBypassOccupancy()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId  = await CompleteIncumbentAsync(harness, "繁日双语");
        var old    = await harness.GetTaskAsync(oldId);
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        var task   = await harness.GetTaskAsync(taskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var owner = await db.DownloadTasks.Include(t => t.Files).SingleAsync(t => t.Id == oldId);
            owner.Status         = DownloadTaskStatus.Replaced;
            owner.CompletedAtUtc = null;
            foreach (var file in owner.Files)
            {
                file.IsSelectedForDownload = false;
                file.SupersededByTaskId    = taskId;
            }

            await db.SaveChangesAsync();
        }

        harness.Store.Torrents.Remove(old.InfoHash);
        await PrepareReplacementAfterFailedCheckAsync(harness, taskId);
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        var held = await harness.GetTaskAsync(taskId);
        Assert.Equal(TaskRootOf(harness, taskId), held.SavePath);
        Assert.NotNull(held.ArchiveReview);
        Assert.NotNull(held.ArchiveReview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_ManualPausePreservedDuringSafetyRecovery(bool isolation)
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        await harness.Provider.GetRequiredService<DownloadService>().PauseAsync(taskId);
        harness.Store.Paused.Remove(task.InfoHash);
        harness.Store.StoppedAdds.Remove(task.InfoHash);
        if (isolation)
        {
            await using var db      = await harness.CreateDbContextAsync();
            var             tracked = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
            tracked.VerificationIntent = null;
            tracked.IsolationState     = DownloadIsolationState.Deleting;
            await db.SaveChangesAsync();
        }

        var count = harness.Store.PauseCalls.Count;
        await harness.ReconcileAsync();
        Assert.True(harness.Store.PauseCalls.Count > count);
        Assert.True((await harness.GetTaskAsync(taskId)).IsUserPaused);
        Assert.Empty(harness.Store.ResumeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_ResumeFailureKeepsRecoverableRunAndEvidence(bool invalidate)
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreatePartialCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with
                                                              {
                                                                  Progress = file.Index == 0 ? 1 : 0
                                                              }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume interrupted");
        await harness.ReconcileAsync();
        var held = await harness.GetTaskAsync(taskId);
        // 启动状态与数据完整性分离：Resume 失败绝不误标用户暂停、绝不改写验证证据；
        // 候选已验证（意图收尾），启动转入 RunRecovery 等待观察确认与有界重试。
        Assert.Equal(DownloadTaskStatus.Downloading, held.Status);
        Assert.True(held.IsSystemPaused);
        Assert.False(held.IsUserPaused);
        Assert.Null(held.VerificationIntent);
        var run = DownloadRunRecovery.FromJson(held.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunPurposes.ContinueDownload, run.Purpose);
        Assert.Equal(DownloadRunStates.ResponseUnknown, run.State);
        harness.Factory.SessionConfigurator = null;
        if (invalidate)
        {
            // 布局随后被外部改动：启动恢复路径绝不借此触发隔离、删除或重新校验——
            // 那是普通对账的职责；启动恢复只更新自己的记录，文件与证据保持原样。
            harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                                 .Select(file => file with { Size = 999 }).ToList();
            for (var i = 0; i < 6; i++) await harness.ReconcileAsync();
            var stalled = await harness.GetTaskAsync(taskId);
            Assert.NotEqual(DownloadIsolationState.Deleting, stalled.IsolationState);
            Assert.Null(stalled.FailedStep);
            Assert.Single(harness.Store.RecheckCalls);
            Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.Hash == task.InfoHash);
            // 后续一次自动重试成功即确认运行并清空记录（回普通下载跟踪）；未确认时保留待恢复记录。
            var stalledRun = DownloadRunRecovery.FromJson(stalled.RunRecovery);
            Assert.True(stalledRun is null ||
                        stalledRun.State is DownloadRunStates.FailedRetryable or DownloadRunStates.DownloaderStopped);
            Assert.True(stalled.RunRecovery is null ? !stalled.IsSystemPaused : stalled.IsSystemPaused);
        }

        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        var settled = await harness.GetTaskAsync(taskId);
        // 有界自动重试（预算 ≤ MaxRunResumeAttempts）耗尽后不再自动重发；无论记录是否已确认清空，
        // 任务都不转失败、不误标用户暂停、不触发重新校验或删除。
        Assert.True(harness.Store.ResumeCalls.Count <= 3);
        Assert.Single(harness.Store.RecheckCalls);
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.Hash == task.InfoHash);
        Assert.NotEqual(DownloadTaskStatus.Failed, settled.Status);
        Assert.False(settled.IsUserPaused);
    }

    [Fact]
    public async Task Verification_RootChangesDuringResultRead_DoesNotResume()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 1 }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        var queries = 0;
        harness.Factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = _ =>
        {
            if (++queries == 2)
                harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
                {
                    SavePath = "/changed-during-read"
                };
            return Task.CompletedTask;
        };
        await harness.ReconcileAsync();
        Assert.Empty(harness.Store.ResumeCalls);
        Assert.NotNull((await harness.GetTaskAsync(taskId)).VerificationIntent);
    }

    [Fact]
    public async Task Verification_CorruptIntentCannotFallBackToOrdinaryPreparation()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        await harness.ReconcileAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
            task.VerificationIntent = "{broken";
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Failed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.ResumeCalls);
    }

    [Fact]
    public async Task Archive_PlanChanged_StopsPendingRootMove()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        var temporary = TaskRootOf(harness, taskId);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { SavePath = temporary };
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
            tracked.SavePath = temporary;
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        harness.Store.DeferLocation = true;
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == taskId);
            file.AnimeRelativePathOverride = "changed.mp4";
            await db.SaveChangesAsync();
        }

        harness.Store.PendingLocations.Clear();
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { IsMoving = false };
        var moves = harness.Store.SetLocationCalls.Count;
        await harness.ReconcileAsync();
        Assert.Equal(moves, harness.Store.SetLocationCalls.Count);
        Assert.Equal(DownloadTaskStatus.Failed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Equal(temporary, harness.Store.Torrents[task.InfoHash].SavePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingHash_AdoptsProgressAndArchivesAfterCompletion(bool complete)
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       bytes   = SingleFileTorrent();
        var       taskId  = await harness.CreateUploadAsync(bytes);
        var       task    = await harness.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var multi));
        var original = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files.Single(), multi);
        harness.Store.AddTorrent(task.InfoHash, "Externally added", QbittorrentTorrentCategory.Downloading,
                                 complete ? 1 : 0.5, "/external", [original]);
        harness.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, original, 1024, complete ? 1 : 0.5)];
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Empty(harness.Store.AddCalls);
        var adopted = await harness.GetTaskAsync(taskId);
        Assert.Equal(complete ? DownloadTaskStatus.Completed : DownloadTaskStatus.Downloading, adopted.Status);
        Assert.Equal(complete ? MediaRoot : "/external", adopted.SavePath);
        Assert.Null(adopted.ArchiveReview);
        Assert.Empty(harness.Store.ResumeCalls);
        if (!complete) Assert.Empty(harness.Store.RenameFileCalls);
    }

    [Fact]
    public async Task Archive_AsynchronousMove_RecoversWithoutApproval()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        var temporary = TaskRootOf(harness, taskId);
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { SavePath = temporary };
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
            tracked.SavePath = temporary;
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        harness.Store.DeferLocation = true;
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        Assert.NotNull((await harness.GetTaskAsync(taskId)).PendingSavePath);
        harness.Store.CompleteMoves();
        await harness.ReconcileAsync();
        var archived = await harness.GetTaskAsync(taskId);
        Assert.Null(archived.ArchiveReview);
        Assert.Null(archived.PendingSavePath);
        Assert.Equal(MediaRoot, archived.SavePath);
    }

    [Fact]
    public async Task Verification_ReselectionAfterSuccessInvalidatesEvidenceAndIsolatesBeforeDownload()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreatePartialCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with
                                                              {
                                                                  Progress = file.Index == 0 ? 1 : 0
                                                              }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync();
        var successorId = Guid.NewGuid();
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.DownloadTasks.Add(new DownloadTask
            {
                Id       = successorId, InfoHash  = new string('c', 40), Status = DownloadTaskStatus.Failed,
                SavePath = "/unused", TorrentFile = SingleFileTorrent("繁日双语"), NamingSnapshot = "Successor"
            });
            var file = await db.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == taskId && f.FileIndex == 0);
            file.IsSelectedForDownload = false;
            file.SupersededByTaskId    = successorId;
            await db.SaveChangesAsync();
        }

        await harness.Provider.GetRequiredService<DownloadService>().DeleteAsync(successorId, false, false);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == taskId && f.FileIndex == 0);
            Assert.True(file.IsSelectedForDownload);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }

        var resumeCount = harness.Store.ResumeCalls.Count;
        await harness.ReconcileAsync();
        Assert.Equal(DownloadIsolationState.Deleting, (await harness.GetTaskAsync(taskId)).IsolationState);
        Assert.Equal(resumeCount, harness.Store.ResumeCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_ManagedCandidateMatchesOwnIndexAndActualOccupantSize(bool differentSize)
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var incumbentId = await CompleteIncumbentAsync(harness, "繁日双语");
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == incumbentId);
            file.FileIndex = 7; // 内部键只在所属任务内稳定，与挑战者的 0 不同。
            if (differentSize) file.Size = 2048;
            await db.SaveChangesAsync();
        }

        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        await harness.ReconcileAsync();
        var task   = await harness.GetTaskAsync(taskId);
        var intent = DownloadVerificationIntent.FromJson(task.VerificationIntent);
        if (differentSize)
        {
            Assert.Null(intent);
            Assert.Equal(TaskRootOf(harness, taskId), task.SavePath);
        }
        else
        {
            Assert.NotNull(intent);
            Assert.Equal(0, Assert.Single(intent.Files).FileIndex);
        }
    }

    private static byte[] MixedTorrent()
    {
        static string Encode(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";
        var names = new[]
        {
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][01][1080p][简日双语].mp4",
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][02][1080p][简日双语].mp4", "readme.txt"
        };
        var entries = string.Concat(names.Select(name => $"d6:lengthi1024e4:pathl{Encode(name)}ee"));
        return Encoding.UTF8.GetBytes($"d4:infod5:filesl{entries}e4:name{Encode("Test Anime")}" +
                                      $"12:piece lengthi16384e6:pieces20:{new string('0', 20)}ee");
    }
}
