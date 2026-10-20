using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

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
                                                                      params string[]     filePaths)
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
        var big        = await harness.GetTaskAsync(bigId);
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
                                                            .Select(file => file with
                                                             {
                                                                 Progress = file.Index == 0 ? 1.0 : 0
                                                             })
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
                                                                harness
                                                                   .CreateUploadAsync(ContainmentBatch(ContainmentFileName(1),
                                                                                      "readme.txt")));

        Assert.Equal(addsBefore, harness.Store.AddCalls.Count);
        Assert.Empty(harness.Store.DeleteCalls);
        var stillIncumbent = await harness.GetTaskAsync(smallId);
        Assert.Equal(DownloadTaskStatus.Completed, stillIncumbent.Status);
        Assert.Null(stillIncumbent.ReplacedByTaskId);
        Assert.Equal(await ContainmentFileIdAsync(harness, smallId, 0),
                     (await EpisodeRegistryAsync(harness, 1))!.IncumbentVideoFileId);
        await using var db = await harness.CreateDbContextAsync();
        var tombstone = await db.DownloadTasks.AsNoTracking()
                                .SingleAsync(task => task.Status           == DownloadTaskStatus.Replaced &&
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
        var small = await harness.GetTaskAsync(smallId);

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
        var old = await harness.GetTaskAsync(oldId);

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

    [Fact]
    public async Task Cleanup_PureSelectionLossTombstone_NeverAuthorizesRemoteCleanup()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var incumbentId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1));
        var incumbent   = await harness.GetTaskAsync(incumbentId);

        // 等价内容包同集落选：留下从未提交/接管的纯择优墓碑（不进入下载器）。
        var bytes = ContainmentBatch(ContainmentFileName(1), "readme.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateUploadAsync(bytes));
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var loserTorrent, out _));
        var loserHash = loserTorrent.InfoHashes.V1!.ToHex().ToLowerInvariant();
        Assert.DoesNotContain(loserHash, harness.Store.Torrents.Keys);

        // 同 hash 的外部种子：用户/外部工具自添、从未被本应用接管，且实际根与墓碑规划根不同。
        harness.Store.AddTorrent(loserHash, "external", QbittorrentTorrentCategory.Seeding, 1.0, "/external",
                                 [ContainmentFileName(1), "readme.txt"]);

        await harness.ReconcileAsync();

        // 纯落选墓碑不构成远端清理授权：外部种子原样保留，绝不发出删除请求（更不允许连文件删除）。
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Contains(loserHash, harness.Store.Torrents.Keys);
        Assert.Equal("/external", harness.Store.Torrents[loserHash].SavePath);
        // 现任资源与墓碑终止记录不受影响。
        Assert.Contains(incumbent.InfoHash, harness.Store.Torrents.Keys);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Single(await db.DownloadTasks.AsNoTracking()
                              .Where(task => task.Status           == DownloadTaskStatus.Replaced &&
                                             task.ReplacedByTaskId == incumbentId)
                              .ToListAsync());
    }

    [Fact]
    public async Task Cleanup_UnsubmittedReplacedRecord_NeverDeletesSameRootExternalTorrent()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        // 待提交任务即被替代（合法持久化历史）：从未发过提交请求，没有任何生命周期证据。
        var bytes  = ContainmentBatch(ContainmentFileName(1));
        var taskId = await harness.CreateUploadAsync(bytes);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        var hash = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(row => row.Id == taskId);
            tracked.Status           = DownloadTaskStatus.Replaced;
            tracked.ReplacedByTaskId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        // 同 hash 外部种子恰位于同一规划根：根一致也不能代替生命周期证据。
        harness.Store.AddTorrent(hash, "external", QbittorrentTorrentCategory.Seeding, 1.0, "/downloads",
                                 [ContainmentFileName(1)]);

        await harness.ReconcileAsync();

        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Contains(hash, harness.Store.Torrents.Keys);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Cleanup_ManagedTorrentRootMovedExternally_ConservativelyRetained()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var taskId = await harness.CreateUploadAsync(ContainmentBatch(ContainmentFileName(1)));
        await harness.ReconcileAsync(); // 真实提交：AttemptCount/SubmittedAtUtc 证据落库，种子受管。
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);

        // 受管种子被外部移动到别的根：实际根与任务确认根不再一致。
        harness.Store.Torrents[task.InfoHash] =
            harness.Store.Torrents[task.InfoHash] with { SavePath = "/moved-elsewhere" };
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tracked = await db.DownloadTasks.SingleAsync(row => row.Id == taskId);
            tracked.Status           = DownloadTaskStatus.Replaced;
            tracked.ReplacedByTaskId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();

        // 有提交证据但根不一致：同样保守保留，绝不按 hash 删除外部移动后的受管种子。
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.Equal("/moved-elsewhere", harness.Store.Torrents[task.InfoHash].SavePath);
    }

    [Theory]
    [InlineData(true)]  // 先删第 1 集承接方：旧包职责提前回退，ReplacedByTaskId 仍指第 2 集优胜。
    [InlineData(false)] // 先删第 2 集承接方：ReplacedByTaskId 所指任务即被删。
    public async Task Delete_CompletedBatchDutiesRestored_AfterWinnerDeletionInAnyOrder(bool deleteEp1WinnerFirst)
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await CompleteContainmentIncumbentAsync(harness, ContainmentFileName(1, "繁日双语"),
                                                            ContainmentFileName(2, "繁日双语"));
        var old = await harness.GetTaskAsync(oldId);

        var ep1WinnerId = await harness.CreateUploadAsync(LargerVariantTorrent(1, "简日双语"));
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(ep1WinnerId)).InfoHash);
        await harness.ReconcileAsync();

        var ep2WinnerId = await harness.CreateUploadAsync(LargerVariantTorrent(2, "简日双语"));
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(ep2WinnerId)).InfoHash);
        await harness.ReconcileAsync();

        // 旧包职责耗尽翻转为被替换，冗余清理环保留内容移除种子。
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(oldId)).Status);
        await harness.ReconcileAsync();
        Assert.DoesNotContain(old.InfoHash, harness.Store.Torrents.Keys);

        var service = harness.Provider.GetRequiredService<DownloadService>();
        var (deletedId, keptId, deletedEpisode, keptEpisode) = deleteEp1WinnerFirst
            ? (ep1WinnerId, ep2WinnerId, 1, 2)
            : (ep2WinnerId, ep1WinnerId, 2, 1);
        var deleteCallsBefore = harness.Store.DeleteCalls.ToArray();
        await service.DeleteAsync(deletedId, false, false);

        // 旧包重新承担被删承接方的集：确有完成记录即恢复 Completed，过期替换声明清除。
        var restored = await harness.GetTaskAsync(oldId);
        Assert.Equal(DownloadTaskStatus.Completed, restored.Status);
        Assert.Null(restored.ReplacedByTaskId);
        var restoredRow = (await EpisodeRegistryAsync(harness, deletedEpisode))!;
        Assert.Equal(await ContainmentFileIdAsync(harness, oldId, deletedEpisode - 1),
                     restoredRow.IncumbentVideoFileId);
        Assert.Null(restoredRow.SupersededVideoFileId);

        // 仍在使用的另一承接方不受影响；本次记录式删除不新增任何下载器删除请求。
        var kept = await harness.GetTaskAsync(keptId);
        Assert.Equal(DownloadTaskStatus.Completed, kept.Status);
        Assert.Null(kept.ReplacedByTaskId);
        Assert.Contains(kept.InfoHash, harness.Store.Torrents.Keys);
        Assert.Equal(await ContainmentFileIdAsync(harness, keptId, 0),
                     (await EpisodeRegistryAsync(harness, keptEpisode))!.IncumbentVideoFileId);
        Assert.Equal(deleteCallsBefore, harness.Store.DeleteCalls);
    }
}
