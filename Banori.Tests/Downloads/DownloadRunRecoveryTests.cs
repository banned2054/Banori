using Banned.Qbittorrent.Exceptions;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     问题四回归：数据完整与做种/继续下载是否恢复分离（RunRecovery journal-first 状态机）。
///     Full=Seeding（Completed 宿主，失败绝不改写状态/FailedStep/Error/CompletedAtUtc）；
///     Partial=ContinueDownload（Downloading+IsSystemPaused 宿主，确认运行后清记录回普通下载跟踪）。
/// </summary>
public sealed partial class DownloadVerificationTests
{
    // ───────────────────────── Full（Purpose=Seeding） ─────────────────────────

    /// <summary>F1：Resume 失败仍完成——数据完整性与启动恢复分离，任务保持 Completed、可恢复、被调度观察。</summary>
    [Fact]
    public async Task RunRecovery_Full_ResumeFailure_KeepsCompletedWithResponseUnknown()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume interrupted");

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Null(completed.FailedStep);
        Assert.Null(completed.Error);
        Assert.Null(completed.VerificationIntent);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunPurposes.Seeding, run.Purpose);
        Assert.Equal(DownloadRunStates.ResponseUnknown, run.State);
        Assert.Equal(1, run.AttemptCount);
        Assert.Equal(DownloadRunRequestOutcomes.Unknown, run.RequestOutcome);
        // 待确认状态进入观察调度：worker 按最早到期唤醒。
        Assert.NotNull(completed.NextObservationAtUtc);
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.ReannounceCalls);
    }

    /// <summary>F2：Resume 响应丢失但已生效——立即观察确认运行（ConfirmedRunning），绝不重复启动。</summary>
    [Fact]
    public async Task RunRecovery_Full_ApplyThenLost_ConfirmsRunningWithoutRestart()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator = session => session.ResumeApplyThenThrowTransport = true;

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.ConfirmedRunning, run.State);
        Assert.NotNull(run.ObservedRunningAtUtc);
        Assert.Equal(DownloadRunRequestOutcomes.Unknown, run.RequestOutcome);
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Single(harness.Store.ReannounceCalls);
        // 已确认运行不再自动轮询；做种可达性交给下载器自行通告后的下一轮观察。
        Assert.Null(completed.NextObservationAtUtc);
    }

    /// <summary>F3：响应未知的有界自动重试——耗尽转 FailedRetryable，可人工恢复，绝不写失败步骤。</summary>
    [Fact]
    public async Task RunRecovery_Full_BoundedAutoRetry_ExhaustsToFailedRetryable()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume keeps failing");

        await harness.ReconcileAsync();
        for (var i = 0; i < 4; i++) await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        Assert.Null(completed.Error);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.FailedRetryable, run.State);
        // 预算 = MaxRunResumeAttempts（3）：初次 + 两次有界自动重试，之后不再自动重发。
        Assert.Equal(3, harness.Store.ResumeCalls.Count);
        Assert.Single(harness.Store.RecheckCalls);
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.Hash == task.InfoHash);
        Assert.Equal(DownloadIsolationState.None, completed.IsolationState);
    }

    /// <summary>F4：Resume 成功但立即被分享限制停止——Sent 后停止是下载器策略，不自动再次拉起。</summary>
    [Fact]
    public async Task RunRecovery_Full_SentThenExternalStop_IsDownloaderStoppedWithoutAutoRestart()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator = session => session.AfterResumeAsync = () =>
        {
            // 分享率达标：qB 接受启动后立即停止（stoppedUP）。
            harness.Store.Paused.Add(task.InfoHash);
            return Task.CompletedTask;
        };

        await harness.ReconcileAsync();
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.DownloaderStopped, run.State);
        // 只启动一次，不再自动重试（不与分享限制对抗）。
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.ReannounceCalls);
    }

    /// <summary>F5：下载器明确拒绝（种子不存在）——转终态等待人工核对，不自动重试、不重校验。</summary>
    [Fact]
    public async Task RunRecovery_Full_ExplicitRejection_StopsWithoutRetry()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new QbittorrentNotFoundException("torrent gone");

        await harness.ReconcileAsync();
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.DownloaderStopped, run.State);
        Assert.Equal(DownloadRunRequestOutcomes.Rejected, run.RequestOutcome);
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Single(harness.Store.RecheckCalls);
    }

    /// <summary>F7：Reannounce 失败只记录——不影响 ConfirmedRunning、完整性证据与校验/隔离/删除台账。</summary>
    [Fact]
    public async Task RunRecovery_Full_ReannounceFailure_IsRecordedOnly()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ReannounceException = new QbittorrentException("tracker unreachable");

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.ConfirmedRunning, run.State);
        Assert.NotNull(run.LastReannounceError);
        Assert.Single(harness.Store.RecheckCalls);
        Assert.Single(harness.Store.ReannounceCalls);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>F8：完成后种子被外部删除——只记录 DownloaderStopped，任务保持 Completed、绝不转 Lost。</summary>
    [Fact]
    public async Task RunRecovery_Full_TorrentRemovedAfterRequest_StaysCompleted()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume interrupted");
        await harness.ReconcileAsync();

        // 外部删除种子后推进观察。
        harness.Store.Torrents.Remove(task.InfoHash);
        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        Assert.Null(completed.Error);
        var run = DownloadRunRecovery.FromJson(completed.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.DownloaderStopped, run.State);
        Assert.Contains("已无该种子", run.Reason);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>F9：人工「恢复做种」入口——FailedRetryable 单次请求，成功转 AwaitingConfirm 并确认运行。</summary>
    [Fact]
    public async Task RunRecovery_Full_ManualResumeRun_SingleAttemptThenConfirmed()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume interrupted");
        await harness.ReconcileAsync();
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Equal(DownloadRunStates.FailedRetryable,
                     DownloadRunRecovery.FromJson((await harness.GetTaskAsync(taskId)).RunRecovery)!.State);

        harness.Factory.SessionConfigurator = null;
        using (var scope = harness.Provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await service.ResumeRunAsync(taskId);
            // 已在待确认/确认运行状态的任务拒绝重复人工入口。
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeRunAsync(taskId));
        }

        var requested = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, requested.Status);
        Assert.Single(harness.Store.ReannounceCalls); // 立即观察确认运行（Seeding）后通告一次。
        var run = DownloadRunRecovery.FromJson(requested.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunStates.ConfirmedRunning, run.State);
        Assert.Equal(4, harness.Store.ResumeCalls.Count); // 3 次自动（含首次）+ 1 次人工。
    }

    /// <summary>F10：完成后应用重启（新 scope）——仅凭持久化恢复记录继续观察并确认运行。</summary>
    [Fact]
    public async Task RunRecovery_Full_Restart_ContinuesObservationFromPersistedRecord()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume interrupted");
        await harness.ReconcileAsync();
        var before = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadRunStates.ResponseUnknown,
                     DownloadRunRecovery.FromJson(before.RunRecovery)!.State);

        // 应用重启：新进程新 scope，只有持久化记录与下载器事实可用。
        harness.Factory.SessionConfigurator = null;
        using var scope   = harness.Provider.CreateScope();
        var       service = scope.ServiceProvider.GetRequiredService<DownloadService>();
        await service.ReconcileAsync();

        var recovered = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, recovered.Status);
        Assert.Equal(DownloadRunStates.ConfirmedRunning,
                     DownloadRunRecovery.FromJson(recovered.RunRecovery)!.State);
        Assert.Single(harness.Store.ReannounceCalls);
        // 只补发一次有界重试，不重复提交种子。
        Assert.Single(harness.Store.AddCalls);
    }

    // ───────────────────── Partial（Purpose=ContinueDownload） ─────────────────────

    /// <summary>P1：部分完成的 Resume 失败不误标用户暂停——保持 Downloading+系统暂停与可恢复记录。</summary>
    [Fact]
    public async Task RunRecovery_Partial_ResumeFailure_KeepsSystemPausedNotUserPaused()
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
        Assert.Equal(DownloadTaskStatus.Downloading, held.Status);
        Assert.True(held.IsSystemPaused);
        Assert.False(held.IsUserPaused);
        Assert.Null(held.FailedStep);
        Assert.Null(held.Error);
        var run = DownloadRunRecovery.FromJson(held.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunPurposes.ContinueDownload, run.Purpose);
        Assert.Equal(DownloadRunStates.ResponseUnknown, run.State);
    }

    /// <summary>P2：部分完成响应丢失但已生效——确认运行后清记录（含系统暂停），回普通下载跟踪，不重复 Resume。</summary>
    [Fact]
    public async Task RunRecovery_Partial_ConfirmedRunning_ClearsRecordAndResumesTracking()
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
        harness.Factory.SessionConfigurator = session => session.ResumeApplyThenThrowTransport = true;

        await harness.ReconcileAsync();

        var running = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, running.Status);
        Assert.False(running.IsSystemPaused);
        Assert.False(running.IsUserPaused);
        Assert.Null(running.VerificationIntent);
        Assert.Null(running.RunRecovery);
        Assert.Single(harness.Store.ResumeCalls);
        // 清空记录后回到普通下载跟踪：不会因启动恢复再暂停。
        await harness.ReconcileAsync();
        var tracked = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, tracked.Status);
        Assert.False(tracked.IsSystemPaused);
        Assert.Empty(harness.Store.PauseCalls);
    }

    /// <summary>P3：部分完成启动失败有界重试——耗尽仍 Downloading+系统暂停，UI 有「继续下载」入口可用。</summary>
    [Fact]
    public async Task RunRecovery_Partial_BoundedRetry_ExhaustsWhileStayingSystemPaused()
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
            session => session.ResumeException = new IOException("Resume keeps failing");

        await harness.ReconcileAsync();
        for (var i = 0; i < 4; i++) await harness.ReconcileAsync();

        var held = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, held.Status);
        Assert.True(held.IsSystemPaused);
        Assert.False(held.IsUserPaused);
        Assert.Equal(DownloadRunStates.FailedRetryable,
                     DownloadRunRecovery.FromJson(held.RunRecovery)!.State);
        Assert.Equal(3, harness.Store.ResumeCalls.Count);
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.Hash == task.InfoHash);
        Assert.Equal(DownloadIsolationState.None, held.IsolationState);
    }

    /// <summary>P4：人工暂停活跃的启动恢复——转 UserPaused 停自动重试（记录保留），恢复不靠自动拉起。</summary>
    [Fact]
    public async Task RunRecovery_Partial_UserPause_MarksRunUserPausedAndStopsAutoRetry()
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
        var resumeCount = harness.Store.ResumeCalls.Count;

        using (var scope = harness.Provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await service.PauseAsync(taskId);
        }

        var paused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, paused.Status);
        Assert.True(paused.IsUserPaused);
        Assert.Equal(DownloadRunStates.UserPaused,
                     DownloadRunRecovery.FromJson(paused.RunRecovery)!.State);

        harness.Factory.SessionConfigurator = null;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        // 用户暂停期间对账绝不自动恢复启动，也不把 UserPaused 记录改写为可自动重试状态。
        Assert.Equal(resumeCount, harness.Store.ResumeCalls.Count);
        var still = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadRunStates.UserPaused,
                     DownloadRunRecovery.FromJson(still.RunRecovery)!.State);
    }

    // ───────────────────────── 投影与文案 ─────────────────────────

    /// <summary>文案：完成任务的启动恢复状态能回答「文件是否完整/做种是否恢复/在等什么/如何恢复」。</summary>
    [Fact]
    public void RunRecovery_View_AnswersCompletenessSeedingAndRecoveryQuestions()
    {
        var baseView = new DownloadTaskView
        {
            Id           = Guid.NewGuid(),
            FeedItemId   = null,
            Status       = DownloadTaskStatus.Completed,
            CreatedAtUtc = DateTimeOffset.Parse("2026-11-11T04:00:00Z"),
            RunPurpose   = DownloadRunPurposes.Seeding,
            RunState     = DownloadRunStates.FailedRetryable,
            RunReason    = "启动请求 3 次仍未确认生效；已停止自动重试，可人工恢复"
        };

        Assert.Contains("内容已完成", baseView.WaitingForText);
        Assert.Contains("做种未恢复", baseView.WaitingForText);
        Assert.Contains("恢复做种", baseView.NextStepText);
        Assert.Contains("不会重新校验", baseView.NextStepText);
        Assert.Contains("数据完整性已确认", Assert.Single(baseView.PhaseNotes, note => note.Contains("数据完整性已确认")));

        var awaiting = baseView with { RunState = DownloadRunStates.AwaitingConfirm };
        Assert.Contains("等待确认做种", awaiting.WaitingForText);
        Assert.Contains("无需操作", awaiting.NextStepText);

        var stopped = baseView with { RunState = DownloadRunStates.DownloaderStopped };
        Assert.Contains("下载器已停止做种", stopped.WaitingForText);

        var partial = baseView with
        {
            Status = DownloadTaskStatus.Downloading,
            IsSystemPaused = true,
            PreparationStage = DownloadPreparationStage.ReadyToRun,
            RunPurpose = DownloadRunPurposes.ContinueDownload,
            RunState = DownloadRunStates.FailedRetryable
        };
        Assert.Equal(DownloadDisplayPhase.WaitingStartConfirm, partial.Phase);
        Assert.Contains("候选已校验", partial.WaitingForText);
        Assert.Contains("继续下载", partial.NextStepText);
    }

    /// <summary>恢复做种入口只对 FailedRetryable/DownloaderStopped 开放；待确认与已确认状态拒绝人工重入。</summary>
    [Fact]
    public async Task RunRecovery_ManualEntry_OnlyForRetryableOrStopped()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FileIdentities[task.InfoHash] = CompleteIdentities(harness, task.InfoHash);
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator = session => session.ResumeApplyThenThrowTransport = true;
        await harness.ReconcileAsync();
        Assert.Equal(DownloadRunStates.ConfirmedRunning,
                     DownloadRunRecovery.FromJson((await harness.GetTaskAsync(taskId)).RunRecovery)!.State);

        using var scope   = harness.Provider.CreateScope();
        var       service = scope.ServiceProvider.GetRequiredService<DownloadService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeRunAsync(taskId));
    }

    /// <summary>全部文件身份置为完成（qB index 不变，仅推进度）。</summary>
    private static List<TorrentFileSnapshot> CompleteIdentities(VerificationHarness harness, string hash) =>
        harness.Store.FileIdentities[hash].Select(file => file with { Progress = 1 }).ToList();
}
