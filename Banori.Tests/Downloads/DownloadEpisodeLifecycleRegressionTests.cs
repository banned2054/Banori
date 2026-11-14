using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeVersionGuard_SameSourceVersionSwitches_CrossSourceDoesNot(bool crossSource)
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        // byrbt 第 12 集 v2 先完成并由完成链写成 byrbt 来源的现任行。
        var oldName = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v2]");
        var oldId   = await h.CreateUploadAsync(
            TestTorrentBytes.Create(oldName, "https://tracker.byr.pt/announce.php"));
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(oldId)).InfoHash);
        await h.ReconcileAsync();
        var oldFile = await FindAnimeFileIdAsync(h, oldId);
        var initial = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.NotNull(initial);
        Assert.Equal(oldFile, initial.IncumbentVideoFileId);

        // v3 挑战者只有来源不同：同源走版本对决，跨源（u2）互不竞争。
        var v3       = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v3]");
        var announce = crossSource ? "https://daydream.dmhy.best/announce" : "https://tracker.byr.pt/announce.php";
        var nextId   = await h.CreateUploadAsync(TestTorrentBytes.Create(v3, announce));
        var nextFile = await FindAnimeFileIdAsync(h, nextId);

        if (crossSource)
        {
            // 跨源不写 byrbt 的 Pending，也不登记 u2 的现任；旧任务不被跨源版本守卫顶替。
            Assert.Null((await FindSourceEpisodeVersionAsync(h, 12, "byrbt"))!.PendingVideoFileId);
            Assert.Null(await FindSourceEpisodeVersionAsync(h, 12, "u2"));
        }
        else
        {
            var pending = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
            Assert.Equal(oldFile, pending!.IncumbentVideoFileId);
            Assert.Equal(nextFile, pending.PendingVideoFileId);
        }

        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(nextId)).InfoHash);
        await h.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(nextId)).Status);
        var switched = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        if (crossSource)
        {
            // 跨源完成只写自己的 u2 行：byrbt 现任、任务状态与承接来源都不受影响。
            Assert.Equal(oldFile, switched!.IncumbentVideoFileId);
            Assert.Null(switched.PendingVideoFileId);
            Assert.Null(switched.SupersededVideoFileId);
            Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(oldId)).Status);
            var u2Row = await FindSourceEpisodeVersionAsync(h, 12, "u2");
            Assert.NotNull(u2Row);
            Assert.Equal(nextFile, u2Row.IncumbentVideoFileId);
            Assert.Null(u2Row.SupersededVideoFileId);
        }
        else
        {
            // 同源：登记在案的待完成优胜完成后切换现任，旧现任保留为承接来源，旧任务职责耗尽退场。
            Assert.Equal(nextFile, switched!.IncumbentVideoFileId);
            Assert.Equal(oldFile, switched.SupersededVideoFileId);
            Assert.Null(switched.PendingVideoFileId);
            Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(oldId)).Status);
        }
    }

    [Fact]
    public async Task EpisodeVersionGuard_TwoSourcesCompleteAndSwitchIndependently()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        async Task<Guid> CompleteAsync(string name, string announce)
        {
            var taskId = await h.CreateUploadAsync(TestTorrentBytes.Create(name, announce));
            await h.ReconcileAsync();
            CompleteTorrent(h, (await h.GetTaskAsync(taskId)).InfoHash);
            await h.ReconcileAsync();
            return taskId;
        }

        // 两个来源各有一份第 12 集任务并完成：各自成行，互不覆盖。
        // 文件名语言不同保证两个任务的 info-hash 不同；命名模板只取标题与集数，归档目标仍同集。
        var byrbtV1 = await CompleteAsync(NekoTitle("简日双语", episode : 12),
                                          "https://tracker.byr.pt/announce.php");
        var u2V1 = await CompleteAsync(NekoTitle("繁日双语", episode : 12),
                                       "https://daydream.dmhy.best/announce");
        var byrbtV1File = await FindAnimeFileIdAsync(h, byrbtV1);
        var u2V1File    = await FindAnimeFileIdAsync(h, u2V1);
        Assert.Equal(byrbtV1File, (await FindSourceEpisodeVersionAsync(h, 12, "byrbt"))!.IncumbentVideoFileId);
        Assert.Equal(u2V1File, (await FindSourceEpisodeVersionAsync(h, 12, "u2"))!.IncumbentVideoFileId);

        // 两个来源再各登记一份 v2 并完成：同源版本切换只改自己的行。
        var byrbtV2 = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v2]"),
            "https://tracker.byr.pt/announce.php"));
        var u2V2 = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("繁日双语", episode : 12).Replace("[12]", "[12v2]"),
            "https://daydream.dmhy.best/announce"));
        var byrbtV2File = await FindAnimeFileIdAsync(h, byrbtV2);
        var u2V2File    = await FindAnimeFileIdAsync(h, u2V2);
        await h.ReconcileAsync();

        CompleteTorrent(h, (await h.GetTaskAsync(byrbtV2)).InfoHash);
        await h.ReconcileAsync();
        var byrbtSwitched = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.Equal(byrbtV2File, byrbtSwitched!.IncumbentVideoFileId);
        Assert.Equal(byrbtV1File, byrbtSwitched.SupersededVideoFileId);
        Assert.Null(byrbtSwitched.PendingVideoFileId);
        var u2Pending = await FindSourceEpisodeVersionAsync(h, 12, "u2");
        Assert.Equal(u2V1File, u2Pending!.IncumbentVideoFileId);
        Assert.Equal(u2V2File, u2Pending.PendingVideoFileId);
        Assert.Null(u2Pending.SupersededVideoFileId);

        CompleteTorrent(h, (await h.GetTaskAsync(u2V2)).InfoHash);
        await h.ReconcileAsync();
        var u2Switched = await FindSourceEpisodeVersionAsync(h, 12, "u2");
        Assert.Equal(u2V2File, u2Switched!.IncumbentVideoFileId);
        Assert.Equal(u2V1File, u2Switched.SupersededVideoFileId);
        Assert.Null(u2Switched.PendingVideoFileId);
        var byrbtFinal = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.Equal(byrbtV2File, byrbtFinal!.IncumbentVideoFileId);
        Assert.Equal(byrbtV1File, byrbtFinal.SupersededVideoFileId);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(byrbtV1)).Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(u2V1)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(byrbtV2)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(u2V2)).Status);
    }

    [Fact]
    public async Task EpisodeVersionGuard_DeleteOneSourceTask_DoesNotClearOtherSourceRow()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        var byrbtId = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12), "https://tracker.byr.pt/announce.php"));
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(byrbtId)).InfoHash);
        await h.ReconcileAsync();

        var u2Id = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("繁日双语", episode : 12), "https://daydream.dmhy.best/announce"));
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(u2Id)).InfoHash);
        await h.ReconcileAsync();
        var u2File = await FindAnimeFileIdAsync(h, u2Id);
        Assert.Equal(u2File, (await FindSourceEpisodeVersionAsync(h, 12, "u2"))!.IncumbentVideoFileId);

        // 删除 byrbt 任务：指针按文件 Id 全局清理只影响 byrbt 行，不得误清 u2 的现任与职责。
        await h.Service.DeleteAsync(byrbtId, false, false);

        var byrbtRow = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.NotNull(byrbtRow);
        Assert.Null(byrbtRow.IncumbentVideoFileId);
        Assert.Null(byrbtRow.PendingVideoFileId);
        Assert.Null(byrbtRow.SupersededVideoFileId);
        var u2Row = await FindSourceEpisodeVersionAsync(h, 12, "u2");
        Assert.NotNull(u2Row);
        Assert.Equal(u2File, u2Row.IncumbentVideoFileId);
        Assert.Null(u2Row.PendingVideoFileId);
        Assert.Null(u2Row.SupersededVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(u2Id)).Status);
    }

    [Fact]
    public async Task EpisodeVersionGuard_ReuseVerificationEarlySwitch_OnlySameSourcePending()
    {
        using var h = await CreateHarnessAsync(enableFileArchive : true);
        await h.SaveSelectionRulesAsync(NekoRules());
        await h.SaveFileNamingTemplateAsync("{title} E{episode}");

        // byrbt 第 12 集完成并归档到正式目标；u2 新包同名同大小，构成跨源复用候选。
        var byrbtId = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12), "https://tracker.byr.pt/announce.php"));
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(byrbtId)).InfoHash);
        await h.ReconcileAsync();
        var byrbtFile = await FindAnimeFileIdAsync(h, byrbtId);

        var u2Id = await h.CreateUploadAsync(BatchTorrent(
            [NekoTitle("简日双语", episode : 12), NekoTitle("简日双语", episode : 13)],
            "https://daydream.dmhy.best/announce"));
        var u2File = await FindAnimeFileIdAsync(h, u2Id);
        Assert.NotNull(u2File);

        // 模拟旧单行语义可能留下的跨源污染：byrbt 行的 Pending 指向本任务文件；
        // 同时给 u2 自己的登记行一个待完成优胜。提前切换应只处理同源（u2）行。
        await using (var db = await h.CreateDbContextAsync())
        {
            var byrbtRow = await db.EpisodeVersions.SingleAsync(row => row.SubjectId   == 42 &&
                                                                       row.Episode     == 12 &&
                                                                       row.SourceScope == "byrbt");
            byrbtRow.PendingVideoFileId = u2File;
            db.EpisodeVersions.Add(new EpisodeVersion
            {
                SubjectId = 42, Episode = 12, SourceScope = "u2", PendingVideoFileId = u2File
            });
            await db.SaveChangesAsync();
        }

        // 复用校验链：候选根停止添加 → 受控映射改名 onto byrbt 成片 → recheck 证实同内容。
        h.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await h.ReconcileAsync(); // 提交（候选根）。
        await h.ReconcileAsync(); // 映射 + recheck（进入 Checking）。
        await h.ReconcileAsync(); // 观察位。
        var u2Hash = (await h.GetTaskAsync(u2Id)).InfoHash;
        h.Store.FileIdentities[u2Hash] = h.Store.FileIdentities[u2Hash]
                                          .Select(file => file with { Progress = file.Index == 0 ? 1.0 : 0.0 })
                                          .ToList();
        h.Store.FinishRecheck(u2Hash);
        await h.ReconcileAsync(); // 结论轮：候选通过 → 只提前切换 u2 行。

        var switched = await FindSourceEpisodeVersionAsync(h, 12, "u2");
        Assert.NotNull(switched);
        Assert.Equal(u2File, switched.IncumbentVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
        var untouched = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.NotNull(untouched);
        Assert.Equal(byrbtFile, untouched.IncumbentVideoFileId);
        Assert.Equal(u2File, untouched.PendingVideoFileId);
        Assert.Null(untouched.SupersededVideoFileId);
        // 第 13 集尚未下载完：提前切换不是整包完成，任务仍在下载。
        Assert.Equal(DownloadTaskStatus.Downloading, (await h.GetTaskAsync(u2Id)).Status);
    }

    [Fact]
    public async Task EpisodeVersionGuard_CrossGroupLargerBatch_ReplacesUnparseableSingle()
    {
        // 旧预期是“跨组不接管”：不可解析现任的 task-level 集数/版本回退让它参与竞争，但跨组 2 集批
        // 不得顶替。用户平级合集规则取代了这一点：同优先级层内版本不跨组/跨语言比较，初始正片数
        // 2 > 1 的合集应替换不可解析单集；前者仍是本集的有效现任，登记期写入待完成优胜。
        using var harness     = await CreateHarnessAsync();
        var       incumbentId = await harness.CreateUploadAsync(BatchTorrent("feature.mkv", "feature.ass"), "mikan");
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.Include(value => value.Files)
                               .SingleAsync(value => value.Id == incumbentId);
            task.Episode = 12;
            task.Version = 2;
            foreach (var file in task.Files)
            {
                file.Kind                  = file.FileIndex == 0 ? DownloadFileKind.Anime : DownloadFileKind.Subtitle;
                file.EpisodeNumberOverride = 12;
                file.IsManualOverride      = true;
            }

            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(incumbentId)).InfoHash);
        await harness.ReconcileAsync();
        var incumbentFile = await FindAnimeFileIdAsync(harness, incumbentId);

        var challengerId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                                        NekoTitle("简日双语", episode : 13)), "byrbt");
        var challengerFile = await FindAnimeFileIdAsync(harness, challengerId);
        var registered     = await FindSourceEpisodeVersionAsync(harness, 12, "other");
        Assert.NotNull(registered);
        // 前提：现任虽无法从文件名解析，task-level Episode=12/Version=2 仍使它作为本集现任参与裁决。
        Assert.Equal(incumbentFile, registered.IncumbentVideoFileId);
        // 同层跨组/跨语言不比较版本，按初始正片数 2 > 1 由合集胜出并登记待完成优胜。
        Assert.Equal(challengerFile, registered.PendingVideoFileId);

        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(challengerId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(challengerId)).Status);
        var switched = await FindSourceEpisodeVersionAsync(harness, 12, "other");
        Assert.NotNull(switched);
        Assert.Equal(challengerFile, switched.IncumbentVideoFileId);
        Assert.Equal(incumbentFile, switched.SupersededVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
    }

    [Fact]
    public async Task EpisodeVersionGuard_CrossGroupHigherVersion_DoesNotTakeOver()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        // 同来源已完成现任：喵萌奶茶屋、简日、v1。
        var incumbentId = await CompleteSubscriptionEpisodeAsync(
            h, "guard-group-incumbent", NekoTitle("简日双语"), 1, "JpSc", "喵萌奶茶屋");
        var incumbentFile = await FindAnimeFileIdAsync(h, incumbentId);

        // 另一字幕组的 v2 挑战者：版本更高也不能凭版本顶替现任；登记期不写待完成优胜。
        // 该发布名格式由 AniParser 解析为漫猫字幕组、简日（与现任同语言），只比较字幕组差异。
        var challengerName = "[Comicat][test anime][12v2][1080p][简日双语][mp4]";
        var challengerItemId = await SeedSubscriptionEpisodeAsync(
            h, "guard-group-challenger", challengerName, 2, "JpSc", "漫猫字幕组");
        var challengerId = await h.CreateDownloadAsync(challengerItemId);
        var challengerFile = await FindAnimeFileIdAsync(h, challengerId);

        var registered = await FindSourceEpisodeVersionAsync(h, 12, "mikan");
        Assert.NotNull(registered);
        Assert.Equal(incumbentFile, registered.IncumbentVideoFileId);
        Assert.Null(registered.PendingVideoFileId);

        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(challengerId)).InfoHash);
        await h.ReconcileAsync();

        var after = await FindSourceEpisodeVersionAsync(h, 12, "mikan");
        Assert.NotNull(after);
        Assert.Equal(incumbentFile, after.IncumbentVideoFileId);
        Assert.Null(after.SupersededVideoFileId);
        Assert.Null(after.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(incumbentId)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(challengerId)).Status);
    }

    [Fact]
    public async Task EpisodeVersionGuard_SameGroupDifferentLanguageHigherVersion_DoesNotTakeOver()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        // 同来源同组现任：喵萌奶茶屋、简日、v1。
        var incumbentId = await CompleteSubscriptionEpisodeAsync(
            h, "guard-language-incumbent", NekoTitle("简日双语"), 1, "JpSc", "喵萌奶茶屋");
        var incumbentFile = await FindAnimeFileIdAsync(h, incumbentId);

        // 同组但繁日的 v2 挑战者：语言不同，版本更高也不得顶替。
        var challengerName = NekoTitle("繁日双语").Replace("[12]", "[12v2]");
        var challengerItemId = await SeedSubscriptionEpisodeAsync(
            h, "guard-language-challenger", challengerName, 2, "JpTc", "喵萌奶茶屋");
        var challengerId = await h.CreateDownloadAsync(challengerItemId);
        var challengerFile = await FindAnimeFileIdAsync(h, challengerId);

        var registered = await FindSourceEpisodeVersionAsync(h, 12, "mikan");
        Assert.NotNull(registered);
        Assert.Equal(incumbentFile, registered.IncumbentVideoFileId);
        Assert.Null(registered.PendingVideoFileId);

        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(challengerId)).InfoHash);
        await h.ReconcileAsync();

        var after = await FindSourceEpisodeVersionAsync(h, 12, "mikan");
        Assert.NotNull(after);
        Assert.Equal(incumbentFile, after.IncumbentVideoFileId);
        Assert.Null(after.SupersededVideoFileId);
        Assert.Null(after.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(incumbentId)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(challengerId)).Status);
    }

    [Fact]
    public async Task EpisodeVersionGuard_SameGroupSameLanguageHigherVersion_TakesOver()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        var incumbentId = await CompleteSubscriptionEpisodeAsync(
            h, "guard-version-incumbent", NekoTitle("简日双语"), 1, "JpSc", "喵萌奶茶屋");
        var incumbentFile = await FindAnimeFileIdAsync(h, incumbentId);

        // 同组同语言的 v2：登记期待完成优胜，完成后正常切换现任。
        var challengerName = NekoTitle("简日双语").Replace("[12]", "[12v2]");
        var challengerItemId = await SeedSubscriptionEpisodeAsync(
            h, "guard-version-challenger", challengerName, 2, "JpSc", "喵萌奶茶屋");
        var challengerId = await h.CreateDownloadAsync(challengerItemId);
        var challengerFile = await FindAnimeFileIdAsync(h, challengerId);

        var registered = await FindSourceEpisodeVersionAsync(h, 12, "mikan");
        Assert.NotNull(registered);
        Assert.Equal(incumbentFile, registered.IncumbentVideoFileId);
        Assert.Equal(challengerFile, registered.PendingVideoFileId);

        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(challengerId)).InfoHash);
        await h.ReconcileAsync();

        var after = await FindSourceEpisodeVersionAsync(h, 12, "mikan");
        Assert.NotNull(after);
        Assert.Equal(challengerFile, after.IncumbentVideoFileId);
        Assert.Equal(incumbentFile, after.SupersededVideoFileId);
        Assert.Null(after.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(incumbentId)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(challengerId)).Status);
    }

    [Fact]
    public async Task DeleteIntermediateEpisodeWinner_TransfersAndThenUnwindsPerEpisodeChain()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());
        var a  = await h.CreateUploadAsync(BatchTorrent(NekoTitle("繁日双语"), NekoTitle("繁日双语", episode : 13)), "mikan");
        var b  = await h.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语"), NekoTitle("简日双语", episode : 14)), "mikan");
        var v2 = NekoTitle("简日双语").Replace("[12]", "[12v2]");
        var c  = await h.CreateUploadAsync(BatchTorrent(v2, NekoTitle("简日双语", episode : 15)), "mikan");

        await h.Service.DeleteAsync(b, false, false);
        await using (var db = await h.CreateDbContextAsync())
        {
            var a12 = await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == a && file.FileIndex == 0);
            var registry = await db.EpisodeVersions.SingleAsync(row => row.SubjectId == 42 && row.Episode == 12);
            Assert.False(a12.IsSelectedForDownload);
            Assert.Equal(c, a12.SupersededByTaskId);
            Assert.Equal(await FindAnimeFileIdAsync(h, c), registry.PendingVideoFileId);
            Assert.False(await db.DownloadTaskFiles.AnyAsync(file => file.DownloadTaskId == b && file.FileIndex == 1));
        }

        await h.Service.DeleteAsync(c, false, false);
        await using (var db = await h.CreateDbContextAsync())
        {
            var a12 = await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == a && file.FileIndex == 0);
            Assert.True(a12.IsSelectedForDownload);
            Assert.Null(a12.SupersededByTaskId);
            Assert.Null(await db.DownloadTaskFiles.Where(file => file.SupersededByTaskId              != null)
                                .Select(file => file.SupersededByTaskId).FirstOrDefaultAsync(id => id == c));
        }
    }

    /// <summary>订阅条目第 12 集：按发布名与快照字段播种，返回条目 Id（尚未创建任务）。</summary>
    private static Task<Guid> SeedSubscriptionEpisodeAsync(
        TestHarness harness, string stableId, string torrentName, int version, string language, string group) =>
        SeedAcceptedItemAsync(harness, stableId, torrentName, version > 1 ? NamingV2 : NamingV1, version, 12m,
                              language : language, preference : group);

    /// <summary>登记并完成一条订阅条目第 12 集任务：返回任务 Id，完成链已写入每集登记表。</summary>
    private static async Task<Guid> CompleteSubscriptionEpisodeAsync(
        TestHarness harness, string stableId, string torrentName, int version, string language, string group)
    {
        var itemId = await SeedSubscriptionEpisodeAsync(harness, stableId, torrentName, version, language, group);
        var taskId = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(taskId)).InfoHash);
        await harness.ReconcileAsync();
        return taskId;
    }
}
