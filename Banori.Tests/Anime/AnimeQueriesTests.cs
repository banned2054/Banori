using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Subscriptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Anime;

/// <summary>动画总览查询新口径：数据源为下载任务，被替换旧版本与未产生任务的条目不显示。</summary>
public sealed class AnimeQueriesTests
{
    [Fact]
    public async Task ListAsync_ShowsOnlyCurrentTask_ReplacedAndTasklessItemsHidden()
    {
        using var harness   = await AnimeQueriesHarness.CreateAsync();
        var       subjectId = await harness.AddSubjectAsync("星屑旅途");
        var       replaced  = await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Replaced, 1);
        var       current   = await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Completed, 2);

        // 绑定了 Subject、解析出集数但从未产生下载任务的 RSS 条目不应出现在动画目录。
        await harness.AddFeedItemAsync(subjectId, 2m);

        var cards = await harness.Queries.ListAsync();

        var card = Assert.Single(cards);
        Assert.Equal(subjectId, card.SubjectId);
        var episode = Assert.Single(card.Episodes);
        Assert.Equal(1m, episode.Episode);
        Assert.Equal(current, episode.Final.TaskId);
        Assert.NotEqual(replaced, episode.Final.TaskId);
    }

    [Fact]
    public async Task ListAsync_SameEpisodeMultipleCurrentTasks_PrefersHigherVersionThenNewer()
    {
        using var harness   = await AnimeQueriesHarness.CreateAsync();
        var       subjectId = await harness.AddSubjectAsync("星屑旅途");
        var       baseTime  = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var olderV2 = await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Completed, 1, 2,
                                                 baseTime);
        var newerV2 = await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Completed, 2, 2,
                                                 baseTime.AddHours(1));
        await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Completed, 3, 1,
                                   baseTime.AddHours(2));

        var cards = await harness.Queries.ListAsync();

        var episode = Assert.Single(Assert.Single(cards).Episodes);
        Assert.Equal(newerV2, episode.Final.TaskId);
        Assert.NotEqual(olderV2, episode.Final.TaskId);
    }

    [Fact]
    public async Task ListAsync_IngredientsFallback_MissingJsonYieldsPlaceholderGroup()
    {
        using var harness   = await AnimeQueriesHarness.CreateAsync();
        var       subjectId = await harness.AddSubjectAsync("星屑旅途");
        var withSnapshot = await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Completed, 1,
                                                      group : "悠亚字幕组", language : "简体中文");
        var withoutSnapshot = await harness.AddTaskAsync(subjectId, 2m, DownloadTaskStatus.Downloading, 2,
                                                         ingredientsJson : null);

        var episodes = Assert.Single(await harness.Queries.ListAsync()).Episodes;

        var first = episodes.Single(episode => episode.Episode == 1m).Final;
        Assert.Equal(withSnapshot, first.TaskId);
        Assert.Equal("悠亚字幕组", first.Group);
        Assert.Equal("简体中文", first.Language);
        var second = episodes.Single(episode => episode.Episode == 2m).Final;
        Assert.Equal(withoutSnapshot, second.TaskId);
        Assert.Equal("未知字幕组", second.Group);
        Assert.Null(second.Language);
    }

    [Fact]
    public async Task ListAsync_GroupsSubjects_CardNamingAndEpisodeOrder()
    {
        using var harness   = await AnimeQueriesHarness.CreateAsync();
        var       cnSubject = await harness.AddSubjectAsync("星屑旅途", "Hoshizuma Tabi");
        var       nameOnly  = await harness.AddSubjectAsync("Solitary Cafe");
        await harness.AddTaskAsync(cnSubject, 2m, DownloadTaskStatus.Completed, 1);
        await harness.AddTaskAsync(cnSubject, 1m, DownloadTaskStatus.Completed, 2);
        await harness.AddTaskAsync(nameOnly, 1m, DownloadTaskStatus.Completed, 3);
        // 无集数或无 Subject 快照的任务不参与聚合。
        await harness.AddTaskAsync(cnSubject, null, DownloadTaskStatus.Completed, 4);
        await harness.AddTaskAsync(null, 1m, DownloadTaskStatus.Completed, 5);

        var cards = await harness.Queries.ListAsync();

        // 卡片顺序沿用 CurrentCulture 排序（继承原实现），此处不对其文化相关结果做断言。
        Assert.Equal(new HashSet<string> { "Solitary Cafe", "星屑旅途" },
                     cards.Select(card => card.DisplayName).ToHashSet());
        var cnCard = cards.Single(card => card.DisplayName == "星屑旅途");
        Assert.Equal([1m, 2m], cnCard.Episodes.Select(episode => episode.Episode));
        Assert.Equal(1m, cards.Single(card => card.DisplayName == "Solitary Cafe").Episodes.Single().Episode);
    }

    [Fact]
    public async Task ListAsync_FailedCurrentTask_StillListedWithStatus()
    {
        using var harness   = await AnimeQueriesHarness.CreateAsync();
        var       subjectId = await harness.AddSubjectAsync("星屑旅途");
        var failedId = await harness.AddTaskAsync(subjectId, 1m, DownloadTaskStatus.Failed, 1,
                                                  failedStep : "提交");

        var final = Assert.Single(Assert.Single(await harness.Queries.ListAsync()).Episodes).Final;

        Assert.Equal(failedId, final.TaskId);
        Assert.Equal(DownloadTaskStatus.Failed, final.Status);
    }

    private sealed class AnimeQueriesHarness : IDisposable
    {
        private readonly DirectoryInfo   _directory;
        private readonly ServiceProvider _provider;
        private          int             _nextSubjectId = 1;

        private AnimeQueriesHarness(DirectoryInfo                           directory, ServiceProvider provider,
                                    IDbContextFactory<ApplicationDbContext> factory)
        {
            _directory = directory;
            _provider  = provider;
            Factory    = factory;
            Queries    = new AnimeQueries(factory);
        }

        public AnimeQueries                            Queries { get; }
        public IDbContextFactory<ApplicationDbContext> Factory { get; }

        public void Dispose()
        {
            _provider.Dispose();
            _directory.Delete(true);
        }

        public static async Task<AnimeQueriesHarness> CreateAsync()
        {
            var directory = Directory.CreateTempSubdirectory("Banori.Anime.Tests.");
            var services  = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                                   options
                                                                      .UseSqlite($"Data Source={Path.Combine(directory.FullName, "anime.db")};Pooling=False"));
            // 与其它测试保持一致的 Identity 模型配置：Identity 服务注册影响 ApplicationDbContext
            // 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
            services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                          IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();
            var factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new AnimeQueriesHarness(directory, provider, factory);
        }

        public async Task<int> AddSubjectAsync(string nameCn, string? name = null)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var subject = new MediaSubject
            {
                Id          = _nextSubjectId++,
                Name        = name ?? nameCn,
                NameCn      = nameCn,
                Platform    = "TV",
                CachedAtUtc = DateTimeOffset.UnixEpoch
            };
            db.MediaSubjects.Add(subject);
            await db.SaveChangesAsync();
            return subject.Id;
        }

        public async Task<Guid> AddTaskAsync(int? subjectId, decimal? episode, DownloadTaskStatus status, int hashIndex,
                                             int version = 1, DateTimeOffset? created = null, string? group = null,
                                             string? language = null, string? ingredientsJson = null,
                                             string? failedStep = null)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var task = new DownloadTask
            {
                Id             = Guid.NewGuid(),
                SubjectId      = subjectId,
                Episode        = episode,
                Status         = status,
                Version        = version,
                // 40 位小写十六进制且互不相同，满足唯一索引 (InfoHash, ActiveFlag)。
                InfoHash       = hashIndex.ToString("x40"),
                SavePath       = "/downloads",
                NamingSnapshot = "snapshot",
                CreatedAtUtc   = created ?? DateTimeOffset.UnixEpoch,
                FailedStep     = failedStep
            };
            if (ingredientsJson is null && (group is not null || language is not null))
                ingredientsJson = new TorrentNamingIngredients(null, "title", "01", version, group, null,
                                                               language).ToJson();
            task.NamingIngredients = ingredientsJson;
            db.DownloadTasks.Add(task);
            await db.SaveChangesAsync();
            return task.Id;
        }

        public async Task AddFeedItemAsync(int subjectId, decimal episode)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var subscription = new MediaSubscription
                { Id = Guid.NewGuid(), Name = "订阅", FeedUrlProtected = "https://example.org/rss" };
            db.MediaSubscriptions.Add(subscription);
            db.FeedItems.Add(new FeedItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = subscription.Id,
                StableId       = $"item-{episode}",
                Title          = $"条目 {episode}",
                FirstSeenAtUtc = DateTimeOffset.UnixEpoch,
                Status         = FeedItemStatus.Accepted,
                SubjectId      = subjectId,
                Episode        = episode
            });
            await db.SaveChangesAsync();
        }
    }
}
