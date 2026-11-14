using Banned.Qbittorrent.Exceptions;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Banori.Tests.Downloads;

/// <summary>
///     第三阶段验收：校验轮次、结果收敛与恢复。使用生产默认时间策略与可步进时钟，覆盖
///     「请求已接收、操作已执行、查询结果已更新」三个时间点错位的观察序列——校验启动窗口内旧停止
///     快照不下结论、结果稳定窗口核对晚到文件进度、「确认缺失」与「结果无法确认/校验执行未确认」
///     分别记录、校验期双期限到期先停止确认再退出自动推进。
/// </summary>
public sealed class DownloadVerificationPhaseThreeTests
{
    private const string DownloadRoot = "/downloads";

    // ───────────────────────── 等待校验开始 ─────────────────────────

    [Fact]
    public async Task CheckStartWindow_OldStopSnapshots_WaitFastWithoutReissueOrConclusion()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        // RecordOnly：recheck 请求被接收但下载器始终停留在停止状态（请求尚未生效）。
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck。
        Assert.Equal(1, harness.Store.RecheckCalls.Count(hash => hash == task.InfoHash));

        // 校验启动窗口（30 秒）内的停止快照：持续快速观察，不重发、不下结论、不隔离。
        for (var i = 0; i < 6; i++) await harness.RoundAsync();
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.True(waiting.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, waiting.IsolationState);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, waiting.PreparationStage);
        Assert.Equal(1, harness.Store.RecheckCalls.Count);

        // 同一时刻的脉冲风暴不消耗补发预算。
        for (var i = 0; i < 5; i++) await harness.ReconcileNowAsync();
        Assert.Equal(1, harness.Store.RecheckCalls.Count);

        // 窗口过期：允许一次有界补发。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());
    }

    /// <summary>
    ///     快速校验（qB 5.3 实测路径）：校验在两次采样之间完成、永远观察不到 Checking 时，
    ///     凭「请求前基线未完成 + 请求后停止态完成」的完成见证自动推进——不隔离、不重下、
    ///     见证先于补发（不发出第二次 recheck）。
    /// </summary>
    [Fact]
    public async Task FastRecheckCompletesBetweenSamples_DefaultTiming_AutoAdvancesWithoutIsolation()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.CompleteImmediately;
        harness.Factory.SessionConfigurator = session => session.BeforeRecheckAsync = () =>
        {
            // 数据早已完整但 qB 仍按 0% 处理（stopped 添加不会自动校验）；只能在请求时刻播种进度，
            // 映射前的候选零进度检查不得看到它。
            harness.Store.FileIdentities[task.InfoHash] = ProgressOf(harness, task.InfoHash, 1.0);
            return Task.CompletedTask;
        };

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck（结果在两次采样之间应用）。
        Assert.Equal(1, harness.Store.RecheckCalls.Count(hash => hash == task.InfoHash));

        for (var i = 0; i < 3; i++) await harness.RoundAsync(); // 见证 → 结果读取（稳定窗口积累中）。
        var witnessing = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(witnessing);
        Assert.Equal(DownloadVerificationCompletionEvidence.BaselineIncompleteToComplete,
                     witnessing.CompletionEvidenceKind);

        for (var i = 0; i < 6; i++) await harness.RoundAsync(); // 稳定窗口满 → 登记复用并收尾完成。
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.VerificationIntent);
        Assert.Equal(DownloadIsolationState.None, completed.IsolationState);
        Assert.Equal(1, harness.Store.RecheckCalls.Count); // 见证先于补发：无需第二次请求。
        Assert.Single(harness.Store.AddCalls);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Single(harness.Store.ReannounceCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    // ───────────────────────── 立即观察与请求发送状态 ─────────────────────────

    /// <summary>快速校验可能在下一个采样间隔前就结束：recheck 发出后同一轮必须立即观察并捕获 Checking。</summary>
    [Fact]
    public async Task RecheckIssued_ImmediatelyObservesChecking_WithoutWaitingNextSample()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck，同一轮立即查询快照。

        var intent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(intent);
        Assert.Equal(DownloadVerificationStep.Observing, intent.Step);
        Assert.True(intent.ObservedChecking);
        Assert.NotNull(intent.CheckStartedAtUtc);
        Assert.Single(harness.Store.RecheckCalls);
    }

    /// <summary>成功发出 recheck 但未观察到 Checking 时，下一次观察按准备采样间隔安排，不空等空闲轮询。</summary>
    [Fact]
    public async Task SuccessfulRecheck_NotObservedChecking_SchedulesNextObservationAfterSampleInterval()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck（RecordOnly：种子保持停止，观察不到 Checking）。

        var task   = await harness.GetTaskAsync(taskId);
        var intent = DownloadVerificationIntent.FromJson(task.VerificationIntent);
        Assert.NotNull(intent);
        Assert.Equal(DownloadVerificationStep.Requested, intent.Step);
        Assert.Equal(DownloadVerificationRequestState.Sent, intent.RequestState);
        Assert.False(intent.ObservedChecking);

        var interval = harness.Provider.GetRequiredService<DownloadTimingPolicy>().PreparationSampleInterval;
        Assert.Equal(TimeSpan.FromSeconds(1), interval);
        Assert.Equal(harness.Time.GetUtcNow() + interval, task.NextObservationAtUtc);
    }

    /// <summary>
    ///     checkingResumeData 不是内容校验证据（立即观察与后续采样一致）：请求后观察到只记录
    ///     ResumeDataSeenAtUtc 提示，不设 ObservedChecking/ObservedRawState，继续等待内容校验或完成见证。
    /// </summary>
    [Fact]
    public async Task RecheckIssued_ImmediatelyObservesCheckingResumeData_OnlyAsHintNotEvidence()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterResumeDataChecking;

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck；恢复数据校验出现在请求之后。

        var intent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(intent);
        Assert.Equal(DownloadVerificationStep.Requested, intent.Step);
        Assert.False(intent.ObservedChecking);
        Assert.Null(intent.ObservedRawState);
        Assert.Null(intent.CheckStartedAtUtc);
        Assert.NotNull(intent.ResumeDataSeenAtUtc);
        Assert.Single(harness.Store.RecheckCalls);
    }

    /// <summary>后续采样只接受 checkingDL/checkingUP：checkingResumeData 只校验恢复数据，不是内容校验证据。</summary>
    [Fact]
    public async Task SubsequentCheckingResumeData_IsNotContentValidationEvidence()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck（RecordOnly：请求生效前仍停止）。

        var issued =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(issued);
        Assert.Equal(DownloadVerificationStep.Requested, issued.Step);
        Assert.False(issued.ObservedChecking);

        // 后续采样看到 qB 正在恢复数据校验（checkingResumeData）：不能归属为本轮内容校验证据。
        harness.Store.Rechecking.Add(task.InfoHash);
        harness.Store.RawStates[task.InfoHash] = "checkingResumeData";
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        var resumeData =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(resumeData);
        Assert.False(resumeData.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Requested, resumeData.Step);
        Assert.Null(resumeData.CheckStartedAtUtc);

        // 真正的数据校验（checkingDL）才可作为本轮证据。
        harness.Store.RawStates[task.InfoHash] = "checkingDL";
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        var data = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(data);
        Assert.True(data.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Observing, data.Step);
        Assert.Equal("checkingDL", data.ObservedRawState);
    }

    /// <summary>
    ///     发送异常不能断言请求未发出，也不能消耗校验请求预算：按采样间隔重试，连续失败达上限才转未确认，
    ///     且保持暂停、不删数据、不隔离。
    /// </summary>
    [Fact]
    public async Task RecheckSendFailures_DoNotConsumeIssueBudget_ThirdFailureEntersUnconfirmed()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        harness.Factory.SessionConfigurator =
            session => session.RecheckException = new QbittorrentException("模拟 recheck 发送失败");

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次发送失败。
        var first =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(first);
        Assert.Equal(DownloadVerificationRequestState.SendFailed, first.RequestState);
        Assert.Equal(1, first.SendFailureCount);
        Assert.Equal(0, first.IssuedCount);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        await harness.AdvanceAsync(TimeSpan.FromSeconds(2)); // 失败只按采样间隔重试，不消耗请求预算。
        var second =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(second);
        Assert.Equal(2, second.SendFailureCount);
        Assert.Equal(0, second.IssuedCount);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(2)); // 连续第 3 次失败 → 未确认。
        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("发送失败", halted.Error);
        var unconfirmed = DownloadVerificationIntent.FromJson(halted.VerificationIntent);
        Assert.NotNull(unconfirmed);
        Assert.Equal(DownloadVerificationStep.Unconfirmed, unconfirmed.Step);
        Assert.Equal(3, unconfirmed.SendFailureCount);
        Assert.Equal(0, unconfirmed.IssuedCount);
        Assert.Equal(DownloadIsolationState.None, halted.IsolationState);
        Assert.Equal(3, harness.Store.RecheckCalls.Count);
        Assert.Single(harness.Store.AddCalls);
        Assert.Empty(harness.Store.DeleteCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    /// <summary>未确认态的人工「重新校验」：保留候选映射与文件（不隔离、不重下），只重置本轮证据并立即重发。</summary>
    [Fact]
    public async Task Reverify_UnconfirmedTask_ReissuesWithoutClearingCandidatesOrIsolating()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        // RecordOnly：种子始终零进度停止，无校验执行证据 → 未确认；CompleteImmediately 会应用
        // 真实校验结果（零进度仍是不完整，但语义已变），不再用于构造该场景。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;
        await harness.RoundAsync();                           // 候选根提交。
        await harness.RoundAsync();                           // 映射 + 首次 recheck。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 有界补发。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 仍未观察到 Checking → 未确认。

        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        var before = DownloadVerificationIntent.FromJson(halted.VerificationIntent);
        Assert.NotNull(before);
        Assert.Equal(DownloadVerificationStep.Unconfirmed, before.Step);
        var addCount     = harness.Store.AddCalls.Count;
        var recheckCount = harness.Store.RecheckCalls.Count;

        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ReverifyAsync(taskId);
        }

        var reset = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, reset.Status);
        Assert.False(reset.IsUserPaused);
        Assert.Equal(DownloadIsolationState.None, reset.IsolationState);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, reset.PreparationStage);
        Assert.Null(reset.FailedStep);
        Assert.Null(reset.Error);
        var after = DownloadVerificationIntent.FromJson(reset.VerificationIntent);
        Assert.NotNull(after);
        Assert.Equal(DownloadVerificationStep.Requested, after.Step);
        Assert.Equal(DownloadVerificationRequestState.NotSent, after.RequestState);
        Assert.Equal(before.Round + 1, after.Round);
        Assert.Equal(before.CandidateRoot, after.CandidateRoot);
        Assert.Equal<DownloadVerificationMapping>(before.Files, after.Files);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Equal(addCount, harness.Store.AddCalls.Count);

        // 下一轮立即重发，无需等待校验启动窗口。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileNowAsync();
        Assert.Equal(recheckCount + 1, harness.Store.RecheckCalls.Count);
        var reissued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(reissued);
        Assert.Equal(DownloadVerificationStep.Observing, reissued.Step);
        Assert.True(reissued.ObservedChecking);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Equal(addCount, harness.Store.AddCalls.Count);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    /// <summary>
    ///     人工暂停优先于校验未确认：保留的人工暂停展示为「已暂停（人工）」，页面不提供「重新校验」；
    ///     空闲对账不得重发请求或改写证据——用户未恢复前任务保持人工暂停。
    /// </summary>
    [Fact]
    public async Task UserPausedUnconfirmed_KeepsManualPhaseAndIdleReconcileDoesNotReissue()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterUnconfirmedAsync(harness);
        await PreserveUserPauseAsync(harness, taskId);

        var view = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.PausedManually, view.Phase);
        Assert.Contains("恢复", view.NextStepText);
        Assert.DoesNotContain("重新校验", view.NextStepText);
        Assert.DoesNotContain(view.PhaseNotes, note => note.Contains("重新校验"));

        var rechecks = harness.Store.RecheckCalls.Count;
        for (var i = 0; i < 3; i++) await harness.RoundAsync();
        await harness.ReconcileNowAsync();

        var idle = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, idle.Status);
        Assert.True(idle.IsUserPaused);
        Assert.Null(idle.NextObservationAtUtc);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(idle.VerificationIntent)!.Step);
        Assert.Equal(rechecks, harness.Store.RecheckCalls.Count);
        Assert.Equal(DownloadIsolationState.None, idle.IsolationState);
        Assert.Single(harness.Store.AddCalls);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>
    ///     未确认后的人工恢复等价于重新发起一轮：意图回到 Requested/NotSent、轮次 +1、证据清零，
    ///     候选与文件保留；下一轮重发 recheck，不删除、不重加、不隔离。
    /// </summary>
    [Fact]
    public async Task ResumeUserPausedUnconfirmed_ResetsRoundAndReissuesWithoutIsolation()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await EnterUnconfirmedAsync(harness);
        await PreserveUserPauseAsync(harness, taskId);
        var before = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(before);
        var addCount     = harness.Store.AddCalls.Count;
        var recheckCount = harness.Store.RecheckCalls.Count;

        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ResumeAsync(taskId);
        }

        var resumedAt = harness.Time.GetUtcNow();
        var resumed   = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, resumed.Status);
        Assert.True(resumed.IsSystemPaused);
        Assert.False(resumed.IsUserPaused);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, resumed.PreparationStage);
        Assert.Null(resumed.FailedStep);
        Assert.Null(resumed.Error);
        Assert.Equal(resumedAt + TimeSpan.FromSeconds(1), resumed.NextObservationAtUtc);

        var reset = DownloadVerificationIntent.FromJson(resumed.VerificationIntent);
        Assert.NotNull(reset);
        Assert.Equal(DownloadVerificationStep.Requested, reset.Step);
        Assert.Equal(DownloadVerificationRequestState.NotSent, reset.RequestState);
        Assert.Equal(before.Round + 1, reset.Round);
        Assert.False(reset.ObservedChecking);
        Assert.Equal(0, reset.SendFailureCount);
        Assert.Null(reset.DeadlineUtc);
        Assert.Null(reset.MovingObservedAtUtc);
        Assert.Equal(before.CandidateRoot, reset.CandidateRoot);
        Assert.Equal<DownloadVerificationMapping>(before.Files, reset.Files);
        Assert.Empty(harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Equal(addCount, harness.Store.AddCalls.Count);

        // 等待观察间隔到期：下一轮立即重发，不清候选、不隔离、不重下。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(recheckCount + 1, harness.Store.RecheckCalls.Count);
        var reissued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(reissued);
        Assert.Equal(DownloadVerificationStep.Requested, reissued.Step);
        Assert.Equal(DownloadVerificationRequestState.Sent, reissued.RequestState);
        Assert.Equal(reset.Round, reissued.Round);
        Assert.Equal(reset.IssuedCount + 1, reissued.IssuedCount);
        Assert.Equal(DownloadIsolationState.None, (await harness.GetTaskAsync(taskId)).IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Equal(addCount, harness.Store.AddCalls.Count);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    // ───────────────────────── 数据确认后继续做种 ─────────────────────────

    /// <summary>校验通过且所有选中文件完成、快照停止：恢复做种一次并重新通告 tracker，随后进入完成归档。</summary>
    [Fact]
    public async Task FullReuse_AllSelectedComplete_ResumesSeedingAndReannounces()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.VerificationIntent);
        Assert.Equal(1, harness.Store.ResumeCalls.Count(item => item == hash));
        Assert.Equal(1, harness.Store.ReannounceCalls.Count(item => item == hash));

        // 已完成任务不再进入对账：分享限制后的停止不会被反复拉起形成 Resume 循环。
        await harness.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(1, harness.Store.ResumeCalls.Count(item => item == hash));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>tracker 通告失败只影响做种可达性，不得否定已确认的数据完整性或把完成改为失败。</summary>
    [Fact]
    public async Task ReannounceFailure_DoesNotFailConfirmedCompletion()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);
        harness.Factory.SessionConfigurator =
            session => session.ReannounceException = new QbittorrentException("模拟 tracker 不可达");
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        Assert.Null(completed.Error);
        Assert.Equal(DownloadIsolationState.None, completed.IsolationState);
        Assert.Equal(1, harness.Store.ResumeCalls.Count(item => item == hash));
        // fake 在抛出前登记调用：失败只告警，不改变完成判定。
        Assert.Equal(1, harness.Store.ReannounceCalls.Count(item => item == hash));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>恢复做种只是 best-effort：启动请求失败同样不否定已确认的数据完整性，也不自动重下。</summary>
    [Fact]
    public async Task ResumeSeedingFailure_DoesNotFailConfirmedCompletion()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("模拟恢复做种请求失败");
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.Error);
        Assert.Equal(DownloadIsolationState.None, completed.IsolationState);
        // fake 先登记尝试再抛出：这里确认代码确实调用过 Resume，只是请求失败；
        // 失败后不再通告 tracker，也不改变完成判定。
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.ReannounceCalls);
        Assert.Empty(harness.Store.DeleteCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    // ───────────────────────── 结果稳定窗口 ─────────────────────────

    [Fact]
    public async Task LateFileProgressAfterCheck_StabilityWindowDefersConclusion_ThenCompletes()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验已结束但文件进度滞后（部分值）：稳定窗口前不隔离重下。
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        await SampleAsync(harness);
        var holding = await harness.GetTaskAsync(taskId);
        Assert.True(holding.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, holding.IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);

        // 文件进度晚到更新为完整：样本重置后跨稳定窗口一致 → 直接完成，不重复 recheck、不启动下载。
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        var rechecks = harness.Store.RecheckCalls.Count;
        for (var i = 0; i < 5; i++) await SampleAsync(harness);

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(rechecks, harness.Store.RecheckCalls.Count);
        // 数据完整性确认后继续做种：完整且停止时恢复一次并通告 tracker（旧实现完成后不启动）。
        Assert.Equal(1, harness.Store.ResumeCalls.Count(hash => hash == completed.InfoHash));
        Assert.Equal(1, harness.Store.ReannounceCalls.Count(hash => hash == completed.InfoHash));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    [Fact]
    public async Task StableIncompleteResults_ConfirmedMissing_IsolatesWithDistinctReason()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 候选数据稳定缺失（多次同样低进度且本轮校验证据成立）：确认缺失走隔离，区别于未知。
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);

        // 稳定结论当轮进入隔离（后续轮次可能已推进 Deleting → Readding），验证隔离链已启动而非停在观察。
        var isolated = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState);
        Assert.Null(isolated.VerificationIntent);
        Assert.DoesNotContain(hash, harness.Store.ResumeCalls);
        Assert.Contains("确认缺失", DecisionOf(isolated).Reason);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }
    }

    [Fact]
    public async Task UnstableResults_StabilityMaxWindow_FailsAsUnconfirmedResult()
    {
        // 缩短稳定观察上限（其余参数同生产默认）：内容持续变化的结果在观察上限内仍无一致样本。
        using var harness = await SchedulingHarness.CreateAsync(new DownloadTimingPolicy
        {
            ResultStabilityMaxWindow = TimeSpan.FromSeconds(10)
        });
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);
        harness.Store.FinishRecheck(hash);

        // 文件结果每轮变化（晚到更新/抖动）：一致样本永不积累，稳定观察上限到期按「结果无法确认」
        // 受阻——与「确认缺失」分别记录，不隔离、不动远端数据。
        for (var round = 0; round < 8; round++)
        {
            harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, round % 2 == 0 ? 0.3 : 0.7);
            await SampleAsync(harness);
            if ((await harness.GetTaskAsync(taskId)).Status == DownloadTaskStatus.Failed) break;
        }

        var unconfirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, unconfirmed.Status);
        Assert.Equal("复用校验", unconfirmed.FailedStep);
        Assert.Contains("无法稳定确认", unconfirmed.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(unconfirmed.VerificationIntent)!.Step);
        Assert.Equal(DownloadIsolationState.None, unconfirmed.IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>复审回归：观察中断（长期停采/重启）不是结果不稳定——序列重新积累，不能沿用旧样本下结论。</summary>
    [Fact]
    public async Task StabilitySamples_ObservationGap_ReaccumulatesBeforeConclusion()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash)                 = await EnterVerifiedCheckAsync(harness);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        await SampleAsync(harness); // 已积累两个一致样本。

        // 停采一小时后恢复：即使内容与旧样本一致，恢复后的第一个样本也必须重新积累，
        // 不得直接凑满样本数触发「确认缺失」隔离。
        await harness.AdvanceAsync(TimeSpan.FromHours(1));
        var resumed = await harness.GetTaskAsync(taskId);
        Assert.True(resumed.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, resumed.IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);

        // 恢复后的序列重新跨稳定窗口积累一致样本，才允许确认缺失。
        for (var i = 0; i < 3; i++) await SampleAsync(harness);
        var isolated = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState);
        Assert.Contains("确认缺失", DecisionOf(isolated).Reason);
    }

    /// <summary>复审回归：退出校验后再次进入 Checking（外部再次校验）使旧稳定样本作废，重新积累。</summary>
    [Fact]
    public async Task StabilitySamples_ReenteredChecking_VoidsAccumulatedSamples()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash)                 = await EnterVerifiedCheckAsync(harness);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        await SampleAsync(harness); // 已积累两个一致样本。

        // 再次进入校验阶段：校验进行中，退出校验前积累的稳定样本不再有效。
        harness.Store.Rechecking.Add(hash);
        await SampleAsync(harness);
        harness.Store.FinishRecheck(hash);

        // 新一轮观察的首个样本即使内容与旧样本一致，也必须重新积累，不直接触发隔离。
        await SampleAsync(harness);
        var holding = await harness.GetTaskAsync(taskId);
        Assert.True(holding.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, holding.IsolationState);

        for (var i = 0; i < 3; i++) await SampleAsync(harness);
        Assert.NotEqual(DownloadIsolationState.None,
                        (await harness.GetTaskAsync(taskId)).IsolationState);
    }

    // ───────────────────────── 校验中的双期限 ─────────────────────────

    [Fact]
    public async Task StuckChecking_NoProgress_ExpiresStopsThenFailsWhenStopIneffective()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验长期无有效进展（10 分钟）：到期退出前先停止校验的临时运行，停止确认窗口内不重复发。
        await harness.AdvanceAsync(TimeSpan.FromMinutes(11));
        Assert.Contains(hash, harness.Store.PauseCalls);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        // 停止请求始终不生效（fake 保持 Checking）：窗口反复过期后按有界预算补发，耗尽才受阻失败。
        for (var i = 0; i < 4; i++) await harness.AdvanceAsync(TimeSpan.FromSeconds(16));

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("复用校验", failed.FailedStep);
        Assert.Contains("停止请求未生效", failed.Error);
        Assert.True(harness.Store.PauseCalls.Count is >= 1 and <= 3);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>复审回归：停止确认独立于进度——停止请求未确认期间校验继续推进，也不得绕过确认窗口。</summary>
    [Fact]
    public async Task TimeoutStopPending_ProgressChangesDoNotBypassStopConfirmation()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 无进展到期：发出第 1 次停止请求。
        await harness.AdvanceAsync(TimeSpan.FromMinutes(11));
        Assert.Equal(1, harness.Store.PauseCalls.Count);

        // 此后校验持续推进（每轮进度都变）：停止请求仍未生效时按确认窗口有界补发，耗尽才失败，
        // 不能因进度变化重置期限、把未确认的停止请求无限搁置。
        for (var i = 0; i < 4; i++)
        {
            harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.1 * (i + 1) };
            await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        }

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Contains("停止请求未生效", failed.Error);
        Assert.Equal(3, harness.Store.PauseCalls.Count);
    }

    /// <summary>复审回归：超时停止生效后，校验是被主动中断的——部分结果不判「确认缺失」。</summary>
    [Fact]
    public async Task TimeoutStop_TakesEffect_HaltsUnconfirmedInsteadOfConfirmingMissing()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash)                 = await EnterVerifiedCheckAsync(harness);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);

        // 无进展到期 → 停止请求生效（校验被中断、种子回到停止）。
        await harness.AdvanceAsync(TimeSpan.FromMinutes(11));
        Assert.Contains(hash, harness.Store.PauseCalls);
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);

        // 中断时刻的部分结果不是「校验执行完毕后的稳定缺失」：按校验执行未确认保持暂停，
        // 不转隔离（隔离意味着确认缺失并独立重下），保留人工「重新校验」入口。
        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("校验执行未确认", halted.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
        Assert.Equal(DownloadIsolationState.None, halted.IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>复审回归：超时停止生效后，文件接口保留的旧 100% 不能写成 ReusedVerified。</summary>
    [Fact]
    public async Task CheckingWithSteadyProgress_TotalLimit_StopsThenHaltsUnconfirmedEvenIfFilesLookComplete()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 持续有进展（9 分钟一档，不触发无进展期限），累计超过单轮校验总上限（2 小时）。
        for (var i = 0; i < 14; i++)
        {
            harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.05 * (i + 1) };
            await harness.AdvanceAsync(TimeSpan.FromMinutes(9));
        }

        // 总上限到期：退出前先停止临时运行；不重复 recheck、不带着运行中的校验离开。
        Assert.Contains(hash, harness.Store.PauseCalls);
        var rechecks = harness.Store.RecheckCalls.Count;
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        // 停止生效且文件接口报告完整（可能是中断前的旧快照）：仍不能证明本轮校验执行过，
        // 不写 ReusedVerified、不进完成归档——按校验执行未确认受阻，人工重试再走隔离链。
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);

        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Equal("复用校验", halted.FailedStep);
        Assert.Contains("校验执行未确认", halted.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
        Assert.Equal(rechecks, harness.Store.RecheckCalls.Count);
        Assert.Empty(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    /// <summary>复审二轮回归：人工暂停中断校验后恢复，不得沿用暂停前的 Checking 证据与文件结果。</summary>
    [Fact]
    public async Task ManualPauseInterruptsChecking_VoidsEvidenceAndReverifies()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验中人工暂停：本轮证据先于暂停请求作废（恢复只解除人工暂停，不会重新校验）。
        using (var scope = harness.Provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await service.PauseAsync(taskId);
            var pausedIntent =
                DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
            Assert.NotNull(pausedIntent);
            Assert.False(pausedIntent.ObservedChecking);
            Assert.Equal(DownloadVerificationStep.Requested, pausedIntent.Step);

            await service.ResumeAsync(taskId);
        }

        // 恢复后校验已被中断，文件接口保留的旧 100% 不得据此完成：等待校验开始窗口过期后补发 recheck。
        harness.Store.FinishRecheck(hash);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        var rechecks = harness.Store.RecheckCalls.Count;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(rechecks + 1, harness.Store.RecheckCalls.Count);
        var reverifying = await harness.GetTaskAsync(taskId);
        Assert.True(reverifying.Status.IsActive());
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }

        // 新轮次完整执行（观察到 Checking 且结束、结果稳定）后才登记复用并完成。
        await harness.RoundAsync(); // 观察新轮 Checking。
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>复审三轮回归：暂停请求尚未在下载器生效时，作废后的意图不得把残留 Checking 重新归属为本轮证据。</summary>
    [Fact]
    public async Task DelayedManualPause_PendingStopKeepsEvidenceVoided_ReverifiesAfterResume()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验中人工暂停：证据先作废落库；下载器侧暂停尚未生效（种子仍报告 Checking）。
        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().PauseAsync(taskId);
        }

        // 暂停已落库、停止尚未生效的一轮观察：残留 Checking 属于已作废的上一轮，不得归属为新轮证据。
        await SampleAsync(harness);
        var pausedIntent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(pausedIntent);
        Assert.False(pausedIntent.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Requested, pausedIntent.Step);

        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ResumeAsync(taskId);
        }

        // 停止生效后文件接口保留的旧 100% 不得据此完成：必须先补发新一轮 recheck 再走完整校验。
        harness.Store.FinishRecheck(hash);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        var rechecks = harness.Store.RecheckCalls.Count;
        await SampleAsync(harness);
        Assert.Equal(rechecks + 1, harness.Store.RecheckCalls.Count);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        // 新轮次完整执行（观察到 Checking 且结束、结果稳定）后才登记复用并完成。
        await harness.RoundAsync(); // 观察新轮 Checking。
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>复审四轮回归：请求已发出、首次 Checking 未采到时人工暂停，同样作废请求锚点，恢复后重新校验。</summary>
    [Fact]
    public async Task ManualPauseBeforeFirstCheckingObserved_VoidsRequestAnchor_Reverifies()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck（种子进入校验，首次 Checking 尚未采到）。

        // 人工暂停：本轮请求虽未观察到 Checking，仍可能被中断——证据与请求锚点一并作废，
        // 并把暂停记为第一次停止请求。
        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().PauseAsync(taskId);
        }

        var pausedIntent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(pausedIntent);
        Assert.False(pausedIntent.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Requested, pausedIntent.Step);
        Assert.Null(pausedIntent.RecheckRequestedAtUtc);
        Assert.NotNull(pausedIntent.TimeoutStopRequestedAtUtc);

        // 暂停已落库、停止尚未生效的一轮观察：残留 Checking 属于已作废轮次，不得归属为本轮证据。
        await SampleAsync(harness);
        var residual = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(residual);
        Assert.False(residual.ObservedChecking);

        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().ResumeAsync(taskId);
        }

        // 停止生效后文件接口保留的旧 100% 不得据此完成：必须先补发新一轮 recheck 再走完整校验。
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Store.FileIdentities[task.InfoHash] = ProgressOf(harness, task.InfoHash, 1.0);
        harness.Store.Torrents[task.InfoHash]       = harness.Store.Torrents[task.InfoHash] with { Progress = 1.0 };
        var rechecks = harness.Store.RecheckCalls.Count;
        await SampleAsync(harness);
        Assert.Equal(rechecks + 1, harness.Store.RecheckCalls.Count);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        await harness.RoundAsync(); // 观察新轮 Checking。
        harness.Store.FinishRecheck(task.InfoHash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>复审四轮回归：作废轮次的残留校验停止不生效时，按确认窗口有界补发并受阻退出，不永久等待。</summary>
    [Fact]
    public async Task VoidedRoundResidualChecking_StopUnconfirmed_ExpiresToFailed()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验中人工暂停作废本轮证据（暂停即第一次停止请求）；停止始终不生效、校验一直报告进行中，
        // 恢复只解除人工暂停，残留校验仍不可归属。
        using (var scope = harness.Provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await service.PauseAsync(taskId);
            await service.ResumeAsync(taskId);
        }

        Assert.Equal(1, harness.Store.PauseCalls.Count);

        // 停止确认窗口过期按预算补发，耗尽后明确受阻——不得无限停留在 Downloading
        // 等待一个已作废的校验自然结束。
        for (var i = 0; i < 4; i++) await harness.AdvanceAsync(TimeSpan.FromSeconds(16));

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("复用校验", failed.FailedStep);
        Assert.Contains("停止请求未生效", failed.Error);
        Assert.True(harness.Store.PauseCalls.Count >= 2);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>复审三轮回归：人工暂停已验证待启动（ReadyToRun）的任务不作废验证结果，恢复后不重复校验。</summary>
    [Fact]
    public async Task ManualPauseOfReadyToRun_PreservesVerifiedResult_ResumeDoesNotRecheck()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreatePartialCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck（进入 Checking）。
        await harness.RoundAsync(); // 观察位：本轮 Checking 记为证据。
        var observing = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(observing);
        Assert.True(observing.ObservedChecking);

        // 候选完整、其余文件未完成：结果稳定后登记复用；Resume 失败不再吞进完成收尾，
        // 启动状态转入 RunRecovery（先落库再请求，观察确认），意图照常清空。
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with
                                                              {
                                                                  Progress = file.Index == 0 ? 1 : 0
                                                              }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        harness.Factory.SessionConfigurator =
            session => session.ResumeException = new IOException("Resume interrupted");
        // 首次 Resume 失败转 ResponseUnknown；每个确认窗口（15s）内有界自动重试一次，
        // 预算（MaxRunResumeAttempts=3）耗尽转 FailedRetryable，等待人工恢复。
        for (var i = 0; i < 4; i++) await SampleAsync(harness); // 稳定窗口 + 完成收尾 + 首次 Resume。
        var responding = DownloadRunRecovery.FromJson((await harness.GetTaskAsync(taskId)).RunRecovery);
        Assert.NotNull(responding);
        Assert.Equal(DownloadRunStates.ResponseUnknown, responding.State);
        for (var i = 0; i < 4; i++) await harness.AdvanceAsync(TimeSpan.FromSeconds(16));
        var ready = await harness.GetTaskAsync(taskId);
        Assert.Null(ready.VerificationIntent);
        var run = DownloadRunRecovery.FromJson(ready.RunRecovery);
        Assert.NotNull(run);
        Assert.Equal(DownloadRunPurposes.ContinueDownload, run.Purpose);
        // 有界自动重试耗尽：转可人工恢复，任务保持系统暂停，绝不写失败步骤。
        Assert.Equal(DownloadRunStates.FailedRetryable, run.State);

        // 人工暂停：预算已耗尽的启动恢复保持 FailedRetryable——自动重试不因暂停「复活」，
        // 人工恢复走专用入口（ResumeRun）；已验证的证据与文件原样，恢复后不重新校验。
        using (var scope = harness.Provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DownloadService>();
            await service.PauseAsync(taskId);
            var paused = await harness.GetTaskAsync(taskId);
            Assert.True(paused.IsUserPaused);
            Assert.Equal(DownloadRunStates.FailedRetryable,
                         DownloadRunRecovery.FromJson(paused.RunRecovery)!.State);
            await service.ResumeAsync(taskId);
        }

        harness.Factory.SessionConfigurator = null;
        var rechecks = harness.Store.RecheckCalls.Count;
        for (var i = 0; i < 3; i++) await SampleAsync(harness);
        var finished = await harness.GetTaskAsync(taskId);
        Assert.Null(finished.VerificationIntent);
        Assert.Equal(rechecks, harness.Store.RecheckCalls.Count);
    }

    /// <summary>复审二轮回归：只有回读才观察到的快速起止校验同样作废旧稳定序列。</summary>
    [Fact]
    public async Task StabilitySamples_CheckingSeenOnlyAtReRead_VoidsSamples()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash)                 = await EnterVerifiedCheckAsync(harness);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        await SampleAsync(harness); // 已积累两个一致样本。

        // 入口快照仍是停止态、回读时校验正在进行（快速起止的校验只被回读看到）：旧稳定序列立即作废。
        var queries = 0;
        harness.Factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = _ =>
        {
            if (++queries == 2) harness.Store.Rechecking.Add(hash);
            return Task.CompletedTask;
        };
        await SampleAsync(harness);
        harness.Factory.SessionConfigurator = null;
        Assert.Equal(2, queries);

        // 回到停止态后的首个同内容样本必须重新积累，不得沿用旧样本数直接凑满触发隔离。
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        var holding = await harness.GetTaskAsync(taskId);
        Assert.True(holding.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, holding.IsolationState);

        for (var i = 0; i < 3; i++) await SampleAsync(harness);
        Assert.NotEqual(DownloadIsolationState.None,
                        (await harness.GetTaskAsync(taskId)).IsolationState);
    }

    /// <summary>复审三轮回归：只有回读观察到的移动同样作废整轮证据，移动结束后必须重新校验。</summary>
    [Fact]
    public async Task MovingSeenOnlyAtReRead_VoidsRoundEvidence_AndReverifies()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash)                 = await EnterVerifiedCheckAsync(harness);
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        await SampleAsync(harness); // 已积累两个一致样本，文件结果完整。

        // 入口快照仍是停止态、回读时移动正在进行（快速移动只被回读看到）：移动可能已改变目标，
        // 整轮证据作废而不只是稳定序列——移动前的 Checking 与文件结果都不得沿用。
        var queries = 0;
        harness.Factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = _ =>
        {
            if (++queries == 2) harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { IsMoving = true };
            return Task.CompletedTask;
        };
        await SampleAsync(harness);
        harness.Factory.SessionConfigurator = null;
        Assert.Equal(2, queries);

        var voided = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(voided);
        Assert.False(voided.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Requested, voided.Step);

        // 移动结束后：不得凭移动前的证据直接完成，必须先补发新一轮 recheck 再走完整校验。
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { IsMoving = false };
        var rechecks = harness.Store.RecheckCalls.Count;
        await SampleAsync(harness);
        Assert.Equal(rechecks + 1, harness.Store.RecheckCalls.Count);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        await SampleAsync(harness); // 观察新轮 Checking，记为证据。
        harness.Store.FinishRecheck(hash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>复审五回归：移动等待独立计时——前面长校验的耗时不得占用移动等待额度，超期从首次观察起算。</summary>
    [Fact]
    public async Task LongCheckThenMoving_WaitsFullMovingWindow_BeforeTimingOut()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验持续有进展地运行 18 分钟（9 分钟一档，未到单轮总上限）：校验本身完全正常。
        for (var i = 0; i < 2; i++)
        {
            harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.5 * (i + 1) };
            await harness.AdvanceAsync(TimeSpan.FromMinutes(9));
        }

        // 校验结束进入移动：首次观察作废整轮校验证据，同时记录移动等待起点。
        harness.Store.FinishRecheck(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { IsMoving = true };
        await SampleAsync(harness);
        var voided = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(voided);
        Assert.False(voided.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Requested, voided.Step);
        Assert.NotNull(voided.MovingObservedAtUtc);

        // 移动刚开始数秒：18 分钟的前置校验不得让移动一露面就被判超时，任务保持等待。
        await SampleAsync(harness);
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.True(waiting.Status.IsActive());
        Assert.Equal(DownloadIsolationState.None, waiting.IsolationState);

        // 移动确实持续超期（首次观察起 10 分钟）才按持续超时受阻。
        await harness.AdvanceAsync(TimeSpan.FromMinutes(11));
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Contains("校验或移动持续超时", failed.Error);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>复审回归：旧 JSON 意图缺校验锚点时，恢复观察即补齐，单轮总上限仍从补齐时刻起算并生效。</summary>
    [Fact]
    public async Task LegacyIntent_MissingAnchors_BackfillsAndEnforcesTotalLimit()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);
        await RewriteIntentAsync(harness, taskId, legacy => legacy with
        {
            CheckStartedAtUtc = null, LastCheckProgressAtUtc = null, LastCheckProgress = null
        });

        // 持续有进展：无进展期限不触发；锚点补齐后累计超过单轮总上限（2 小时）仍必须到期停止。
        for (var i = 0; i < 15; i++)
        {
            harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.05 * (i + 1) };
            await harness.AdvanceAsync(TimeSpan.FromMinutes(9));
        }

        Assert.Contains(hash, harness.Store.PauseCalls);
        Assert.True((await harness.GetTaskAsync(taskId)).Status.IsActive());

        // 停止生效后按校验被中断保持暂停（未确认），不进结果判定。
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("校验执行未确认", halted.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
    }

    // ───────────────────────── 助手 ─────────────────────────

    /// <summary>推进候选任务到「校验执行未确认」的持久化状态：暂停、意图 Unconfirmed、不隔离。</summary>
    private static async Task<Guid> EnterUnconfirmedAsync(SchedulingHarness harness)
    {
        var taskId = await CreateCandidateAsync(harness);
        // RecordOnly：种子始终零进度停止——无校验执行证据的未确认路径；
        // CompleteImmediately 会应用真实校验结果，不能再用它构造「无证据」场景。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;
        await harness.RoundAsync();                           // 候选根提交。
        await harness.RoundAsync();                           // 映射 + 首次 recheck。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 窗口过期 → 有界补发。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 仍未观察到 Checking → 未确认。
        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
        return taskId;
    }

    /// <summary>
    ///     构造「校验未确认时人工暂停标志仍被保留」的持久化组合：修复前的实现会在进入未确认时
    ///     清掉该标志，这里直接落库锁定展示与恢复契约。
    /// </summary>
    private static async Task PreserveUserPauseAsync(SchedulingHarness harness, Guid taskId)
    {
        await using var db      = await harness.CreateDbContextAsync();
        var             tracked = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        tracked.IsUserPaused = true;
        await db.SaveChangesAsync();
    }

    private static async Task<DownloadTaskView> ViewOfAsync(SchedulingHarness harness, Guid taskId)
    {
        var queries =
            new DownloadTaskQueries(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());
        return Assert.Single((await queries.ListAsync(new DownloadTaskListFilter())).Items, item => item.Id == taskId);
    }

    private static async Task<Guid> CreateCandidateAsync(SchedulingHarness harness)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        return taskId;
    }

    /// <summary>三文件任务：Index 0 为候选（外部占用其归档目标），其余文件独立下载且不完整。</summary>
    private static async Task<Guid> CreatePartialCandidateAsync(SchedulingHarness harness)
    {
        var taskId  = await harness.CreateUploadAsync(MixedTorrent());
        var service = harness.Provider.GetRequiredService<DownloadFileService>();
        var targets = await service.ComputeArchivalTargetsAsync(taskId);
        SeedExternalOccupant(harness, targets.Single(target => target.FileIndex == 0).TargetPath, 1024);
        return taskId;
    }

    /// <summary>三文件发布名：Index 0 为正片候选，其余为独立下载的配套文件。</summary>
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

    /// <summary>候选任务推进到「本轮 Checking 已记为证据、校验阶段持续」；返回任务 Id 与 hash。</summary>
    private static async Task<(Guid TaskId, string Hash)> EnterVerifiedCheckAsync(SchedulingHarness harness)
    {
        var taskId = await CreateCandidateAsync(harness);
        var task   = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 发出 recheck（进入 Checking）。
        await harness.RoundAsync(); // 观察位：本轮 Checking 记为证据。
        var checking = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadPreparationStage.AwaitingVerification, checking.PreparationStage);
        var intent = DownloadVerificationIntent.FromJson(checking.VerificationIntent);
        Assert.NotNull(intent);
        Assert.True(intent.ObservedChecking);
        return (taskId, task.InfoHash);
    }

    /// <summary>推进一个校验采样间隔（2 秒）再对账：驱动校验后结果稳定窗口的采样节奏。</summary>
    private static Task SampleAsync(SchedulingHarness harness) =>
        harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));

    private static List<TorrentFileSnapshot> ProgressOf(SchedulingHarness harness, string hash, double progress) =>
        harness.Store.FileIdentities[hash].Select(file => file with { Progress = progress }).ToList();

    private static async Task RewriteIntentAsync(
        SchedulingHarness harness, Guid taskId, Func<DownloadVerificationIntent, DownloadVerificationIntent> rewrite)
    {
        await using var db      = await harness.CreateDbContextAsync();
        var             tracked = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        var intent = DownloadVerificationIntent.FromJson(tracked.VerificationIntent) ??
                     throw new InvalidOperationException("校验意图缺失。");
        tracked.VerificationIntent = rewrite(intent).ToJson();
        await db.SaveChangesAsync();
    }

    private static async Task<string> ArchivalTargetAsync(SchedulingHarness harness, Guid taskId)
    {
        var service = harness.Provider.GetRequiredService<DownloadFileService>();
        var targets = await service.ComputeArchivalTargetsAsync(taskId);
        return Assert.Single(targets).TargetPath;
    }

    private static void SeedExternalOccupant(SchedulingHarness harness, string target, long size)
    {
        var hash   = new string('e', 40);
        var parent = target[..target.LastIndexOf('/')];
        var name   = target[(parent.Length + 1)..];
        harness.Store.AddTorrent(hash, "External Seed", QbittorrentTorrentCategory.Seeding, 1.0, parent);
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, name, size, 1.0)];
    }

    private static DownloadPathDecision DecisionOf(DownloadTask task) =>
        DownloadPathDecision.FromJson(task.PathDecision) ?? throw new InvalidOperationException("路径决策缺失。");

    /// <summary>喵萌式发布名：与既有用例完全同形态，确保 AniParser 解析出集数并分类为正片。</summary>
    private static byte[] SingleFileTorrent() =>
        TestTorrentBytes.Create("【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][01][1080p][简日双语].mp4");
}
