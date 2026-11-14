using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadVerificationTests
{
    [Fact]
    public async Task Verification_SharedDownloadBeforeConfirmation_DoesNotTrustLaterCompletion()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();

        harness.Store.FinishRecheck(task.InfoHash);
        harness.Store.StoppedAdds.Remove(task.InfoHash);
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 0.5 }).ToList();
        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with { Progress = 0.5 };
        await harness.ReconcileAsync();

        var intent = DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent);
        Assert.NotNull(intent);
        Assert.Equal(true, intent.UnverifiedWriteRisk);
        Assert.Single(harness.Store.PauseCalls);

        // 暂停后进度才更新到 100%，不能排除停止前已补写共享文件，也不能沿用旧校验证据。
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        var held = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, held.Status);
        Assert.Equal("复用校验", held.FailedStep);
        Assert.NotNull(held.VerificationIntent);
        Assert.Empty(harness.Store.ResumeCalls);
        await using var db = await harness.CreateDbContextAsync();
        Assert.NotEqual(DownloadFileVerificationOutcomes.ReusedVerified,
                        (await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == taskId))
                       .VerificationOutcome);
    }

    [Fact]
    public async Task Verification_LegacyPauseWithoutRiskClassification_DoesNotTrustOldEvidence()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await CreateCandidateAsync(harness);
        var       task    = await harness.GetTaskAsync(taskId);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        harness.Store.FinishRecheck(task.InfoHash);
        CompleteTorrent(harness, task.InfoHash);
        harness.Store.Paused.Add(task.InfoHash);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            var intent  = DownloadVerificationIntent.FromJson(tracked.VerificationIntent)!;
            tracked.VerificationIntent = (intent with { PauseAttempts = 1 }).ToJson()
                                                                            .Replace(",\"unverifiedWriteRisk\":null",
                                                                                      string.Empty,
                                                                                      StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Failed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Empty(harness.Store.ResumeCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Verification_ResumeExecutedButResponseLost_DoesNotPauseOrRestart(bool completed, bool stopped)
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
        harness.Factory.SessionConfigurator = session => session.AfterResumeAsync = () =>
        {
            if (completed)
            {
                CompleteTorrent(harness, task.InfoHash);
                harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
                {
                    Category = QbittorrentTorrentCategory.Seeding
                };
            }

            if (stopped) harness.Store.Paused.Add(task.InfoHash);
            throw new IOException("Resume executed, acknowledgement lost");
        };
        await harness.ReconcileAsync();
        var pending = await harness.GetTaskAsync(taskId);
        // 启动状态与数据完整性分离：Resume 已执行但响应丢失时，不误标用户暂停、不落回 Paused；
        // 完成证据已落库（意图收尾），启动状态在 RunRecovery 中等待观察确认。
        // 启动状态与数据完整性分离：Resume 已执行但响应丢失时不误标用户暂停、不落回 Paused；
        // 完成证据已落库（意图收尾），启动状态在 RunRecovery 中等待观察确认——立即观察发现
        // 已在运行（ContinueDownload）时按确认清空记录，回到普通下载跟踪。
        var run = DownloadRunRecovery.FromJson(pending.RunRecovery);
        Assert.Null(pending.VerificationIntent);
        Assert.Single(harness.Store.ResumeCalls);
        if (run is null)
        {
            Assert.False(pending.IsUserPaused);
            Assert.Equal(DownloadTaskStatus.Downloading, pending.Status);
        }
        else
        {
            Assert.Equal(DownloadRunPurposes.ContinueDownload, run.Purpose);
            Assert.Equal(DownloadRunStates.ResponseUnknown, run.State);
            Assert.True(pending.IsSystemPaused);
            Assert.Equal(DownloadTaskStatus.Downloading, pending.Status);
        }

        // 用新 scope 的服务模拟进程内状态丢失，只依赖持久化恢复记录和下载器当前事实推进。
        harness.Factory.SessionConfigurator = null;
        using var scope   = harness.Provider.CreateScope();
        var       service = scope.ServiceProvider.GetRequiredService<DownloadService>();
        await service.ReconcileAsync();
        await service.ReconcileAsync();

        var recovered = await harness.GetTaskAsync(taskId);
        Assert.Null(recovered.VerificationIntent);
        Assert.Equal(completed ? DownloadTaskStatus.Completed : DownloadTaskStatus.Downloading,
                     recovered.Status);
        if (stopped && !completed)
        {
            // Resume 已被接受后停止是下载器策略（分享限制/人工），不再自动拉起：
            // 保持系统暂停与人工「继续下载」入口，绝不改写数据完整性证据。
            Assert.True(recovered.IsSystemPaused);
            Assert.Equal(DownloadRunStates.DownloaderStopped,
                         DownloadRunRecovery.FromJson(recovered.RunRecovery)!.State);
        }
        else
        {
            // 已确认运行（清空记录回普通下载跟踪）或数据本就完整（完成收尾）。
            Assert.False(recovered.IsSystemPaused);
            Assert.Null(recovered.RunRecovery);
        }

        Assert.Empty(harness.Store.PauseCalls);
        // 有界重试最多补发一次（预算 3 内）；Sent 后停止不再自动拉起。
        Assert.True(harness.Store.ResumeCalls.Count is 1 or 2);
        Assert.Single(harness.Store.RecheckCalls);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified,
                     (await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == taskId &&
                                                                     file.FileIndex      == 0))
                    .VerificationOutcome);
    }

    private static async Task<Guid> CreatePartialCandidateAsync(VerificationHarness harness)
    {
        var taskId = await harness.CreateUploadAsync(MixedTorrent());
        var targets = await harness.Provider.GetRequiredService<DownloadFileService>()
                                   .ComputeArchivalTargetsAsync(taskId);
        SeedExternalOccupant(harness, targets.Single(target => target.FileIndex == 0).TargetPath, 1024);
        return taskId;
    }
}
