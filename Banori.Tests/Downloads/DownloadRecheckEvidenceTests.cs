using Banned.Qbittorrent.Exceptions;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     复检证据验收（问题二/三）：请求前基线 + 停止态完整性方向变化的完成见证、pieces 掉落的
///     请求作用证据、checkingResumeData 不作证据、请求前外部校验不归属、重启作废旧证据。
///     行为依据：qB 5.3 隔离实例实测（qb-recheck-evidence.md §0/§2/§4）——快速校验完全躲过
///     状态采样、pieces_have 请求后同步清零再恢复、重启保留旧完成状态。
/// </summary>
public sealed class DownloadRecheckEvidenceTests
{
    // ───────────────────────── 完成见证 ─────────────────────────

    /// <summary>快速完成自动推进：基线未完成 + 请求后停止态完成 → 见证成立，登记复用并完成，不补发。</summary>
    [Fact]
    public async Task FastCompletion_WithIncompleteBaseline_WitnessesAndCompletesWithoutReissue()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.CompleteImmediately;
        SeedCompleteDataAtRecheck(harness, task.InfoHash);

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck（结果在两次采样之间应用）。
        Assert.Equal(1, harness.Store.RecheckCalls.Count(hash => hash == task.InfoHash));

        for (var i = 0; i < 9; i++) await harness.RoundAsync(); // 见证 → 结果读取 → 稳定窗口 → 完成收尾。

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.VerificationIntent);
        Assert.Equal(DownloadIsolationState.None, completed.IsolationState);
        Assert.Equal(1, harness.Store.RecheckCalls.Count); // 先见证、后补发：无需第二次请求。
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Single(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>基线完整且请求后仍完整：完成无法归因于本轮请求 → 未确认（原因文案区分），不隔离、不登记复用。</summary>
    [Fact]
    public async Task BaselineAlreadyComplete_NoChange_StaysUnconfirmedWithoutReuse()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck（RecordOnly：请求未生效）。

        // 请求前基线已是完整状态（模拟上一轮已完成遗留的 100%）：重写意图基线与下载器快照一致。
        await RewriteIntentAsync(harness, taskId, intent => intent with { Baseline = CompleteBaselineOf(intent) });
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Progress = 1.0, Completed = 1024
        };

        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 窗口过期 → 补发（新基线仍完整）。
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 基线完整 → 仍完整：不构成证据 → 未确认。

        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("请求前数据已是完整状态", halted.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
        Assert.Equal(DownloadIsolationState.None, halted.IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Empty(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    /// <summary>
    ///     基线完整 + 请求后完整性下降（stoppedUP → stoppedDL、amount_left 增大）= 本轮校验执行且缺失：
    ///     进入结果读取并确认缺失 → 隔离，绝不登记复用。
    /// </summary>
    [Fact]
    public async Task BaselineComplete_CompletenessDropsAfterRequest_IsolatesWithoutRegisteringReuse()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck（RecordOnly）。

        // 请求前基线完整；本轮 recheck 揭示真实数据只到 62.5%（qB 实测 partial 场景）。
        await RewriteIntentAsync(harness, taskId, intent => intent with { Baseline = CompleteBaselineOf(intent) });
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Progress = 1.0, Completed = 1024
        };
        harness.Store.FileIdentities[task.InfoHash] = ProgressOf(harness, task.InfoHash, 0.625);
        harness.Store.RecheckBehavior               = FakeRecheckBehavior.CompleteImmediately;

        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 窗口过期 → 补发，立即应用不完整结果。
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        Assert.Equal(0.625, harness.Store.Torrents[task.InfoHash].Progress);
        for (var i = 0; i < 4; i++) await SampleAsync(harness); // 见证（完整→下降）→ 稳定窗口 → 确认缺失。

        var isolated = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState); // 隔离机可能在后续轮次推进。
        Assert.NotEqual(DownloadTaskStatus.Completed, isolated.Status);
        Assert.Empty(harness.Store.ResumeCalls);
        Assert.Contains(task.InfoHash, harness.Store.DeleteCalls.Select(call => call.Hash));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }
    }

    /// <summary>部分缺失且可观察到本轮校验（checkingDL）：稳定后确认缺失 → 隔离，不登记复用。</summary>
    [Fact]
    public async Task PartialMissing_ObservableCheck_Isolates()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + recheck（进入 checkingDL）。
        await harness.RoundAsync(); // 观察位：本轮 Checking 记为证据。
        harness.Store.FileIdentities[task.InfoHash] = ProgressOf(harness, task.InfoHash, 0.625);
        harness.Store.FinishRecheck(task.InfoHash);
        for (var i = 0; i < 4; i++) await SampleAsync(harness); // 结果稳定窗口。

        var isolated = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState); // 隔离机可能在后续轮次推进。
        Assert.Contains(task.InfoHash, harness.Store.DeleteCalls.Select(call => call.Hash));
        Assert.Empty(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }
    }

    /// <summary>无见证、无 checking（零数据快速校验 / 请求未生效）：保持未确认，不判缺失、不隔离。</summary>
    [Fact]
    public async Task NoWitnessAndNoChecking_StaysUnconfirmedWithoutIsolation()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;

        await harness.RoundAsync();                           // 候选根提交。
        await harness.RoundAsync();                           // 映射 + 首次 recheck。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 窗口过期 → 有界补发。
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31)); // 仍无证据 → 未确认。

        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("多次请求校验后仍未观察到校验阶段", halted.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
        Assert.Equal(DownloadIsolationState.None, halted.IsolationState);
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        Assert.Empty(harness.Store.DeleteCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    // ───────────────────────── checkingResumeData 与请求前外部校验 ─────────────────────────

    /// <summary>立即与后续观察到的 checkingResumeData 都不作证据：只记提示，checkingDL 才归属本轮。</summary>
    [Fact]
    public async Task CheckingResumeData_ImmediateAndSubsequent_IsNeverEvidence()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterResumeDataChecking;

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + recheck；请求后出现恢复数据校验。
        var issued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(issued);
        Assert.False(issued.ObservedChecking);
        Assert.NotNull(issued.ResumeDataSeenAtUtc);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5)); // 后续采样仍见恢复数据校验：仍不作证据。
        var resumeData =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(resumeData);
        Assert.False(resumeData.ObservedChecking);
        Assert.Equal(DownloadVerificationStep.Requested, resumeData.Step);

        // 真正的内容校验（checkingDL）才可作为本轮证据。
        harness.Store.ResumeDataChecking.Remove(task.InfoHash);
        harness.Store.Rechecking.Add(task.InfoHash);
        harness.Store.RawStates[task.InfoHash] = "checkingDL";
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));
        var dataCheck = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(dataCheck);
        Assert.True(dataCheck.ObservedChecking);
        Assert.Equal("checkingDL", dataCheck.ObservedRawState);
    }

    /// <summary>请求前已有的外部内容校验不归属本轮：等待结束、不发送叠加请求；结束后重新基线再发。</summary>
    [Fact]
    public async Task PreRequestExternalChecking_IsNotAttributedAndDoesNotStackRecheck()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck。

        // 回到「尚未发出」并让下载器进入外部内容校验（如用户/其他工具触发）。
        await RewriteIntentAsync(harness, taskId, intent => intent with
        {
            Step                  = DownloadVerificationStep.Requested,
            RequestState          = DownloadVerificationRequestState.NotSent,
            ObservedChecking      = false,
            RecheckRequestedAtUtc = null,
            DeadlineUtc           = null,
            Baseline              = null
        });
        harness.Store.Rechecking.Add(task.InfoHash);
        harness.Store.RawStates[task.InfoHash] = "checkingDL";

        for (var i = 0; i < 3; i++) await SampleAsync(harness);
        var waiting = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(waiting);
        Assert.False(waiting.ObservedChecking); // 外部校验不归属本轮。
        Assert.Equal(1, harness.Store.RecheckCalls.Count); // 校验期间不叠加 recheck。
        Assert.DoesNotContain(task.InfoHash, harness.Store.PauseCalls);

        // 外部校验结束：重新捕获基线并发出本轮请求，外部校验仍未被记为本轮证据。
        harness.Store.FinishRecheck(task.InfoHash);
        await SampleAsync(harness);
        var reissued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(reissued);
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        Assert.False(reissued.ObservedChecking);
        Assert.NotNull(reissued.Baseline);
    }

    // ───────────────────────── pieces 请求作用证据 ─────────────────────────

    /// <summary>pieces 掉 0（基线 160 → 请求后 0）= 请求已被接收；随后停止态完成 → 见证成立并完成。</summary>
    [Fact]
    public async Task PiecesDropToZero_ProvesRequestApplied_Completes()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.PiecesHaveOverrides[task.InfoHash] = 160; // 此前中断校验保留的部分分片。
        harness.Store.RecheckBehavior                    = FakeRecheckBehavior.CompleteImmediately;
        SeedCompleteDataAtRecheck(harness, task.InfoHash);

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + recheck：请求后立即读 pieces 掉 0。
        var issued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(issued);
        Assert.NotNull(issued.RecheckAppliedAtUtc);
        Assert.Equal(1, harness.Store.RecheckCalls.Count);

        for (var i = 0; i < 9; i++) await harness.RoundAsync(); // pieces 恢复为完整结果 → 见证 → 完成。

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(1, harness.Store.RecheckCalls.Count);
        Assert.Single(harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>pieces 掉 0 后部分恢复（160/256）= 校验执行且不完整：确认缺失 → 隔离，不登记复用。</summary>
    [Fact]
    public async Task PiecesDropThenPartialRecovery_Isolates()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.PiecesHaveOverrides[task.InfoHash] = 256; // 基线已有全部分片（此前完成过校验）。
        harness.Store.RecheckBehavior                    = FakeRecheckBehavior.CompleteImmediately;
        harness.Factory.SessionConfigurator = session => session.BeforeRecheckAsync = () =>
        {
            // 数据只到 62.5%；请求时刻播种，映射前候选零进度检查不得看到它。
            harness.Store.FileIdentities[task.InfoHash] = ProgressOf(harness, task.InfoHash, 0.625);
            return Task.CompletedTask;
        };

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + recheck：pieces 掉 0、结果部分恢复。
        var issued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(issued);
        Assert.NotNull(issued.RecheckAppliedAtUtc);
        Assert.Equal(0.625, harness.Store.Torrents[task.InfoHash].Progress);
        for (var i = 0; i < 4; i++) await SampleAsync(harness); // 见证（校验执行且不完整）→ 稳定 → 确认缺失。

        var isolated = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState); // 隔离机可能在后续轮次推进。
        Assert.Contains(task.InfoHash, harness.Store.DeleteCalls.Select(call => call.Hash));
        Assert.Empty(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }
    }

    /// <summary>pieces 查询失败不阻断：回退到快照基线见证，仍能确认本轮完成。</summary>
    [Fact]
    public async Task PropertiesQueryFailure_FallsBackToBaselineWitness()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Factory.SessionConfigurator = session =>
        {
            session.PropertiesException = new QbittorrentException("模拟 pieces 属性查询失败");
            session.BeforeRecheckAsync  = SeedCompleteDataAtRecheckAsync(harness, task.InfoHash);
        };
        harness.Store.RecheckBehavior = FakeRecheckBehavior.CompleteImmediately;

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + recheck（属性查询失败，无 RecheckApplied）。
        var issued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(issued);
        Assert.Null(issued.RecheckAppliedAtUtc);

        for (var i = 0; i < 9; i++) await harness.RoundAsync(); // 快照基线见证照常成立。

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(1, harness.Store.RecheckCalls.Count);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>响应丢失但请求已生效：SendFailed 保留基线与锚点，下一轮凭见证完成，不重复 recheck。</summary>
    [Fact]
    public async Task ResponseLostButApplied_WitnessesFromBaselineWithoutReissue()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Factory.SessionConfigurator = session =>
        {
            session.RecheckException    = new QbittorrentException("模拟 recheck 响应丢失");
            session.BeforeRecheckAsync  = SeedCompleteDataAtRecheckAsync(harness, task.InfoHash);
        };
        harness.Store.RecheckBehavior = FakeRecheckBehavior.CompleteImmediately;

        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + recheck 发送异常（请求可能已被接收，结果已应用）。
        var attempted = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(attempted);
        Assert.Equal(DownloadVerificationRequestState.SendFailed, attempted.RequestState);
        Assert.NotNull(attempted.Baseline);

        for (var i = 0; i < 9; i++) await harness.RoundAsync(); // 基线未完成 → 请求后停止态完成：见证。

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        // 见证先于发送重试：SendFailed 保留的基线与锚点在下一轮直接成立完成见证，不再补发请求。
        Assert.Equal(1, harness.Store.RecheckCalls.Count);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Single(harness.Store.ResumeCalls);
    }

    // ───────────────────────── 重启作废 ─────────────────────────

    /// <summary>重启作废旧证据：意图来自其他实例 → 基线/见证/锚点作废（保留候选映射），重发；重启后的完成状态不判完成。</summary>
    [Fact]
    public async Task Restart_InvalidatesOldEvidence_ReissuesWithoutTrustingStaleCompletion()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck。
        Assert.Equal(1, harness.Store.RecheckCalls.Count);

        // 应用重启后下载器保留了旧的完整状态（resume data）：不得据此判定本轮完成。
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            Progress = 1.0, Completed = 1024
        };
        // 重启作废以进程为单位：把意图标记为来自其他实例（模拟重启后新进程读到旧意图）。
        await RewriteIntentAsync(harness, taskId, intent => intent with { ObserverInstanceId = Guid.NewGuid() });
        await harness.RoundAsync(); // 重启后的第一轮：作废旧证据。

        var invalidated =
            DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(invalidated);
        Assert.Null(invalidated.Baseline);
        Assert.Null(invalidated.RecheckAttemptedAtUtc);
        Assert.Null(invalidated.RecheckRequestedAtUtc);
        Assert.Null(invalidated.CompletionEvidenceKind);
        Assert.Equal(DownloadVerificationStep.Requested, invalidated.Step);
        Assert.Equal(DownloadVerificationRequestState.NotSent, invalidated.RequestState);
        var invalidatedTask = await harness.GetTaskAsync(taskId);
        Assert.NotEqual(DownloadTaskStatus.Completed, invalidatedTask.Status); // 不凭旧基线判完成。
        Assert.Equal(DownloadIsolationState.None, invalidatedTask.IsolationState); // 不凭重启后的状态判缺失。

        // 下一轮重发：新基线已是完整状态 → 完成仍无法归因 → 未确认（原因区分），不登记复用。
        await harness.RoundAsync();
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));

        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("请求前数据已是完整状态", halted.Error);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Empty(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Null(file.VerificationOutcome);
        }
    }

    /// <summary>重启后重发并取得证据 → 完成：重启不影响证据规则本身，只作废旧轮次。</summary>
    [Fact]
    public async Task Restart_ReissueWithFreshEvidence_Completes()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck。

        // 重启作废以进程为单位：把意图标记为来自其他实例（模拟重启后新进程读到旧意图）。
        await RewriteIntentAsync(harness, taskId, intent => intent with { ObserverInstanceId = Guid.NewGuid() });
        await harness.RoundAsync(); // 重启后的第一轮：作废旧证据，回到「尚未发出」。

        // 重启后配置快速校验：本轮请求带来新的基线与真实结果。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.CompleteImmediately;
        SeedCompleteDataAtRecheck(harness, task.InfoHash);
        await harness.RoundAsync(); // 重发（RecheckCalls 2），新基线未完成。
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        for (var i = 0; i < 9; i++) await harness.RoundAsync(); // 见证 → 稳定 → 完成。

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.VerificationIntent);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Single(harness.Store.ResumeCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        }
    }

    /// <summary>重启后无证据 → 未确认：作废后按原有窗口与预算收敛，不隔离、不判缺失。</summary>
    [Fact]
    public async Task Restart_WithoutEvidence_StaysUnconfirmed()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;
        await harness.RoundAsync(); // 候选根提交。
        await harness.RoundAsync(); // 映射 + 首次 recheck。

        // 重启作废以进程为单位：把意图标记为来自其他实例（模拟重启后新进程读到旧意图）。
        await RewriteIntentAsync(harness, taskId, intent => intent with { ObserverInstanceId = Guid.NewGuid() });
        await harness.RoundAsync(); // 重启后的第一轮：作废旧证据。
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.ReconcileNowAsync(); // 重发。
        Assert.Equal(2, harness.Store.RecheckCalls.Count);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(31));

        var halted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, halted.Status);
        Assert.Contains("多次请求校验后仍未观察到校验阶段", halted.Error);
        Assert.Equal(DownloadVerificationStep.Unconfirmed,
                     DownloadVerificationIntent.FromJson(halted.VerificationIntent)!.Step);
        Assert.Equal(DownloadIsolationState.None, halted.IsolationState);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    /// <summary>
    ///     生产创建路径自动携带实例 Id：不手工注入，走真实入口创建候选并发 recheck，落库意图的
    ///     ObserverInstanceId 非空（重启作废守卫要求 owner 非空，缺值即守卫恒不触发）；随后以外来 Id
    ///     覆盖落库（模拟重启后新进程读到旧意图），下一轮对账作废旧证据并重发、重建基线。
    /// </summary>
    [Fact]
    public async Task ProductionCreatedIntent_CarriesObserverInstanceId_AndForeignStampReissues()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.RecordOnly;

        await harness.RoundAsync(); // 候选根提交（意图创建，不注入任何 Id）。
        await harness.RoundAsync(); // 映射 + 首次 recheck（捕获基线）。

        var issued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(issued);
        Assert.NotNull(issued.ObserverInstanceId); // 生产路径自动携带进程实例 Id。
        Assert.NotNull(issued.Baseline);

        // 用外来 Id 覆盖落库，模拟重启后新进程读到旧意图：基线与锚点属其他进程，不得沿用。
        await RewriteIntentAsync(harness, taskId, intent => intent with { ObserverInstanceId = Guid.NewGuid() });
        await harness.RoundAsync(); // 重启后的第一轮：作废旧证据。

        var invalidated = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(invalidated);
        Assert.Null(invalidated.Baseline); // 旧基线已作废。
        Assert.Equal(DownloadVerificationRequestState.NotSent, invalidated.RequestState);

        await harness.RoundAsync(); // 下一轮对账：重新捕获基线并重发。
        Assert.Equal(2, harness.Store.RecheckCalls.Count); // RecheckCalls 增加。
        var reissued = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(reissued);
        Assert.NotNull(reissued.Baseline); // 基线被重建。
        Assert.Empty(harness.Store.DeleteCalls); // 作废与重发不隔离、不删种。
    }

    // ───────────────────────── 助手 ─────────────────────────

    /// <summary>请求时刻播种完整数据（qB 不知情的完整文件）：映射前的候选零进度检查不得看到它。</summary>
    private static void SeedCompleteDataAtRecheck(SchedulingHarness harness, string hash) =>
        harness.Factory.SessionConfigurator =
            session => session.BeforeRecheckAsync = SeedCompleteDataAtRecheckAsync(harness, hash);

    private static Func<Task> SeedCompleteDataAtRecheckAsync(SchedulingHarness harness, string hash) => () =>
    {
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        return Task.CompletedTask;
    };

    private static DownloadVerificationBaseline CompleteBaselineOf(DownloadVerificationIntent intent) =>
        new(1.0, 0, 1024, intent.CreatedAtUtc, 1024, QbittorrentTorrentCategory.Stopped, "stoppedUP", false,
            intent.CreatedAtUtc, intent.RecheckAttemptedAtUtc ?? intent.CreatedAtUtc, intent.Round, 256, 256);

    private static async Task<Guid> CreateCandidateAsync(SchedulingHarness harness)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        return taskId;
    }

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

    private static Task SampleAsync(SchedulingHarness harness) => harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));

    private static List<TorrentFileSnapshot> ProgressOf(SchedulingHarness harness, string hash, double progress) =>
        harness.Store.FileIdentities[hash].Select(file => file with { Progress = progress }).ToList();

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

    /// <summary>喵萌式发布名：与既有用例完全同形态，确保 AniParser 解析出集数并分类为正片。</summary>
    private static byte[] SingleFileTorrent() =>
        TestTorrentBytes.Create("【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][01][1080p][简日双语].mp4");
}
