using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>下载任务列表查询层筛选测试：直接落库构造历史任务，验证 SQL 端搜索、分页与分组等价性。</summary>
public sealed class DownloadTaskQueriesFilterTests
{
    private static DownloadTaskQueries CreateQueries(DownloadServiceTests.TestHarness harness) =>
        new(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());

    private static async Task<DownloadTaskListPage> QueryAsync(
        DownloadServiceTests.TestHarness harness, DownloadTaskListFilter? filter = null)
    {
        var queries = CreateQueries(harness);
        return await queries.ListAsync(filter ?? new DownloadTaskListFilter());
    }

    [Fact]
    public async Task Search_ReachesTasksBeyondLatestFiveHundred()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, Enumerable.Range(0, 505)
                                                                    .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                             $"Bulk - {index:0000}",
                                                                             DownloadTaskListTestSeed.At(index))));

        var all = await QueryAsync(harness);
        Assert.Multiple(
                        () => Assert.Equal(505, all.TotalCount),
                        () => Assert.Equal(6, all.TotalPages),
                        () => Assert.Equal(100, all.Items.Count));

        var oldest = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "Bulk - 0000" });
        var item   = Assert.Single(oldest.Items);
        Assert.Multiple(
                        () => Assert.Equal("Bulk - 0000", item.NamingSnapshot),
                        () => Assert.Equal(1, oldest.TotalCount));

        var partial = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "Bulk - 05" });
        Assert.Equal(5, partial.TotalCount);
    }

    [Fact]
    public async Task Pagination_StableOrder_NoGapOrOverlap()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        // 两行同刻创建：次级 Id 排序保证同刻行的翻页位置确定。
        var tasks = Enumerable.Range(0, 248)
                              .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                                $"Page - {index:0000}",
                                                                                DownloadTaskListTestSeed.At(index)))
                              .Concat(
                               [
                                   DownloadTaskListTestSeed.NewTask("Tie A", DownloadTaskListTestSeed.At(600)),
                                   DownloadTaskListTestSeed.NewTask("Tie B", DownloadTaskListTestSeed.At(600))
                               ])
                              .ToList();
        await DownloadTaskListTestSeed.SeedAsync(harness, tasks);

        var first     = await QueryAsync(harness);
        var collected = new List<string?>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await QueryAsync(harness, new DownloadTaskListFilter { Page = page });
            Assert.Multiple(
                            () => Assert.Equal(250, result.TotalCount),
                            () => Assert.Equal(page, result.Page));
            collected.AddRange(result.Items.Select(task => task.NamingSnapshot));
        }

        var repeat = await QueryAsync(harness);
        Assert.Multiple(
                        () => Assert.Equal(250, collected.Count),
                        // 全量翻页恰好覆盖所有任务，不重不漏；重复查询页序确定。
                        () => Assert.Equal(tasks.Select(task => task.NamingSnapshot)
                                                .OrderBy(name => name, StringComparer.Ordinal),
                                           collected.OrderBy(name => name, StringComparer.Ordinal)),
                        () => Assert.Equal(first.Items.Select(task => task.Id),
                                           repeat.Items.Select(task => task.Id)));
        var last = await QueryAsync(harness, new DownloadTaskListFilter { Page = 3 });
        Assert.Equal(50, last.Items.Count);
    }

    [Fact]
    public async Task Pagination_ClampsOutOfRangePage()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, Enumerable.Range(0, 5)
                                                                    .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                             $"Few - {index}",
                                                                             DownloadTaskListTestSeed.At(index))));

        var beyond = await QueryAsync(harness, new DownloadTaskListFilter { Page = 99 });
        Assert.Multiple(
                        () => Assert.Equal(1, beyond.Page),
                        () => Assert.Equal(5, beyond.Items.Count));

        var zero = await QueryAsync(harness, new DownloadTaskListFilter { Page = 0 });
        Assert.Equal(1, zero.Page);
    }

    [Fact]
    public async Task Search_IgnoresCaseAndWhitespace()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness,
        [
            DownloadTaskListTestSeed.NewTask("LoliHouse Frieren - 01", DownloadTaskListTestSeed.At(0),
                                             ingredients : DownloadTaskListTestSeed.Ingredients("LoliHouse", "简")),
            DownloadTaskListTestSeed.NewTask("VCB-Studio 葬送的芙莉莲 - 02", DownloadTaskListTestSeed.At(1),
                                             ingredients : DownloadTaskListTestSeed.Ingredients("VCB-Studio", "繁"))
        ]);

        var lowerCase = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "lolihouse" });
        Assert.Equal(1, lowerCase.TotalCount);

        var chinese = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "芙莉莲" });
        Assert.Equal("VCB-Studio 葬送的芙莉莲 - 02", Assert.Single(chinese.Items).NamingSnapshot);

        var blank = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "   " });
        Assert.Equal(2, blank.TotalCount);
    }

    [Fact]
    public async Task Search_TreatsLikeWildcardsAsLiteral()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness,
        [
            DownloadTaskListTestSeed.NewTask("分_01测试", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("分X01测试", DownloadTaskListTestSeed.At(1)),
            DownloadTaskListTestSeed.NewTask("100%完成", DownloadTaskListTestSeed.At(2)),
            DownloadTaskListTestSeed.NewTask("200完成", DownloadTaskListTestSeed.At(3)),
            DownloadTaskListTestSeed.NewTask("普通任务", DownloadTaskListTestSeed.At(4),
                                             ingredients : DownloadTaskListTestSeed.Ingredients("50%Off"))
        ]);

        var underscore = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "分_01" });
        Assert.Equal(["分_01测试"], underscore.Items.Select(task => task.NamingSnapshot).ToList());

        var percent = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "100%" });
        Assert.Equal(["100%完成"], percent.Items.Select(task => task.NamingSnapshot).ToList());

        var percentPrefix = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "%完成" });
        Assert.Equal(["100%完成"], percentPrefix.Items.Select(task => task.NamingSnapshot).ToList());

        var groupPercent = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "50%O" });
        Assert.Equal(["普通任务"], groupPercent.Items.Select(task => task.NamingSnapshot).ToList());
    }

    [Fact]
    public async Task Search_MatchesSubjectFeedItemAndChineseGroup_ButNotLanguage()
    {
        using var harness     = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       subjectTask = DownloadTaskListTestSeed.NewTask("词条任务", DownloadTaskListTestSeed.At(0));
        var chineseGroup = DownloadTaskListTestSeed.NewTask("神盟任务", DownloadTaskListTestSeed.At(1),
                                                            subjectId : null,
                                                            ingredients : DownloadTaskListTestSeed.Ingredients("神盟"));
        var languageOnly = DownloadTaskListTestSeed.NewTask("纯语言任务", DownloadTaskListTestSeed.At(2),
                                                            subjectId : null,
                                                            ingredients :
                                                            DownloadTaskListTestSeed.Ingredients("VCB-Studio", "简"));
        var feedTask = DownloadTaskListTestSeed.NewTask("种子名任务", DownloadTaskListTestSeed.At(3),
                                                        subjectId : null);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var subscriptionId = Guid.NewGuid();
            var feedItemId     = Guid.NewGuid();
            db.MediaSubscriptions.Add(new MediaSubscription
            {
                Id                     = subscriptionId, Name = "订阅", FeedUrlProtected = "protected",
                RefreshIntervalMinutes = 60, CreatedAtUtc     = DownloadTaskListTestSeed.Base,
                UpdatedAtUtc           = DownloadTaskListTestSeed.Base
            });
            db.FeedItems.Add(new FeedItem
            {
                Id    = feedItemId, SubscriptionId         = subscriptionId, StableId = "stable-search",
                Title = "FeedTitle特例 - 03", FirstSeenAtUtc = DownloadTaskListTestSeed.Base
            });
            feedTask.FeedItemId = feedItemId;
            db.DownloadTasks.AddRange(subjectTask, chineseGroup, languageOnly, feedTask);
            await db.SaveChangesAsync();
        }

        var subjectName = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "测试作品" });
        Assert.Equal(["词条任务"], subjectName.Items.Select(task => task.NamingSnapshot).ToList());

        var feedTitle = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "FeedTitle特例" });
        Assert.Equal(["种子名任务"], feedTitle.Items.Select(task => task.NamingSnapshot).ToList());

        var groupChinese = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "神盟" });
        Assert.Equal(["神盟任务"], groupChinese.Items.Select(task => task.NamingSnapshot).ToList());

        // 语言值不是搜索字段：搜「简」不得命中语言为简的任务。
        var language = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "简" });
        Assert.Empty(language.Items);
    }

    [Fact]
    public async Task Search_FallsBackToOriginalName_WhenChineseNameBlank()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.MediaSubjects.AddRange(
                                      new MediaSubject
                                      {
                                          Id          = 43, Name = "FallbackEmptyCn", NameCn = "", Platform = "TV",
                                          CachedAtUtc = DownloadTaskListTestSeed.Base
                                      },
                                      new MediaSubject
                                      {
                                          Id          = 44, Name = "FallbackBlankCn", NameCn = "   ", Platform = "TV",
                                          CachedAtUtc = DownloadTaskListTestSeed.Base
                                      });
            await db.SaveChangesAsync();
        }

        await DownloadTaskListTestSeed.SeedAsync(harness,
        [
            DownloadTaskListTestSeed.NewTask("空中文回退", DownloadTaskListTestSeed.At(0), subjectId : 43),
            DownloadTaskListTestSeed.NewTask("空白中文回退", DownloadTaskListTestSeed.At(1), subjectId : 44),
            DownloadTaskListTestSeed.NewTask("有效中文名", DownloadTaskListTestSeed.At(2))
        ]);

        // 中文名缺失或空白时按原名列匹配：关键词只出现在原名，其余搜索字段均不含。
        var fallback = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "Fallback" });
        Assert.Equal(["空白中文回退", "空中文回退"],
                     fallback.Items.Select(task => task.NamingSnapshot).ToList());

        var byChinese = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "测试作品" });
        Assert.Equal(["有效中文名"], byChinese.Items.Select(task => task.NamingSnapshot).ToList());

        // 中文名有效时不回落原名：原名关键词不得命中显示标题为中文名的任务。
        var byOriginal = await QueryAsync(harness, new DownloadTaskListFilter { SearchText = "Test Subject" });
        Assert.Empty(byOriginal.Items);
    }

    [Fact]
    public async Task StatusGroups_MirrorDisplayPhaseBucketing()
    {
        using var harness       = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       mappingIntent = new DownloadVerificationIntent { CreatedAtUtc = DownloadTaskListTestSeed.Base };
        var requestedIntent = new DownloadVerificationIntent
        {
            CreatedAtUtc = DownloadTaskListTestSeed.Base, Step = DownloadVerificationStep.Requested
        };
        var rows = new[]
        {
            DownloadTaskListTestSeed.NewTask("S01", DownloadTaskListTestSeed.At(0),
                                             DownloadTaskStatus.PendingSubmit),
            DownloadTaskListTestSeed.NewTask("S02", DownloadTaskListTestSeed.At(1),
                                             DownloadTaskStatus.Submitting),
            DownloadTaskListTestSeed.NewTask("S03", DownloadTaskListTestSeed.At(2),
                                             DownloadTaskStatus.Downloading),
            DownloadTaskListTestSeed.NewTask("S04", DownloadTaskListTestSeed.At(3),
                                             DownloadTaskStatus.Downloading,
                                             category : QbittorrentTorrentCategory.Checking),
            DownloadTaskListTestSeed.NewTask("S05", DownloadTaskListTestSeed.At(4),
                                             DownloadTaskStatus.Downloading, systemPaused : true,
                                             isolation : DownloadIsolationState.Deleting),
            DownloadTaskListTestSeed.NewTask("S06", DownloadTaskListTestSeed.At(5),
                                             DownloadTaskStatus.Downloading, systemPaused : true,
                                             verificationIntent : mappingIntent.ToJson()),
            DownloadTaskListTestSeed.NewTask("S07", DownloadTaskListTestSeed.At(6),
                                             DownloadTaskStatus.Downloading, systemPaused : true,
                                             preparation : DownloadPreparationStage.ReadyToRun),
            DownloadTaskListTestSeed.NewTask("S08", DownloadTaskListTestSeed.At(7),
                                             DownloadTaskStatus.Downloading, systemPaused : true),
            DownloadTaskListTestSeed.NewTask("S09", DownloadTaskListTestSeed.At(8),
                                             DownloadTaskStatus.Paused, true),
            DownloadTaskListTestSeed.NewTask("S10", DownloadTaskListTestSeed.At(9),
                                             DownloadTaskStatus.Paused),
            DownloadTaskListTestSeed.NewTask("S11", DownloadTaskListTestSeed.At(10),
                                             DownloadTaskStatus.Paused, true,
                                             externalStop : DownloadTaskListTestSeed.At(11)),
            DownloadTaskListTestSeed.NewTask("S12", DownloadTaskListTestSeed.At(12),
                                             DownloadTaskStatus.Paused,
                                             category : QbittorrentTorrentCategory.Checking),
            DownloadTaskListTestSeed.NewTask("S13", DownloadTaskListTestSeed.At(13),
                                             DownloadTaskStatus.Paused,
                                             category : QbittorrentTorrentCategory.Checking,
                                             verificationIntent : requestedIntent.ToJson()),
            DownloadTaskListTestSeed.NewTask("S14", DownloadTaskListTestSeed.At(14)),
            DownloadTaskListTestSeed.NewTask("S15", DownloadTaskListTestSeed.At(15),
                                             pendingSavePath : "/tmp/staging"),
            DownloadTaskListTestSeed.NewTask("S16", DownloadTaskListTestSeed.At(16),
                                             DownloadTaskStatus.Failed),
            DownloadTaskListTestSeed.NewTask("S17", DownloadTaskListTestSeed.At(17),
                                             DownloadTaskStatus.Lost),
            DownloadTaskListTestSeed.NewTask("S18", DownloadTaskListTestSeed.At(18),
                                             DownloadTaskStatus.Replaced)
        };
        await DownloadTaskListTestSeed.SeedAsync(harness, rows);

        // 期望以生产派生链为准：真实投影出视图后经 GroupOf 归组，SQL 谓词必须与之一致。
        var all = await QueryAsync(harness);
        Assert.Equal(rows.Length, all.TotalCount);
        foreach (var group in DownloadStatusFilter.Choices)
        {
            var expected = all.Items.Where(view => DownloadStatusFilter.GroupOf(view.Phase) == group)
                              .Select(view => view.NamingSnapshot)
                              .OrderBy(name => name, StringComparer.Ordinal)
                              .ToList();
            var result = await QueryAsync(harness, new DownloadTaskListFilter { StatusGroups = Groups(group) });
            Assert.Multiple(
                            () => Assert.Equal(expected, result.Items.Select(task => task.NamingSnapshot)
                                                               .OrderBy(name => name, StringComparer.Ordinal).ToList()),
                            () => Assert.Equal(expected.Count, result.TotalCount));
        }
    }

    [Fact]
    public async Task Languages_FiltersCombineWithStatus_AndSentinelMatchesMissing()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness,
        [
            DownloadTaskListTestSeed.NewTask("简体已完成", DownloadTaskListTestSeed.At(0),
                                             ingredients : DownloadTaskListTestSeed.Ingredients("VCB-Studio", "简")),
            DownloadTaskListTestSeed.NewTask("繁体已完成", DownloadTaskListTestSeed.At(1),
                                             ingredients : DownloadTaskListTestSeed.Ingredients("VCB-Studio", "繁")),
            DownloadTaskListTestSeed.NewTask("无语言任务", DownloadTaskListTestSeed.At(2)),
            DownloadTaskListTestSeed.NewTask("简体已失败", DownloadTaskListTestSeed.At(3),
                                             DownloadTaskStatus.Failed,
                                             ingredients : DownloadTaskListTestSeed.Ingredients("LoliHouse", "简"))
        ]);

        var simplified = await QueryAsync(harness,
                                          new DownloadTaskListFilter { Languages = Groups("简") });
        Assert.Equal(2, simplified.TotalCount);

        var simplifiedOrNone = await QueryAsync(harness,
                                                new DownloadTaskListFilter { Languages = Groups("简", "") });
        Assert.Equal(3, simplifiedOrNone.TotalCount);

        var none = await QueryAsync(harness, new DownloadTaskListFilter { Languages = Groups("") });
        Assert.Equal(["无语言任务"], none.Items.Select(task => task.NamingSnapshot).ToList());

        var combined = await QueryAsync(harness, new DownloadTaskListFilter
        {
            Languages    = Groups("简"),
            StatusGroups = Groups(DownloadStatusFilterGroup.Failed)
        });
        Assert.Equal(["简体已失败"], combined.Items.Select(task => task.NamingSnapshot).ToList());

        var traditionalOrNone = await QueryAsync(harness,
                                                 new DownloadTaskListFilter { Languages = Groups("繁", "") });
        Assert.Equal(2, traditionalOrNone.TotalCount);

        var missing = await QueryAsync(harness, new DownloadTaskListFilter { Languages = Groups("日") });
        Assert.Multiple(() => Assert.Equal(0, missing.TotalCount), () => Assert.Empty(missing.Items));
    }

    [Fact]
    public async Task Languages_OptionsCoverFullHistory_WithSentinelLast()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var tasks = Enumerable.Range(0, 550)
                              .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                                $"无语言 - {index:0000}",
                                                                                DownloadTaskListTestSeed.At(index)))
                              .Concat(Enumerable.Range(0, 50).Select(index => DownloadTaskListTestSeed.NewTask(
                                                                      $"有语言 - {index:0000}",
                                                                      DownloadTaskListTestSeed.At(1000 + index),
                                                                      ingredients : DownloadTaskListTestSeed
                                                                         .Ingredients("VCB-Studio", "简"))))
                              .ToList();
        await DownloadTaskListTestSeed.SeedAsync(harness, tasks);

        var page = await QueryAsync(harness);
        Assert.Multiple(
                        () => Assert.Equal(600, page.TotalCount),
                        // 语言选项取自全部历史任务而非最新 500 条；哨兵排在真实语言之后。
                        () => Assert.Equal(["简", ""], page.Languages));
    }

    private static HashSet<DownloadStatusFilterGroup> Groups(params DownloadStatusFilterGroup[] groups) => [.. groups];

    private static HashSet<string> Groups(params string[] languages) => [.. languages];
}

/// <summary>查询层与页面测试共用的下载任务落库种子工厂：字段可控、时间递增、Hash 唯一。</summary>
internal static class DownloadTaskListTestSeed
{
    public static readonly DateTimeOffset Base = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(int minutes) => Base.AddMinutes(minutes);

    public static TorrentNamingIngredients Ingredients(string? group, string? language = null) =>
        new(null, "标题", "01", 1, group, null, language);

    public static DownloadTask NewTask(
        string                      snapshot,
        DateTimeOffset              createdAtUtc,
        DownloadTaskStatus          status             = DownloadTaskStatus.Completed,
        bool                        userPaused         = false,
        bool                        systemPaused       = false,
        TorrentNamingIngredients?   ingredients        = null,
        string?                     verificationIntent = null,
        QbittorrentTorrentCategory? category           = null,
        DownloadIsolationState      isolation          = DownloadIsolationState.None,
        DownloadPreparationStage    preparation        = DownloadPreparationStage.None,
        string?                     pendingSavePath    = null,
        DateTimeOffset?             externalStop       = null,
        int?                        subjectId          = 42) => new()
    {
        Id                        = Guid.NewGuid(),
        InfoHash                  = $"hash-{Guid.NewGuid():N}",
        NamingSnapshot            = snapshot,
        CreatedAtUtc              = createdAtUtc,
        Status                    = status,
        IsUserPaused              = userPaused,
        IsSystemPaused            = systemPaused,
        NamingIngredients         = ingredients?.ToJson(),
        VerificationIntent        = verificationIntent,
        LastObservedCategory      = category,
        IsolationState            = isolation,
        PreparationStage          = preparation,
        PendingSavePath           = pendingSavePath,
        ExternalStopObservedAtUtc = externalStop,
        SubjectId                 = subjectId
    };

    public static async Task SeedAsync(
        DownloadServiceTests.TestHarness harness, IEnumerable<DownloadTask> tasks)
    {
        await using var db = await harness.CreateDbContextAsync();
        db.DownloadTasks.AddRange(tasks);
        await db.SaveChangesAsync();
    }
}
