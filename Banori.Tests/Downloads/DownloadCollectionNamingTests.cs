using Banori.Core.Rules;
using Banori.Core.Torrents;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    private const string CollectionTitle = "[Nekomoe kissaten][Toaru Anbu no Item][01-06][1080p][JPSC]";

    private static string CollectionVideo(string episode) =>
        $"[Nekomoe kissaten][Toaru Anbu no Item][{episode}][1080p][JPSC].mp4";

    [Theory]
    [InlineData("upload")]
    [InlineData("rss")]
    [InlineData("subscription")]
    public async Task CollectionNaming_UnparseableTitle_UsesActualEpisodesAndIndependentTemplate(string entryPoint)
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("Single {title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var templates = harness.Provider.GetRequiredService<NamingTemplateStore>();
        await templates.SaveCollectionTemplateAsync("合集 {title} {episode:Eee-Eee} {site.source}");
        var paths = Enumerable.Range(1, 6).Reverse().Select(episode => CollectionVideo($"{episode:00}"))
                              .Concat(["notes.txt"]).ToArray();
        var bytes        = CreateMultiFileTorrent(CollectionTitle, paths.Select(path => (path, 1024L)).ToArray());
        var taskId       = await RegisterCollectionAsync(harness, entryPoint, bytes, CollectionTitle);
        var registered   = await harness.GetTaskAsync(taskId);
        var source       = entryPoint == "upload" ? "other" : "mikan";
        var expectedName = $"合集 测试作品 E01-E06 {source}";
        Assert.Equal(expectedName, registered.NamingSnapshot);
        var ingredients = TorrentNamingIngredients.FromJson(registered.NamingIngredients)!;
        Assert.Equal("01-E06", ingredients.EpisodeText);
        Assert.Null(ingredients.FileTemplate); // 文件模板是全局实时规则，不随任务快照保存。
        if (entryPoint != "subscription") Assert.Null(registered.Episode);

        // 登记后配置变更不能覆盖快照；真实完成链仍用合集名，文件继续逐集命名。
        await templates.SaveCollectionTemplateAsync("Changed {title}");
        var originalPaths = paths.Select(path => CollectionTitle + "/" + path).ToArray();
        harness.Store.FileIdentities[registered.InfoHash] = originalPaths.Select((path, index) =>
            new TorrentFileSnapshot(index, path, 1024)).ToList();
        harness.Store.Files[registered.InfoHash] = [.. originalPaths];
        await harness.ReconcileAsync();
        Assert.Equal(expectedName, Assert.Single(harness.Store.AddCalls).Rename);
        CompleteTorrent(harness, registered.InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        Assert.Equal(expectedName, Assert.Single(harness.Store.RenameCalls).Name);
        await using var db = await harness.CreateDbContextAsync();
        var videos = await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == taskId &&
                                                              file.Kind           == DownloadFileKind.Anime)
                             .OrderBy(file => file.CurrentRelativePath).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 6).Select(episode => $"Media/Anime/测试作品 E{episode:00}.mp4"),
                     videos.Select(file => file.CurrentRelativePath));
    }

    [Theory]
    [InlineData("E{episode:ee-Eee}", "E01-E02,04-E06")]
    [InlineData("{episode:ee-ee}", "01-02,04-06")]
    [InlineData("E{episode:ee-ee}", "E01-02,04-06")]
    [InlineData("{episode:Eee-Eee}", "E01-E02,E04-E06")]
    [InlineData("{episode:eee-eee}", "001-002,004-006")]
    public async Task CollectionNaming_GapsDuplicatesAndSubtitles_UsesTemplateRangeFormat(
        string format, string expected)
    {
        using var harness = await CreateHarnessAsync();
        await harness.Provider.GetRequiredService<NamingTemplateStore>()
                     .SaveCollectionTemplateAsync("{title} " + format);
        var paths = new[] { "06", "01", "04", "02", "05" }.Select(CollectionVideo)
                                                          .Concat([
                                                               CollectionVideo("01").Replace(".mp4", ".mkv"),
                                                               CollectionVideo("03").Replace(".mp4", ".ass"),
                                                               "bonus 99.txt"
                                                           ]);
        // 标题宣称完整范围，实际缺第三集；字幕也不能补出不存在的视频集数。
        var bytes  = CreateMultiFileTorrent(CollectionTitle, paths.Select(path => (path, 1024L)).ToArray());
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        Assert.Equal("测试作品 " + expected, task.NamingSnapshot);
        Assert.Equal("01-E02,04-E06", TorrentNamingIngredients.FromJson(task.NamingIngredients)!.EpisodeText);
    }

    [Fact]
    public async Task CollectionNaming_UnsetTemplateInheritsOrdinaryTemplate()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveNamingTemplateAsync("普通 {title} E{episode:ee-ee}");
        var bytes = CreateMultiFileTorrent(CollectionTitle, (CollectionVideo("01"), 1024),
                                           (CollectionVideo("02"), 1024));
        var collectionId = await harness.CreateUploadAsync(bytes);
        Assert.Equal("普通 测试作品 E01-02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);

        await harness.Provider.GetRequiredService<NamingTemplateStore>().SaveCollectionTemplateAsync("合集 {title}");
        var singleId =
            await harness.CreateUploadAsync(CreateMultiFileTorrent("Single", (CollectionVideo("07"), 1024),
                                                                  (CollectionVideo("07").Replace(".mp4", ".ass"), 64)));
        Assert.Equal("普通 测试作品 E07", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.Equal(7, (await harness.GetTaskAsync(singleId)).Episode);
    }

    [Fact]
    public async Task CollectionNaming_ParseableRangeTitle_UsesCollectionTemplateWhenFilesHaveNoEpisodes()
    {
        using var harness = await CreateHarnessAsync();
        await harness.Provider.GetRequiredService<NamingTemplateStore>()
                     .SaveCollectionTemplateAsync("合集 {title} {episode:ee-ee}");
        var bytes = CreateMultiFileTorrent("【喵萌奶茶屋】[Test Anime][01-06][1080p][简日双语]",
                                           ("unparsed.mkv", 1024));
        var taskId = await harness.CreateUploadAsync(bytes);
        Assert.Equal("合集 测试作品 01-06", (await harness.GetTaskAsync(taskId)).NamingSnapshot);
    }

    [Fact]
    public async Task CollectionNaming_DecimalEpisodesRemainSeparate()
    {
        using var harness = await CreateHarnessAsync();
        await harness.Provider.GetRequiredService<NamingTemplateStore>()
                     .SaveCollectionTemplateAsync("{title} {episode:Eee-Eee}");
        var paths = new[] { "01", "01.5", "02" }.Select(episode =>
                                                            ($"[VCB-Studio] Test Anime [{episode}][Ma10p_1080p][x265_10bit].mkv",
                                                             1024L)).ToArray();
        var bytes  = CreateMultiFileTorrent("Unparsed", paths);
        var taskId = await harness.CreateUploadAsync(bytes);
        Assert.Equal("测试作品 E01,E01.5,E02", (await harness.GetTaskAsync(taskId)).NamingSnapshot);
    }

    [Fact]
    public async Task CollectionNaming_OverlappingVideoRanges_AreMergedWithoutInventingMissingEpisodes()
    {
        using var harness = await CreateHarnessAsync();
        await harness.Provider.GetRequiredService<NamingTemplateStore>()
                     .SaveCollectionTemplateAsync("{title} {episode:Eee-Eee}");
        var bytes = CreateMultiFileTorrent("Unparsed",
                                           ("【喵萌奶茶屋】[Test Anime][01-03][1080p][简日双语].mkv", 1024),
                                           ("【喵萌奶茶屋】[Test Anime][03-06][1080p][简日双语].mkv", 1024),
                                           ("【喵萌奶茶屋】[Test Anime][08][1080p][简日双语].mkv", 1024));
        var taskId = await harness.CreateUploadAsync(bytes);
        var task   = await harness.GetTaskAsync(taskId);
        Assert.Equal("测试作品 E01-E06,E08", task.NamingSnapshot);
        Assert.Null(task.Episode);
    }

    [Theory]
    [InlineData("{title} E{episode}")]
    [InlineData(null)]
    public async Task CollectionNaming_SubscriptionWithoutFileTemplate_KeepsFileNaming(
        string? originalTemplate)
    {
        using var harness = await CreateHarnessAsync();
        await harness.Provider.GetRequiredService<NamingTemplateStore>()
                     .SaveCollectionTemplateAsync("合集 {title} {episode:Eee-Eee}");
        const string stableId = "legacy-collection";
        var          itemId   = await SeedAcceptedItemAsync(harness, stableId, CollectionTitle);
        harness.TorrentBytes[stableId] = CreateMultiFileTorrent(CollectionTitle,
                                                                (CollectionVideo("01"), 1024),
                                                                (CollectionVideo("02"), 1024));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var item = await db.FeedItems.SingleAsync(value => value.Id == itemId);
            item.NamingIngredients = (TorrentNamingIngredients.FromJson(item.NamingIngredients)! with
            {
                Template = originalTemplate, FileTemplate = null
            }).ToJson();
            await db.SaveChangesAsync();
        }

        var taskId = await harness.CreateDownloadAsync(itemId);
        var task   = await harness.GetTaskAsync(taskId);
        Assert.Equal("合集 测试作品 E01-E02", task.NamingSnapshot);
        // 文件模板不随任务落库；旧订阅条目即使带 FileTemplate，创建任务时也会清除。
        Assert.Null(TorrentNamingIngredients.FromJson(task.NamingIngredients)!.FileTemplate);
    }

    private static async Task<Guid> RegisterCollectionAsync(
        TestHarness harness, string entryPoint, byte[] bytes, string title)
    {
        if (entryPoint == "upload") return await harness.CreateUploadAsync(bytes);
        harness.TorrentBytes["collection"] = bytes;
        if (entryPoint == "subscription")
        {
            var             itemId = await SeedAcceptedItemAsync(harness, "collection", title);
            await using var db     = await harness.CreateDbContextAsync();
            var             item   = await db.FeedItems.SingleAsync(value => value.Id == itemId);
            item.NamingIngredients = (TorrentNamingIngredients.FromJson(item.NamingIngredients)! with
            {
                FileTemplate = "{title} E{episode}"
            }).ToJson();
            await db.SaveChangesAsync();
            return await harness.CreateDownloadAsync(itemId);
        }

        harness.FeedReader.Items.Add(RssItem("collection", title, "https://mikanani.me/Download/collection.torrent"));
        var result = await harness.CreateFromRssFeedAsync("https://mikanani.me/RSS/collection", subjectId : 42);
        Assert.Equal(0, result.FailedCount);
        return Assert.Single(result.Items).TaskId!.Value;
    }
}
