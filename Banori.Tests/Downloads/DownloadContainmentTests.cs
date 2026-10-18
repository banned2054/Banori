using System.Text;
using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>第四阶段：包含关系承接与冗余种子清理（职责耗尽退场、等价/部分重叠保留边界）。</summary>
public sealed partial class DownloadVerificationTests
{
    private static string ContainmentFileName(int episode, string language = "简日双语") =>
        $"【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][{episode:00}][1080p][{language}].mp4";

    private static string ContainmentSubtitleName(int episode, string language = "简日双语") =>
        ContainmentFileName(episode, language)[..^4] + ".ass";

    /// <summary>多文件合集种子（与 DownloadServiceTests.BatchTorrent 同构造）：文件名即发布名。</summary>
    private static byte[] ContainmentBatch(params string[] filePaths)
    {
        var files = string.Concat(filePaths.Select(path =>
                                                       "d6:lengthi1024e4:pathl"                 +
                                                       string.Concat(path.Split('/').Select(B)) + "ee"));
        var pieceCount = (filePaths.Length * 1024 + 16383) / 16384;
        var pieces     = new string('0', checked(pieceCount * 20));
        return Encoding.UTF8.GetBytes("d4:infod5:filesl"                                + files     +
                                      "e4:name8:Original12:piece lengthi16384e6:pieces" + B(pieces) + "ee");

        static string B(string text) => Encoding.UTF8.GetByteCount(text) + ":" + text;
    }

    /// <summary>不同大小的升级变体：大小与现任成片不同，不构成复用候选，走任务根独立下载。</summary>
    private static byte[] LargerVariantTorrent(int episode, string language) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestTorrentBytes.Create(ContainmentFileName(episode, language)))
                                                .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                         StringComparison.Ordinal));

    private static async Task<EpisodeVersion?> EpisodeRegistryAsync(VerificationHarness harness, decimal episode)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.EpisodeVersions.AsNoTracking()
                       .SingleOrDefaultAsync(row => row.SubjectId == 42 && row.Episode == episode);
    }

    private static async Task<Guid> ContainmentFileIdAsync(VerificationHarness harness, Guid taskId, int fileIndex)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.DownloadTaskFiles.AsNoTracking()
                       .Where(file => file.DownloadTaskId == taskId && file.FileIndex == fileIndex)
                       .Select(file => file.Id).SingleAsync();
    }

    private static async Task<Guid> CompleteContainmentIncumbentAsync(VerificationHarness harness,
                                                                      params string[] filePaths)
    {
        var taskId = await harness.CreateUploadAsync(ContainmentBatch(filePaths));
        var task   = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        return taskId;
    }

    [Fact]
    public async Task Containment_BigPackageReusesIncumbentData_RetiresSmallBeforeNewEpisodesComplete()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var smallId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1));
        var small   = await harness.GetTaskAsync(smallId);

        // 大包严格包含小包（第 1 集同文件）且带缺集第 2 集：包含承接改判大包胜出，第 1 集保持
        // 选中并登记待完成优胜，不因同内容平级的现任保留而取消选择。
        var bigId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1),
                                                                     ContainmentFileName(2)));
        var big   = await harness.GetTaskAsync(bigId);
        var registered = await EpisodeRegistryAsync(harness, 1);
        Assert.NotNull(registered);
        Assert.Equal(await ContainmentFileIdAsync(harness, smallId, 0), registered.IncumbentVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 0), registered.PendingVideoFileId);

        // 复用校验链：候选根停止添加 → 受控映射改名 onto 小包成片 → recheck 证实同内容。
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck（进入 Checking）。
        await harness.ReconcileAsync(); // 观察位。
        harness.Store.FileIdentities[big.InfoHash] = harness.Store.FileIdentities[big.InfoHash]
                                                            .Select(file => file with { Progress = 1.0 })
                                                            .ToList();
        harness.Store.FinishRecheck(big.InfoHash);
        await harness.ReconcileAsync(); // 结论轮：候选通过 → 证据落库。

        await using (var db = await harness.CreateDbContextAsync())
        {
            var reused = await db.DownloadTaskFiles.AsNoTracking()
                                  .SingleAsync(file => file.DownloadTaskId == bigId && file.FileIndex == 0);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, reused.VerificationOutcome);
        }

        // 校验通过即逐集承接现任（大包新集仍在下载，不必等整包完成），小包职责耗尽翻转被替换。
        var switched = await EpisodeRegistryAsync(harness, 1);
        Assert.NotNull(switched);
        Assert.Equal(await ContainmentFileIdAsync(harness, bigId, 0), switched.IncumbentVideoFileId);
        Assert.Equal(await ContainmentFileIdAsync(harness, smallId, 0), switched.SupersededVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
        var retiredSmall = await harness.GetTaskAsync(smallId);
        Assert.Equal(DownloadTaskStatus.Replaced, retiredSmall.Status);
        Assert.Equal(bigId, retiredSmall.ReplacedByTaskId);
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(bigId)).Status);

        // 冗余清理：保留内容移除小包种子（deleteFiles=false），数据仍承载于大包的校验目标路径。
        await harness.ReconcileAsync();
        Assert.Contains(harness.Store.DeleteCalls, call => call.Hash == small.InfoHash && !call.DeleteFiles);
        Assert.DoesNotContain(small.InfoHash, harness.Store.Torrents.Keys);
        Assert.Contains(big.InfoHash, harness.Store.Torrents.Keys);
    }

    [Fact]
    public async Task Containment_EquivalentContentWithoutUniqueFiles_KeepsIncumbentAndRecordsTombstone()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var smallId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1));

        // 等价内容集合：只有第 1 集同文件与不可解析附件，没有任何选中独有内容——不构成严格超集，
        // 沿用现任保留；整包落选留终止记录供查重，不添加到下载器。
        var addsBefore = harness.Store.AddCalls.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1), "readme.txt")));

        Assert.Equal(addsBefore, harness.Store.AddCalls.Count);
        Assert.Empty(harness.Store.DeleteCalls);
        var stillIncumbent = await harness.GetTaskAsync(smallId);
        Assert.Equal(DownloadTaskStatus.Completed, stillIncumbent.Status);
        Assert.Null(stillIncumbent.ReplacedByTaskId);
        Assert.Equal(await ContainmentFileIdAsync(harness, smallId, 0),
                     (await EpisodeRegistryAsync(harness, 1))!.IncumbentVideoFileId);
        await using var db = await harness.CreateDbContextAsync();
        var tombstone = await db.DownloadTasks.AsNoTracking()
                                .SingleAsync(task => task.Status == DownloadTaskStatus.Replaced &&
                                                     task.ReplacedByTaskId == smallId);
        Assert.Equal("择优", tombstone.FailedStep);
    }

    [Fact]
    public async Task Containment_UniqueSubtitleDuty_BlocksTakeoverAndKeepsSmallSeed()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var smallId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1),
                                                              ContainmentSubtitleName(1));
        var small   = await harness.GetTaskAsync(smallId);

        // 大包正片覆盖小包第 1 集，但没有小包的独有字幕：职责文件未被全部覆盖，不构成包含承接，
        // 第 1 集回落到现任保留语义取消选择；小包保留全部职责。
        var bigId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1),
                                                                     ContainmentFileName(2)));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var ep1 = await db.DownloadTaskFiles.AsNoTracking()
                              .SingleAsync(file => file.DownloadTaskId == bigId && file.FileIndex == 0);
            Assert.False(ep1.IsSelectedForDownload);
            Assert.Contains("同集已有更优任务", ep1.SelectionReason);
            var ep2 = await db.DownloadTaskFiles.AsNoTracking()
                              .SingleAsync(file => file.DownloadTaskId == bigId && file.FileIndex == 1);
            Assert.True(ep2.IsSelectedForDownload);
        }

        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        Assert.Equal(await ContainmentFileIdAsync(harness, smallId, 0),
                     (await EpisodeRegistryAsync(harness, 1))!.IncumbentVideoFileId);
        Assert.Null(await EpisodeRegistryAsync(harness, 2));
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(smallId)).Status);
        Assert.Null((await harness.GetTaskAsync(smallId)).ReplacedByTaskId);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Contains(small.InfoHash, harness.Store.Torrents.Keys);
    }

    [Fact]
    public async Task Cleanup_CompletedBatchReplacedBySeparateWinners_RetiresAfterLastDutyExits()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1, "繁日双语"),
                                                            ContainmentFileName(2, "繁日双语"));
        var old   = await harness.GetTaskAsync(oldId);

        // 第 1 集由更高优先级的简日单集替换：完成切换后旧包仍承担第 2 集，不退出。
        var ep1WinnerId = await harness.CreateUploadAsync(LargerVariantTorrent(1, "简日双语"));
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(ep1WinnerId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(await ContainmentFileIdAsync(harness, ep1WinnerId, 0),
                     (await EpisodeRegistryAsync(harness, 1))!.IncumbentVideoFileId);
        var afterFirst = await harness.GetTaskAsync(oldId);
        Assert.Equal(DownloadTaskStatus.Completed, afterFirst.Status);
        Assert.Null(afterFirst.ReplacedByTaskId);

        // 第 2 集也被替换完成：最后一项职责退出，旧包翻转为被替换，清理环保留内容移除种子。
        var ep2WinnerId = await harness.CreateUploadAsync(LargerVariantTorrent(2, "简日双语"));
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(ep2WinnerId)).InfoHash);
        await harness.ReconcileAsync();

        var retired = await harness.GetTaskAsync(oldId);
        Assert.Equal(DownloadTaskStatus.Replaced, retired.Status);
        Assert.Equal(ep2WinnerId, retired.ReplacedByTaskId);
        await harness.ReconcileAsync();
        Assert.Contains(harness.Store.DeleteCalls, call => call.Hash == old.InfoHash && !call.DeleteFiles);
        Assert.DoesNotContain(old.InfoHash, harness.Store.Torrents.Keys);
        Assert.Equal(2, harness.Store.Torrents.Count);
    }
}
