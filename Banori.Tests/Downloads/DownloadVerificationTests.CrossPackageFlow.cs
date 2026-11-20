using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     跨包逐集裁决的真实入口回归：上传登记即完成候选识别与文件选择，对账链执行 qB 复用校验和
///     完成归档；落选文件保持未选中，已验证复用的共享数据不得被改名、移动或连数据删除。
/// </summary>
public sealed partial class DownloadVerificationTests
{
    [Fact]
    public async Task CrossPackage_LoserAndReusedCandidate_CompleteWithoutTouchingSharedData()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var ep12OwnerId  = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(12));
        var ep13OwnerId  = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(13));
        var ep12Owner    = await harness.GetTaskAsync(ep12OwnerId);
        var ep13Owner    = await harness.GetTaskAsync(ep13OwnerId);
        var sharedFile   = Assert.Single(harness.Store.FileIdentities[ep12Owner.InfoHash]);
        var ownerRenames = harness.Store.RenameFileCalls.Count(call => call.Hash  == ep12Owner.InfoHash);
        var ownerMoves   = harness.Store.SetLocationCalls.Count(call => call.Hash == ep12Owner.InfoHash);

        var incomingId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(12),
                                                                          ContainmentFileName(13, "繁日双语")));
        var incoming = await harness.GetTaskAsync(incomingId);
        var selectedTarget =
            (await harness.Provider.GetRequiredService<DownloadFileService>()
                          .ComputeArchivalTargetsAsync(incomingId)).Single(value => value.FileIndex == 0).TargetPath;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var files = await db.DownloadTaskFiles.AsNoTracking()
                                .Where(file => file.DownloadTaskId == incomingId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
            Assert.True(files[0].IsSelectedForDownload);
            Assert.False(files[1].IsSelectedForDownload);
            Assert.Contains("同集已有更优任务", files[1].SelectionReason, StringComparison.Ordinal);
        }

        var ep12Row = (await EpisodeRegistryAsync(harness, 12))!;
        Assert.Equal(await ContainmentFileIdAsync(harness, ep12OwnerId, 0), ep12Row.IncumbentVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, incomingId, 0), ep12Row.PendingVideoFileId);
        Assert.Null((await EpisodeRegistryAsync(harness, 13))!.PendingVideoFileId);

        // 第 12 集与已完成现任同身份：真实对账链发出校验，并把通过证据绑定到归档目标。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        Assert.Contains(incoming.InfoHash, harness.Store.RecheckCalls);

        harness.Store.FileIdentities[incoming.InfoHash] =
            harness.Store.FileIdentities[incoming.InfoHash]
                   .Select(file => file with { Progress = file.Index == 0 ? 1.0 : 0 }).ToList();
        harness.Store.FinishRecheck(incoming.InfoHash);
        await harness.ReconcileAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var reused = await db.DownloadTaskFiles.AsNoTracking()
                                 .SingleAsync(file => file.DownloadTaskId == incomingId && file.FileIndex == 0);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, reused.VerificationOutcome);
            Assert.Equal(selectedTarget, reused.VerificationPath);
        }

        CompleteTorrent(harness, incoming.InfoHash);
        for (var i = 0; i < 6; i++) await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(incomingId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(DownloadArchiveReview.FromJson(completed.ArchiveReview)?.Reason);
        // 落选文件仍需停靠，整包保留旧版全局根；只有正片被归档到媒体子目录。
        Assert.Equal(DownloadRoot, completed.SavePath);
        Assert.Equal(selectedTarget,
                     harness.Store.Torrents[incoming.InfoHash].SavePath + "/" +
                     harness.Store.FileIdentities[incoming.InfoHash].Single(file => file.Index == 0).Path);
        Assert.Equal(await ContainmentFileIdAsync(harness, incomingId, 0),
                     (await EpisodeRegistryAsync(harness, 12))!.IncumbentVideoFileId);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var files = await db.DownloadTaskFiles.AsNoTracking()
                                .Where(file => file.DownloadTaskId == incomingId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
            Assert.True(files[0].IsSelectedForDownload);
            Assert.Equal(selectedTarget, files[0].VerificationPath);
            Assert.False(files[1].IsSelectedForDownload);
            Assert.Contains("同集已有更优任务", files[1].SelectionReason, StringComparison.Ordinal);
        }

        // 共享文件由 qB 侧保留：允许无数据删除的冗余种子退出，但新任务不得再改名或移动它。
        Assert.Equal(ownerRenames, harness.Store.RenameFileCalls.Count(call => call.Hash == ep12Owner.InfoHash));
        Assert.Equal(ownerMoves, harness.Store.SetLocationCalls.Count(call => call.Hash  == ep12Owner.InfoHash));
        Assert.DoesNotContain(harness.Store.DeleteCalls,
                              call => call.Hash == ep12Owner.InfoHash && call.DeleteFiles);
        Assert.DoesNotContain(harness.Store.DeleteCalls,
                              call => call.Hash == ep13Owner.InfoHash && call.DeleteFiles);
        if (harness.Store.FileIdentities.TryGetValue(ep12Owner.InfoHash, out var ownerFiles))
            Assert.Equal(sharedFile, Assert.Single(ownerFiles));
    }
}
