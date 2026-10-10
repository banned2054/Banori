using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banned.Qbittorrent.Exceptions;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Torrents;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Downloads;

/// <summary>
///     P3 下载闭环验收：响应丢失、提交后重启、重复触发、人工暂停与安全替换。
///     全部使用假下载器会话与真实 SQLite 临时库，不触发真实下载。
/// </summary>
public sealed class DownloadServiceTests
{
    private const string NamingV1 = "测试作品 E01 [Sc]";
    private const string NamingV2 = "测试作品 E01 v2 [Sc]";

    /// <summary>喵萌奶茶屋式发布名的解析后系列标题（与命名快照同口径）。</summary>
    private const string NekoSeriesTitle =
        "与你相恋到生命尽头 きみが死ぬまで恋をしたい Kimi ga Shinu made Koi wo Shitai";

    [Fact]
    public async Task Create_PersistsIntentWithHashAndBlocksActiveDuplicates()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");

        var taskId = await harness.CreateDownloadAsync(itemId);

        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(DownloadTaskStatus.PendingSubmit, task.Status);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Test Anime - 01"), task.InfoHash);
        Assert.Equal("/downloads/测试作品/" + $"{NamingV1}-{task.InfoHash[..8]}", task.SavePath);
        Assert.Equal(NamingV1, task.NamingSnapshot);
        Assert.Equal(1m, task.Episode);

        // 同一条目重复点击被拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));
        // 不同条目指向同一资源（同 hash）同样被拒绝。
        var sameHashItemId = await SeedAcceptedItemAsync(harness, "item-1-dup", "Test Anime - 01");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(sameHashItemId));
        // 被替换的终止记录同样挡住重复登记（其存在即查重依据），删除记录后才可重新创建。
        var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        entity.Status = DownloadTaskStatus.Replaced;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));
        await harness.Service.DeleteAsync(taskId, false, false);
        var recreatedTaskId = await harness.CreateDownloadAsync(itemId);
        Assert.NotEqual(taskId, recreatedTaskId);
    }

    [Fact]
    public async Task CreateUpload_PersistsTaskWithoutFeedItemAndSubmitsThroughExistingPipeline()
    {
        using var harness = await CreateHarnessAsync();
        var       bytes   = TestTorrentBytes.Create("Uploaded Anime - 01");

        var taskId = await harness.CreateUploadAsync(bytes);

        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(DownloadTaskStatus.PendingSubmit, task.Status);
        Assert.Null(task.FeedItemId);
        // 上传标题无法解析、经表单 ID 绑定预置 Subject；不解析集数不落命名要素。
        Assert.Equal(42, task.SubjectId);
        Assert.Null(task.Episode);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Uploaded Anime - 01"), task.InfoHash);
        Assert.Equal("upload://" + task.InfoHash, task.TorrentUrl);
        Assert.Equal("Uploaded Anime - 01", task.NamingSnapshot);
        Assert.Equal("/downloads/Uploaded Anime - 01-" + task.InfoHash[..8], task.SavePath);

        // 沿用既有提交管线：一轮对账后进入下载中，不因缺少条目来源而绕过隔离路径。
        await harness.ReconcileAsync();
        var submitted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, submitted.Status);
        var add = Assert.Single(harness.Store.AddCalls);
        Assert.Equal(task.SavePath, add.SavePath);
        Assert.Empty(add.Tags);
        Assert.Equal("Uploaded Anime - 01", add.Rename);
    }

    [Fact]
    public async Task CreateUpload_WithSiteSource_PersistsSourceAndSubmitsAsTag()
    {
        using var harness = await CreateHarnessAsync();
        var       bytes   = TestTorrentBytes.Create("Uploaded Anime - 01");

        var taskId = await harness.CreateUploadAsync(bytes, "byrbt");

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal("byrbt", task.SiteSource);

        await harness.ReconcileAsync();
        var add = Assert.Single(harness.Store.AddCalls);
        Assert.Equal(["byrbt"], add.Tags);
    }

    [Fact]
    public async Task CreateUpload_DetectsSiteSourceFromTorrentTracker()
    {
        using var harness = await CreateHarnessAsync();

        // 不手填来源：tracker.byr.pt 命中 ByrTorrent 同源映射，自动识别为 byrbt。
        var byrbtBytes = TestTorrentBytes.Create("Byrbt Anime - 01", "https://tracker.byr.pt:6969/announce");
        var byrbtTask  = await harness.CreateUploadAsync(byrbtBytes);
        Assert.Equal("byrbt", (await harness.GetTaskAsync(byrbtTask)).SiteSource);

        // tracker 无法识别时回退手填值；tracker 判定结果优先于手填。
        var unknownBytes = TestTorrentBytes.Create("Unknown Anime - 01");
        var manualTask   = await harness.CreateUploadAsync(unknownBytes, "tjupt");
        Assert.Equal("tjupt", (await harness.GetTaskAsync(manualTask)).SiteSource);

        var knownBytes   = TestTorrentBytes.Create("Known Anime - 01", "udp://daydream.dmhy.best:6969/announce");
        var overrideTask = await harness.CreateUploadAsync(knownBytes, "tjupt");
        Assert.Equal("U2", (await harness.GetTaskAsync(overrideTask)).SiteSource);
    }

    [Fact]
    public async Task CreateUpload_EnforcesHashDeduplicationAcrossStatuses()
    {
        using var harness = await CreateHarnessAsync();
        var       bytes   = TestTorrentBytes.Create("Uploaded Anime - 01");
        var       taskId  = await harness.CreateUploadAsync(bytes);

        // 活动任务存在时同 hash 上传被拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateUploadAsync(bytes));

        // 已完成同样拒绝重新登记（与条目路径一致）。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status = DownloadTaskStatus.Completed;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateUploadAsync(bytes));

        // 被替换的终止记录同样拒绝重复登记；删除记录后可重新上传。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status = DownloadTaskStatus.Replaced;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateUploadAsync(bytes));
        await harness.Service.DeleteAsync(taskId, false, false);
        var recreated = await harness.CreateUploadAsync(bytes);
        Assert.NotEqual(taskId, recreated);
    }

    [Fact]
    public async Task CreateUpload_RejectsUnconfiguredDownloaderAndInvalidTorrentBytes()
    {
        using var unconfigured = await CreateHarnessAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                unconfigured
                                                                   .CreateUploadAsync(TestTorrentBytes
                                                                       .Create("Uploaded Anime - 01")));

        using var configured = await CreateHarnessAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => configured.CreateUploadAsync([0x62, 0x61, 0x64]));
    }

    [Fact]
    public async Task CreateFromRssFeed_CreatesTaskPerItemBoundToProvidedSubject()
    {
        using var harness = await CreateHarnessAsync();
        var       feedUrl = "https://example.com/rss.xml";
        harness.FeedReader.Items.Add(RssItem("rss-1", "RSS 番剧 第 1 集 [1080p]",
                                             "https://example.com/torrents/rss-1.torrent"));
        harness.FeedReader.Items.Add(RssItem("rss-2", "RSS 番剧 第 2 集 [1080p]",
                                             "https://example.com/torrents/rss-2.torrent"));

        var result = await harness.CreateFromRssFeedAsync(feedUrl, subjectId : 42);

        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(0, result.FailedCount);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(RssDownloadItemOutcome.Created, item.Outcome);
            Assert.NotNull(item.TaskId);
        });

        // 每个 RSS 条目各自登记为一个任务：不关联订阅与条目，统一绑定表单提供的 Subject；
        // 标题无法解析时命名取条目标题、不落集数。
        await using var db    = await harness.CreateDbContextAsync();
        var             tasks = await db.DownloadTasks.AsNoTracking().ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task =>
        {
            Assert.Null(task.FeedItemId);
            Assert.Equal(42, task.SubjectId);
            Assert.Null(task.Episode);
            Assert.Equal(DownloadTaskStatus.PendingSubmit, task.Status);
            Assert.StartsWith("https://example.com/torrents/", task.TorrentUrl);
            Assert.StartsWith("/downloads/", task.SavePath);
            Assert.EndsWith($"-{task.InfoHash[..8]}", task.SavePath);
        });
        Assert.Equal("RSS 番剧 第 1 集 [1080p]",
                     tasks.Single(task => task.TorrentUrl!.EndsWith("rss-1.torrent")).NamingSnapshot);
        Assert.Equal("RSS 番剧 第 2 集 [1080p]",
                     tasks.Single(task => task.TorrentUrl!.EndsWith("rss-2.torrent")).NamingSnapshot);

        // 沿用既有提交管线：一轮对账后全部进入下载中。
        await harness.ReconcileAsync();
        Assert.All(await db.DownloadTasks.AsNoTracking().ToListAsync(),
                   task => Assert.Equal(DownloadTaskStatus.Downloading, task.Status));
        Assert.Equal(2, harness.Store.AddCalls.Count);
    }

    [Fact]
    public async Task CreateFromRssFeed_WithParseableTitle_RendersBangumiNameFromMapping()
    {
        using var harness = await CreateHarnessAsync();
        // 标题映射命中的 Subject：{title} 用 Bangumi 中文名，不再用解析标题。
        await harness.SeedSubjectMappingAsync(9001, "Sousou no Frieren", "葬送的芙莉莲", NekoParsedTitle());
        var title = NekoTitle("简日双语");
        harness.TorrentNames["mikan-12"] = title;
        harness.FeedReader.Items.Add(RssItem("mikan-12", title, "https://mikanani.me/Download/mikan-12.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi");

        Assert.Equal(1, result.CreatedCount);
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        // {source} 取种子下载地址域名判定的来源站点，与 SiteSource tag 同源。
        Assert.Equal("[Anime]葬送的芙莉莲 E12 mikan", task.NamingSnapshot);
        Assert.Equal("mikan", task.SiteSource);
        Assert.Equal(9001, task.SubjectId);
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        Assert.NotNull(ingredients);
        Assert.Equal("葬送的芙莉莲", ingredients.Title);
        Assert.Equal("12", ingredients.EpisodeText);
        Assert.Equal("喵萌奶茶屋", ingredients.Group);
        Assert.Equal("mikan", ingredients.Source);
    }

    [Fact]
    public async Task CreateFromRssFeed_SubjectWithoutCnName_UsesOriginalName()
    {
        using var harness = await CreateHarnessAsync();
        // Subject 无中文名：回退 Bangumi 原名，仍不使用解析标题。
        await harness.SeedSubjectMappingAsync(9002, "Sousou no Frieren", "", NekoParsedTitle());
        harness.TorrentNames["mikan-12"] = NekoTitle("简日双语");
        harness.FeedReader.Items.Add(RssItem("mikan-12", NekoTitle("简日双语"),
                                             "https://mikanani.me/Download/mikan-12.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi");

        Assert.Equal(1, result.CreatedCount);
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal("[Anime]Sousou no Frieren E12 mikan", task.NamingSnapshot);
    }

    [Fact]
    public async Task CreateFromRssFeed_UnboundSubject_RejectsItemAndAsksForBangumiId()
    {
        using var harness = await CreateHarnessAsync();
        // 标题可解析但映射未命中、无表单 ID、链接解析不出：必须拒绝登记并要求补 Bangumi ID。
        harness.TorrentNames["mikan-12"] = NekoTitle("简日双语");
        harness.FeedReader.Items.Add(RssItem("mikan-12", NekoTitle("简日双语"),
                                             "https://mikanani.me/Download/mikan-12.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi");

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        var item = result.Items.Single();
        Assert.Equal(RssDownloadItemOutcome.Failed, item.Outcome);
        Assert.Contains("Bangumi ID", item.Error);
        Assert.Equal(0, await CountTasksAsync(harness));
        // 门禁在下载种子之前生效：不触达种子源。
        Assert.Equal(0, harness.TorrentSource.CallCount);
    }

    [Fact]
    public async Task CreateFromRssFeed_WithExplicitSubjectId_BindsAndLearnsTitleMapping()
    {
        using var harness = await CreateHarnessAsync();
        // 表单显式 ID：拉取元数据落库、命名用中文名，并把解析标题回写映射供后续直接命中。
        var title = NekoTitle("简日双语");
        harness.TorrentNames["neko-a"] = title;
        harness.TorrentNames["neko-b"] = NekoTitle("简日双语", "1080p", 13);
        harness.FeedReader.Items.Add(RssItem("neko-a", title, "https://mikanani.me/Download/neko-a.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi", subjectId : 7);

        Assert.Equal(1, result.CreatedCount);
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(7, task.SubjectId);
        Assert.Equal("[Anime]测试作品7 E12 mikan", task.NamingSnapshot);
        // 元数据已落库，解析标题已回写映射。
        var subject = await db.MediaSubjects.AsNoTracking().SingleAsync(subject => subject.Id == 7);
        Assert.Equal("Subject 7", subject.Name);
        Assert.Equal("测试作品7", subject.NameCn);
        Assert.Equal(7,
                     await db.SubjectTitleMappings.AsNoTracking()
                             .Where(mapping => mapping.NormalizedTitle ==
                                               FeedItemEvaluator.NormalizeTitle(NekoParsedTitle()))
                             .Select(mapping => mapping.SubjectId).SingleAsync());

        // 同系列第二条不再需要表单 ID：直接命中回写的映射。
        harness.FeedReader.Items.Clear();
        harness.FeedReader.Items.Add(RssItem("neko-b", NekoTitle("简日双语", "1080p", 13),
                                             "https://mikanani.me/Download/neko-b.torrent"));
        var second = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi");
        Assert.Equal(1, second.CreatedCount);
        var secondTask = await harness.GetTaskAsync(second.Items.Single().TaskId!.Value);
        Assert.Equal(7, secondTask.SubjectId);
        Assert.Equal(13m, secondTask.Episode);
    }

    [Fact]
    public async Task CreateFromRssFeed_ResolvesSubjectFromItemLink_WhenMappingMisses()
    {
        using var harness = await CreateHarnessAsync();
        // 链接解析兜底：Mikan 条目链接解析出 Subject，同样落库并回写映射。
        var link = new Uri("https://example.com/posts/mikan-12");
        harness.LinkResolver.ResolvedLinks[link.AbsoluteUri] = 9;
        harness.TorrentNames["mikan-12"]                     = NekoTitle("简日双语");
        harness.FeedReader.Items.Add(RssItem("mikan-12", NekoTitle("简日双语"),
                                             "https://mikanani.me/Download/mikan-12.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/Bangumi");

        Assert.Equal(1, result.CreatedCount);
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(9, task.SubjectId);
        Assert.True(await db.SubjectTitleMappings.AsNoTracking().AnyAsync(mapping =>
                                                                              mapping.NormalizedTitle ==
                                                                              FeedItemEvaluator
                                                                                 .NormalizeTitle(NekoParsedTitle()) &&
                                                                              mapping.SubjectId == 9));
    }

    [Fact]
    public async Task CreateFromRssFeed_SkipsDuplicateHashesAcrossStatuses()
    {
        using var harness = await CreateHarnessAsync();
        // 预置一个活动任务；RSS 条目的种子名注册为同名以产生相同 hash。
        var uploadedTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Uploaded Anime - 01"));
        harness.TorrentNames["dup"] = "Uploaded Anime - 01";
        harness.FeedReader.Items.Add(RssItem("dup", "重复资源条目", "https://example.com/torrents/dup.torrent"));

        var first = await harness.CreateFromRssFeedAsync("https://example.com/rss.xml", subjectId : 42);

        var item = Assert.Single(first.Items);
        Assert.Equal(RssDownloadItemOutcome.Duplicate, item.Outcome);
        Assert.Equal(0, first.CreatedCount);
        Assert.Equal(1, await CountTasksAsync(harness));

        // 已完成同样跳过；非活动终态（被替换）才允许重新登记。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == uploadedTaskId);
            entity.Status = DownloadTaskStatus.Completed;
            await db.SaveChangesAsync();
        }

        Assert.Equal(RssDownloadItemOutcome.Duplicate,
                     (await harness.CreateFromRssFeedAsync("https://example.com/rss.xml", subjectId : 42))
                    .Items[0].Outcome);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == uploadedTaskId);
            entity.Status = DownloadTaskStatus.Replaced;
            await db.SaveChangesAsync();
        }

        // 被替换的终止记录同样挡住重复登记，不再创建新任务。
        var third = await harness.CreateFromRssFeedAsync("https://example.com/rss.xml", subjectId : 42);
        Assert.Equal(RssDownloadItemOutcome.Duplicate, third.Items[0].Outcome);
        Assert.Equal(1, await CountTasksAsync(harness));
    }

    [Fact]
    public async Task CreateFromRssFeed_PartialFailuresDoNotBlockOtherItems()
    {
        using var harness   = await CreateHarnessAsync();
        var       failedUri = "https://example.com/torrents/broken.torrent";
        harness.TorrentSource.ThrowOnUri[failedUri] = new HttpRequestException("模拟源站不可达");
        harness.FeedReader.Items.Add(RssItem("broken", "下载失败的条目", failedUri));
        // 无种子地址的条目同样失败，但不影响其他条目。
        harness.FeedReader.Items.Add(new FeedSourceItem("guid:no-torrent", "no-torrent", "没有种子的条目",
                                                        new Uri("https://example.com/posts/none"), null,
                                                        harness.Timestamp));
        harness.FeedReader.Items.Add(RssItem("ok", "正常条目", "https://example.com/torrents/ok.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://example.com/rss.xml", subjectId : 42);

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(RssDownloadItemOutcome.Failed,
                     result.Items.Single(item => item.Title == "下载失败的条目").Outcome);
        Assert.Contains("没有种子地址",
                        result.Items.Single(item => item.Title == "没有种子的条目").Error);
        Assert.Equal(RssDownloadItemOutcome.Created, result.Items.Single(item => item.Title == "正常条目").Outcome);

        // feed 本身抓取失败时整批失败，不落任何任务。
        harness.FeedReader.Items.Clear();
        harness.FeedReader.ThrowOnRead = new HttpRequestException("模拟 feed 不可达");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
                                                           harness
                                                              .CreateFromRssFeedAsync("https://example.com/rss.xml"));
        Assert.Equal(1, await CountTasksAsync(harness));
    }

    [Fact]
    public async Task CreateFromRssFeed_PassesProxyToFeedAndTorrentRequests()
    {
        using var harness = await CreateHarnessAsync();
        var       proxyId = Guid.NewGuid();
        harness.FeedReader.Items.Add(RssItem("proxied", "走代理的条目", "https://example.com/torrents/proxied.torrent"));

        await harness.CreateFromRssFeedAsync("https://example.com/rss.xml", proxyId, 42);

        Assert.Equal(proxyId, harness.FeedReader.LastProxyId);
        Assert.Equal(proxyId, harness.TorrentSource.LastProxyId);
    }

    [Fact]
    public async Task CreateFromRssFeed_RejectsUnconfiguredDownloaderAndInvalidInput()
    {
        using var unconfigured = await CreateHarnessAsync(false);
        unconfigured.FeedReader.Items.Add(RssItem("rss-1", "任意条目", "https://example.com/torrents/rss-1.torrent"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                unconfigured
                                                                   .CreateFromRssFeedAsync("https://example.com/rss.xml"));

        using var configured = await CreateHarnessAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => configured
                                                       .CreateFromRssFeedAsync("ftp://example.com/rss.xml"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        configured
                                                           .CreateFromRssFeedAsync("http://example.com/rss.xml"));

        // 空 feed 视为无效输入，不落任何任务。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                configured
                                                                   .CreateFromRssFeedAsync("https://example.com/rss.xml"));
        Assert.Equal(0, await CountTasksAsync(configured));
    }

    /// <summary>构造喵萌奶茶屋式发布名：同系列同集数，仅语言与分辨率变化。</summary>
    private static string NekoTitle(string language, string resolution = "1080p", int episode = 12)
    {
        return $"【喵萌奶茶屋】★07月新番★[{NekoSeriesTitle.Replace(" ", " / ")}][{episode}][{resolution}]" +
               $"[{language}].mp4";
    }

    /// <summary>解析器对喵萌式发布名产出的系列标题（含 " / " 分隔符）：标题映射键与评估器同源。</summary>
    private static string NekoParsedTitle()
    {
        return new AniParser().Parse(NekoTitle("简日双语"))!.Title;
    }

    private static void AddNekoFeedItem(TestHarness harness, string stableId, string title)
    {
        harness.TorrentNames[stableId] = title;
        harness.FeedReader.Items.Add(RssItem(stableId, title, $"https://mikanani.me/Download/{stableId}.torrent"));
    }

    /// <summary>
    ///     简繁两种系列名拼写（与妳/与你）解析标题不同、批内分组不合并的真实场景：
    ///     同 Subject 同集仍须由登记裁决互认，更优的简日后到登记即终止繁日（不等下载完成），
    ///     繁日保留终止记录不再提交，简日正常提交下载。
    /// </summary>
    [Fact]
    public async Task CreateFromRssFeed_DifferentSpellingVariants_BetterOneReplacesInferiorEndToEnd()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var tcTitle =
            "【喵萌奶茶屋】★07月新番★[与妳相恋到生命尽头 / きみが死ぬまで恋をしたい / Kimi ga Shinu made Koi wo Shitai][12][1080p][繁日双语].mp4";
        var scTitle =
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头 / きみが死ぬまで恋をしたい / Kimi ga Shinu made Koi wo Shitai][12][1080p][简日双语].mp4";
        var tcParsed = new AniParser().Parse(tcTitle)!;
        var scParsed = new AniParser().Parse(scTitle)!;
        Assert.NotEqual(FeedItemEvaluator.NormalizeTitle(tcParsed.Title),
                        FeedItemEvaluator.NormalizeTitle(scParsed.Title));
        await harness.SeedSubjectMappingAsync(541285, "きみが死ぬまで恋をしたい", "与你相恋到生命尽头",
                                              tcParsed.Title, scParsed.Title);
        harness.TorrentNames["neko-tc"] = tcTitle;
        harness.TorrentNames["neko-sc"] = scTitle;
        // feed 按发布时间倒序：繁日（更晚发布）在前、简日在后，与事故现场一致。
        harness.FeedReader.Items.Add(RssItem("neko-tc", tcTitle, "https://mikanani.me/Download/neko-tc.torrent"));
        harness.FeedReader.Items.Add(RssItem("neko-sc", scTitle, "https://mikanani.me/Download/neko-sc.torrent"));

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        // 两条都登记是允许的：更优的简日登记的同一事务里直接终止繁日，不是重复下载。
        Assert.Equal(2, result.CreatedCount);
        await using var db = await harness.CreateDbContextAsync();
        var inferior = await db.DownloadTasks.AsNoTracking()
                               .SingleAsync(task => task.InfoHash == TestTorrentBytes.InfoHashOf(tcTitle));
        var better = await db.DownloadTasks.AsNoTracking()
                             .SingleAsync(task => task.InfoHash == TestTorrentBytes.InfoHashOf(scTitle));
        Assert.Equal(541285, inferior.SubjectId);
        Assert.Equal(541285, better.SubjectId);
        Assert.Equal(inferior.Id, better.ReplacesTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, inferior.Status);
        Assert.Equal(better.Id, inferior.ReplacedByTaskId);

        // 繁日已终止不再提交：只有简日进入下载器，随后正常完成。
        await harness.ReconcileAsync();
        Assert.True(harness.Store.Torrents.ContainsKey(TestTorrentBytes.InfoHashOf(scTitle)));
        Assert.False(harness.Store.Torrents.ContainsKey(TestTorrentBytes.InfoHashOf(tcTitle)));
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf(scTitle));
        harness.Store.Files[TestTorrentBytes.InfoHashOf(scTitle)] = ["原文件名.mkv"];
        await harness.ReconcileAsync();

        var completedBetter = await harness.GetTaskAsync(better.Id);
        Assert.Equal(DownloadTaskStatus.Completed, completedBetter.Status);
        var replacedInferior = await harness.GetTaskAsync(inferior.Id);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedInferior.Status);
        Assert.Equal(better.Id, replacedInferior.ReplacedByTaskId);
        // 繁日从未提交，下载器中没有它的种子，不存在删除动作。
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task CreateFromRssFeed_SameEpisodeLanguageVariants_KeepsOnlyBestByRules()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var jpSc = NekoTitle("简日双语");
        var jpTc = NekoTitle("繁日双语");
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var both = NekoTitle("简繁日双语");
        AddNekoFeedItem(harness, "neko-jpsc", jpSc);
        AddNekoFeedItem(harness, "neko-jptc", jpTc);
        AddNekoFeedItem(harness, "neko-both", both);

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        // 默认规则语言序 JpSc 优先：同一集只有简日登记，繁日与简繁日批内择优落选。
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(2, result.SupersededCount);
        Assert.Equal(0, result.FailedCount);
        var created = result.Items.Single(item => item.Outcome == RssDownloadItemOutcome.Created);
        Assert.Equal(jpSc, created.Title);
        Assert.All(result.Items.Where(item => item.Outcome == RssDownloadItemOutcome.Superseded),
                   item => Assert.Contains(jpSc, item.Error));
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(TestTorrentBytes.InfoHashOf(jpSc), task.InfoHash);
        Assert.Equal(12m, task.Episode);
    }

    [Fact]
    public async Task CreateFromRssFeed_LanguagePriorityConfigured_SwitchesWinner()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules(EnumLanguage.JpTc, EnumLanguage.JpSc, EnumLanguage.Sc));
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var jpTc = NekoTitle("繁日双语");
        AddNekoFeedItem(harness, "neko-jpsc", NekoTitle("简日双语"));
        AddNekoFeedItem(harness, "neko-jptc", jpTc);

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(jpTc, result.Items.Single(item => item.Outcome == RssDownloadItemOutcome.Created).Title);
    }

    [Fact]
    public async Task CreateFromRssFeed_ResolutionNotAllowed_LosesBatchSelection()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var hd = NekoTitle("简日双语");
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var sd = NekoTitle("简日双语", "720p");
        AddNekoFeedItem(harness, "neko-hd", hd);
        AddNekoFeedItem(harness, "neko-sd", sd);

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        // 默认规则只允许 1080p：720p 在硬性过滤后落选，不进入登记。
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SupersededCount);
        Assert.Equal(hd, result.Items.Single(item => item.Outcome    == RssDownloadItemOutcome.Created).Title);
        Assert.Contains(hd, result.Items.Single(item => item.Outcome == RssDownloadItemOutcome.Superseded).Error);
    }

    [Fact]
    public async Task CreateFromRssFeed_AllVariantsBelowAllowedResolution_FallsBackToBestEffort()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var jpSc = NekoTitle("简日双语", "720p");
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var jpTc = NekoTitle("繁日双语", "720p");
        AddNekoFeedItem(harness, "neko-sd-jpsc", jpSc);
        AddNekoFeedItem(harness, "neko-sd-jptc", jpTc);

        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        // 整组都不满足分辨率规则时兜底参与排名：仍只登记一条（一次性下载内容不因规则消失）。
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SupersededCount);
        Assert.Equal(jpSc, result.Items.Single(item => item.Outcome == RssDownloadItemOutcome.Created).Title);
    }

    [Fact]
    public async Task CreateFromRssFeed_BetterVariantAfterUpload_ReplacesUploadedTask()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var uploadTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")),
                                                           "mikan");

        AddNekoFeedItem(harness, "neko-jpsc", NekoTitle("简日双语"));
        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        // 同源同集统一裁决：规则更优的简日登记即终止已上传的繁日（PendingSubmit 直接置被替换，不再提交）。
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SupersededCount);
        await using var db    = await harness.CreateDbContextAsync();
        var             tasks = await db.DownloadTasks.AsNoTracking().ToListAsync();
        Assert.Equal(2, tasks.Count);
        var replacement = tasks.Single(task => task.Id != uploadTaskId);
        Assert.Equal(uploadTaskId, replacement.ReplacesTaskId);
        var uploaded = tasks.Single(task => task.Id == uploadTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, uploaded.Status);
        Assert.Equal(replacement.Id, uploaded.ReplacedByTaskId);
    }

    /// <summary>现任已在下载器中下载时被高优先任务顶替：登记即连文件删除其种子并终止，不等下载完成。</summary>
    [Fact]
    public async Task CreateFromRssFeed_BetterVariantAfterDownloadingIncumbent_DeletesTorrentImmediately()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var tcTitle      = NekoTitle("繁日双语");
        var tcHash       = TestTorrentBytes.InfoHashOf(tcTitle);
        var uploadTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(tcTitle), "mikan");
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(uploadTaskId)).Status);

        AddNekoFeedItem(harness, "neko-jpsc", NekoTitle("简日双语"));
        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        Assert.Equal(1, result.CreatedCount);
        Assert.Contains((tcHash, true), harness.Store.DeleteCalls);
        Assert.False(harness.Store.Torrents.ContainsKey(tcHash));
        var incumbent = await harness.GetTaskAsync(uploadTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, incumbent.Status);
    }

    [Fact]
    public async Task CreateFromRssFeed_WorseVariantAfterUpload_SupersededByArbitration()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var uploadTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");

        AddNekoFeedItem(harness, "neko-jptc", NekoTitle("繁日双语"));
        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        // 已上传的简日按规则更优：新来的繁日不进入下载，结果明细说明占用者；
        // 但仍落一条终止记录指向现任，供同 hash 重复添加时直接终止。
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.SupersededCount);
        var superseded = result.Items.Single(item => item.Outcome == RssDownloadItemOutcome.Superseded);
        Assert.Contains("同集已有更优任务", superseded.Error);
        Assert.Equal(2, await CountTasksAsync(harness));
        await using var db = await harness.CreateDbContextAsync();
        var record = await db.DownloadTasks.AsNoTracking()
                             .SingleAsync(task => task.InfoHash == TestTorrentBytes.InfoHashOf(NekoTitle("繁日双语")));
        Assert.Equal(DownloadTaskStatus.Replaced, record.Status);
        Assert.Equal(uploadTaskId, record.ReplacedByTaskId);
        Assert.Equal(12m, record.Episode);

        // 同 hash 重复添加命中终止记录：直接判定重复，不再登记。
        harness.FeedReader.Items.Clear();
        AddNekoFeedItem(harness, "neko-jptc-again", NekoTitle("繁日双语"));
        var repeat = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");
        Assert.Equal(RssDownloadItemOutcome.Duplicate, repeat.Items[0].Outcome);
        Assert.Equal(2, await CountTasksAsync(harness));
    }

    [Fact]
    public async Task Create_CrossPathBeatsUploadedVariant_RegistersWithReplacement()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var uploadTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")),
                                                           "mikan");
        // ParsedTitle 与评估器同源：用真实解析产出（含原始分隔符），不手写清洗后的标题。
        var parsedTitle = new AniParser().Parse(NekoTitle("简日双语"))!.Title;
        var itemId = await SeedAcceptedItemAsync(harness, "sub-jpsc", NekoTitle("简日双语"), NamingV1, 1, 12m,
                                                 parsedTitle, "JpSc",
                                                 "喵萌奶茶屋");
        var taskId = await harness.CreateDownloadAsync(itemId);

        // 订阅优胜与上传任务同 Subject（42）互认，按规则顶替后登记。
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(uploadTaskId, task.ReplacesTaskId);
        Assert.Equal(12m, task.Episode);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(uploadTaskId)).Status);

        // 订阅登记跨路径直接终止未提交现任；下一轮只提交新任务，不会再提交旧 PendingSubmit。
        await harness.ReconcileAsync();
        Assert.Single(harness.Store.AddCalls);
        Assert.Equal(task.InfoHash, TorrentInfoHashExtractor.TryExtractInfoHash(
            harness.Store.AddCalls[0].Bytes, out var submittedHash) ? submittedHash : null);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(uploadTaskId)).Status);
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(task.Id)).Status);
    }

    [Fact]
    public async Task Create_CrossPathLosesToUploadedVariant_BlockedWithReason()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var parsedTitle = new AniParser().Parse(NekoTitle("繁日双语"))!.Title;
        var itemId = await SeedAcceptedItemAsync(harness, "sub-jptc", NekoTitle("繁日双语"), NamingV1, 1, 12m,
                                                 parsedTitle, "JpTc",
                                                 "喵萌奶茶屋");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));

        // 上传的简日按规则更优：订阅侧登记被拒并说明占用者，人工可删除旧任务后重试。
        Assert.Contains("同集已有更优任务", exception.Message);
        Assert.Equal(1, await CountTasksAsync(harness));
    }

    /// <summary>测试规则：喵萌奶茶屋列入组序，语言序沿用默认（简日 JpSc 优先）。</summary>
    private static SelectionRuleConfig NekoRules(params EnumLanguage[] languageOrder)
    {
        return SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["喵萌奶茶屋"],
            LanguageOrder = languageOrder.Length > 0 ? languageOrder : [EnumLanguage.JpSc, EnumLanguage.Sc]
        };
    }

    private static FeedSourceItem RssItem(string stableId, string title, string torrentUrl)
    {
        return new FeedSourceItem($"guid:{stableId}", stableId, title,
                                  new Uri($"https://example.com/posts/{stableId}"),
                                  new Uri(torrentUrl),
                                  new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
    }

    private static async Task<int> CountTasksAsync(TestHarness harness)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.DownloadTasks.AsNoTracking().CountAsync();
    }

    [Fact]
    public async Task Reconcile_SubmitsPendingTaskWithIsolatedPath()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        await harness.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        Assert.Equal(1, task.AttemptCount);
        Assert.NotNull(task.SubmittedAtUtc);
        var add = Assert.Single(harness.Store.AddCalls);
        Assert.Equal(task.SavePath, add.SavePath);
        Assert.Equal(["mikan"], add.Tags);
        Assert.Equal(NamingV1, add.Rename);
        var snapshot = Assert.Single(harness.Store.Torrents.Values);
        Assert.StartsWith(task.SavePath, snapshot.SavePath);
    }

    [Fact]
    public async Task Reconcile_ResponseLostButRegistered_ConfirmsByHashWithoutResubmitting()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        harness.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;

        await harness.ReconcileAsync();
        var submitted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Submitting, submitted.Status);
        Assert.Equal(1, submitted.AttemptCount);

        // 下一轮对账发现下载器中实际已有该任务：直接确认，不重复提交。
        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();

        var confirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, confirmed.Status);
        Assert.Single(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Reconcile_ResponseLostAndNotRegistered_ResubmitsThenFailsAfterCap()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        harness.Factory.SessionConfigurator =
            session => session.AddException = new QbittorrentException("模拟响应丢失");

        for (var attempt = 1; attempt <= DownloadService.MaxAutoSubmitAttempts; attempt++)
        {
            await harness.ReconcileAsync();
            var task = await harness.GetTaskAsync(taskId);
            Assert.Equal(DownloadTaskStatus.Submitting, task.Status);
            Assert.Equal(attempt, task.AttemptCount);
        }

        // 达到自动重试上限后如实失败，等待人工处理；下载器中没有留下任何任务。
        await harness.ReconcileAsync();
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("提交", failed.FailedStep);
        Assert.Empty(harness.Store.Torrents);
    }

    [Fact]
    public async Task Reconcile_AfterRestart_ConfirmsSubmittingTaskByHashWithoutResubmitting()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 模拟提交请求已到达下载器但应用在确认前重启：下载器有任务（隔离路径一致），本地停在 Submitting。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var stored = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            stored.Status         = DownloadTaskStatus.Submitting;
            stored.AttemptCount   = 1;
            stored.SubmittedAtUtc = stored.CreatedAtUtc;
            await db.SaveChangesAsync();
            harness.Store.AddTorrent(stored.InfoHash, "旧登记",
                                     QbittorrentTorrentCategory.Downloading, 0.2, stored.SavePath);
        }

        // 新的作用域与服务实例（等同重启后的进程）按 hash 对账确认。
        await harness.ReconcileAsync();

        var confirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, confirmed.Status);
        Assert.Empty(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Reconcile_InFlightAddThenDelete_RemovesLateTorrentAndDatabaseRow()
    {
        using var harness = await CreateHarnessAsync();
        using var secondScope = harness.Provider.CreateScope();
        var deletingService = secondScope.ServiceProvider.GetRequiredService<DownloadService>();
        var itemId = await SeedAcceptedItemAsync(harness, "race-delete", "Test Anime - 01");
        var taskId = await harness.CreateDownloadAsync(itemId);
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        var gateWaitAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Provider.GetRequiredService<DownloadCoordinationGate>().WaitAttempted =
            () => gateWaitAttempted.TrySetResult();
        var addStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
        {
            addStarted.TrySetResult();
            await releaseAdd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var reconcile = harness.ReconcileAsync();
        await addStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deletion = Task.Run(() => deletingService.DeleteAsync(taskId, true, true));
        await gateWaitAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(deletion.IsCompleted);
        Assert.True(harness.Store.Torrents.ContainsKey(hash) is false);
        await using (var beforeReleaseDb = await harness.CreateDbContextAsync())
            Assert.True(await beforeReleaseDb.DownloadTasks.AnyAsync(task => task.Id == taskId));
        releaseAdd.TrySetResult();
        await Task.WhenAll(reconcile, deletion).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(harness.Store.Torrents.ContainsKey(hash));
        await using var db = await harness.CreateDbContextAsync();
        Assert.False(await db.DownloadTasks.AnyAsync(task => task.Id == taskId));
    }

    [Fact]
    public async Task Reconcile_InFlightAddThenBetterRssRegistration_WaitsAndRemovesOldTorrent()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var oldTitle = NekoTitle("繁日双语");
        var oldId = await harness.CreateUploadAsync(TestTorrentBytes.Create(oldTitle), "mikan");
        var oldHash = TestTorrentBytes.InfoHashOf(oldTitle);
        AddNekoFeedItem(harness, "race-better", NekoTitle("简日双语"));
        var gateWaitAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Provider.GetRequiredService<DownloadCoordinationGate>().WaitAttempted =
            () => gateWaitAttempted.TrySetResult();
        var addStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
        {
            addStarted.TrySetResult();
            await releaseAdd.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var reconcile = harness.ReconcileAsync();
        await addStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var registrationScope = harness.Provider.CreateScope();
        var otherScopedService = registrationScope.ServiceProvider.GetRequiredService<DownloadService>();
        var registration = Task.Run(() => otherScopedService.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview"));
        await gateWaitAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(registration.IsCompleted);
        Assert.False(harness.Store.Torrents.ContainsKey(oldHash));
        Assert.Equal(1, await CountTasksAsync(harness));
        releaseAdd.TrySetResult();
        await Task.WhenAll(reconcile, registration).WaitAsync(TimeSpan.FromSeconds(10));
        await harness.ReconcileAsync();

        Assert.False(harness.Store.Torrents.ContainsKey(oldHash));
        var oldTask = await harness.GetTaskAsync(oldId);
        Assert.Equal(DownloadTaskStatus.Replaced, oldTask.Status);
        Assert.Single(harness.Store.Torrents);
    }

    [Fact]
    public async Task Reconcile_StalePendingSnapshotReplacedDuringHashLookup_IsNotSubmitted()
    {
        using var harness = await CreateHarnessAsync();
        var pendingId = await harness.CreateDownloadAsync(
            await SeedAcceptedItemAsync(harness, "stale-snapshot", "Snapshot Pending - 01"));
        var sentinelId = await harness.CreateDownloadAsync(
            await SeedAcceptedItemAsync(harness, "stale-sentinel", "Snapshot Sentinel - 01"));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var sentinel = await db.DownloadTasks.SingleAsync(task => task.Id == sentinelId);
            sentinel.Status = DownloadTaskStatus.Downloading;
            harness.Store.AddTorrent(sentinel.InfoHash, "sentinel", QbittorrentTorrentCategory.Downloading,
                                     0.2, sentinel.SavePath);
            await db.SaveChangesAsync();
        }

        harness.Factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = async hashes =>
        {
            Assert.Contains(TestTorrentBytes.InfoHashOf("Snapshot Sentinel - 01"), hashes);
            await using var db = await harness.CreateDbContextAsync();
            var pending = await db.DownloadTasks.SingleAsync(task => task.Id == pendingId);
            pending.Status = DownloadTaskStatus.Replaced;
            pending.ReplacedByTaskId = Guid.NewGuid();
            await db.SaveChangesAsync();
        };

        await harness.ReconcileAsync();

        Assert.Empty(harness.Store.AddCalls);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(pendingId)).Status);
    }

    [Fact]
    public async Task Reconcile_LateAddResultForReplacedTask_IsRemovedFromDownloader()
    {
        using var harness = await CreateHarnessAsync();
        var itemId = await SeedAcceptedItemAsync(harness, "race-stale", "Test Anime - 01");
        var taskId = await harness.CreateDownloadAsync(itemId);
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
        {
            await using var db = await harness.CreateDbContextAsync();
            var current = await db.DownloadTasks.SingleAsync(task => task.Id == taskId);
            current.Status = DownloadTaskStatus.Replaced;
            current.ReplacedByTaskId = Guid.NewGuid();
            await db.SaveChangesAsync();
        };

        await harness.ReconcileAsync();

        Assert.False(harness.Store.Torrents.ContainsKey(hash));
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Reconcile_PersistsSubmittingBeforeRequest_ThenRestartConfirmsWithoutResubmit()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 在请求真正发往下载器的瞬间读取数据库：Submitting 必须已落库，否则崩溃后会重复提交。
        DownloadTaskStatus? statusAtRequest = null;
        harness.Factory.SessionConfigurator = session =>
        {
            session.RegisterThenThrowTransport = true;
            session.BeforeAddTorrentAsync = async () =>
            {
                await using var db = await harness.CreateDbContextAsync();
                statusAtRequest = (await db.DownloadTasks.AsNoTracking()
                                           .SingleAsync(value => value.Id == taskId)).Status;
            };
        };

        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Submitting, statusAtRequest);
        Assert.Equal(DownloadTaskStatus.Submitting, (await harness.GetTaskAsync(taskId)).Status);

        // 应用在确认前重启：新进程按 Hash 对账确认，不重复提交。
        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();

        var confirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, confirmed.Status);
        Assert.Single(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Reconcile_PendingTaskWithPreExistingTorrent_AdoptsInPlace()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 下载器中已有同 hash 种子（用户或其他工具预先添加，位于其自己的目录且已完成做种）。
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.AddTorrent(hash, "外部已有任务", QbittorrentTorrentCategory.Seeding, 1.0,
                                 "/downloads/用户自己的目录", ["原文件名.mkv"]);

        await harness.ReconcileAsync();

        // 接管：不重复提交，本地路径对齐为真实位置，补打来源 tag，并按快照直接推进到完成改名。
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Empty(harness.Store.AddCalls);
        Assert.Equal("/downloads/用户自己的目录", task.SavePath);
        Assert.Contains((hash, "mikan"), harness.Store.AddTagCalls);
        var rename = Assert.Single(harness.Store.RenameCalls);
        // 完成改名后的种子名与命名快照一致，不带内容文件的扩展名。
        Assert.Equal(NamingV1, rename.Name);
    }

    [Fact]
    public async Task Reconcile_FailsResponseWithExistingTorrent_AdoptsInsteadOfFailing()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // qB 对重复种子返回 "Fails." 但种子确实已存在：按 hash 确认后接管而不是标记失败。
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.AddTorrent(hash, "重复添加已存在的任务", QbittorrentTorrentCategory.Downloading, 0.5,
                                 "/downloads/其他位置");
        harness.Factory.SessionConfigurator = session => session.FailOnDuplicate = true;

        await harness.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        Assert.NotEqual(DownloadTaskStatus.Failed, task.Status);
        Assert.Equal("/downloads/其他位置", task.SavePath);
    }

    [Fact]
    public async Task Reconcile_FailsResponseWithoutExistingTorrent_MarksTaskFailed()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 下载器拒绝且按 hash 确认不存在：如实失败，等待人工处理。
        harness.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";

        await harness.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Equal("提交", task.FailedStep);
    }

    [Fact]
    public async Task Reconcile_TrackedTaskWithMismatchedSavePath_FailsWithoutTakingOver()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        // 下载器中同 hash 任务的保存路径不在本任务的隔离目录：不得继续改名或替换。
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { SavePath = "/downloads/其他目录" };

        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("对账", failed.FailedStep);
    }

    [Fact]
    public async Task Reconcile_TrackedTaskWithCaseDifferentSavePath_FailsWithoutTakingOver()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        // 目录大小写在 Linux 上敏感：/downloads 与 /Downloads 是不同目录，不得接管。
        var stored = await harness.GetTaskAsync(taskId);
        var hash   = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            SavePath = stored.SavePath.Replace("/downloads/", "/Downloads/", StringComparison.Ordinal)
        };

        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("对账", failed.FailedStep);
    }

    [Fact]
    public async Task Reconcile_DuplicateActiveHashRow_BlockedByUniqueIndex()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        await using var db       = await harness.CreateDbContextAsync();
        var             existing = await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        db.DownloadTasks.Add(new DownloadTask
        {
            Id          = Guid.NewGuid(), FeedItemId = existing.FeedItemId, InfoHash = existing.InfoHash,
            TorrentFile = existing.TorrentFile, TorrentUrl = existing.TorrentUrl,
            SavePath    = existing.SavePath, NamingSnapshot = existing.NamingSnapshot,
            Status      = DownloadTaskStatus.PendingSubmit, CreatedAtUtc = existing.CreatedAtUtc
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task PauseAndResume_UserPauseSurvivesReconcileAndOutofBandResumeIsDetected()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        await harness.Service.PauseAsync(taskId);
        var paused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, paused.Status);
        Assert.True(paused.IsUserPaused);
        Assert.Single(harness.Store.PauseCalls);

        // 对账发现任务仍是暂停状态：不得自动恢复。
        await harness.ReconcileAsync();
        var stillPaused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, stillPaused.Status);
        Assert.Empty(harness.Store.ResumeCalls);

        // 用户在下载器内手动恢复：对账应把任务转回下载中。
        harness.Store.Paused.Remove(TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        var resumed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, resumed.Status);
        Assert.False(resumed.IsUserPaused);

        // 页面恢复动作同样可用。
        await harness.Service.PauseAsync(taskId);
        await harness.Service.ResumeAsync(taskId);
        Assert.Contains(TestTorrentBytes.InfoHashOf("Test Anime - 01"), harness.Store.ResumeCalls);
    }

    [Fact]
    public async Task Reconcile_CompletesWithRenameAndMarksOldTaskReplaced()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        harness.Store.Torrents[TestTorrentBytes.InfoHashOf("Test Anime - 01")] =
            harness.Store.Torrents[TestTorrentBytes.InfoHashOf("Test Anime - 01")] with
            {
                Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
            };
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        var rename = Assert.Single(harness.Store.RenameCalls);
        // 种子名不带扩展名（与添加时 rename 参数同形），扩展名只落在内容文件上。
        Assert.Equal(NamingV1, rename.Name);
        // 实际文件也被改名，而不仅是种子展示名。
        var renameFile = Assert.Single(harness.Store.RenameFileCalls);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Test Anime - 01"), renameFile.Hash);
        Assert.Equal("原文件名.mkv", renameFile.OldPath);
        Assert.Equal(NamingV1 + ".mkv", renameFile.NewPath);

        // v2 到来：自动记录替换关系，完成后旧任务被标记为被替换，文件与任务记录保留。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var v2Task   = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(taskIdV1, v2Task.ReplacesTaskId);

        // 先提交 v2，再让它在下载器中完成；随后一轮对账完成改名并标记 v1 被替换。
        await harness.ReconcileAsync();
        var v2Hash = TestTorrentBytes.InfoHashOf("Test Anime - 01 v2");
        harness.Store.Torrents[v2Hash] = harness.Store.Torrents[v2Hash] with
        {
            Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
        };
        await harness.ReconcileAsync();

        var replaced = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replaced.Status);
        Assert.Equal(taskIdV2, replaced.ReplacedByTaskId);
        Assert.Equal(2, harness.Store.Torrents.Count);
    }

    [Fact]
    public async Task Reconcile_CompletesMultiFileTorrent_ByRenamingFilesPerEpisodeAndFolder()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] =
        [
            "作品文件夹/[VCB-Studio] Test Anime [01][Ma10p_1080p][x265_10bit].mkv",
            "作品文件夹/NCOP.mp4",
            "作品文件夹/subs.ass"
        ];

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        // 能解析出集数的文件按命名要素改为各自集数；附属文件挪进 Seed/，不混入成片命名。
        Assert.Equal(
        [
            ("作品文件夹/[VCB-Studio] Test Anime [01][Ma10p_1080p][x265_10bit].mkv",
             "作品文件夹/" + NamingV1 + ".mkv"),
            ("作品文件夹/NCOP.mp4", "作品文件夹/Seed/NCOP.mp4"),
            ("作品文件夹/subs.ass", "作品文件夹/Seed/subs.ass")
        ], harness.Store.RenameFileCalls.Select(call => (call.OldPath, call.NewPath)).ToList());
        var folderRename = Assert.Single(harness.Store.RenameFolderCalls);
        Assert.Equal("作品文件夹", folderRename.OldPath);
        Assert.Equal(NamingV1, folderRename.NewPath);
        // 种子名不带扩展名，直接使用命名快照。
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(NamingV1, rename.Name);
    }

    [Fact]
    public async Task Reconcile_CorrectsSubject484623EpisodeForSingleFileUsingFileVersion()
    {
        using var harness = await CreateHarnessAsync();
        await SeedSubjectAsync(harness, 484623);
        var itemId = await SeedAcceptedItemAsync(harness, "subject-484623-single", "Test Anime - 14 v2",
                                                 "测试作品 E14 v2 [Sc]", 2, 14, subjectId: 484623);
        var taskId = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 14 v2");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] = ["作品文件夹/[VCB-Studio] Test Anime [14][Ma10p_1080p][x265_10bit].mkv"];

        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        var renameFile = Assert.Single(harness.Store.RenameFileCalls);
        Assert.Equal("测试作品 E01 v2 [Sc].mkv", renameFile.NewPath);
    }

    [Fact]
    public async Task Reconcile_CorrectsSubject484623EpisodeRangeForMultiFileNames()
    {
        using var harness = await CreateHarnessAsync();
        await SeedSubjectAsync(harness, 484623);
        var itemId = await SeedAcceptedItemAsync(harness, "subject-484623-range", "Test Anime - 14-15",
                                                 "测试作品 E14-E15 [Sc]", 1, 14, subjectId: 484623);
        var taskId = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 14-15");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] =
        [
            "作品文件夹/[VCB-Studio] Test Anime [14][Ma10p_1080p][x265_10bit].mkv",
            "作品文件夹/[VCB-Studio] Test Anime [15][Ma10p_1080p][x265_10bit].mkv"
        ];

        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Equal(
        [
            ("作品文件夹/[VCB-Studio] Test Anime [14][Ma10p_1080p][x265_10bit].mkv",
             "作品文件夹/测试作品 E01 [Sc].mkv"),
            ("作品文件夹/[VCB-Studio] Test Anime [15][Ma10p_1080p][x265_10bit].mkv",
             "作品文件夹/测试作品 E02 [Sc].mkv")
        ], harness.Store.RenameFileCalls.Select(call => (call.OldPath, call.NewPath)).ToList());
    }

    [Fact]
    public async Task Reconcile_MultiFileScatteredFiles_AreSortedIntoSeedAndStillComplete()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        CompleteTorrent(harness, hash);
        // 顶层散文件：没有公共根目录可改，逐文件整理后仍能完成。
        harness.Store.Files[hash] = ["original.mkv", "subtitle.ass"];

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(
        [
            ("original.mkv", "Seed/original.mkv"),
            ("subtitle.ass", "Seed/subtitle.ass")
        ], harness.Store.RenameFileCalls.Select(call => (call.OldPath, call.NewPath)).ToList());
        Assert.Empty(harness.Store.RenameFolderCalls);
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(NamingV1, rename.Name);
    }

    [Fact]
    public async Task Reconcile_MultiFileScatteredWithoutNamingIngredients_FailsInsteadOfFakeCompletion()
    {
        using var harness = await CreateHarnessAsync();
        // 上传种子标题无法解析：没有命名要素，走命名快照兜底路径。
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Uploaded Anime - 01"));
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Uploaded Anime - 01");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] = ["original.mkv", "subtitle.ass"];

        await harness.ReconcileAsync();

        // 多个顶层散文件无法唯一映射到命名快照：必须如实失败，不得保留原名却标记完成。
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("改名", failed.FailedStep);
        Assert.Contains("顶层文件", failed.Error);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.RenameFolderCalls);
        Assert.Empty(harness.Store.RenameCalls);
    }

    [Fact]
    public async Task Reconcile_SingleFileSnapshotAlreadyEndsWithExtension_DoesNotAppendTwice()
    {
        using var harness = await CreateHarnessAsync();
        // RSS 原始标题自带 .mp4 后缀且无法解析：快照兜底改名不得产出 .mp4.mp4。
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Uploaded Anime - 01.mp4"));
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Uploaded Anime - 01.mp4");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] = ["Uploaded Anime - 01.mp4"];

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        var renameFile = Assert.Single(harness.Store.RenameFileCalls);
        Assert.Equal("Uploaded Anime - 01.mp4", renameFile.NewPath);
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal("Uploaded Anime - 01.mp4", rename.Name);
    }

    [Fact]
    public async Task CreateUpload_LongEscapedNamingIngredientsPersistForFeedItemAndTask()
    {
        using var harness = await CreateHarnessAsync();
        var template = new string('中', 140) + " {title} E{episode}";
        await harness.SaveNamingTemplateAsync(template);
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")));
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(149, task.NamingSnapshot.Length);
        Assert.True(task.NamingIngredients!.Length > 1000);
        Assert.NotNull(TorrentNamingIngredients.FromJson(task.NamingIngredients));

        await using var db = await harness.CreateDbContextAsync();
        var feedItem = new FeedItem
        {
            Id = Guid.NewGuid(), SubscriptionId = Guid.NewGuid(), StableId = "long-name",
            Title = new string('中', 140), FirstSeenAtUtc = harness.Timestamp,
            Status = FeedItemStatus.Pending, DecisionStatus = ReleaseDecisionStatus.NeedsReview,
            Reason = "test", NamingIngredients = task.NamingIngredients
        };
        db.MediaSubscriptions.Add(new MediaSubscription
        {
            Id = feedItem.SubscriptionId, Name = "long", FeedUrlProtected = "unused", IsEnabled = false,
            RefreshIntervalMinutes = 30
        });
        db.FeedItems.Add(feedItem);
        await db.SaveChangesAsync();
        Assert.Equal(task.NamingIngredients,
                     await db.FeedItems.AsNoTracking().Where(item => item.Id == feedItem.Id)
                           .Select(item => item.NamingIngredients).SingleAsync());
    }

    [Fact]
    public async Task CreateUpload_WithParseableTitle_RendersBangumiNameAndStoresIngredients()
    {
        using var harness = await CreateHarnessAsync();
        // 绑定预置 Subject 42（中文名「测试作品」）：{title} 用 Bangumi 中文名而非解析标题。
        var title =
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头 / きみが死ぬまで恋をしたい / Kimi ga Shinu made Koi wo Shitai][12][1080p][简日双语].mp4";
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(title));

        var task = await harness.GetTaskAsync(taskId);
        // 默认模板渲染：{source} 缺失自然收缩。
        var expectedSnapshot = "[Anime]测试作品 E12";
        Assert.Equal(expectedSnapshot, task.NamingSnapshot);
        Assert.Equal(42, task.SubjectId);
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        Assert.NotNull(ingredients);
        Assert.Equal(expectedSnapshot, new ReleaseNamePreviewPolicy().Create(ingredients));
        Assert.Equal("测试作品", ingredients.Title);
        Assert.Equal("12", ingredients.EpisodeText);
        Assert.Equal("喵萌奶茶屋", ingredients.Group);

        // 沿用既有提交管线：提交与完成改名都使用模板渲染的命名。
        await harness.ReconcileAsync();
        var add = Assert.Single(harness.Store.AddCalls);
        Assert.Equal(expectedSnapshot, add.Rename);
        var hash = TestTorrentBytes.InfoHashOf(title);
        CompleteTorrent(harness, hash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        var renameFile = Assert.Single(harness.Store.RenameFileCalls);
        Assert.Equal("原文件名.mkv", renameFile.OldPath);
        Assert.Equal(expectedSnapshot + ".mkv", renameFile.NewPath);
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(expectedSnapshot, rename.Name);
    }

    [Fact]
    public async Task CreateUpload_UsesSeparateFileTemplateAndSnapshotsBangumiAndAniParserValues()
    {
        using var harness = await CreateHarnessAsync();
        const string seedTemplate = "Seed {bgm.id} {bgm.nameCn} E{ani.episode}";
        const string fileTemplate = "{ani.title} E{ani.episode} - {bgm.name}";
        await harness.SaveNamingTemplateAsync(seedTemplate);
        await harness.SaveFileNamingTemplateAsync(fileTemplate);
        var title = NekoTitle("简日双语");

        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(title));
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal("Seed 42 测试作品 E12", task.NamingSnapshot);
        var ingredients = Assert.IsType<TorrentNamingIngredients>(TorrentNamingIngredients.FromJson(task.NamingIngredients));
        Assert.Equal("测试作品", ingredients.BangumiNameCn);
        Assert.Equal(42, ingredients.BangumiId);
        Assert.False(string.IsNullOrWhiteSpace(ingredients.AniTitle));
        Assert.Equal(seedTemplate, ingredients.Template);
        Assert.Equal(fileTemplate, ingredients.FileTemplate);

        var hash = TestTorrentBytes.InfoHashOf(title);
        harness.Store.Files[hash] = [title];
        await harness.ReconcileAsync();
        CompleteTorrent(harness, hash);
        await harness.ReconcileAsync();

        Assert.Equal("Seed 42 测试作品 E12", Assert.Single(harness.Store.RenameCalls).Name);
        Assert.Equal("Seed 42 测试作品 E12.mp4", Assert.Single(harness.Store.RenameFileCalls).NewPath);
    }

    [Fact]
    public async Task CreateUpload_WithoutSubjectBinding_RejectsAndAsksForBangumiId()
    {
        using var harness = await CreateHarnessAsync();
        // 标题映射未命中且未填 ID：拒绝登记并要求补 Bangumi ID，不落任何任务。
        var title = NekoTitle("简日双语");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                                harness
                                                                                   .CreateUploadAsync(TestTorrentBytes
                                                                                           .Create(title),
                                                                                        subjectId : null));

        Assert.Contains("Bangumi ID", exception.Message);
        Assert.Equal(0, await CountTasksAsync(harness));
    }

    [Fact]
    public async Task CreateUpload_WithExplicitSubjectId_BindsAndLearnsTitleMapping()
    {
        using var harness = await CreateHarnessAsync();
        // 表单显式 ID 绑定：元数据经 Bangumi API 拉取落库，解析标题回写映射。
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), null, 7);

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(7, task.SubjectId);
        Assert.Equal("[Anime]测试作品7 E12", task.NamingSnapshot);
        await using var db = await harness.CreateDbContextAsync();
        Assert.True(await db.SubjectTitleMappings.AsNoTracking().AnyAsync(mapping =>
                                                                              mapping.NormalizedTitle ==
                                                                              FeedItemEvaluator
                                                                                 .NormalizeTitle(NekoParsedTitle()) &&
                                                                              mapping.SubjectId == 7));
    }

    [Fact]
    public async Task CreateUpload_WithUnparseableTitle_FallsBackToRawNameWithoutIngredients()
    {
        using var harness = await CreateHarnessAsync();

        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Uploaded Anime - 01"));

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal("Uploaded Anime - 01", task.NamingSnapshot);
        Assert.Null(task.NamingIngredients);
    }

    [Fact]
    public async Task Reconcile_SameNamingAcrossIsolationFolders_CompletesWithoutConflict()
    {
        using var harness = await CreateHarnessAsync();
        var       itemIdA = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdA = await harness.CreateDownloadAsync(itemIdA);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdA)).Status);

        // 同集不同字幕组：命名预览相同、hash 不同、隔离目录不同，完成时不得按名字误判冲突。
        var itemIdB = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 [GroupB]");
        var taskIdB = await harness.CreateDownloadAsync(itemIdB);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 [GroupB]"));
        await harness.ReconcileAsync();

        var completedB = await harness.GetTaskAsync(taskIdB);
        Assert.Equal(DownloadTaskStatus.Completed, completedB.Status);
        Assert.Null(completedB.FailedStep);
    }

    [Fact]
    public async Task Reconcile_NewReplacementFailure_LeavesOldResourceUntouched()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();

        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        harness.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("提交", failed.FailedStep);
        var untouched = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Downloading, untouched.Status);
        // 被拒绝的 v2 未进入下载器，旧资源是唯一在下载器中的任务。
        var snapshot = Assert.Single(harness.Store.Torrents.Values);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Test Anime - 01"), snapshot.Hash);
    }

    [Fact]
    public async Task Create_ReplacementTargetsCompletedVersion_SkipsFailedIntermediate()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        // v2 提交被下载器拒绝：失败任务没有可供替代的旧文件。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        harness.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Failed, (await harness.GetTaskAsync(taskIdV2)).Status);

        // v3 的替换对象应是已完成的 v1，而不是失败的 v2。
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV3)).ReplacesTaskId);

        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(taskIdV1)).Status);
        var failedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Failed, failedV2.Status);
        Assert.Null(failedV2.ReplacedByTaskId);
    }

    [Fact]
    public async Task Reconcile_LaterVersionCompletingAfterOlder_LinksReplacementAtCompletion()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);

        // 两个任务都登记后才开始下载：创建 v2 时 v1 尚未完成，创建时无法记录替换对象。
        await harness.ReconcileAsync();
        Assert.Null((await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);

        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        var completedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, completedV1.Status);
        Assert.Null(completedV1.ReplacedByTaskId);

        // v2 完成时补记替换关系：v1 被标记为被替换。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();

        var replacedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV1.Status);
        Assert.Equal(taskIdV2, replacedV1.ReplacedByTaskId);
        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Equal(taskIdV1, completedV2.ReplacesTaskId);
    }

    [Fact]
    public async Task Reconcile_OlderVersionCompletingLast_DoesNotReplaceNewerResource()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        await harness.ReconcileAsync();

        // v2（更高版本）先完成、v1 后完成：旧版本不得反向把新版本标记为被替换。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Null(completedV2.ReplacedByTaskId);
        Assert.Null(completedV2.ReplacesTaskId);
        var completedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, completedV1.Status);
        Assert.Null(completedV1.ReplacesTaskId);
    }

    [Fact]
    public async Task Reconcile_StaleRecordedReplacement_ReSelectedAtCompletion()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        // v2、v3 都在 v1 已完成时创建：两者创建时都记录“替换 v1”。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV3)).ReplacesTaskId);

        // v2 先完成并替换 v1，使 v3 的记录过期。
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(taskIdV1)).Status);

        // v3 完成时重新选择：替换对象应是当前有效的 v2，而不是已被替换的 v1。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();

        var completedV3 = await harness.GetTaskAsync(taskIdV3);
        Assert.Equal(DownloadTaskStatus.Completed, completedV3.Status);
        Assert.Equal(taskIdV2, completedV3.ReplacesTaskId);
        var replacedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV2.Status);
        Assert.Equal(taskIdV3, replacedV2.ReplacedByTaskId);
        // v1 的替换记录保持指向 v2，不被 v3 覆盖。
        var replacedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV1.Status);
        Assert.Equal(taskIdV2, replacedV1.ReplacedByTaskId);
    }

    [Fact]
    public async Task Reconcile_NewerVersionCompletedFirst_ClearsStaleDeclarationOnLaterCompletion()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        // v2、v3 创建时都记录替换 v1。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);

        // v3 先完成并替换 v1，使 v2 的声明过期。
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();
        Assert.Equal(taskIdV3, (await harness.GetTaskAsync(taskIdV1)).ReplacedByTaskId);

        // v2 后完成：v1 已被 v3 替换、v3 版本更高不可反向替换，过期声明应被清除而非保留。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();

        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Null(completedV2.ReplacesTaskId);
        var replacedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV1.Status);
        Assert.Equal(taskIdV3, replacedV1.ReplacedByTaskId);
        var completedV3 = await harness.GetTaskAsync(taskIdV3);
        Assert.Equal(DownloadTaskStatus.Completed, completedV3.Status);
        Assert.Equal(taskIdV1, completedV3.ReplacesTaskId);
    }

    [Fact]
    public async Task Reconcile_MissingTorrentMarksLostAndRetryResubmits()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        harness.Store.Torrents.Remove(TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        var lost = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Lost, lost.Status);
        Assert.Equal("对账", lost.FailedStep);

        await harness.Service.RetryAsync(taskId);
        await harness.ReconcileAsync();
        var retried = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, retried.Status);
        Assert.Equal(2, harness.Store.AddCalls.Count);
    }

    [Fact]
    public async Task Reconcile_UnconfiguredDownloader_SkipsSilently()
    {
        using var harness = await CreateHarnessAsync(false);
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");

        // 未启用下载器时创建意图被拒绝，对账静默跳过，不产生任何任务或提交。
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));
        await harness.ReconcileAsync();
        Assert.Empty(harness.Store.AddCalls);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.DownloadTasks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Reconcile_ServerErrorOnTrackedTask_MarksFailedWithDownloaderStep()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Error
        };

        await harness.ReconcileAsync();
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("下载器", failed.FailedStep);
    }

    private static void CompleteTorrent(TestHarness harness, string hash)
    {
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
        };
    }

    [Fact]
    public async Task TorrentInDownloader_ReflectsPresenceAndToleranceOfUnreachableDownloader()
    {
        using var harness = await CreateHarnessAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Delete Me - 01"));

        // 未提交：下载器中不存在。
        Assert.False(await harness.Service.TorrentInDownloaderAsync(taskId));

        await harness.ReconcileAsync();
        Assert.True(await harness.Service.TorrentInDownloaderAsync(taskId));

        // 下载器不可达时按未知处理，不阻断删除面板。
        harness.Factory.ThrowOnCreate = true;
        Assert.Null(await harness.Service.TorrentInDownloaderAsync(taskId));

        // 下载器被停用后同样返回未知。
        await harness.SaveDisabledDownloaderSettingsAsync();
        Assert.Null(await harness.Service.TorrentInDownloaderAsync(taskId));
    }

    [Fact]
    public async Task Delete_RemovesRecordOnlyAndKeepsDownloaderTorrentByDefault()
    {
        using var harness = await CreateHarnessAsync();
        var       bytes   = TestTorrentBytes.Create("Delete Me - 01");
        var       taskId  = await harness.CreateUploadAsync(bytes);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Delete Me - 01");

        await harness.Service.DeleteAsync(taskId, false, false);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.DownloadTasks.AsNoTracking().ToListAsync());
        Assert.True(harness.Store.Torrents.ContainsKey(hash));
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WithDownloaderTorrent_RemovesTorrentAndForwardsDeleteFilesFlag()
    {
        using var harness = await CreateHarnessAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Delete Me - 01"));
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Delete Me - 01");

        await harness.Service.DeleteAsync(taskId, true, true);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.DownloadTasks.AsNoTracking().ToListAsync());
        Assert.False(harness.Store.Torrents.ContainsKey(hash));
        Assert.Equal([(hash, true)], harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_WithoutDownloaderTorrent_SkipsDownloaderStepIdempotently()
    {
        using var harness = await CreateHarnessAsync();
        // 停留在待提交：下载器中尚无种子。勾选了删除下载器种子也应静默跳过而不是失败。
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Never Submitted - 01"));

        await harness.Service.DeleteAsync(taskId, true, false);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.DownloadTasks.AsNoTracking().ToListAsync());
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_DownloaderFailureKeepsRecordForRetry()
    {
        using var harness = await CreateHarnessAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Downloader Down - 01"));
        await harness.ReconcileAsync();
        harness.Factory.ThrowOnCreate = true;

        await Assert.ThrowsAsync<QbittorrentException>(() =>
                                                           harness.Service.DeleteAsync(taskId,
                                                               true, false));

        Assert.NotNull(await harness.GetTaskAsync(taskId));
    }

    [Fact]
    public async Task Delete_CleansReplacementReferencesAndRestoresReplacedTask()
    {
        using var harness   = await CreateHarnessAsync();
        var       oldTask   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Old Version - 01"));
        var       newTask   = await harness.CreateUploadAsync(TestTorrentBytes.Create("New Version - 01"));
        var       thirdTask = await harness.CreateUploadAsync(TestTorrentBytes.Create("Third Version - 01"));
        await using (var db = await harness.CreateDbContextAsync())
        {
            // oldTask 已被 newTask 替换（曾完成、文件仍在）；thirdTask 计划完成后替换 newTask。
            var oldRow = await db.DownloadTasks.SingleAsync(value => value.Id == oldTask);
            oldRow.Status           = DownloadTaskStatus.Replaced;
            oldRow.ReplacedByTaskId = newTask;
            oldRow.CompletedAtUtc   = harness.Timestamp;
            var newRow = await db.DownloadTasks.SingleAsync(value => value.Id == newTask);
            newRow.Status         = DownloadTaskStatus.Completed;
            newRow.ReplacesTaskId = oldTask;
            var thirdRow = await db.DownloadTasks.SingleAsync(value => value.Id == thirdTask);
            thirdRow.ReplacesTaskId = newTask;
            await db.SaveChangesAsync();
        }

        await harness.Service.DeleteAsync(newTask, false, false);

        await using var verify = await harness.CreateDbContextAsync();
        Assert.Null(await verify.DownloadTasks.AsNoTracking()
                                .SingleOrDefaultAsync(value => value.Id == newTask));
        // 替换者被删除后，旧任务回退为已完成（文件仍是当前版本），第三任务的替换声明清空。
        var restoredOld = await verify.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == oldTask);
        Assert.Equal(DownloadTaskStatus.Completed, restoredOld.Status);
        Assert.Null(restoredOld.ReplacedByTaskId);
        var remainingThird = await verify.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == thirdTask);
        Assert.Null(remainingThird.ReplacesTaskId);
    }

    /// <summary>从未提交的落选任务保留终止墓碑；删除优胜者不得批量复活候选。</summary>
    [Fact]
    public async Task Delete_KeepsNeverSubmittedReplacedLoserAsTombstone()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var tcTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        AddNekoFeedItem(harness, "neko-jpsc", NekoTitle("简日双语"));
        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview");

        var replaced = await harness.GetTaskAsync(tcTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, replaced.Status);
        Assert.Null(replaced.CompletedAtUtc);

        var scTaskId = result.Items.Single(item => item.Outcome == RssDownloadItemOutcome.Created).TaskId!.Value;
        await harness.Service.DeleteAsync(scTaskId, false, false);

        var restored = await harness.GetTaskAsync(tcTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, restored.Status);
        Assert.Null(restored.ReplacedByTaskId);

        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(tcTaskId)).Status);
        Assert.Empty(harness.Store.AddCalls);
    }

    [Fact]
    public async Task PrecheckBulkDelete_CountsExistingTasksAndDownloaderTorrents()
    {
        using var harness = await CreateHarnessAsync();
        var       first   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk A - 01"));
        var       second  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk B - 01"));
        await harness.ReconcileAsync();
        // 对账后再登记的任务停留在待提交：构造“部分在下载器中”的混合场景。
        var pending = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk C - 01"));

        var precheck = await harness.Service.PrecheckBulkDeleteAsync([first, pending]);
        Assert.Equal(2, precheck.TaskCount);
        Assert.Equal(1, precheck.TorrentsInDownloader);
        Assert.True(precheck.DownloaderChecked);

        // 已删除的任务不计入；下载器不可达时按未知处理。
        await harness.Service.DeleteAsync(second, false, false);
        Assert.Equal(1, (await harness.Service.PrecheckBulkDeleteAsync([second, pending])).TaskCount);

        harness.Factory.ThrowOnCreate = true;
        var unknown = await harness.Service.PrecheckBulkDeleteAsync([first, pending]);
        Assert.Equal(2, unknown.TaskCount);
        Assert.False(unknown.DownloaderChecked);
    }

    [Fact]
    public async Task DeleteMany_RemovesSelectedTasksAndDownloaderTorrentsInOnePass()
    {
        using var harness = await CreateHarnessAsync();
        var       first   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk A - 01"));
        var       second  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk B - 01"));
        var       third   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk C - 01"));
        await harness.ReconcileAsync();
        var hashA = TestTorrentBytes.InfoHashOf("Bulk A - 01");
        var hashC = TestTorrentBytes.InfoHashOf("Bulk C - 01");

        var deleted = await harness.Service.DeleteManyAsync([first, third], true, true);

        Assert.Equal(2, deleted);
        await using var db        = await harness.CreateDbContextAsync();
        var             remaining = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(second, remaining.Id);
        Assert.False(harness.Store.Torrents.ContainsKey(hashA));
        Assert.False(harness.Store.Torrents.ContainsKey(hashC));
        Assert.True(harness.Store.Torrents.ContainsKey(TestTorrentBytes.InfoHashOf("Bulk B - 01")));
        Assert.Equal(2, harness.Store.DeleteCalls.Count);
        Assert.All(harness.Store.DeleteCalls, call => Assert.True(call.DeleteFiles));
        Assert.Contains((hashA, true), harness.Store.DeleteCalls);
        Assert.Contains((hashC, true), harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteMany_ToleratesMissingIdsAndSkipsAbsentTorrents()
    {
        using var harness = await CreateHarnessAsync();
        var       first   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk A - 01"));
        var       second  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk B - 01"));

        // 未提交：下载器中没有种子；夹带的无效 Id 静默跳过。
        var deleted = await harness.Service.DeleteManyAsync([first, Guid.NewGuid()],
                                                            true, false);

        Assert.Equal(1, deleted);
        Assert.NotNull(await harness.GetTaskAsync(second));
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(second, (await db.DownloadTasks.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteMany_CleansReplacementReferencesAcrossBatch()
    {
        using var harness     = await CreateHarnessAsync();
        var       oldTask     = await harness.CreateUploadAsync(TestTorrentBytes.Create("Batch Old - 01"));
        var       replaceTask = await harness.CreateUploadAsync(TestTorrentBytes.Create("Batch New - 01"));
        var       otherTask   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Batch Other - 01"));
        await using (var db = await harness.CreateDbContextAsync())
        {
            // oldTask 已被 replaceTask 替换（曾完成、文件仍在）；一次性删除替换者与其余任务，旧任务应回退为已完成。
            var oldRow = await db.DownloadTasks.SingleAsync(value => value.Id == oldTask);
            oldRow.Status           = DownloadTaskStatus.Replaced;
            oldRow.ReplacedByTaskId = replaceTask;
            oldRow.CompletedAtUtc   = harness.Timestamp;
            await db.SaveChangesAsync();
        }

        var deleted = await harness.Service.DeleteManyAsync([replaceTask, otherTask],
                                                            false, false);

        Assert.Equal(2, deleted);
        var restored = await harness.GetTaskAsync(oldTask);
        Assert.Equal(DownloadTaskStatus.Completed, restored.Status);
        Assert.Null(restored.ReplacedByTaskId);
        Assert.Null(restored.ReplacesTaskId);
    }

    /// <summary>与命名预览同源生成的命名要素：渲染结果必须恰好等于 naming（"测试作品 E01 [Sc]" 形态）。</summary>
    private static string BuildNamingIngredients(decimal episode, int version)
    {
        return new TorrentNamingIngredients("{title} E{episode} {version} [{group}]", "测试作品",
                                            ReleaseNamePreviewPolicy.FormatEpisodeText(episode), version, "Sc",
                                            null).ToJson();
    }

    private static async Task SeedSubjectAsync(TestHarness harness, int subjectId)
    {
        await using var db = await harness.CreateDbContextAsync();
        if (!await db.MediaSubjects.AnyAsync(subject => subject.Id == subjectId))
        {
            db.MediaSubjects.Add(new MediaSubject
            {
                Id = subjectId, Name = "Test Subject", NameCn = "测试作品", Platform = "TV",
                CachedAtUtc = harness.Timestamp
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Guid> SeedAcceptedItemAsync(TestHarness harness, string stableId, string torrentName,
                                                          string      naming      = NamingV1, int version = 1,
                                                          decimal     episode     = 1,
                                                          string?     parsedTitle = null, string? language = null,
                                                          string?     preference  = null, int? subjectId = 42)
    {
        harness.TorrentNames[stableId] = torrentName;
        await using var db             = await harness.CreateDbContextAsync();
        var             subscriptionId = Guid.NewGuid();
        db.MediaSubscriptions.Add(new MediaSubscription
        {
            Id        = subscriptionId, Name          = $"订阅-{stableId}", FeedUrlProtected = "unused",
            IsEnabled = false, RefreshIntervalMinutes = 30
        });
        var itemId = Guid.NewGuid();
        db.FeedItems.Add(new FeedItem
        {
            Id                = itemId, SubscriptionId = subscriptionId, StableId = stableId, Title = torrentName,
            TorrentUrl        = $"https://mikanani.me/Download/{stableId}.torrent",
            FirstSeenAtUtc    = harness.Timestamp.AddHours(-1), Status      = FeedItemStatus.Accepted,
            DecisionStatus    = ReleaseDecisionStatus.Accepted, ParsedTitle = parsedTitle ?? torrentName,
            MediaType         = "SingleEpisode", Episode                    = episode, Version = version, Group = "S1",
            Language          = language ?? "Sc", Resolution                = "R1080P", SubjectId = subjectId,
            Preference        = preference, Reason                          = "测试决策。",
            NamingPreview     = naming,
            NamingIngredients = BuildNamingIngredients(episode, version),
            EvaluatedAtUtc    = harness.Timestamp
        });
        // P4 批量择优：条目必须是其所在同集组的当前优胜候选才能创建下载任务。
        // 组键 (SubjectId, MediaType, Episode) 有唯一索引；同一测试内同组再播种条目时
        // 以 stableId 区分 MediaType，保证每个条目都能作为优胜通过创建前置核验。
        var groupKey  = $"42|SingleEpisode|{episode}";
        var mediaType = harness.UsedWinnerGroups.Add(groupKey) ? "SingleEpisode" : $"SingleEpisode#{stableId}";
        db.CandidateSelections.Add(new CandidateSelection
        {
            Id               = Guid.NewGuid(), SubjectId = 42, MediaType       = mediaType, Episode = episode,
            WinnerFeedItemId = itemId, RuleVersion       = 1, RankingBasis     = "测试优胜",
            NamingSnapshot   = naming, IntentTaskId      = null, SelectedAtUtc = harness.Timestamp
        });
        await db.SaveChangesAsync();
        return itemId;
    }

    internal static Task<TestHarness> CreateHarnessForPageTestsAsync()
    {
        return CreateHarnessAsync();
    }

    private static async Task<TestHarness> CreateHarnessAsync(bool configureDownloader = true)
    {
        var directory           = Directory.CreateTempSubdirectory("MediaManager.P3.Tests.");
        var timestamp           = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var harnessTorrentNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var services            = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "p3.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
        var store = new FakeTorrentStore();
        services.AddSingleton(store);
        services.AddSingleton<FakeQbittorrentSessionFactory>();
        services.AddSingleton<IQbittorrentSessionFactory>(provider =>
                                                              provider
                                                                 .GetRequiredService<FakeQbittorrentSessionFactory>());
        var feedReader = new FakeFeedSourceReader();
        services.AddSingleton(feedReader);
        services.AddSingleton<IFeedSourceReader>(feedReader);
        var torrentFileSource = new FakeTorrentFileSource(uri =>
        {
            // 每个条目的种子名由 seeding 注册；未注册的条目按 stableId 生成独立种子。
            var stableId =
                Path.GetFileNameWithoutExtension(uri.Segments[^1]);
            return TestTorrentBytes.Create(harnessTorrentNames
                                              .GetValueOrDefault(stableId, stableId));
        });
        services.AddSingleton<ITorrentFileSource>(torrentFileSource);
        var subjectProvider = new FakeSubjectProvider(timestamp);
        var linkResolver    = new FakeSubjectLinkResolver();
        services.AddSingleton<ISubjectMetadataProvider>(subjectProvider);
        services.AddSingleton<ISubjectLinkResolver>(linkResolver);
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddNotificationTestFakes();
        services.AddSingleton<AniParser>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        services.AddSingleton<MediaManager.Core.Subjects.SubjectEpisodeCorrectionPolicy>();
        services.AddScoped<ManageSelectionRuleSettings>();
        services.AddScoped<ISelectionRuleProvider, SettingsSelectionRuleProvider>();
        services.AddScoped<NamingTemplateStore>();
        services.AddSingleton<DownloadCoordinationGate>();
        services.AddScoped<DownloadService>();
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：Identity 服务注册影响
        // ApplicationDbContext 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new TestHarness(directory, timestamp, provider, store,
                                      provider.GetRequiredService<FakeQbittorrentSessionFactory>(),
                                      provider.GetRequiredService<DownloadService>(), harnessTorrentNames,
                                      feedReader, torrentFileSource, subjectProvider, linkResolver);
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.MediaSubjects.Add(new MediaSubject
        {
            Id          = 42, Name = "Test Subject", NameCn = "测试作品", Platform = "TV",
            CachedAtUtc = timestamp
        });
        await db.SaveChangesAsync();

        if (configureDownloader) await harness.SaveDownloaderSettingsAsync();

        return harness;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }

    /// <summary>Bangumi 元数据替身：未特制时按 Id 生成确定性元数据（中文名优先供命名断言）。</summary>
    internal sealed class FakeSubjectProvider(DateTimeOffset timestamp) : ISubjectMetadataProvider
    {
        public Dictionary<int, SubjectMetadata> Overrides { get; } = [];

        public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Overrides.TryGetValue(subjectId, out var metadata)
                                       ? metadata
                                       : new SubjectMetadata(subjectId, $"Subject {subjectId}",
                                                             $"测试作品{subjectId}", "TV", "2026-01-01", null,
                                                             timestamp));
        }
    }

    /// <summary>条目链接解析替身：按绝对地址精确返回配置的 Subject ID，未配置视为解析不出。</summary>
    internal sealed class FakeSubjectLinkResolver : ISubjectLinkResolver
    {
        public Dictionary<string, int> ResolvedLinks { get; } = new(StringComparer.Ordinal);

        public Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = null,
                                                CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolvedLinks.TryGetValue(episodeUri.AbsoluteUri, out var subjectId)
                                       ? (int?)subjectId
                                       : null);
        }
    }

    /// <summary>条目 stableId 与种子名的注册表；种子字节按种子名确定性生成，同名单同 hash。</summary>
    internal sealed class TestHarness(
        DirectoryInfo                 directory,
        DateTimeOffset                timestamp,
        ServiceProvider               provider,
        FakeTorrentStore              store,
        FakeQbittorrentSessionFactory factory,
        DownloadService               service,
        Dictionary<string, string>    torrentNames,
        FakeFeedSourceReader          feedReader,
        FakeTorrentFileSource         torrentFileSource,
        FakeSubjectProvider           subjectProvider,
        FakeSubjectLinkResolver       linkResolver) : IDisposable
    {
        public DirectoryInfo                 Directory        { get; } = directory;
        public DateTimeOffset                Timestamp        { get; } = timestamp;
        public FakeTorrentStore              Store            { get; } = store;
        public FakeQbittorrentSessionFactory Factory          { get; } = factory;
        public DownloadService               Service          { get; } = service;
        public IServiceProvider Provider { get; } = provider;
        public Dictionary<string, string>    TorrentNames     { get; } = torrentNames;
        public HashSet<string>               UsedWinnerGroups { get; } = new(StringComparer.Ordinal);
        public FakeFeedSourceReader          FeedReader       { get; } = feedReader;
        public FakeTorrentFileSource         TorrentSource    { get; } = torrentFileSource;
        public FakeSubjectProvider           SubjectProvider  { get; } = subjectProvider;
        public FakeSubjectLinkResolver       LinkResolver     { get; } = linkResolver;

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                Directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        public Task<Guid> CreateDownloadAsync(Guid itemId)
        {
            return Service.CreateForFeedItemAsync(itemId);
        }

        /// <summary>
        ///     上传包装：默认绑定预置 Subject 42（多数用例只关心登记后的管线行为）；
        ///     门禁相关用例显式传 subjectId 覆盖（null 表示不绑定）。
        /// </summary>
        public Task<Guid> CreateUploadAsync(byte[] torrentBytes, string? siteSource = null, int? subjectId = 42)
        {
            return Service.CreateFromUploadAsync(torrentBytes, siteSource, subjectId);
        }

        public Task<RssDownloadResult> CreateFromRssFeedAsync(string feedUrl, Guid? proxyId = null,
                                                              int?   subjectId = null)
        {
            return Service.CreateFromRssFeedAsync(feedUrl, proxyId, subjectId);
        }

        /// <summary>落 Subject 元数据与标题映射：rawTitles 用评估器同源归一化，供绑定门禁命中。</summary>
        public async Task SeedSubjectMappingAsync(int             subjectId, string name, string nameCn,
                                                  params string[] rawTitles)
        {
            await using var db = await CreateDbContextAsync();
            if (!await db.MediaSubjects.AnyAsync(subject => subject.Id == subjectId))
                db.MediaSubjects.Add(new MediaSubject
                {
                    Id = subjectId, Name = name, NameCn = nameCn, Platform = "TV", CachedAtUtc = Timestamp
                });

            foreach (var rawTitle in rawTitles)
                db.SubjectTitleMappings.Add(new SubjectTitleMapping
                {
                    Id              = Guid.NewGuid(),
                    NormalizedTitle = FeedItemEvaluator.NormalizeTitle(rawTitle),
                    SubjectId       = subjectId,
                    UpdatedAtUtc    = Timestamp
                });
            await db.SaveChangesAsync();
        }

        public Task ReconcileAsync()
        {
            return Service.ReconcileAsync();
        }

        public async Task<DownloadTask> GetTaskAsync(Guid taskId)
        {
            await using var db = await CreateDbContextAsync();
            return await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        }

        public Task SaveDownloaderSettingsAsync()
        {
            var settingsService =
                new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(), Factory);
            return settingsService.SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", "/downloads",
                                                                     true), "secret");
        }

        public Task SaveDisabledDownloaderSettingsAsync()
        {
            var settingsService =
                new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(), Factory);
            return settingsService.SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", "/downloads",
                                                                     false), "secret");
        }

        public Task SaveNamingTemplateAsync(string template)
        {
            var namingTemplateStore = new NamingTemplateStore(provider.GetRequiredService<IApplicationSettingStore>());
            return namingTemplateStore.SaveAsync(template);
        }

        public Task SaveFileNamingTemplateAsync(string template)
        {
            var namingTemplateStore = new NamingTemplateStore(provider.GetRequiredService<IApplicationSettingStore>());
            return namingTemplateStore.SaveFileTemplateAsync(template);
        }

        public Task SaveSelectionRulesAsync(SelectionRuleConfig config)
        {
            var ruleSettings = new ManageSelectionRuleSettings(
                                                               provider.GetRequiredService<IApplicationSettingStore>(),
                                                               NullLogger<ManageSelectionRuleSettings>.Instance);
            return ruleSettings.SaveAsync(config);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync()
        {
            var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            return factory.CreateDbContextAsync();
        }
    }
}
