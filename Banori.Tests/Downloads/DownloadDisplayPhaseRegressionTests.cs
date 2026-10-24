using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     第四阶段展示问题回归：服务推进 → 持久化 → DownloadTaskQueries 投影 → 派生展示全链路核对。
///     覆盖停止确认生效后的阶段切换（P2-1）、退出校验后的内容进度与观察时间同步（P2-2，含停止
///     确认期间与 Unknown 快照的采样节奏）、普通任务在 qB 端重新校验的如实展示与退出恢复（P2-3，
///     含人工暂停任务）。
/// </summary>
public sealed class DownloadDisplayPhaseRegressionTests
{
    /// <summary>复审回归（P2-1）：保护暂停生效并进入稳定观察后，不得继续显示“等待停止确认”。</summary>
    [Fact]
    public async Task PauseConfirmedThenStabilizing_PhaseFollowsCurrentFacts()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验结束回到做种（所选文件完整）：保护暂停先发出，请求尚未生效时仍显示等待停止确认。
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 1.0);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 1.0 };
        harness.Store.FinishRecheck(hash);
        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] =
            harness.Store.Torrents[hash] with { Category = QbittorrentTorrentCategory.Seeding };
        await SampleAsync(harness);

        var pending = await ViewOfAsync(harness, taskId);
        Assert.True(pending.StopConfirmPending);
        Assert.Equal(DownloadDisplayPhase.WaitingStopConfirm, pending.Phase);

        // 假会话暂停立即生效（下一次快照为 Stopped）：停止确认后开始积累稳定样本，
        // 展示必须跟着当前事实切换为等待结果稳定，而非停留在停止确认。
        await SampleAsync(harness);
        var stabilizing = await ViewOfAsync(harness, taskId);
        Assert.False(stabilizing.StopConfirmPending);
        Assert.True(stabilizing.ResultStabilizing);
        Assert.Equal(DownloadDisplayPhase.WaitingResultStable, stabilizing.Phase);
    }

    /// <summary>复审回归（P2-2）：退出校验进入稳定观察后，进度数值、语义与观察时间必须同源切换到内容观察值。</summary>
    [Fact]
    public async Task StableObservation_SyncsContentProgressAndObservationTime()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验期进度 0.9 落库：带“校验”前缀的校验进度。
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.9 };
        await SampleAsync(harness);
        var checking = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.Checking, checking.Phase);
        Assert.Equal(0.9, checking.Progress);
        Assert.Equal("校验 ", checking.ProgressCaption);

        // 校验结束、停止态内容进度 0.4：稳定观察必须同步新的内容进度与本次观察时间，
        // 不得把上一轮校验进度伪装成无前缀的内容观察值。
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 0.4 };
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        await SampleAsync(harness);
        var observedAt = harness.Time.GetUtcNow();

        var stabilizing = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.WaitingResultStable, stabilizing.Phase);
        Assert.Equal(0.4, stabilizing.Progress);
        Assert.Null(stabilizing.ProgressCaption);
        Assert.Equal(observedAt, stabilizing.LastSyncedAtUtc);
    }

    /// <summary>
    ///     复审回归（P2-2 遗漏）：停止确认期间（发出保护暂停与确认窗口内）同步本轮内容进度与观察时间，
    ///     不把上一轮校验进度当作内容观察值展示。
    /// </summary>
    [Fact]
    public async Task StopConfirm_SyncsContentProgressWhilePausePending()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验期进度 0.9 落库（校验语义，带前缀）。
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.9 };
        await SampleAsync(harness);
        Assert.Equal(DownloadDisplayPhase.Checking, (await ViewOfAsync(harness, taskId)).Phase);

        // 校验结束回到做种、所选内容 40%：发出保护暂停的当轮即同步内容进度与本次观察时间。
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 0.4 };
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.FinishRecheck(hash);
        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] =
            harness.Store.Torrents[hash] with { Category = QbittorrentTorrentCategory.Seeding };
        await SampleAsync(harness);
        var pauseAt = harness.Time.GetUtcNow();

        var pending = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.WaitingStopConfirm, pending.Phase);
        Assert.True(pending.StopConfirmPending);
        Assert.Null(pending.ProgressCaption);
        Assert.Equal(0.4, pending.Progress);
        Assert.Equal(pauseAt, pending.LastSyncedAtUtc);

        // 暂停尚未生效、确认窗口内的下一轮观察（内容进度 0.5）：窗口内不补发暂停，但仍同步新观察值。
        harness.Store.Paused.Remove(hash);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 0.5 };
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.5);
        await SampleAsync(harness);
        var observedAt = harness.Time.GetUtcNow();

        var waiting = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.WaitingStopConfirm, waiting.Phase);
        Assert.True(waiting.StopConfirmPending);
        Assert.Equal(0.5, waiting.Progress);
        Assert.Equal(observedAt, waiting.LastSyncedAtUtc);
        Assert.Single(harness.Store.PauseCalls);
    }

    /// <summary>
    ///     复审回归（调度）：停止确认窗口内的 Unknown 快照保留旧进度与观察时间，但不得跳过下次采样安排
    ///     ——否则停止确认期间从 2 秒确认采样退回空闲轮询节奏。
    /// </summary>
    [Fact]
    public async Task StopConfirm_UnknownSnapshot_KeepsProgressButStillSchedulesNextSample()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var (taskId, hash) = await EnterVerifiedCheckAsync(harness);

        // 校验结束做种 0.4：发出保护暂停，当轮同步内容进度并安排确认采样。
        harness.Store.FileIdentities[hash] = ProgressOf(harness, hash, 0.4);
        harness.Store.Torrents[hash]       = harness.Store.Torrents[hash] with { Progress = 0.4 };
        harness.Store.FinishRecheck(hash);
        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] =
            harness.Store.Torrents[hash] with { Category = QbittorrentTorrentCategory.Seeding };
        await SampleAsync(harness);
        var pauseAt = harness.Time.GetUtcNow();
        Assert.True((await ViewOfAsync(harness, taskId)).StopConfirmPending);

        // 确认窗口内快照转为不可解释（Unknown，进度 0.99）：旧进度与观察时间保留，
        // 但下一轮采样仍按确认间隔安排，不残留已过期的到期时间。
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Unknown, Progress = 0.99
        };
        await SampleAsync(harness);
        var samplingAt = harness.Time.GetUtcNow();

        var interval = harness.Provider.GetRequiredService<DownloadTimingPolicy>().VerificationSampleInterval;
        var task     = await harness.GetTaskAsync(taskId);
        Assert.Equal(samplingAt + interval, task.NextObservationAtUtc);
        Assert.Equal(0.4, task.Progress);
        Assert.Equal(pauseAt, task.LastSyncedAtUtc);
        Assert.Equal(DownloadDisplayPhase.WaitingStopConfirm, (await ViewOfAsync(harness, taskId)).Phase);
        Assert.Single(harness.Store.PauseCalls);
    }

    /// <summary>复审回归（P2-3 遗漏）：人工暂停的普通任务在 qB 端重新校验时如实显示校验中，退出后恢复暂停展示。</summary>
    [Fact]
    public async Task PausedRegularTaskQbRecheck_ShowsCheckingWhilePaused()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       running = await RunToDownloadingAsync(harness, taskId);
        Assert.False(running.IsUserPaused);

        // 页面人工暂停：显示已暂停（人工）。
        using (var scope = harness.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DownloadService>().PauseAsync(taskId);
        }

        var paused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, paused.Status);
        Assert.True(paused.IsUserPaused);
        Assert.Equal(DownloadDisplayPhase.PausedManually, (await ViewOfAsync(harness, taskId)).Phase);

        // qB 端重新校验：暂停不改动任务状态，展示按最近观察类别如实切换为校验中（带前缀）。
        var hash = running.InfoHash;
        harness.Store.Rechecking.Add(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.9 };
        await harness.RoundAsync();

        var checking = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.Checking, checking.Phase);
        Assert.Equal("校验 ", checking.ProgressCaption);
        Assert.Equal(0.9, checking.Progress);

        // 退出校验回到停止：类别被下一次观察覆盖，恢复人工暂停展示。
        harness.Store.FinishRecheck(hash);
        await harness.RoundAsync();
        var restored = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.PausedManually, restored.Phase);
        Assert.Null(restored.ProgressCaption);
    }

    /// <summary>复审回归（P2-3）：普通任务在 qB 端重新校验时如实显示校验中，退出校验后恢复下载展示。</summary>
    [Fact]
    public async Task RegularTaskQbRecheck_ShowsCheckingPhaseAndRestoresAfterExit()
    {
        using var harness = await SchedulingHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       running = await RunToDownloadingAsync(harness, taskId);
        Assert.False(running.IsUserPaused);
        Assert.Equal(DownloadDisplayPhase.Downloading, (await ViewOfAsync(harness, taskId)).Phase);

        // qB 端重新校验（快照类别 Checking）：显示校验阶段并标注校验进度。
        var hash = running.InfoHash;
        harness.Store.Rechecking.Add(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.9 };
        var (pauses, rechecks, resumes) = (harness.Store.PauseCalls.Count,
                                           harness.Store.RecheckCalls.Count, harness.Store.ResumeCalls.Count);
        await harness.RoundAsync();

        var checking = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.Checking, checking.Phase);
        Assert.Equal("校验 ", checking.ProgressCaption);
        Assert.Equal(0.9, checking.Progress);

        // 退出校验回到下载：恢复下载展示，不粘在校验阶段；全程不为展示发出多余控制请求。
        harness.Store.FinishRecheck(hash);
        await harness.RoundAsync();
        var downloading = await ViewOfAsync(harness, taskId);
        Assert.Equal(DownloadDisplayPhase.Downloading, downloading.Phase);
        Assert.Null(downloading.ProgressCaption);
        Assert.Equal((pauses, rechecks, resumes), (harness.Store.PauseCalls.Count,
                                                   harness.Store.RecheckCalls.Count, harness.Store.ResumeCalls.Count));
    }

    // ───────────────────────── 助手（最小复用第三阶段的构造方式） ─────────────────────────

    /// <summary>驱动普通任务（无复用候选）完成提交与准备，直到进入真实下载。</summary>
    private static async Task<DownloadTask> RunToDownloadingAsync(SchedulingHarness harness, Guid taskId)
    {
        DownloadTask? running = null;
        for (var i = 0; i < 10; i++)
        {
            await harness.RoundAsync();
            running = await harness.GetTaskAsync(taskId);
            if (running is { Status: DownloadTaskStatus.Downloading, IsSystemPaused: false } &&
                running.VerificationIntent is null) break;
        }

        Assert.NotNull(running);
        Assert.Equal(DownloadTaskStatus.Downloading, running.Status);
        Assert.Null(running.VerificationIntent);
        return running;
    }

    private static async Task<DownloadTaskView> ViewOfAsync(SchedulingHarness harness, Guid taskId)
    {
        var queries =
            new DownloadTaskQueries(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());
        return Assert.Single(await queries.ListAsync(500), item => item.Id == taskId);
    }

    private static async Task<Guid> CreateCandidateAsync(SchedulingHarness harness)
    {
        var taskId = await harness.CreateUploadAsync(SingleFileTorrent());
        SeedExternalOccupant(harness, await ArchivalTargetAsync(harness, taskId), 1024);
        return taskId;
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
        return (taskId, task.InfoHash);
    }

    /// <summary>推进一个校验采样间隔（2 秒）再对账：驱动校验后结果稳定窗口的采样节奏。</summary>
    private static Task SampleAsync(SchedulingHarness harness) =>
        harness.AdvanceAsync(TimeSpan.FromSeconds(2.5));

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
