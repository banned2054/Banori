using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     升级前仍在 Downloading 的任务按旧归档日志收尾：远端移动未完成、改名已生效未确认、
///     旧布局已确认但未落 Completed 三种阶段都从正式 ReconcileAsync 走完整完成链。
/// </summary>
public sealed partial class DownloadServiceTests
{
    public enum LegacyArchiveStage
    {
        /// <summary>qB 仍在 .tasks/{taskId}，改名与移动都尚未执行。</summary>
        MovePending,

        /// <summary>qB 已按旧日志改名并到达下载根，数据库尚未确认。</summary>
        RemoteApplied,

        /// <summary>旧布局已经确认（pending 清理、日志固定），任务尚未写成 Completed 就重启。</summary>
        ConfirmedBeforeCompletion
    }

    [Theory]
    [InlineData(LegacyArchiveStage.MovePending, false)]
    [InlineData(LegacyArchiveStage.MovePending, true)]
    [InlineData(LegacyArchiveStage.RemoteApplied, false)]
    [InlineData(LegacyArchiveStage.RemoteApplied, true)]
    [InlineData(LegacyArchiveStage.ConfirmedBeforeCompletion, false)]
    [InlineData(LegacyArchiveStage.ConfirmedBeforeCompletion, true)]
    public async Task LegacyArchive_DownloadingTask_CompletesOnOldLayoutAcrossRestart(
        LegacyArchiveStage stage, bool reuseUnverified)
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var fixture = await SeedDownloadingLegacyArchiveAsync(
                                                              harness, BatchTorrent(NekoTitle("简日双语", episode : 12)),
                                                              stage, reuseUnverified);

        var renameBefore   = harness.Store.RenameFileCalls.Count;
        var locationBefore = harness.Store.SetLocationCalls.Count;
        var priorRow       = await FindEpisodeRowAsync(harness);

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(fixture.Id);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        Assert.Null(completed.PendingSavePath);
        Assert.Null(completed.ArchiveReview);
        Assert.Equal("/downloads", completed.SavePath);
        Assert.Equal(DownloadPathDecisionKinds.LegacyArchiveFinal,
                     DownloadPathDecision.FromJson(completed.PathDecision)!.Kind);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == fixture.Id);
            Assert.Equal(fixture.Target, file.CurrentRelativePath);
            Assert.Null(file.PendingRenameFrom);
            Assert.Null(file.PendingRenameTo);
            Assert.Equal(reuseUnverified ? DownloadFileVerificationOutcomes.ReuseUnverified : null,
                         file.VerificationOutcome);
        }

        Assert.Equal("/downloads", harness.Store.Torrents[fixture.Hash].SavePath);
        Assert.Equal(fixture.Target, Assert.Single(harness.Store.FileIdentities[fixture.Hash]).Path);

        if (stage == LegacyArchiveStage.MovePending)
        {
            Assert.Contains(harness.Store.SetLocationCalls,
                            call => call.Hash == fixture.Hash && call.SavePath == "/downloads");
            Assert.Equal(locationBefore + 1, harness.Store.SetLocationCalls.Count);
            Assert.Equal(renameBefore   + 1, harness.Store.RenameFileCalls.Count);
            Assert.Contains(harness.Store.RenameFileCalls,
                            call => call.Hash == fixture.Hash && call.NewPath == fixture.Target);
        }
        else
        {
            // 改名或移动已生效：不重发已经落在 qB 的副作用。
            Assert.Equal(locationBefore, harness.Store.SetLocationCalls.Count);
            Assert.Equal(renameBefore, harness.Store.RenameFileCalls.Count);
        }

        var row = await FindEpisodeRowAsync(harness);
        Assert.NotNull(row);
        Assert.Equal(fixture.FileId, row.IncumbentVideoFileId);
        Assert.Null(row.PendingVideoFileId);
        if (priorRow?.IncumbentVideoFileId is { } previous && previous != fixture.FileId)
            Assert.Equal(previous, row.SupersededVideoFileId);

        var renameAfter   = harness.Store.RenameFileCalls.Count;
        var locationAfter = harness.Store.SetLocationCalls.Count;
        var restarted     = harness.Provider.GetRequiredService<DownloadService>();
        await restarted.ReconcileAsync();
        await restarted.ReconcileAsync();
        Assert.Equal(renameAfter, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationAfter, harness.Store.SetLocationCalls.Count);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(fixture.Id)).Status);
    }

    [Fact]
    public async Task LegacyArchive_ConfirmedBeforeCompletion_RestartsAfterEveryStage()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var fixture = await SeedDownloadingLegacyArchiveAsync(
                                                              harness, BatchTorrent(NekoTitle("简日双语", episode : 12)),
                                                              LegacyArchiveStage.ConfirmedBeforeCompletion);

        // 连续用新的服务实例模拟多次进程重启：旧布局日志必须一直固定，绝不切到媒体根。
        for (var i = 0; i < 3; i++)
        {
            var restarted = harness.Provider.GetRequiredService<DownloadService>();
            await restarted.ReconcileAsync();
            Assert.Equal("/downloads", (await harness.GetTaskAsync(fixture.Id)).SavePath);
        }

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(fixture.Id)).Status);
        Assert.Equal(DownloadPathDecisionKinds.LegacyArchiveFinal,
                     DownloadPathDecision.FromJson((await harness.GetTaskAsync(fixture.Id)).PathDecision)!.Kind);
    }

    private static async Task<EpisodeVersion?> FindEpisodeRowAsync(TestHarness harness)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.EpisodeVersions.AsNoTracking()
                       .SingleOrDefaultAsync(row => row.SubjectId == 42 && row.Episode == 12);
    }

    /// <summary>
    ///     从真实“已完成旧任务”派生出 Downloading 起点：数据库仍是旧归档日志，qB 已按所选内容完成，
    ///     再按阶段分别构造远端尚未移动 / 已生效未确认 / 已确认但未落完成三种快照。
    /// </summary>
    private static async Task<(Guid Id, string Hash, string Target, Guid FileId)> SeedDownloadingLegacyArchiveAsync(
        TestHarness harness, byte[] bytes, LegacyArchiveStage stage, bool reuseUnverified = false)
    {
        var (taskId, hash, _, target) = await SeedLegacyArchiveJournalAsync(harness, bytes, false);
        var name     = target["Media/Anime/".Length..];
        var taskRoot = DownloadTaskPlacement.TaskRoot("/downloads", taskId);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.Include(value => value.Files)
                               .SingleAsync(value => value.Id == taskId);
            var file = task.Files.Single();
            task.Status            = DownloadTaskStatus.Downloading;
            task.CompletedAtUtc    = null;
            file.PendingRenameFrom = name;
            file.PendingRenameTo   = target;

            switch (stage)
            {
                case LegacyArchiveStage.MovePending :
                case LegacyArchiveStage.RemoteApplied :
                    task.SavePath            = taskRoot;
                    file.CurrentRelativePath = name;
                    break;
                case LegacyArchiveStage.ConfirmedBeforeCompletion :
                    task.SavePath            = "/downloads";
                    file.CurrentRelativePath = target;
                    task.PendingSavePath     = null;
                    file.PendingRenameFrom   = null;
                    file.PendingRenameTo     = null;
                    task.PathDecision = new DownloadPathDecision
                    {
                        Kind         = DownloadPathDecisionKinds.LegacyArchiveFinal,
                        Root         = "/downloads",
                        Reason       = "升级前的归档日志已确认，完成收尾沿用旧版全局下载根与媒体目录前缀。",
                        DecidedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                    }.ToJson();
                    break;
            }

            // 曾经复用失败后转入 .tasks 独立下载：历史结果保留在文件上，旧布局归档仍可恢复。
            if (reuseUnverified)
            {
                file.VerificationOutcome = DownloadFileVerificationOutcomes.ReuseUnverified;
                file.VerificationPath    = "/downloads/" + target;
            }

            await db.SaveChangesAsync();
        }

        var remoteName = stage == LegacyArchiveStage.MovePending ? name : target;
        var remoteRoot = stage == LegacyArchiveStage.MovePending ? taskRoot : "/downloads";
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            SavePath = remoteRoot, IsMoving = false, Progress = 1.0,
            Category = QbittorrentTorrentCategory.Seeding
        };
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, remoteName, 1024, 1.0)];
        harness.Store.Files[hash]          = [remoteName];
        await ClearMigrationMarkerAsync(harness);

        return (taskId, hash, target, await FindAnimeFileIdAsync(harness, taskId) ?? Guid.Empty);
    }
}
