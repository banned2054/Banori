using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Tests.Downloads;

/// <summary>
///     P2 回归：包含承接的严格超集增量必须以常规逐集择优后的最终承担集合为准——常规落选集的
///     文件终将被取消选择，不得充当独有内容增量，也不得为旧包职责充数；没有新增职责时保留现任。
/// </summary>
public sealed partial class DownloadVerificationTests
{
    [Fact]
    public async Task Containment_IncrementFromLosingEpisode_KeepsIncumbentsAndThrows()
    {
        await AssertLosingIncrementRejectedAsync(ContainmentFileName(1), ContainmentFileName(2, "繁日双语"));
    }

    [Fact]
    public async Task Containment_IncrementFromLosingEpisode_IgnoresBatchFileOrder()
    {
        await AssertLosingIncrementRejectedAsync(ContainmentFileName(2, "繁日双语"), ContainmentFileName(1));
    }

    [Fact]
    public async Task Containment_GenuineSupersetAcrossMultipleIncumbents_StillTakesOver()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var ep1Id = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1));
        var ep2Id = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(2));

        // 第 3 集是真正新增职责：两集同文件部分在常规择优后由包含承接改判新包并登记待完成
        // 优胜，全部正片保持选中——严格超集承接不因落选增量修正而被误伤。
        var bigId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1),
                                                                     ContainmentFileName(2),
                                                                     ContainmentFileName(3)));
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 0),
                     (await EpisodeRegistryAsync(harness, 1))!.PendingVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 1),
                     (await EpisodeRegistryAsync(harness, 2))!.PendingVideoFileId);
        await using var db = await harness.CreateDbContextAsync();
        Assert.True(await db.DownloadTaskFiles.AsNoTracking()
                            .Where(file => file.DownloadTaskId == bigId)
                            .AllAsync(file => file.IsSelectedForDownload));
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(ep1Id)).Status);
        Assert.Null((await harness.GetTaskAsync(ep1Id)).ReplacedByTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(ep2Id)).Status);
        Assert.Null((await harness.GetTaskAsync(ep2Id)).ReplacedByTaskId);
    }

    [Fact]
    public async Task Containment_MultiEpisodeIncumbent_SameFileExtensionTakesOver()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1),
                                                            ContainmentFileName(2));

        // 旧多集包 E1/E2 → 新包同文件 E1/E2 + 缺集 E3：两集均由包含承接改判新包并登记待完成
        // 优胜，全部正片保持选中；已完成现任不动，退出交给冗余清理收敛。
        var bigId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1),
                                                                     ContainmentFileName(2),
                                                                     ContainmentFileName(3)));
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 0),
                     (await EpisodeRegistryAsync(harness, 1))!.PendingVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 1),
                     (await EpisodeRegistryAsync(harness, 2))!.PendingVideoFileId);
        await using var db = await harness.CreateDbContextAsync();
        Assert.True(await db.DownloadTaskFiles.AsNoTracking()
                            .Where(file => file.DownloadTaskId == bigId)
                            .AllAsync(file => file.IsSelectedForDownload));
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(oldId)).Status);
        Assert.Null((await harness.GetTaskAsync(oldId)).ReplacedByTaskId);
    }

    [Fact]
    public async Task Containment_TwoSingleEpisodeIncumbents_MergedIntoSameFilePackage()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var ep1Id = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1));
        var ep2Id = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(2));

        // 两个旧单集 E1/E2 → 新同文件 E1/E2 包：对各自现任都是严格超集（对方集即独有内容），
        // 两集合并改判新包胜出并登记待完成优胜，旧单集任务保持不动。
        var bigId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1),
                                                                     ContainmentFileName(2)));
        var ep1Row = await EpisodeRegistryAsync(harness, 1);
        var ep2Row = await EpisodeRegistryAsync(harness, 2);
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 0), ep1Row!.PendingVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 1), ep2Row!.PendingVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, ep1Id, 0), ep1Row.IncumbentVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, ep2Id, 0), ep2Row.IncumbentVideoFileId);
        await using var db = await harness.CreateDbContextAsync();
        Assert.True(await db.DownloadTaskFiles.AsNoTracking()
                            .Where(file => file.DownloadTaskId == bigId)
                            .AllAsync(file => file.IsSelectedForDownload));
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(ep1Id)).Status);
        Assert.Null((await harness.GetTaskAsync(ep1Id)).ReplacedByTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(ep2Id)).Status);
        Assert.Null((await harness.GetTaskAsync(ep2Id)).ReplacedByTaskId);
    }

    private static async Task AssertLosingIncrementRejectedAsync(params string[] filePaths)
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var ep1Id      = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1));
        var ep2Id      = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(2));
        var addsBefore = harness.Store.AddCalls.Count;

        // E2 繁日常规择优落选，其文件不构成严格超集增量：E1 无新增职责沿用现任保留，整包落选
        // 抛出并留「择优」终止墓碑，不登记待完成优胜，也不添加到下载器。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                harness.CreateUploadAsync(ContainmentBatch(filePaths)));

        Assert.Equal(addsBefore, harness.Store.AddCalls.Count);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(ep1Id)).Status);
        Assert.Null((await harness.GetTaskAsync(ep1Id)).ReplacedByTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(ep2Id)).Status);
        Assert.Null((await harness.GetTaskAsync(ep2Id)).ReplacedByTaskId);
        Assert.Null((await EpisodeRegistryAsync(harness, 1))!.PendingVideoFileId);
        Assert.Null((await EpisodeRegistryAsync(harness, 2))!.PendingVideoFileId);
        await using var db = await harness.CreateDbContextAsync();
        var tombstone = Assert.Single(await db.DownloadTasks.AsNoTracking()
                                              .Where(task => task.Status == DownloadTaskStatus.Replaced)
                                              .ToListAsync());
        Assert.Equal("择优", tombstone.FailedStep);
    }
}
