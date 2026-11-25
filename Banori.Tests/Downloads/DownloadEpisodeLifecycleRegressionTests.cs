using Banned.AniParser.Models.Enums;
using Banori.Core.Rules.Selection;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeVersionGuard_SameGroupSameLanguageVersionUpgrade_SwitchesAcrossSources(bool crossSource)
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

        // 同组同语言 v3 挑战者：无论同源还是跨源（u2）都构成版本升级，挂 Pending 并在完成后顶替旧版。
        var v3       = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v3]");
        var announce = crossSource ? "https://daydream.dmhy.best/announce" : "https://tracker.byr.pt/announce.php";
        var nextId   = await h.CreateUploadAsync(TestTorrentBytes.Create(v3, announce));
        var nextFile = await FindAnimeFileIdAsync(h, nextId);

        var pending = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.Equal(oldFile, pending!.IncumbentVideoFileId);
        Assert.Equal(nextFile, pending.PendingVideoFileId);
        if (crossSource)
        {
            var u2Pending = await FindSourceEpisodeVersionAsync(h, 12, "u2");
            Assert.NotNull(u2Pending);
            Assert.Equal(nextFile, u2Pending.PendingVideoFileId);
        }

        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(nextId)).InfoHash);
        await h.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(nextId)).Status);
        var switched = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.Equal(nextFile, switched!.IncumbentVideoFileId);
        Assert.Equal(oldFile, switched.SupersededVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(oldId)).Status);
        if (crossSource)
        {
            var u2Row = await FindSourceEpisodeVersionAsync(h, 12, "u2");
            Assert.NotNull(u2Row);
            Assert.Equal(nextFile, u2Row.IncumbentVideoFileId);
            Assert.Null(u2Row.PendingVideoFileId);
        }
    }

    [Fact]
    public async Task EpisodeVersionGuard_TwoSourcesSameGroupLanguageVersion_CoexistAndUpgradeTogether()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        async Task<Guid> CompleteAsync(byte[] torrentBytes)
        {
            var taskId = await h.CreateUploadAsync(torrentBytes);
            await h.ReconcileAsync();
            CompleteTorrent(h, (await h.GetTaskAsync(taskId)).InfoHash);
            await h.ReconcileAsync();
            return taskId;
        }

        // 两个来源同组同语言同版本（不同封装/info-hash）允许共存并各自成行。
        var byrbtV1 = await CompleteAsync(TestTorrentBytes.Create(NekoTitle("简日双语", episode : 12),
                                                                  "https://tracker.byr.pt/announce.php"));
        var u2V1 = await CompleteAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12).Replace(".mp4", ".mkv", StringComparison.Ordinal),
            "https://daydream.dmhy.best/announce"));
        var byrbtV1File = await FindAnimeFileIdAsync(h, byrbtV1);
        var u2V1File    = await FindAnimeFileIdAsync(h, u2V1);
        Assert.Equal(byrbtV1File, (await FindSourceEpisodeVersionAsync(h, 12, "byrbt"))!.IncumbentVideoFileId);
        Assert.Equal(u2V1File, (await FindSourceEpisodeVersionAsync(h, 12, "u2"))!.IncumbentVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(byrbtV1)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(u2V1)).Status);

        // 新来同组同语言 v2：同时顶替 byrbt 与 u2 的旧 v1，完成后两者一并退场。
        var byrbtV2 = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v2]"),
            "https://tracker.byr.pt/announce.php"));
        var byrbtV2File = await FindAnimeFileIdAsync(h, byrbtV2);
        await h.ReconcileAsync();

        CompleteTorrent(h, (await h.GetTaskAsync(byrbtV2)).InfoHash);
        await h.ReconcileAsync();
        var byrbtSwitched = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.Equal(byrbtV2File, byrbtSwitched!.IncumbentVideoFileId);
        Assert.Equal(byrbtV1File, byrbtSwitched.SupersededVideoFileId);
        Assert.Null(byrbtSwitched.PendingVideoFileId);
        var u2Switched = await FindSourceEpisodeVersionAsync(h, 12, "u2");
        Assert.Equal(byrbtV2File, u2Switched!.IncumbentVideoFileId);
        Assert.Equal(u2V1File, u2Switched.SupersededVideoFileId);
        Assert.Null(u2Switched.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(byrbtV1)).Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(u2V1)).Status);
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(byrbtV2)).Status);
    }

    [Fact]
    public async Task EpisodeVersionGuard_DeleteOneSourceTask_DoesNotClearOtherSourceRow()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());

        // 同组同语言同版本跨源共存，删除其中一源任务不误清另一源的现任与职责。
        var byrbtId = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12), "https://tracker.byr.pt/announce.php"));
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(byrbtId)).InfoHash);
        await h.ReconcileAsync();

        var u2Id = await h.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语", episode : 12).Replace(".mp4", ".mkv", StringComparison.Ordinal),
            "https://daydream.dmhy.best/announce"));
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
    public async Task EpisodeVersionGuard_ReuseVerificationEarlySwitch_SwitchesRegisteredCrossSourcePending()
    {
        using var h = await CreateHarnessAsync(enableFileArchive : true);
        await h.SaveSelectionRulesAsync(NekoRules());
        await h.SaveFileNamingTemplateAsync("{title} E{episode}");

        // byrbt 第 12 集单集完成并归档到正式目标；u2 合集包（E12+E13）跨源胜出并复用 E12。
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

        // 登记期已自动把 u2File 挂为 byrbt 与 u2 两行的 PendingVideoFileId。
        Assert.Equal(u2File, (await FindSourceEpisodeVersionAsync(h, 12, "byrbt"))!.PendingVideoFileId);
        Assert.Equal(u2File, (await FindSourceEpisodeVersionAsync(h, 12, "u2"))!.PendingVideoFileId);

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
        await h.ReconcileAsync(); // 结论轮：候选通过 → 提前切换 u2 与被顶替的 byrbt 行，旧单集退场。

        var switched = await FindSourceEpisodeVersionAsync(h, 12, "u2");
        Assert.NotNull(switched);
        Assert.Equal(u2File, switched.IncumbentVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
        var retired = await FindSourceEpisodeVersionAsync(h, 12, "byrbt");
        Assert.NotNull(retired);
        Assert.Equal(u2File, retired.IncumbentVideoFileId);
        Assert.Null(retired.PendingVideoFileId);
        Assert.Equal(byrbtFile, retired.SupersededVideoFileId);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(byrbtId)).Status);
        // 第 13 集尚未下载完：提前切换不是整包完成，任务仍在下载。
        Assert.Equal(DownloadTaskStatus.Downloading, (await h.GetTaskAsync(u2Id)).Status);
    }

    [Fact]
    public async Task CrossSourcePriority_OtherLowerGroupReplacedByMikanHigherGroup_AndReverseRejected()
    {
        using var h = await CreateHarnessAsync(enableFileArchive : true);
        await h.SaveSelectionRulesAsync(new SelectionRuleConfig
        {
            Mode               = SelectionMode.Independent,
            GroupOrder         = ["喵萌奶茶屋", "ANi"],
            LanguageOrder      = [EnumLanguage.JpSc, EnumLanguage.Sc],
            DimensionOrder     = SelectionDimensionOrder.GroupFirst,
            CombinedEntries    = [],
            LanguageMatch      = LanguageMatchMode.Exact,
            Unmatched          = UnmatchedPolicy.NeedsReview,
            AllowedResolutions = [EnumResolution.R1080P]
        });

        // 1. other 先上传低优字幕组（ANi）并完成。
        var aniTitle = "[ANi] 测试作品 - 12 [1080P][Baha][WEB-DL][AAC AVC][CHT].mp4";
        var otherId  = await h.CreateUploadAsync(TestTorrentBytes.Create(aniTitle), subjectId : 42);
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(otherId)).InfoHash);
        await h.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(otherId)).Status);

        // 2. mikan 来了高优字幕组（喵萌奶茶屋）：跨源顶替 other，挂 Pending、走临时目录并在完成后退掉 other。
        var nekoTitle = NekoTitle("简日双语", episode : 12);
        h.TorrentNames["mikan-neko-12"] = nekoTitle;
        h.FeedReader.Items.Add(RssItem("mikan-neko-12", nekoTitle, "https://mikanani.me/Download/mikan-neko-12.torrent"));
        var rssResult = await h.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi", subjectId : 42);
        Assert.Equal(1, rssResult.CreatedCount);
        var mikanId   = rssResult.Items.Single().TaskId!.Value;
        var mikanFile = await FindAnimeFileIdAsync(h, mikanId);

        Assert.Equal(mikanFile, (await FindSourceEpisodeVersionAsync(h, 12, SourceScopes.Other))!.PendingVideoFileId);
        await h.ReconcileAsync();
        Assert.Contains(".tasks/", (await h.GetTaskAsync(mikanId)).SavePath, StringComparison.Ordinal);

        CompleteTorrent(h, (await h.GetTaskAsync(mikanId)).InfoHash);
        await h.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(mikanId)).Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(otherId)).Status);

        // 3. 反向：已有高优 mikan 时，再上传低优 other（ANi）应直接落选拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.CreateUploadAsync(TestTorrentBytes.Create(aniTitle, length : 2048), subjectId : 42));
    }

    [Fact]
    public async Task CrossSourcePriority_UnlistedOther_ReplacedByListedMikan_AndCrossGroupVersionIgnored()
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(new SelectionRuleConfig
        {
            Mode               = SelectionMode.Independent,
            GroupOrder         = ["ANi"],
            LanguageOrder      = [EnumLanguage.JpSc],
            DimensionOrder     = SelectionDimensionOrder.GroupFirst,
            CombinedEntries    = [],
            LanguageMatch      = LanguageMatchMode.Exact,
            Unmatched          = UnmatchedPolicy.NeedsReview,
            AllowedResolutions = [EnumResolution.R1080P]
        });

        // 1. 手动上传未在榜字幕组 A（喵萌奶茶屋 v1）作为 other 现任。
        var unlistedA = NekoTitle("简日双语", episode : 12);
        var aId       = await h.CreateUploadAsync(TestTorrentBytes.Create(unlistedA), subjectId : 42);
        var aTask     = await h.GetTaskAsync(aId);
        Assert.Equal(12m, aTask.Episode);
        await h.ReconcileAsync();
        CompleteTorrent(h, aTask.InfoHash);
        await h.ReconcileAsync();

        // 2. 跨来源（u2）来了未在榜字幕组 B（漫猫字幕组 v2）：版本只在同组同语言有效，跨组无效且都不在榜 → 先到先得，B 落选。
        var unlistedBV2 = "[Comicat][test anime][12v2][1080p][简日双语][mp4].mp4";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.CreateUploadAsync(TestTorrentBytes.Create(unlistedBV2, "https://daydream.dmhy.best/announce"),
                                subjectId : 42));
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(aId)).Status);

        // 3. 即使规则是 NeedsReview，mikan 来了在榜字幕组（ANi）仍应跨源顶替未在榜的 other。
        var listedTitle = "[ANi] 测试作品 - 12 [1080P][Baha][WEB-DL][AAC AVC][CHT].mp4";
        h.TorrentNames["mikan-listed-12"] = listedTitle;
        h.FeedReader.Items.Add(RssItem("mikan-listed-12", listedTitle,
                                       "https://mikanani.me/Download/mikan-listed-12.torrent"));
        var rssResult = await h.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi", subjectId : 42);
        Assert.Equal(1, rssResult.CreatedCount);
        var mikanId = rssResult.Items.Single().TaskId!.Value;
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(mikanId)).InfoHash);
        await h.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(mikanId)).Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await h.GetTaskAsync(aId)).Status);
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
