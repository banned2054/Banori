using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banned.Qbittorrent.Exceptions;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MonoTorrent.BEncoding;
using System.Text;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>
///     P3 下载闭环验收：响应丢失、提交后重启、重复触发、人工暂停与安全替换。
///     全部使用假下载器会话与真实 SQLite 临时库，不触发真实下载。
/// </summary>
public sealed partial class DownloadServiceTests
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
        // 未识别为正片的种子留在下载根目录，不再按 hash 隔离。
        Assert.Equal("/downloads", task.SavePath);
        Assert.Equal("/downloads", task.DownloadRootPath);
        Assert.Equal(DownloadDirectorySettings.DefaultMediaDirectory, task.MediaDirectory);
        Assert.Equal(DownloadDirectorySettings.DefaultSeedDirectory, task.SeedDirectory);
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
        // 上传标题无法解析、经表单 ID 绑定预置 Subject；不落集数，命名快照保留原名。
        Assert.Equal(42, task.SubjectId);
        Assert.Null(task.Episode);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Uploaded Anime - 01"), task.InfoHash);
        Assert.Equal("upload://" + task.InfoHash, task.TorrentUrl);
        Assert.Equal("Uploaded Anime - 01", task.NamingSnapshot);
        Assert.Equal("/downloads", task.SavePath);

        // 沿用既有提交管线：一轮对账后进入下载中，提交登记时选择的下载根目录。
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
            Assert.Equal("/downloads", task.SavePath);
            Assert.Equal("/downloads", task.DownloadRootPath);
            Assert.Equal(DownloadDirectorySettings.DefaultMediaDirectory, task.MediaDirectory);
            Assert.Equal(DownloadDirectorySettings.DefaultSeedDirectory, task.SeedDirectory);
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
                                               TitleNormalizer.Normalize(NekoParsedTitle()))
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
                                                                              TitleNormalizer
                                                                                 .Normalize(NekoParsedTitle()) &&
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

    [Theory]
    [InlineData("upload", true)]
    [InlineData("rss", true)]
    [InlineData("subscription", true)]
    [InlineData("upload", false)]
    [InlineData("rss", false)]
    [InlineData("subscription", false)]
    public async Task Intake_SnapshotsDirectoriesAndOriginalFileIdentity_ForEveryEntryPoint(
        string entryPoint, bool contentOnly)
    {
        using var harness     = await CreateHarnessAsync(enableFileArchive : true);
        var       directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        await directories.SaveAsync("Library/Anime", "Seeding/Originals");
        var  original = contentOnly ? NekoTitle("简日双语") : "Unparseable original.mkv";
        var  bytes    = TestTorrentBytes.Create(original);
        Guid taskId;
        switch (entryPoint)
        {
            case "upload" :
                taskId = await harness.CreateUploadAsync(bytes);
                break;
            case "rss" :
            {
                harness.TorrentNames["directory-snapshot"] = original;
                harness.FeedReader.Items.Add(RssItem("directory-snapshot", original,
                                                     "https://mikanani.me/Download/directory-snapshot.torrent"));
                var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/preview", subjectId : 42);
                Assert.Equal(0, result.FailedCount);
                taskId = Assert.Single(result.Items).TaskId!.Value;
                break;
            }
            default :
            {
                var itemId = await SeedAcceptedItemAsync(harness, "directory-snapshot", original);
                taskId = await harness.CreateDownloadAsync(itemId);
                break;
            }
        }

        // 创建后全局目录与下载根设置变更不能覆写已登记任务，也不能影响本次提交。
        await directories.SaveAsync("New/Anime", "New/Seed");
        await harness.Provider.GetRequiredService<ManageQbittorrentSettings>().SaveAsync(
         new QbittorrentSettings("http://qb.test:8080", "admin", "/future-downloads", true), "secret");
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal("/downloads", task.DownloadRootPath);
        Assert.Equal("Library/Anime", task.MediaDirectory);
        Assert.Equal("Seeding/Originals", task.SeedDirectory);
        Assert.Equal("/downloads", task.SavePath);
        Assert.Equal(bytes, task.TorrentFile);
        await using var db = await harness.CreateDbContextAsync();
        var file = Assert.Single(await db.DownloadTaskFiles.AsNoTracking()
                                         .Where(value => value.DownloadTaskId == taskId).ToListAsync());
        Assert.Equal(0, file.FileIndex);
        Assert.Equal(1024, file.Size);
        Assert.Equal(original, file.OriginalRelativePath);
        Assert.Equal(original, file.CurrentRelativePath);
        Assert.Equal(contentOnly ? DownloadFileKind.Anime : DownloadFileKind.Seed, file.Kind);
        Assert.False(file.IsManualOverride);
        Assert.False(string.IsNullOrWhiteSpace(file.ClassificationReason));
        Assert.Null(file.PendingRenameTo);
        Assert.Empty(harness.Store.AddCalls);

        await harness.ReconcileAsync();
        Assert.Equal(task.SavePath, Assert.Single(harness.Store.AddCalls).SavePath);
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Reconcile_FileArchive_UsesDirectorySnapshotAndPreservesOriginalNestedPaths()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        await directories.SaveAsync("Library/Anime", "Seeding/Originals");
        var title         = NekoTitle("简日双语");
        var originalPaths = new[] { $"Original Pack/Video/{title}", "Original Pack/Extras/notes.txt" };
        var bytes         = CreateMultiFileTorrent("Original Pack", ($"Video/{title}", 1024), ("Extras/notes.txt", 37));
        var taskId        = await harness.CreateUploadAsync(bytes);
        var registered    = await harness.GetTaskAsync(taskId);
        Assert.Equal("/downloads", registered.SavePath); // 混合种子必须以全局根为保存目录。
        Assert.Equal(12m, registered.Episode);           // 正片全部落在同一集：由文件集胜者充当任务身份。
        var ingredients =
            Assert.IsType<TorrentNamingIngredients>(TorrentNamingIngredients.FromJson(registered.NamingIngredients));
        Assert.Equal("12", ingredients.EpisodeText);
        Assert.Equal("{title}", ingredients.Template);
        Assert.Equal("{title} E{episode}", ingredients.FileTemplate);
        await using var db = await harness.CreateDbContextAsync();
        var originals = await db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal(originalPaths, originals.Select(file => file.OriginalRelativePath));
        Assert.Equal([1024L, 37], originals.Select(file => file.Size));
        Assert.Equal([DownloadFileKind.Anime, DownloadFileKind.Seed], originals.Select(file => file.Kind));
        Assert.Equal(originalPaths, originals.Select(file => file.CurrentRelativePath));

        // 提交前固定下载器文件布局：提交轮即完成文件选择与提前命名，播种必须先于提交。
        harness.Store.FileIdentities[registered.InfoHash] =
        [
            new TorrentFileSnapshot(1, originalPaths[1], 37), // 下载器顺序不是文件身份。
            new TorrentFileSnapshot(0, originalPaths[0], 1024)
        ];
        harness.Store.Files[registered.InfoHash] = [.. originalPaths];
        await directories.SaveAsync("Changed/Media", "Changed/Seed");
        await harness.ReconcileAsync();
        CompleteTorrent(harness, registered.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal("/downloads", completed.SavePath);
        Assert.Null(completed.PendingSavePath);
        // 目标无法证实为空：完成与归档分离，确认后按快照目录迁入正式布局。
        var heldReview = DownloadArchiveReview.FromJson(completed.ArchiveReview);
        Assert.Null(heldReview);
        Assert.Null(completed.ArchiveReview);

        var archived = await db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                               .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal(originalPaths, archived.Select(file => file.OriginalRelativePath));
        Assert.Equal([
            "Library/Anime/测试作品 E12.mp4",
            "Seeding/Originals/Original Pack/Extras/notes.txt"
        ], archived.Select(file => file.CurrentRelativePath));
        Assert.All(archived, file => Assert.Null(file.PendingRenameTo));
        Assert.Empty(harness.Store.SetLocationCalls);
        Assert.Empty(harness.Store.RenameFolderCalls);
        Assert.Equal(registered.NamingSnapshot, Assert.Single(harness.Store.RenameCalls).Name);
    }

    /// <summary>合集上传（无意义种子名、正片跨集）：不落集数身份，归档时逐文件按各自集数命名。</summary>
    [Fact]
    public async Task CreateUpload_MultiEpisodePack_RegistersAsBatchWithoutEpisodeIdentity()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        await directories.SaveAsync("Library/Anime", "Seeding/Originals");
        var title12       = NekoTitle("简日双语", episode : 12);
        var title13       = NekoTitle("简日双语", episode : 13);
        var originalPaths = new[] { $"Batch/Video/{title12}", $"Batch/Video/{title13}" };
        var bytes         = CreateMultiFileTorrent("Batch", ("Video/" + title12, 1024), ("Video/" + title13, 37));
        var taskId        = await harness.CreateUploadAsync(bytes);
        var registered    = await harness.GetTaskAsync(taskId);
        Assert.Null(registered.Episode); // 跨集正片视为合集：不参与同集裁决。
        Assert.Equal("/downloads",
                     registered.SavePath); // 全部正片：直接落在媒体目录。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var originals = await db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                                    .OrderBy(file => file.FileIndex).ToListAsync();
            Assert.Equal([DownloadFileKind.Anime, DownloadFileKind.Anime],
                         originals.Select(file => file.Kind));
            Assert.Equal(originalPaths, originals.Select(file => file.OriginalRelativePath));
        }

        // 提交前固定下载器文件布局：提交轮即完成文件选择与提前命名。
        harness.Store.FileIdentities[registered.InfoHash] =
        [
            new TorrentFileSnapshot(0, originalPaths[0], 1024),
            new TorrentFileSnapshot(1, originalPaths[1], 37)
        ];
        harness.Store.Files[registered.InfoHash] = [.. originalPaths];
        await harness.ReconcileAsync();
        CompleteTorrent(harness, registered.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status); // 完成与归档可用分别展示。
        await using var db2 = await harness.CreateDbContextAsync();
        var archived = await db2.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal(["Library/Anime/测试作品 E12.mp4", "Library/Anime/测试作品 E13.mp4"],
                     archived.Select(file => file.CurrentRelativePath));
    }

    /// <summary>同集多版本同包上传：包内用同一择优规则决胜，落选版本归 Seed，胜者充当任务身份。</summary>
    [Fact]
    public async Task CreateUpload_SameEpisodeVariants_ArbitratesInPackAndDemotesLoserToSeed()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules()); // 简日（JpSc）优先于繁日。
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        await directories.SaveAsync("Library/Anime", "Seeding/Originals");
        var scTitle       = NekoTitle("简日双语");
        var tcTitle       = NekoTitle("繁日双语");
        var originalPaths = new[] { $"Original Pack/Video/{scTitle}", $"Original Pack/Video/{tcTitle}" };
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37));
        var taskId     = await harness.CreateUploadAsync(bytes);
        var registered = await harness.GetTaskAsync(taskId);
        Assert.Equal(12m, registered.Episode); // 胜者版本充当任务身份。
        Assert.Equal("/downloads",
                     registered.SavePath); // 包内含落选 Seed 文件：以全局根为保存目录。
        await using var db = await harness.CreateDbContextAsync();
        var originals = await db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal([DownloadFileKind.Anime, DownloadFileKind.Seed], originals.Select(file => file.Kind));
        Assert.Equal("同集包内择优落选", originals[1].ClassificationReason);

        // 提交前固定下载器文件布局：胜出文件标记为已完整（整包完成时完成判定仍会核对所选文件进度）。
        harness.Store.FileIdentities[registered.InfoHash] =
        [
            new TorrentFileSnapshot(0, originalPaths[0], 1024, 1.0),
            new TorrentFileSnapshot(1, originalPaths[1], 37)
        ];
        harness.Store.Files[registered.InfoHash] = [.. originalPaths];
        await harness.ReconcileAsync();
        CompleteTorrent(harness, registered.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        var archived = await db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                               .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal("Library/Anime/测试作品 E12.mp4", archived[0].CurrentRelativePath);
        Assert.Equal("Seeding/Originals/Original Pack/Video/" + tcTitle, archived[1].CurrentRelativePath);
    }

    /// <summary>标题可解析的多文件包：身份仍来自标题，但包内同集多版本照常择优、落选归 Seed。</summary>
    [Fact]
    public async Task CreateUpload_TitledMultiFilePack_KeepsTitleIdentityAndDemotesLoserToSeed()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        var scTitle    = NekoTitle("简日双语");
        var tcTitle    = NekoTitle("繁日双语");
        var bytes      = CreateMultiFileTorrent(scTitle, ("Video/" + scTitle, 1024), ("Video/" + tcTitle, 37));
        var taskId     = await harness.CreateUploadAsync(bytes);
        var registered = await harness.GetTaskAsync(taskId);
        Assert.Equal(12m, registered.Episode);
        Assert.Equal("/downloads", registered.SavePath);
        await using var db = await harness.CreateDbContextAsync();
        var originals = await db.DownloadTaskFiles.AsNoTracking().Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal([DownloadFileKind.Anime, DownloadFileKind.Seed], originals.Select(file => file.Kind));
        Assert.Equal("同集包内择优落选", originals[1].ClassificationReason);
    }

    /// <summary>
    ///     停止添加主线：提交轮以停止状态添加（无网络下载、无文件操作），登记期落选文件带“不下载”标记，
    ///     系统准备暂停与人工暂停分开保存。
    /// </summary>
    [Fact]
    public async Task Preparation_StopsAddWithoutFileOperationsAndRecordsSelectionPlan()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules()); // 简日（JpSc）优先于繁日。
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        await directories.SaveAsync("Library/Anime", "Seeding/Originals");
        var scTitle = NekoTitle("简日双语");
        var tcTitle = NekoTitle("繁日双语");
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37));
        var  taskId     = await harness.CreateUploadAsync(bytes);
        var  registered = await harness.GetTaskAsync(taskId);
        long selectedSize;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var originals = await db.DownloadTaskFiles.AsNoTracking()
                                    .Where(file => file.DownloadTaskId == taskId)
                                    .OrderBy(file => file.FileIndex).ToListAsync();
            // 落选文件在登记期就带“不下载”标记与原因，与 Seed 附件（默认下载）分开记录。
            Assert.True(originals[0].IsSelectedForDownload);
            Assert.Null(originals[0].SelectionReason);
            Assert.False(originals[1].IsSelectedForDownload);
            Assert.Equal("同集包内择优落选", originals[1].SelectionReason);
            selectedSize = originals.Where(file => file.IsSelectedForDownload).Sum(file => file.Size);
        }

        Assert.Equal(selectedSize, registered.SelectedTotalBytes);

        await harness.ReconcileAsync(); // 提交：停止状态添加。

        Assert.Contains(registered.InfoHash, harness.Store.StoppedAdds); // 停止添加，未开始下载。
        Assert.Empty(harness.Store.SelectionCalls);                      // 提交轮不做文件操作。
        Assert.Empty(harness.Store.ResumeCalls);
        var submitted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, submitted.Status);
        Assert.True(submitted.IsSystemPaused);
        Assert.False(submitted.IsUserPaused);
        Assert.Equal(DownloadPreparationStage.Planned, submitted.PreparationStage);
    }

    /// <summary>准备流程推进：先应用文件选择（发出后当轮不启动），确认后应用命名并恢复下载。</summary>
    [Fact]
    public async Task Preparation_AppliesFileSelectionThenNamingThenStartsAcrossReconcileRounds()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var scTitle = NekoTitle("简日双语");
        var tcTitle = NekoTitle("繁日双语");
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        await harness.ReconcileAsync(); // 提交（停止添加）。
        await harness.ReconcileAsync(); // 准备第一轮：应用文件选择。

        var selection = Assert.Single(harness.Store.SelectionCalls);
        Assert.Equal(hash, selection.Hash);
        Assert.Equal([1], selection.Indices); // 落选文件的 qB index。
        Assert.False(selection.Download);
        // 选择刚发出，等待下载器确认：当轮不启动下载。
        Assert.DoesNotContain(hash, harness.Store.ResumeCalls);
        Assert.Contains(hash, harness.Store.StoppedAdds);
        var applying = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadPreparationStage.Applying, applying.PreparationStage);

        await harness.ReconcileAsync(); // 准备第二轮：选择已确认，应用命名并启动。

        Assert.Contains(harness.Store.RenameFileCalls,
                        call => call.Hash == hash && call.OldPath.Contains(scTitle, StringComparison.Ordinal));
        Assert.Contains(hash, harness.Store.ResumeCalls);
        Assert.DoesNotContain(hash, harness.Store.StoppedAdds);
        var started = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, started.Status);
        Assert.False(started.IsSystemPaused);
        Assert.Equal(DownloadPreparationStage.ReadyToRun, started.PreparationStage);
        // 下载器内落选文件已被设为不下载；胜出文件保持下载。
        var remote = harness.Store.FileIdentities[hash].OrderBy(file => file.Index).ToList();
        Assert.True(remote[0].IsSelectedForDownload);
        Assert.False(remote[1].IsSelectedForDownload);
    }

    /// <summary>全部文件落选的任务保持停止：不启动、不误报下载成功，等待人工处理。</summary>
    [Fact]
    public async Task Preparation_AllFilesDeselected_NeverStarts()
    {
        using var harness = await CreateHarnessAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create());
        var       task    = await harness.GetTaskAsync(taskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            foreach (var file in await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == taskId).ToListAsync())
            {
                file.IsSelectedForDownload = false;
                file.SelectionReason       = "测试构造：全部落选";
            }

            await db.SaveChangesAsync();
        }

        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();

        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        var waiting = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, waiting.Status);
        Assert.True(waiting.IsSystemPaused);
    }

    /// <summary>只选合集部分文件：选中文件全部完成即满足本次下载目标，不等待整包进度。</summary>
    [Fact]
    public async Task Reconcile_SelectedContentComplete_CompletesWhileWholeTorrentIncomplete()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var scTitle = NekoTitle("简日双语");
        var tcTitle = NekoTitle("繁日双语");
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        await harness.ReconcileAsync(); // 提交。
        await harness.ReconcileAsync(); // 选择。
        await harness.ReconcileAsync(); // 命名并启动。

        // 整包进度未完成，但选中文件（胜出集）已完整；落选文件保持零进度。
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 0.5 };
        harness.Store.FileIdentities[hash] = harness.Store.FileIdentities[hash]
                                                    .Select(file => file with
                                                     {
                                                         Progress = file.Index == 0 ? 1.0 : 0.0
                                                     })
                                                    .ToList();

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(1.0, completed.Progress);
        Assert.NotNull(completed.CompletedAtUtc);
    }

    /// <summary>准备期间用户暂停：新意图优先，准备流程不推进、绝不自动启动。</summary>
    [Fact]
    public async Task Preparation_UserPauseDuringPreparation_StopsAdvancingAndNeverAutoStarts()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create());
        var       task    = await harness.GetTaskAsync(taskId);

        await harness.ReconcileAsync(); // 提交（停止添加）。
        await harness.Service.PauseAsync(taskId);

        for (var round = 0; round < 2; round++) await harness.ReconcileAsync();

        var paused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, paused.Status);
        Assert.True(paused.IsUserPaused);
        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
    }

    /// <summary>接管外部已处于停止状态的同 hash 种子：无法区分人工暂停与响应丢失，按人工暂停处理，不自动启动。</summary>
    [Fact]
    public async Task Reconcile_PreExistingStoppedTorrent_AdoptsAsUserPausedWithoutAutoStart()
    {
        using var harness = await CreateHarnessAsync();
        var       hash    = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.AddTorrent(hash, "外部添加的种子", QbittorrentTorrentCategory.Stopped, 0, "/downloads",
                                 ["Test Anime - 01"]);
        var itemId = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var taskId = await harness.CreateDownloadAsync(itemId);

        await harness.ReconcileAsync();

        Assert.Empty(harness.Store.AddCalls); // 未重复提交。
        var adopted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, adopted.Status);
        Assert.True(adopted.IsUserPaused);

        // 外部预添加的种子从未进入本应用的停止添加流程，没有系统准备意图：
        // 恢复后按普通下载对账，不触发准备流程（与响应丢失后的接管保留准备意图不同）。
        Assert.False(adopted.IsSystemPaused);
        Assert.Empty(harness.Store.ResumeCalls);
    }

    /// <summary>准备未完成时的人工恢复：只解除人工暂停，不直接启动下载器，文件选择仍由准备流程补齐。</summary>
    [Fact]
    public async Task ResumeDuringIncompletePreparation_ContinuesPreparationInsteadOfBypassingSelection()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var scTitle = NekoTitle("简日双语");
        var tcTitle = NekoTitle("繁日双语");
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        await harness.ReconcileAsync(); // 提交（停止添加，准备未开始）。
        await harness.Service.PauseAsync(taskId);
        await harness.Service.ResumeAsync(taskId);

        // 恢复不得绕过准备：不直接启动下载器，落选文件的选择还没应用。
        Assert.DoesNotContain(hash, harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.SelectionCalls);
        var resumed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, resumed.Status);
        Assert.True(resumed.IsSystemPaused);

        await harness.ReconcileAsync(); // 准备：应用文件选择。
        await harness.ReconcileAsync(); // 命名并启动。

        var selection = Assert.Single(harness.Store.SelectionCalls);
        Assert.Equal([1], selection.Indices);
        Assert.False(selection.Download);
        Assert.Contains(hash, harness.Store.ResumeCalls);
        var started = await harness.GetTaskAsync(taskId);
        Assert.False(started.IsSystemPaused);
    }

    /// <summary>目标路径被已完成旧成片占用：准备不被卡死，以原始命名启动下载，完成链备份旧成片后替换。</summary>
    [Fact]
    public async Task Preparation_NamingBlockedByCompletedIncumbent_StillDownloadsAndReplacesOnCompletion()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");

        // 旧版本（简日，1024 字节）先完成并归档，占住目标路径。
        var oldTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var oldTask   = await harness.GetTaskAsync(oldTaskId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, oldTask.InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(oldTaskId)).Status);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var oldFile = await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == oldTaskId);
            // 任务专属根任务迁入后保存根为下载根，媒体目录在相对路径内。
            Assert.Equal("Media/Anime/测试作品 E12.mp4", oldFile.CurrentRelativePath);
        }

        // 新版本（繁日，不同来源可登记，不同压制故大小不同）目标同名：新任务在专属目录独立下载
        // （不再回退原始命名直写），完成链把旧成片移入 .replaced/ 备份后完成替换。
        var replacementBytes = Encoding.UTF8.GetBytes(Encoding.UTF8
                                                              .GetString(TestTorrentBytes.Create(NekoTitle("繁日双语")))
                                                              .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                                       StringComparison.Ordinal));
        var newTaskId = await harness.CreateUploadAsync(replacementBytes, "byrbt");
        var newTask   = await harness.GetTaskAsync(newTaskId);
        Assert.DoesNotContain(harness.Store.RenameFileCalls, call => call.Hash == newTask.InfoHash);

        await harness.ReconcileAsync(); // 提交（停止添加）。
        await harness.ReconcileAsync(); // 准备：任务内命名（不触碰正式路径）并启动下载。

        Assert.Contains(newTask.InfoHash, harness.Store.ResumeCalls);
        var downloading = await harness.GetTaskAsync(newTaskId);
        Assert.Equal(DownloadTaskStatus.Downloading, downloading.Status);
        Assert.False(downloading.IsSystemPaused);
        Assert.Contains(harness.Store.RenameFileCalls,
                        call => call.Hash == newTask.InfoHash && call.NewPath == "Media/Anime/测试作品 E12.mp4");

        // 下载完成后由完成链备份旧成片（.replaced/）并完成替换。
        CompleteTorrent(harness, newTask.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(newTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Contains(harness.Store.RenameFileCalls,
                        call => call.Hash == oldTask.InfoHash && call.NewPath.Contains(".replaced/",
                            StringComparison.Ordinal));
    }

    /// <summary>整包进度 100% 也不能跳过所选文件核验：远端取消了计划要求下载的文件时不得判定完成。</summary>
    [Fact]
    public async Task Reconcile_WholeTorrentCompleteButPlannedFileDeselectedRemotely_DoesNotComplete()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var ep12   = NekoTitle("简日双语", episode : 12);
        var ep13   = NekoTitle("简日双语", episode : 13);
        var bytes  = CreateMultiFileTorrent("Batch", ("Video/" + ep12, 1024), ("Video/" + ep13, 37));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        await harness.ReconcileAsync(); // 提交。
        await harness.ReconcileAsync(); // 准备：选择（无落选）。
        await harness.ReconcileAsync(); // 命名并启动。

        // 远端把计划选中的第 13 集取消选择且零进度；qB 总进度按其当前选择报告 100%。
        // 文件已按计划改名，按 qB index 定位第 13 集（改名后路径不再含集数标记）。
        harness.Store.FileIdentities[hash] = harness.Store.FileIdentities[hash]
                                                    .Select(file => file.Index == 1
                                                                ? file with
                                                                {
                                                                    IsSelectedForDownload = false,
                                                                    Progress = 0.0
                                                                }
                                                                : file with { Progress = 1.0 })
                                                    .ToList();
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { Progress = 1.0 };

        await harness.ReconcileAsync();

        var notCompleted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, notCompleted.Status);
        Assert.True(notCompleted.Progress < 1.0);
        Assert.Null(notCompleted.CompletedAtUtc);
    }

    /// <summary>校验中的种子不执行待恢复的改名：恢复链在 Checking 快照稳定前保持暂停。</summary>
    [Fact]
    public async Task Recovery_DuringCheckingState_DoesNotResumePendingRenames()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var scTitle = NekoTitle("简日双语");
        var tcTitle = NekoTitle("繁日双语");
        var third   = NekoTitle("简日双语", episode : 13);
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37), ("Video/" + third, 11));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        // 第一个改名发出后中断：留下未完成的改名日志。
        harness.Store.AfterRenameFile = () => throw new InvalidOperationException("模拟改名中断");
        await harness.ReconcileAsync(); // 提交。
        await harness.ReconcileAsync(); // 选择。
        await harness.ReconcileAsync(); // 命名：首个改名即中断，剩余文件留待恢复链。
        Assert.Single(harness.Store.RenameFileCalls, call => call.Hash == hash);

        // 期间种子进入校验：恢复链不得继续执行剩余改名。
        harness.Store.AfterRenameFile = null;
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Checking
        };
        await harness.ReconcileAsync();
        Assert.Single(harness.Store.RenameFileCalls, call => call.Hash == hash);

        // 校验结束回到稳定状态后，恢复链继续完成剩余改名并推进准备。
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Stopped
        };
        await harness.ReconcileAsync();
        Assert.Equal(3, harness.Store.RenameFileCalls.Count(call => call.Hash == hash));
    }

    /// <summary>远端文件清单缺项时选择状态无法确认：准备流程不得当作已确认而启动下载。</summary>
    [Fact]
    public async Task Preparation_MissingRemoteFileIdentity_BlocksStartUntilConfirmed()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var scTitle = NekoTitle("简日双语");
        var tcTitle = NekoTitle("繁日双语");
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + scTitle, 1024),
                                           ("Video/"                  + tcTitle, 37));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        await harness.ReconcileAsync(); // 提交。

        // 下载器文件清单缺失一个计划文件：选择是否已应用无法确认。
        harness.Store.FileIdentities[hash] = [harness.Store.FileIdentities[hash][0]];

        await harness.ReconcileAsync(); // 准备：身份缺项阻塞。
        await harness.ReconcileAsync();

        Assert.Empty(harness.Store.SelectionCalls);
        Assert.DoesNotContain(harness.Store.RenameFileCalls, call => call.Hash == hash);
        Assert.DoesNotContain(hash, harness.Store.ResumeCalls);
        var blocked = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, blocked.Status);
        Assert.Equal("文件整理", blocked.FailedStep);
        Assert.True(blocked.IsSystemPaused);

        // 清单恢复完整后人工重试，保留接管停止种子的暂停意图，再显式恢复。
        harness.Store.FileIdentities[hash] =
        [
            new TorrentFileSnapshot(0, "Original Pack/Video/" + scTitle, 1024),
            new TorrentFileSnapshot(1, "Original Pack/Video/" + tcTitle, 37)
        ];
        await harness.Service.RetryAsync(taskId);
        await harness.ReconcileAsync();
        await harness.Service.ResumeAsync(taskId);
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        Assert.Contains(hash, harness.Store.ResumeCalls);
        Assert.False((await harness.GetTaskAsync(taskId)).IsSystemPaused);
    }

    /// <summary>
    ///     准备期间命名失败只有“明确的路径占用”才可回退继续下载：远端文件布局与本地身份无法对应时
    ///     （如路径被改成无关名称，身份匹配被拒），必须阻塞启动，等待布局恢复或人工处理。
    /// </summary>
    [Fact]
    public async Task Preparation_IdentityMismatchDuringNaming_BlocksStartUntilLayoutConfirmed()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var title  = NekoTitle("简日双语");
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(title));
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        await harness.ReconcileAsync(); // 提交（停止添加）。

        // 远端文件路径变为无关名称：文件服务的身份匹配会拒绝，这不同于路径占用。
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, "Existing/old.mkv", 1024)];

        await harness.ReconcileAsync(); // 准备：命名身份校验失败。
        await harness.ReconcileAsync();

        Assert.DoesNotContain(hash, harness.Store.ResumeCalls);
        var blocked = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, blocked.Status);
        Assert.Equal("文件整理", blocked.FailedStep);
        Assert.True(blocked.IsSystemPaused);
        Assert.NotEqual(DownloadPreparationStage.ReadyToRun, blocked.PreparationStage);

        // 远端布局恢复与计划一致后，准备继续推进并启动。
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, title, 1024)];
        await harness.Service.RetryAsync(taskId);
        await harness.ReconcileAsync(); // 重试接管停止种子，保留保守的人工暂停语义。
        await harness.Service.ResumeAsync(taskId);
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        Assert.Contains(hash, harness.Store.ResumeCalls);
        Assert.False((await harness.GetTaskAsync(taskId)).IsSystemPaused);
    }

    /// <summary>接管总进度 100% 的种子同样要经过所选文件核验：计划文件远端未选且零进度时不得判定完成。</summary>
    [Fact]
    public async Task AdoptExisting_WholeTorrentCompleteButPlannedFileDeselected_DoesNotComplete()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var ep12   = NekoTitle("简日双语", episode : 12);
        var ep13   = NekoTitle("简日双语", episode : 13);
        var bytes  = CreateMultiFileTorrent("Batch", ("Video/" + ep12, 1024), ("Video/" + ep13, 37));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        var hash   = task.InfoHash;

        // 下载器中已被外部添加并“完成”：总进度 100%，但第 13 集未选中且零进度。
        harness.Store.AddTorrent(hash, "外部添加的合集", QbittorrentTorrentCategory.Seeding, 1.0, task.SavePath,
                                 [$"Batch/Video/{ep12}", $"Batch/Video/{ep13}"]);
        harness.Store.FileIdentities[hash] =
        [
            new TorrentFileSnapshot(0, $"Batch/Video/{ep12}", 1024, 1.0),
            new TorrentFileSnapshot(1, $"Batch/Video/{ep13}", 37, 0.0, false)
        ];

        await harness.ReconcileAsync(); // 提交对账发现同 hash，直接接管。

        var adopted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, adopted.Status);
        Assert.Null(adopted.CompletedAtUtc);
        Assert.True(adopted.Progress < 1.0); // 展示所选内容进度，而不是虚高的整包进度。
    }

    /// <summary>构造喵萌奶茶屋式发布名：全角斜杠保留标题分隔含义，同时是合法单级种子文件名。</summary>
    private static string NekoTitle(string language, string resolution = "1080p", int episode = 12) =>
        $"【喵萌奶茶屋】★07月新番★[{NekoSeriesTitle.Replace(" ", " ／ ")}][{episode}][{resolution}]" +
        $"[{language}].mp4";

    /// <summary>解析器对喵萌式发布名产出的系列标题（含 " ／ " 分隔符）：标题映射键与评估器同源。</summary>
    private static string NekoParsedTitle() => new AniParser().Parse(NekoTitle("简日双语"))!.Title;

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
            "【喵萌奶茶屋】★07月新番★[与妳相恋到生命尽头 ／ きみが死ぬまで恋をしたい ／ Kimi ga Shinu made Koi wo Shitai][12][1080p][繁日双语].mp4";
        var scTitle =
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头 ／ きみが死ぬまで恋をしたい ／ Kimi ga Shinu made Koi wo Shitai][12][1080p][简日双语].mp4";
        var tcParsed = new AniParser().Parse(tcTitle)!;
        var scParsed = new AniParser().Parse(scTitle)!;
        Assert.NotEqual(TitleNormalizer.Normalize(tcParsed.Title),
                        TitleNormalizer.Normalize(scParsed.Title));
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

    /// <summary>现任已在下载器中下载时被高优先任务顶替：登记只翻转状态不触远端，清理由对账承接。</summary>
    [Fact]
    public async Task CreateFromRssFeed_BetterVariantAfterDownloadingIncumbent_SupersedesWithoutRemoteDeletion()
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

        // 登记即翻转现任状态，但远端种子保留：本地回滚无法撤销远端删除，清理交给对账。
        Assert.Equal(1, result.CreatedCount);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.True(harness.Store.Torrents.ContainsKey(tcHash));
        var incumbent = await harness.GetTaskAsync(uploadTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, incumbent.Status);

        // 下一轮对账移除被顶替种子；逐集承接退出保留内容（承接方完成前旧数据仍是恢复线索）。
        await harness.ReconcileAsync();
        Assert.Contains((tcHash, false), harness.Store.DeleteCalls);
        Assert.False(harness.Store.Torrents.ContainsKey(tcHash));
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
        Assert.Equal(task.InfoHash,
                     TorrentDownloadPolicy.TryLoad(harness.Store.AddCalls[0].Bytes, out var torrent, out _)
                         ? torrent.InfoHashes.V1!.ToHex().ToLowerInvariant()
                         : null);
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

    [Fact]
    public async Task Create_SupersedeDuringRegistration_DoesNotTouchDownloader_CleanupDeferredToReconcile()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var uploadTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        await harness.ReconcileAsync();
        var uploadHash = Assert.Single(harness.Store.Torrents.Keys);

        // 登记期替换不登录下载器：下载器完全不可达时登记仍成功，现任状态照常翻转、种子原样保留。
        // 远端删除无法随本地回滚撤销，因此登记只落库，不产生任何需要撤销的远端副作用。
        harness.Factory.ThrowOnCreate = true;
        var parsedTitle = new AniParser().Parse(NekoTitle("简日双语"))!.Title;
        var itemId = await SeedAcceptedItemAsync(harness, "sub-jpsc", NekoTitle("简日双语"), NamingV1, 1, 12m,
                                                 parsedTitle, "JpSc",
                                                 "喵萌奶茶屋");
        var taskId = await harness.CreateDownloadAsync(itemId);

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, task.Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(uploadTaskId)).Status);
        Assert.True(harness.Store.Torrents.ContainsKey(uploadHash));
        Assert.Empty(harness.Store.DeleteCalls);

        // 下一轮对账在协调门内清理被顶替资源并提交新任务；逐集承接退出保留内容。
        harness.Factory.ThrowOnCreate = false;
        await harness.ReconcileAsync();
        Assert.Equal(2, harness.Store.AddCalls.Count);
        var deletion = Assert.Single(harness.Store.DeleteCalls);
        Assert.Equal(uploadHash, deletion.Hash);
        Assert.False(deletion.DeleteFiles);
        Assert.False(harness.Store.Torrents.ContainsKey(uploadHash));
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(uploadTaskId)).Status);
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Reconcile_SupersededCleanupFailure_RetriedNextRoundWithoutBlockingSubmit()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());
        var uploadTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        await harness.ReconcileAsync();
        var uploadHash = Assert.Single(harness.Store.Torrents.Keys);

        var parsedTitle = new AniParser().Parse(NekoTitle("简日双语"))!.Title;
        var itemId = await SeedAcceptedItemAsync(harness, "sub-jpsc", NekoTitle("简日双语"), NamingV1, 1, 12m,
                                                 parsedTitle, "JpSc",
                                                 "喵萌奶茶屋");
        var taskId = await harness.CreateDownloadAsync(itemId);

        // 清理单项失败不阻塞本轮：被顶替种子保留，新任务照常提交，下一轮按同一未完成记录继续清理。
        harness.Factory.SessionConfigurator =
            session => session.DeleteException = new QbittorrentException("模拟清理失败");
        await harness.ReconcileAsync();
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.True(harness.Store.Torrents.ContainsKey(uploadHash));
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(taskId)).Status);

        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();
        var deletion = Assert.Single(harness.Store.DeleteCalls);
        Assert.Equal(uploadHash, deletion.Hash);
        Assert.False(deletion.DeleteFiles);
        Assert.False(harness.Store.Torrents.ContainsKey(uploadHash));
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(uploadTaskId)).Status);
    }

    /// <summary>测试规则：喵萌奶茶屋列入组序，语言序沿用默认（简日 JpSc 优先）。</summary>
    private static SelectionRuleConfig NekoRules(params EnumLanguage[] languageOrder) =>
        SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["喵萌奶茶屋"],
            LanguageOrder = languageOrder.Length > 0 ? languageOrder : [EnumLanguage.JpSc, EnumLanguage.Sc]
        };

    private static FeedSourceItem RssItem(string stableId, string title, string torrentUrl) =>
        new($"guid:{stableId}", stableId, title,
            new Uri($"https://example.com/posts/{stableId}"),
            new Uri(torrentUrl),
            new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

    private static async Task<int> CountTasksAsync(TestHarness harness)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.DownloadTasks.AsNoTracking().CountAsync();
    }

    /// <summary>读取每集登记表行：逐集现任切换断言的共用入口。</summary>
    private static async Task<EpisodeVersion?> FindEpisodeVersionAsync(
        TestHarness harness, decimal episode = 1, int subjectId = 42)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.EpisodeVersions.AsNoTracking()
                       .SingleOrDefaultAsync(row => row.SubjectId == subjectId && row.Episode == episode);
    }

    /// <summary>任务的胜者正片文件 Id（首个 Anime 文件；文件不可分类的单文件任务回退唯一文件）。</summary>
    private static async Task<Guid?> FindAnimeFileIdAsync(TestHarness harness, Guid taskId)
    {
        await using var db = await harness.CreateDbContextAsync();
        var query = db.DownloadTaskFiles.AsNoTracking()
                      .Where(file => file.DownloadTaskId == taskId);
        var file = await query.Where(file => file.Kind == DownloadFileKind.Anime)
                              .OrderBy(file => file.FileIndex)
                              .FirstOrDefaultAsync();
        if (file is null)
            file = await query.OrderBy(file => file.FileIndex).FirstOrDefaultAsync();
        return file?.Id;
    }

    [Fact]
    public async Task Reconcile_SubmitsPendingTaskWithSelectedSavePath()
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
        Assert.Equal(task.SavePath, snapshot.SavePath);
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
        // 停止状态被接管的种子无法区分是响应丢失还是人工暂停，按人工暂停保守处理，不自动启动；
        // 系统准备意图保留：人工恢复后由准备流程先补齐文件选择再启动。
        Assert.Equal(DownloadTaskStatus.Paused, confirmed.Status);
        Assert.True(confirmed.IsUserPaused);
        Assert.True(confirmed.IsSystemPaused);
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
        using var harness           = await CreateHarnessAsync();
        using var secondScope       = harness.Provider.CreateScope();
        var       deletingService   = secondScope.ServiceProvider.GetRequiredService<DownloadService>();
        var       itemId            = await SeedAcceptedItemAsync(harness, "race-delete", "Test Anime - 01");
        var       taskId            = await harness.CreateDownloadAsync(itemId);
        var       hash              = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        var       gateWaitAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        {
            Assert.True(await beforeReleaseDb.DownloadTasks.AnyAsync(task => task.Id == taskId));
        }

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
        var oldId    = await harness.CreateUploadAsync(TestTorrentBytes.Create(oldTitle), "mikan");
        var oldHash  = TestTorrentBytes.InfoHashOf(oldTitle);
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
        var otherScopedService = registrationScope.ServiceProvider.GetRequiredService<DownloadIntakeService>();
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
                                                          await SeedAcceptedItemAsync(harness, "stale-snapshot",
                                                              "Snapshot Pending - 01"));
        var sentinelId = await harness.CreateDownloadAsync(
                                                           await SeedAcceptedItemAsync(harness, "stale-sentinel",
                                                               "Snapshot Sentinel - 01"));
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
            await using var db      = await harness.CreateDbContextAsync();
            var             pending = await db.DownloadTasks.SingleAsync(task => task.Id == pendingId);
            pending.Status           = DownloadTaskStatus.Replaced;
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
        var       itemId  = await SeedAcceptedItemAsync(harness, "race-stale", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        var       hash    = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
        {
            await using var db      = await harness.CreateDbContextAsync();
            var             current = await db.DownloadTasks.SingleAsync(task => task.Id == taskId);
            current.Status           = DownloadTaskStatus.Replaced;
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
        // 停止添加的种子在响应丢失后被接管：按人工暂停保守处理，等待人工恢复，不自动启动。
        Assert.Equal(DownloadTaskStatus.Paused, confirmed.Status);
        Assert.True(confirmed.IsUserPaused);
        Assert.True(confirmed.IsSystemPaused); // 准备意图保留：人工恢复后仍会先补齐文件选择。
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
        // 完成种子的文件级进度同为完整：接管与普通对账共用所选文件核验的完成判定。
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, "原文件名.mkv", 1024, 1.0)];

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

        // 下载器中同 hash 任务的保存路径与登记目录不同：不得继续改名或替换。
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
            SavePath = stored.SavePath.Replace("/downloads", "/Downloads", StringComparison.Ordinal)
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

        // 用户在下载器内手动恢复：对账应把任务转回下载中（停止添加的种子同时退出停止状态）。
        var infoHash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Paused.Remove(infoHash);
        harness.Store.StoppedAdds.Remove(infoHash);
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
    public async Task Reconcile_CompletesWithRenameAndSwitchesEpisodeIncumbent()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
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

        // v2 到来：登记表记录待完成优胜（豁免的条目现任按版本守卫入表），任务级字段不再声明替换。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var v2Task   = await harness.GetTaskAsync(taskIdV2);
        Assert.Null(v2Task.ReplacesTaskId);
        var registered = await FindEpisodeVersionAsync(harness);
        Assert.NotNull(registered);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV1), registered.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV2), registered.PendingVideoFileId);

        // 先提交 v2，再让它在下载器中完成；随后一轮对账完成改名并把现任切换到 v2 的正片。
        await harness.ReconcileAsync();
        var v2Hash = TestTorrentBytes.InfoHashOf("Test Anime - 01 v2");
        CompleteTorrent(harness, v2Hash);
        await harness.ReconcileAsync();

        // v1 保持 Completed（历史成片，任务不再整包翻 Replaced）；现任与承接来源在登记表里。
        var stillCompleted = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, stillCompleted.Status);
        Assert.Null(stillCompleted.ReplacedByTaskId);
        var switched = await FindEpisodeVersionAsync(harness);
        Assert.NotNull(switched);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV2), switched.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV1), switched.SupersededVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
        Assert.Equal(2, harness.Store.Torrents.Count);
    }

    [Fact]
    public async Task Reconcile_CompletesMultiFileTorrent_ByRenamingFilesPerEpisode()
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
        // 种子名称模板只命名种子：内容目录保持原始结构，不再改根目录名。
        Assert.Empty(harness.Store.RenameFolderCalls);
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
                                                 "测试作品 E14 v2 [Sc]", 2, 14, subjectId : 484623);
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
                                                 "测试作品 E14-E15 [Sc]", 1, 14, subjectId : 484623);
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
        // 顶层散文件：逐文件整理挪进 Seed/，种子名改快照命名后完成。
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
    public async Task Reconcile_MultiFileScatteredWithoutNamingIngredients_CompletesWithTorrentRenameOnly()
    {
        using var harness = await CreateHarnessAsync();
        // 上传种子标题无法解析：命名要素无集数，兼容收尾只改种子名，内容保持原始布局。
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Uploaded Anime - 01"));
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Uploaded Anime - 01");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] = ["original.mkv", "subtitle.ass"];

        await harness.ReconcileAsync();

        // 种子名称模板只命名种子：无法逐文件命名的内容保持原名完成，不伪造文件改名。
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.FailedStep);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.RenameFolderCalls);
        Assert.Equal("Uploaded Anime - 01", Assert.Single(harness.Store.RenameCalls).Name);
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
        using var harness  = await CreateHarnessAsync();
        var       template = new string('中', 140) + " {title} E{episode}";
        await harness.SaveNamingTemplateAsync(template);
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")));
        var task   = await harness.GetTaskAsync(taskId);
        Assert.Equal(149, task.NamingSnapshot.Length);
        Assert.True(task.NamingIngredients!.Length > 1000);
        Assert.NotNull(TorrentNamingIngredients.FromJson(task.NamingIngredients));

        await using var db = await harness.CreateDbContextAsync();
        var feedItem = new FeedItem
        {
            Id     = Guid.NewGuid(), SubscriptionId         = Guid.NewGuid(), StableId = "long-name",
            Title  = new string('中', 140), FirstSeenAtUtc   = harness.Timestamp,
            Status = FeedItemStatus.Pending, DecisionStatus = ReleaseDecisionStatus.NeedsReview,
            Reason = "test", NamingIngredients              = task.NamingIngredients
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
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头 ／ きみが死ぬまで恋をしたい ／ Kimi ga Shinu made Koi wo Shitai][12][1080p][简日双语].mp4";
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
        using var    harness      = await CreateHarnessAsync();
        const string seedTemplate = "Seed {bgm.id} {bgm.nameCn} E{ani.episode}";
        const string fileTemplate = "{ani.title} E{ani.episode} - {bgm.name}";
        await harness.SaveNamingTemplateAsync(seedTemplate);
        await harness.SaveFileNamingTemplateAsync(fileTemplate);
        var title = NekoTitle("简日双语");

        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(title));
        var task   = await harness.GetTaskAsync(taskId);
        Assert.Equal("Seed 42 测试作品 E12", task.NamingSnapshot);
        var ingredients =
            Assert.IsType<TorrentNamingIngredients>(TorrentNamingIngredients.FromJson(task.NamingIngredients));
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
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        var expectedFile = new ReleaseNamePreviewPolicy().Create(ingredients, true) + ".mp4";
        Assert.NotEqual(task.NamingSnapshot                                         + ".mp4", expectedFile);
        var renameFile = Assert.Single(harness.Store.RenameFileCalls);
        Assert.Equal(title, renameFile.OldPath);
        Assert.Equal(expectedFile, renameFile.NewPath);
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
                                                                              TitleNormalizer
                                                                                 .Normalize(NekoParsedTitle()) &&
                                                                              mapping.SubjectId == 7));
    }

    [Fact]
    public async Task CreateUpload_WithUnparseableTitle_PreservesRawSnapshotAndBoundNamingIngredients()
    {
        using var harness = await CreateHarnessAsync();

        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Uploaded Anime - 01"));

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal("Uploaded Anime - 01", task.NamingSnapshot);
        Assert.Null(task.Episode);
        var ingredients =
            Assert.IsType<TorrentNamingIngredients>(TorrentNamingIngredients.FromJson(task.NamingIngredients));
        Assert.Equal(42, ingredients.BangumiId);
        Assert.Equal("Test Subject", ingredients.BangumiName);
        Assert.Equal("测试作品", ingredients.BangumiNameCn);
        Assert.Equal(string.Empty, ingredients.EpisodeText);
        Assert.Equal(ReleaseNamePreviewPolicy.DefaultTemplate, ingredients.Template);
        Assert.False(string.IsNullOrWhiteSpace(ingredients.FileTemplate));
    }

    [Fact]
    public async Task
        Reconcile_SharedOriginalPathConflict_FirstTaskRenamesEarly_SecondFailsAtCompletionWithoutOverwrite()
    {
        using var    harness  = await CreateHarnessAsync(enableFileArchive : true);
        const string original = "Unparseable original.mkv";
        var          firstId  = await harness.CreateUploadAsync(TestTorrentBytes.Create(original));
        for (var i = 0; i < 2; i++) await harness.ReconcileAsync();
        var first = await harness.GetTaskAsync(firstId);
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestTorrentBytes.Create(original))
                                                   .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                            StringComparison.Ordinal));
        var secondId = await harness.CreateUploadAsync(bytes);
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        var second = await harness.GetTaskAsync(secondId);
        Assert.Equal(DownloadTaskStatus.Failed, second.Status);
        Assert.True(second.IsSystemPaused);
        Assert.DoesNotContain(second.InfoHash, harness.Store.ResumeCalls);
        Assert.DoesNotContain(harness.Store.RenameFileCalls, call => call.Hash == second.InfoHash);
        Assert.Equal("Seed/" + original, Assert.Single(harness.Store.FileIdentities[first.InfoHash]).Path);
        Assert.Equal(original, Assert.Single(harness.Store.FileIdentities[second.InfoHash]).Path);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task Reconcile_FileArchive_RenamesContentOnFirstSightWhileDownloading_AndCompletionIsIdempotent()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        await directories.SaveAsync("Library/Anime", "Seeding/Originals");
        var title         = NekoTitle("简日双语");
        var originalPaths = new[] { $"Original Pack/Video/{title}", "Original Pack/Extras/notes.txt" };
        var bytes         = CreateMultiFileTorrent("Original Pack", ($"Video/{title}", 1024), ("Extras/notes.txt", 37));
        var taskId        = await harness.CreateUploadAsync(bytes);
        var registered    = await harness.GetTaskAsync(taskId);

        // 提交前固定下载器文件布局：首轮对账的准备流程见种子即按命名要素改名，不等下载完成。
        harness.Store.FileIdentities[registered.InfoHash] =
        [
            new TorrentFileSnapshot(1, originalPaths[1], 37), // 下载器顺序不是文件身份。
            new TorrentFileSnapshot(0, originalPaths[0], 1024)
        ];
        harness.Store.Files[registered.InfoHash] = [.. originalPaths];

        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(taskId)).Status);

        // 第二轮对账：无待办事项，保持下载中。
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Empty(harness.Store.RenameCalls); // 种子展示名在提交时已设置；改名动作只属于完成链。
        Assert.Empty(harness.Store.SetLocationCalls);
        Assert.Equal(
        [
            ("Original Pack/Video/" + title, "Library/Anime/测试作品 E12.mp4"),
            ("Original Pack/Extras/notes.txt", "Seeding/Originals/Original Pack/Extras/notes.txt")
        ], harness.Store.RenameFileCalls.Select(call => (call.OldPath, call.NewPath)).ToList());
        await using var db = await harness.CreateDbContextAsync();
        var settled = await db.DownloadTaskFiles.AsNoTracking()
                              .Where(file => file.DownloadTaskId == taskId)
                              .OrderBy(file => file.FileIndex).ToListAsync();
        Assert.Equal([
                         "Library/Anime/测试作品 E12.mp4",
                         "Seeding/Originals/Original Pack/Extras/notes.txt"
                     ],
                     settled.Select(file => file.CurrentRelativePath));
        Assert.All(settled, file => Assert.Null(file.PendingRenameTo));

        // 完成链退化为同一计划的幂等校验：不再发出新的文件改名，只补种子展示名并标记完成。
        CompleteTorrent(harness, registered.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(2, harness.Store.RenameFileCalls.Count);
        Assert.Equal(registered.NamingSnapshot, Assert.Single(harness.Store.RenameCalls).Name);
        Assert.Equal("/downloads", completed.SavePath);
    }

    [Theory]
    [InlineData(0.5, false, false)]
    [InlineData(1.0, false, false)]
    [InlineData(1.0, true, false)]
    [InlineData(0.5, false, true)]
    [InlineData(1.0, false, true)]
    [InlineData(1.0, true, true)]
    public async Task Reconcile_AdoptsRenamedFiles_UsesOriginalNamesForClassificationAndFileTemplate(
        double progress, bool alreadyAtTarget, bool withPadding)
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        await harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>()
                     .SaveAsync("Library/Anime", "Seeding/Originals");
        var first  = NekoTitle("简日双语", episode : 1);
        var second = NekoTitle("简日双语", episode : 2);
        var bytes = CreateMultiFileTorrent("Original Pack", ("Video/" + first, 1024), ("empty.txt", 0),
                                           ("Nested/Video/"           + second, 1024));
        if (withPadding)
        {
            var (root, _) = BEncodedDictionary.DecodeTorrent(bytes);
            var info = (BEncodedDictionary)root["info"];
            ((BEncodedList)info["files"]).Insert(1, new BEncodedDictionary
            {
                ["attr"]   = new BEncodedString("p"),
                ["length"] = new BEncodedNumber(15360),
                ["path"]   = new BEncodedList { new BEncodedString(".pad"), new BEncodedString("15360") }
            });
            info["pieces"] = new BEncodedString(new byte[40]);
            bytes          = root.Encode();
        }

        var          id            = await harness.CreateUploadAsync(bytes);
        var          task          = await harness.GetTaskAsync(id);
        const string firstTarget   = "Library/Anime/测试作品 E01.mp4";
        const string secondTarget  = "Library/Anime/测试作品 E02.mp4";
        const string seedTarget    = "Seeding/Originals/Original Pack/empty.txt";
        var          firstCurrent  = alreadyAtTarget ? firstTarget : "Old Library: renamed/a.mp4";
        var          secondCurrent = alreadyAtTarget ? secondTarget : "Old Library: renamed/b.mp4";
        var          seedCurrent   = alreadyAtTarget ? seedTarget : "Old Library: renamed/empty.txt";
        harness.Store.AddTorrent(task.InfoHash, "Previously named torrent",
                                 progress >= 1
                                     ? QbittorrentTorrentCategory.Seeding
                                     : QbittorrentTorrentCategory.Downloading,
                                 progress, "/downloads", [firstCurrent, seedCurrent, secondCurrent]);
        // qB 4.4+ filters padding and numbers real files consecutively, retaining the zero-length file in place.
        // Same-sized videos and reversed API response order must not affect the association.
        harness.Store.FileIdentities[task.InfoHash] =
        [
            new TorrentFileSnapshot(2, secondCurrent, 1024, progress),
            new TorrentFileSnapshot(1, seedCurrent, 0),
            new TorrentFileSnapshot(0, firstCurrent, 1024, progress)
        ];

        await harness.ReconcileAsync();

        var adopted = await harness.GetTaskAsync(id);
        Assert.Equal(progress >= 1 ? DownloadTaskStatus.Completed : DownloadTaskStatus.Downloading, adopted.Status);
        Assert.Empty(harness.Store.AddCalls);
        if (progress < 1) Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.SetLocationCalls);
        // 外部布局接管不授权迁入正式路径：下载完成后经同一归档证据链确认，再验证原名分类与模板。
        if (!alreadyAtTarget)
        {
            if (progress < 1)
            {
                CompleteTorrent(harness, task.InfoHash);
                await harness.ReconcileAsync();
            }

            Assert.Null((await harness.GetTaskAsync(id)).ArchiveReview);
        }

        await using var db = await harness.CreateDbContextAsync();
        var files = await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == id).ToListAsync();
        var firstFile = files.Single(file => file.OriginalRelativePath == "Original Pack/Video/" + first);
        var secondFile = files.Single(file => file.OriginalRelativePath == "Original Pack/Nested/Video/" + second);
        Assert.Equal(DownloadFileKind.Anime, firstFile.Kind);
        Assert.Equal(DownloadFileKind.Anime, secondFile.Kind);
        Assert.Equal(firstTarget, firstFile.CurrentRelativePath);
        Assert.Equal(secondTarget, secondFile.CurrentRelativePath);
        Assert.Equal(seedTarget, files.Single(file => file.Kind == DownloadFileKind.Seed).CurrentRelativePath);
        if (alreadyAtTarget)
        {
            Assert.Empty(harness.Store.RenameFileCalls);
        }
        else
        {
            Assert.Contains(harness.Store.RenameFileCalls,
                            call => call.OldPath == firstCurrent && call.NewPath == firstTarget);
            Assert.Contains(harness.Store.RenameFileCalls,
                            call => call.OldPath == secondCurrent && call.NewPath == secondTarget);
        }
    }

    [Fact]
    public async Task Reconcile_FileArchive_FileTemplateSlashesCreateSubdirectoriesInMediaDirectory()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}/{title}");
        var title      = NekoTitle("简日双语");
        var taskId     = await harness.CreateUploadAsync(TestTorrentBytes.Create(title));
        var registered = await harness.GetTaskAsync(taskId);
        var hash       = registered.InfoHash;
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, title, 1024)];
        harness.Store.Files[hash]          = [title];

        await harness.ReconcileAsync(); // 提交（停止状态添加）。
        await harness.ReconcileAsync(); // 准备流程：应用命名计划并恢复下载。

        // 文件名模板书写的 / 直接建立子目录；任务内命名使用同一相对路径（含媒体目录前缀）。
        Assert.Equal(["Media/Anime/测试作品 E12/测试作品.mp4"], harness.Store.Files[hash]);
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(taskId)).Status);

        CompleteTorrent(harness, hash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Single(harness.Store.RenameFileCalls); // 完成轮不重复改名（归档等待确认）。
        Assert.Null(completed.ArchiveReview);
        Assert.Equal(registered.NamingSnapshot, Assert.Single(harness.Store.RenameCalls).Name);
    }

    [Fact]
    public async Task Reconcile_FileArchive_SharedTargetConflict_DoesNotRenameOrCompleteSecondTask()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var firstId  = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var secondId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "byrbt");
        var first    = await harness.GetTaskAsync(firstId);
        var second   = await harness.GetTaskAsync(secondId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, first.InfoHash);
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(firstId)).Status);
        var blocked = await harness.GetTaskAsync(secondId);
        Assert.Equal(DownloadTaskStatus.Failed, blocked.Status);
        Assert.Null(blocked.CompletedAtUtc);
        Assert.DoesNotContain(second.InfoHash, harness.Store.ResumeCalls);
        Assert.DoesNotContain(harness.Store.RenameFileCalls, call => call.Hash == second.InfoHash);
        Assert.Equal("Media/Anime/测试作品 E12.mp4", Assert.Single(harness.Store.FileIdentities[first.InfoHash]).Path);
    }

    [Fact]
    public async Task Reconcile_FileArchive_ForeignTorrentWithIllegalCharacters_DoesNotBlockCompletion()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        // 未经本应用登记的外部种子：文件名含 ':'（本应用相对路径校验禁止的字符），
        // 占用扫描按原样拼接比对，不得因路径校验抛错拖垮整个对账轮。
        harness.Store.AddTorrent("foreign-hash", "[NSFW]The Intern: A Summer of Lust",
                                 QbittorrentTorrentCategory.Seeding, 1.0, "/downloads/Media/NSFW",
                                 ["The Intern: A Summer of Lust/The Intern: A Summer of Lust.mp4"]);
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var task   = await harness.GetTaskAsync(taskId);
        await harness.ReconcileAsync();
        harness.Store.Files[task.InfoHash] = [NekoTitle("简日双语")];
        CompleteTorrent(harness, task.InfoHash);

        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Reconcile_FileArchive_ForeignTorrentOverlappingTarget_HoldsArchiveForReview()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var task   = await harness.GetTaskAsync(taskId);
        // 外部种子（旧库）与任务的归档目标完全同路径但大小不同：不同压制的真实覆盖，拒绝。
        harness.Store.AddTorrent("foreign-hash", "[Anime]测试作品 E12",
                                 QbittorrentTorrentCategory.Seeding, 1.0, "/downloads/Media/Anime",
                                 ["测试作品 E12.mp4"]);
        harness.Store.FileIdentities["foreign-hash"] =
            [new TorrentFileSnapshot(0, "测试作品 E12.mp4", 4096)];
        await harness.ReconcileAsync();
        harness.Store.Files[task.InfoHash] = [NekoTitle("简日双语")];
        CompleteTorrent(harness, task.InfoHash);

        await harness.ReconcileAsync();

        // 归档证据矩阵：外部占用不再以失败呈现——下载目标已完成，归档保留任务目录并待核对，绝不覆盖。
        var held = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, held.Status);
        Assert.NotNull(held.CompletedAtUtc);
        Assert.Equal("/downloads", held.SavePath);
        var review = DownloadArchiveReview.FromJson(held.ArchiveReview);
        Assert.NotNull(review);
        Assert.NotEmpty(review.Reason);
        Assert.Contains("占用", review.Reason, StringComparison.Ordinal);
        Assert.Equal([new TorrentFileSnapshot(0, "测试作品 E12.mp4", 4096)],
                     harness.Store.FileIdentities["foreign-hash"]);
    }

    [Fact]
    public async Task Reconcile_FileArchive_ForeignTorrentSamePathSameSize_ChecksAndReuses()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var task   = await harness.GetTaskAsync(taskId);
        harness.Store.AddTorrent("foreign-hash", "Existing data", QbittorrentTorrentCategory.Seeding,
                                 1, "/downloads/Media/Anime", ["测试作品 E12.mp4"]);
        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        Assert.Single(harness.Store.RecheckCalls);
        Assert.DoesNotContain(task.InfoHash, harness.Store.ResumeCalls);
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 1 }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.ArchiveReview);
        Assert.Empty(harness.Store.SetLocationCalls);
        Assert.Equal(["测试作品 E12.mp4"], harness.Store.Files["foreign-hash"]);
    }

    [Fact]
    public async Task Reconcile_LegacyFallback_SameNamingAcrossIsolationFolders_CompletesWithoutConflict()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        var       itemIdA = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdA = await harness.CreateDownloadAsync(itemIdA);
        await SetLegacyIsolationPathAsync(harness, taskIdA);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdA)).Status);

        // 明确模拟存量隔离目录（无新目录快照），而非把新登记的共享根目录当作 hash 隔离。
        // 同集不同字幕组：命名相同、hash 和真实目录不同，兼容收尾不得只按名字误判冲突。
        var itemIdB = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 [GroupB]");
        var taskIdB = await harness.CreateDownloadAsync(itemIdB);
        await SetLegacyIsolationPathAsync(harness, taskIdB);
        Assert.NotEqual((await harness.GetTaskAsync(taskIdA)).SavePath,
                        (await harness.GetTaskAsync(taskIdB)).SavePath);
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

        // v3 登记时不再声明任务级替换对象：豁免的条目现任由登记表按版本守卫承接。
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Null((await harness.GetTaskAsync(taskIdV3)).ReplacesTaskId);

        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();

        // v1 保持 Completed，现任经登记表切到 v3 的正片；失败的 v2 不参与。
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdV1)).Status);
        var registry = await FindEpisodeVersionAsync(harness);
        Assert.NotNull(registry);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV3), registry.IncumbentVideoFileId);
        Assert.Null(registry.PendingVideoFileId);
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

        // v2 完成时经版本守卫兜底切换现任（登记时互相豁免、无双活对决记录的场景）。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();

        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Null(completedV2.ReplacedByTaskId);
        var stillCompletedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, stillCompletedV1.Status);
        var registry = await FindEpisodeVersionAsync(harness);
        Assert.NotNull(registry);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV2), registry.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV1), registry.SupersededVideoFileId);
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

        // v2、v3 都在 v1 已完成时创建：登记表按版本守卫先后登记待完成优胜，后者覆盖前者；
        // 任务级替换字段不再声明。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Null((await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);
        Assert.Null((await harness.GetTaskAsync(taskIdV3)).ReplacesTaskId);
        var registered = await FindEpisodeVersionAsync(harness);
        Assert.NotNull(registered);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV1), registered.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV3), registered.PendingVideoFileId);

        // v2 先完成：经版本守卫兜底切换现任；登记的最新待完成优胜（v3）保持不变。
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdV1)).Status);
        var interim = await FindEpisodeVersionAsync(harness);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV2), interim.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV1), interim.SupersededVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV3), interim.PendingVideoFileId);

        // v3 完成：登记在案的优胜接管现任，承接来源更新为 v2。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();

        var completedV3 = await harness.GetTaskAsync(taskIdV3);
        Assert.Equal(DownloadTaskStatus.Completed, completedV3.Status);
        var final = await FindEpisodeVersionAsync(harness);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV3), final.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV2), final.SupersededVideoFileId);
        Assert.Null(final.PendingVideoFileId);
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

        // v2、v3 创建时都经版本守卫登记待完成优胜，最终登记指向 v3。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Null((await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);

        // v3 先完成：登记的优胜切换现任，v1 保持 Completed。
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdV1)).Status);

        // v2 后完成：现任版本（v3）更高，版本守卫不允许反向替换，登记保持不动。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();

        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Null(completedV2.ReplacedByTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdV1)).Status);
        var completedV3 = await harness.GetTaskAsync(taskIdV3);
        Assert.Equal(DownloadTaskStatus.Completed, completedV3.Status);
        var registry = await FindEpisodeVersionAsync(harness);
        Assert.NotNull(registry);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV3), registry.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, taskIdV1), registry.SupersededVideoFileId);
        Assert.Null(registry.PendingVideoFileId);
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

        // 完成的种子其文件级进度同为完整：文件身份一旦播种（或自动登记）就随之确定。
        if (harness.Store.FileIdentities.TryGetValue(hash, out var files))
            harness.Store.FileIdentities[hash] = files.Select(file => file with { Progress = 1.0 }).ToList();
    }

    /// <summary>多文件合集种子：文件名即发布名（Neko 式，可解析集数），info.name 不解析以保持合集语义。</summary>
    private static byte[] BatchTorrent(params string[] filePaths)
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

    private static string NekoSubtitleName(string language, int episode) =>
        NekoTitle(language, episode : episode)[..^4] + ".ass";

    [Fact]
    public async Task CreateFromUpload_BatchAgainstCompletedSingle_DeselectsLostEpisodesAndSubtitles()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());

        // 旧单集（第 12 集）先完成，成为该集现任。
        var incumbentId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var incumbent   = await harness.GetTaskAsync(incumbentId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, incumbent.InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(incumbentId)).Status);

        // 新合集：第 12 集（同组同版本，现任保留）落选，第 13 集缺集胜出；
        // 落选集的随行字幕一并取消选择，无法解析集数的附件保持默认下载。
        var batchId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                                   NekoSubtitleName("简日双语", 12),
                                                                   NekoTitle("简日双语", episode : 13),
                                                                   NekoSubtitleName("简日双语", 13), "NCOP.mp4"),
                                                      "mikan");

        await using var db = await harness.CreateDbContextAsync();
        var files = await db.DownloadTaskFiles.AsNoTracking()
                            .Where(file => file.DownloadTaskId == batchId)
                            .OrderBy(file => file.FileIndex)
                            .ToListAsync();
        Assert.Equal(DownloadFileKind.Seed, files[4].Kind);
        Assert.False(files[0].IsSelectedForDownload);
        Assert.Contains("同集已有更优任务", files[0].SelectionReason);
        Assert.False(files[1].IsSelectedForDownload);
        Assert.Contains("随本集视频落选", files[1].SelectionReason);
        Assert.True(files[2].IsSelectedForDownload);
        Assert.True(files[3].IsSelectedForDownload);
        Assert.True(files[4].IsSelectedForDownload);

        var task = await harness.GetTaskAsync(batchId);
        Assert.Equal(3072, task.SelectedTotalBytes);
        Assert.Null(task.ReplacesTaskId);

        // 现任登记不受落选影响；缺集胜出不写登记表（完成时直接落现任）。
        var registry12 = await FindEpisodeVersionAsync(harness, 12);
        Assert.NotNull(registry12);
        Assert.Equal(await FindAnimeFileIdAsync(harness, incumbentId), registry12.IncumbentVideoFileId);
        Assert.Null(registry12.PendingVideoFileId);
        Assert.Null(await FindEpisodeVersionAsync(harness, 13));

        // 准备流程把落选文件的选择下推到下载器：第 12 集正片与字幕被跳过，其余正常下载。
        // 轮次：提交（停止添加）→ 发出选择变更 → 确认后完成命名并启动。
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        var batchHash = (await harness.GetTaskAsync(batchId)).InfoHash;
        Assert.Contains(harness.Store.SelectionCalls,
                        call => call.Hash == batchHash && !call.Download &&
                                call.Indices.OrderBy(index => index).SequenceEqual([0, 1]));
        Assert.Contains(batchHash, harness.Store.ResumeCalls);
    }

    [Fact]
    public async Task CreateFromUpload_BatchSupersedesUnfinishedBatch_PerEpisodeTakeoverKeepsOtherEpisodes()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());

        // 旧合集（第 12、13 集，繁日）在下载中。
        var oldId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("繁日双语", episode : 12),
                                                                 NekoTitle("繁日双语", episode : 13)),
                                                    "mikan");
        var oldTask = await harness.GetTaskAsync(oldId);
        await harness.ReconcileAsync();
        var oldHash = oldTask.InfoHash;
        harness.Store.Torrents[oldHash] = harness.Store.Torrents[oldHash] with { Progress = 0.4 };
        // 播种文件身份（含选择状态）：fake 会话对选择变更的回显以 FileIdentities 为准。
        harness.Store.FileIdentities[oldHash] =
        [
            new TorrentFileSnapshot(0, NekoTitle("繁日双语", episode : 12), 1024),
            new TorrentFileSnapshot(1, NekoTitle("繁日双语", episode : 13), 1024)
        ];

        // 新合集（第 12 集简日更优、第 14 集缺集）：只承接旧合集的第 12 集，
        // 第 13 集职责保留，旧任务继续下载，不整任务翻转。
        var newId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                                 NekoTitle("简日双语", episode : 14)),
                                                    "mikan");
        var newTask = await harness.GetTaskAsync(newId);

        await using var db = await harness.CreateDbContextAsync();
        var oldFiles = await db.DownloadTaskFiles.AsNoTracking()
                               .Where(file => file.DownloadTaskId == oldId)
                               .OrderBy(file => file.FileIndex)
                               .ToListAsync();
        Assert.False(oldFiles[0].IsSelectedForDownload);
        Assert.Equal(newId, oldFiles[0].SupersededByTaskId);
        Assert.Contains("已被更优任务替代", oldFiles[0].SelectionReason);
        Assert.True(oldFiles[1].IsSelectedForDownload);
        Assert.Null(oldFiles[1].SupersededByTaskId);

        var refreshedOld = await harness.GetTaskAsync(oldId);
        Assert.Equal(DownloadTaskStatus.Downloading, refreshedOld.Status);
        Assert.True(refreshedOld.SelectionSyncPending);
        Assert.Equal(1024, refreshedOld.SelectedTotalBytes);
        Assert.Null(refreshedOld.ReplacedByTaskId);
        Assert.Null(newTask.ReplacesTaskId);

        // 登记表：第 12 集待完成优胜指向新任务正片（现任为空），第 14 集缺集不写表。
        var registry12 = await FindEpisodeVersionAsync(harness, 12);
        Assert.NotNull(registry12);
        Assert.Equal(await FindAnimeFileIdAsync(harness, newId), registry12.PendingVideoFileId);
        Assert.Null(registry12.IncumbentVideoFileId);
        Assert.Null(await FindEpisodeVersionAsync(harness, 14));

        // 对账把承接选择下推到旧合集的下载器种子：只有第 12 集文件被跳过。旧任务处于停止添加的
        // 准备流程中，选择由准备链先下推；同步旗标在其恢复下载、对账确认无差异后清除。
        for (var round = 0; round < 8 && (await harness.GetTaskAsync(oldId)).SelectionSyncPending; round++)
            await harness.ReconcileAsync();
        Assert.Contains(harness.Store.SelectionCalls,
                        call => call.Hash == oldHash && !call.Download && call.Indices.SequenceEqual([0]));
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(oldId)).Status);
        Assert.False((await harness.GetTaskAsync(oldId)).SelectionSyncPending);
    }

    [Fact]
    public async Task Delete_PendingEpisodeWinner_RevivesSupersededFilesAndClearsRegistry()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());

        var oldId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("繁日双语", episode : 12),
                                                                 NekoTitle("繁日双语", episode : 13)),
                                                    "mikan");
        await harness.ReconcileAsync();
        var newId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                                 NekoTitle("简日双语", episode : 14)),
                                                    "mikan");
        await harness.ReconcileAsync();

        // 承接方未完成即被删除：被承接文件恢复下载职责，等待对账重新下推。
        await harness.Service.DeleteAsync(newId, false, false);

        await using var db = await harness.CreateDbContextAsync();
        var oldFiles = await db.DownloadTaskFiles.AsNoTracking()
                               .Where(file => file.DownloadTaskId == oldId)
                               .OrderBy(file => file.FileIndex)
                               .ToListAsync();
        Assert.True(oldFiles[0].IsSelectedForDownload);
        Assert.Null(oldFiles[0].SupersededByTaskId);
        Assert.Null(oldFiles[0].SelectionReason);
        Assert.True(oldFiles[1].IsSelectedForDownload);
        Assert.True((await harness.GetTaskAsync(oldId)).SelectionSyncPending);
        var registry12 = await FindEpisodeVersionAsync(harness, 12);
        Assert.NotNull(registry12);
        Assert.Null(registry12.PendingVideoFileId);

        var oldHash = (await harness.GetTaskAsync(oldId)).InfoHash;
        await harness.ReconcileAsync();
        Assert.Contains(harness.Store.SelectionCalls,
                        call => call.Hash == oldHash && call.Download && call.Indices.SequenceEqual([0]));
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(oldId)).Status);
    }

    [Fact]
    public async Task Delete_CompletedIncumbent_RevertsRegistryToSuperseded()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SeedSubjectMappingAsync(42, "Test Subject", "测试作品", NekoParsedTitle());

        var v1Id = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        var v1   = await harness.GetTaskAsync(v1Id);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, v1.InfoHash);
        await harness.ReconcileAsync();

        var v2Id = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");
        var v2   = await harness.GetTaskAsync(v2Id);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, v2.InfoHash);
        await harness.ReconcileAsync();
        var switched = await FindEpisodeVersionAsync(harness, 12);
        Assert.Equal(await FindAnimeFileIdAsync(harness, v2Id), switched.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, v1Id), switched.SupersededVideoFileId);

        // 删除现任所属任务：登记表回退到它承接的上一任，历史成片重新成为该集现任。
        await harness.Service.DeleteAsync(v2Id, false, false);
        var reverted = await FindEpisodeVersionAsync(harness, 12);
        Assert.Equal(await FindAnimeFileIdAsync(harness, v1Id), reverted.IncumbentVideoFileId);
        Assert.Null(reverted.SupersededVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(v1Id)).Status);
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
    public async Task Delete_WithoutDownloaderTorrent_DeletesIdempotently()
    {
        using var harness = await CreateHarnessAsync();
        // 停留在待提交：下载器中尚无种子。删除请求照常发出，下载器对不存在的 hash 静默忽略。
        var taskId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Never Submitted - 01"));
        var hash   = TestTorrentBytes.InfoHashOf("Never Submitted - 01");

        await harness.Service.DeleteAsync(taskId, true, false);

        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.DownloadTasks.AsNoTracking().ToListAsync());
        Assert.Equal([(hash, false)], harness.Store.DeleteCalls);
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
    public async Task DeleteMany_ToleratesMissingIdsAndAbsentTorrents()
    {
        using var harness = await CreateHarnessAsync();
        var       first   = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk A - 01"));
        var       second  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Bulk B - 01"));

        // 未提交：下载器中没有种子，删除请求照常发出并由下载器幂等忽略；夹带的无效 Id 静默跳过。
        var deleted = await harness.Service.DeleteManyAsync([first, Guid.NewGuid()],
                                                            true, false);

        Assert.Equal(1, deleted);
        Assert.NotNull(await harness.GetTaskAsync(second));
        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal(second, (await db.DownloadTasks.AsNoTracking().SingleAsync()).Id);
        Assert.Equal([(TestTorrentBytes.InfoHashOf("Bulk A - 01"), false)], harness.Store.DeleteCalls);
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

    private static byte[] CreateMultiFileTorrent(string name, params (string Path, long Size)[] files)
    {
        static string Encode(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";

        const int pieceLength = 16384;
        var       totalLength = files.Sum(file => file.Size);
        var       pieceCount  = checked((int)((totalLength + pieceLength - 1) / pieceLength));
        var       pieces      = new string('0', checked(pieceCount * 20));
        var entries = string.Concat(files.Select(file =>
                                                     $"d6:lengthi{file.Size}e4:pathl{string.Concat(file.Path.Split('/').Select(Encode))}ee"));
        return Encoding.UTF8.GetBytes($"d4:infod5:filesl{entries}e4:name{Encode(name)}" +
                                      $"12:piece lengthi{pieceLength}e6:pieces{Encode(pieces)}ee");
    }

    private static async Task SetLegacyIsolationPathAsync(TestHarness harness, Guid taskId)
    {
        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        task.SavePath         = $"/downloads/legacy/{task.InfoHash}";
        task.DownloadRootPath = null;
        task.MediaDirectory   = null;
        task.SeedDirectory    = null;
        db.DownloadTaskFiles.RemoveRange(await db.DownloadTaskFiles
                                                 .Where(file => file.DownloadTaskId == taskId).ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>与命名预览同源生成的命名要素：渲染结果必须恰好等于 naming（"测试作品 E01 [Sc]" 形态）。</summary>
    private static string BuildNamingIngredients(decimal episode, int version) =>
        new TorrentNamingIngredients("{title} E{episode} {version} [{group}]", "测试作品",
                                     ReleaseNamePreviewPolicy.FormatEpisodeText(episode), version, "Sc",
                                     null).ToJson();

    private static async Task SeedSubjectAsync(TestHarness harness, int subjectId)
    {
        await using var db = await harness.CreateDbContextAsync();
        if (!await db.MediaSubjects.AnyAsync(subject => subject.Id == subjectId))
        {
            db.MediaSubjects.Add(new MediaSubject
            {
                Id          = subjectId, Name = "Test Subject", NameCn = "测试作品", Platform = "TV",
                CachedAtUtc = harness.Timestamp
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Guid> SeedAcceptedItemAsync(TestHarness harness, string stableId, string torrentName,
                                                          string      naming      = NamingV1, int version = 1,
                                                          decimal     episode     = 1,
                                                          string?     parsedTitle = null, string? language  = null,
                                                          string?     preference  = null, int?    subjectId = 42)
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

    internal static Task<TestHarness> CreateHarnessForPageTestsAsync() => CreateHarnessAsync();

    private static async Task<TestHarness> CreateHarnessAsync(bool configureDownloader = true,
                                                              bool enableFileArchive   = false)
    {
        var directory           = Directory.CreateTempSubdirectory("Banori.P3.Tests.");
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
        services.AddScoped<SubjectMetadataCache>();
        services.AddSingleton<ISubjectLinkResolver>(linkResolver);
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddNotificationTestFakes();
        services.AddSingleton<AniParser>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        services.AddScoped<ManageSelectionRuleSettings>();
        services.AddScoped<ISelectionRuleProvider, SettingsSelectionRuleProvider>();
        services.AddScoped<NamingTemplateStore>();
        services.AddSingleton<DownloadCoordinationGate>();
        // 原有收尾用例保留未注册文件服务的兼容 fallback；新目录集成用例单独启用。
        services.AddScoped<DownloadDirectorySettingsStore>();
        services.AddSingleton<DownloadFileClassificationPolicy>();
        if (enableFileArchive) services.AddScoped<DownloadFileService>();
        services.AddScoped<DownloadService>();
        services.AddScoped<DownloadIntakeService>();
        services.AddScoped<DownloadTaskQueries>();
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：Identity 服务注册影响
        // ApplicationDbContext 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new TestHarness(directory, timestamp, provider, store,
                                      provider.GetRequiredService<FakeQbittorrentSessionFactory>(),
                                      provider.GetRequiredService<DownloadService>(),
                                      provider.GetRequiredService<DownloadIntakeService>(), harnessTorrentNames,
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
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    /// <summary>Bangumi 元数据替身：未特制时按 Id 生成确定性元数据（中文名优先供命名断言）。</summary>
    internal sealed class FakeSubjectProvider(DateTimeOffset timestamp) : ISubjectMetadataProvider
    {
        public Dictionary<int, SubjectMetadata> Overrides { get; } = [];

        public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Overrides.TryGetValue(subjectId, out var metadata)
                                ? metadata
                                : new SubjectMetadata(subjectId, $"Subject {subjectId}",
                                                      $"测试作品{subjectId}", "TV", "2026-01-01", null,
                                                      timestamp));
    }

    /// <summary>条目链接解析替身：按绝对地址精确返回配置的 Subject ID，未配置视为解析不出。</summary>
    internal sealed class FakeSubjectLinkResolver : ISubjectLinkResolver
    {
        public Dictionary<string, int> ResolvedLinks { get; } = new(StringComparer.Ordinal);

        public Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = null,
                                                CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(ResolvedLinks.TryGetValue(episodeUri.AbsoluteUri, out var subjectId)
                                      ? subjectId
                                      : null);
    }

    /// <summary>条目 stableId 与种子名的注册表；种子字节按种子名确定性生成，同名单同 hash。</summary>
    internal sealed class TestHarness(
        DirectoryInfo                 directory,
        DateTimeOffset                timestamp,
        ServiceProvider               provider,
        FakeTorrentStore              store,
        FakeQbittorrentSessionFactory factory,
        DownloadService               service,
        DownloadIntakeService         intake,
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
        public DownloadIntakeService         Intake           { get; } = intake;
        public IServiceProvider              Provider         { get; } = provider;
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

        public Task<Guid> CreateDownloadAsync(Guid itemId) => Intake.CreateForFeedItemAsync(itemId);

        /// <summary>
        ///     上传包装：默认绑定预置 Subject 42（多数用例只关心登记后的管线行为）；
        ///     门禁相关用例显式传 subjectId 覆盖（null 表示不绑定）。
        /// </summary>
        public Task<Guid> CreateUploadAsync(byte[] torrentBytes, string? siteSource = null, int? subjectId = 42) =>
            Intake.CreateFromUploadAsync(torrentBytes, siteSource, subjectId);

        public Task<RssDownloadResult> CreateFromRssFeedAsync(string feedUrl, Guid? proxyId = null,
                                                              int?   subjectId = null) =>
            Intake.CreateFromRssFeedAsync(feedUrl, proxyId, subjectId);

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
                    NormalizedTitle = TitleNormalizer.Normalize(rawTitle),
                    SubjectId       = subjectId,
                    UpdatedAtUtc    = Timestamp
                });
            await db.SaveChangesAsync();
        }

        public Task ReconcileAsync() => Service.ReconcileAsync();

        /// <summary>人工确认归档（新证据矩阵：迁移须经确认绑定当前计划）。</summary>
        public Task RetryArchiveAsync(Guid taskId) =>
            Provider.GetRequiredService<DownloadFileService>().RetryArchiveAsync(taskId);

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
            var ruleSettings = new ManageSelectionRuleSettings(provider.GetRequiredService<IApplicationSettingStore>(),
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
