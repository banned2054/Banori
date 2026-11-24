using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>无页码虚拟列表查询测试：ID 快照、按 ID 切片摘要、独立详情与万级任务筛选排序基准。</summary>
public sealed class DownloadTaskQueriesVirtualListTests(ITestOutputHelper output)
{
    private static DownloadTaskQueries CreateQueries(DownloadServiceTests.TestHarness harness) =>
        new(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());

    [Fact]
    public async Task ListIdsAndGetSummariesByIds_MatchListAsyncOrderAndComputeHasPendingFileMoves()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();

        var relatedOld = DownloadTaskListTestSeed.NewTask("Old Version", DownloadTaskListTestSeed.At(0),
                                                          DownloadTaskStatus.Replaced,
                                                          ingredients : DownloadTaskListTestSeed.Ingredients("GroupA", "简"));
        var taskPendingSave = DownloadTaskListTestSeed.NewTask("Task Pending Save", DownloadTaskListTestSeed.At(1),
                                                               DownloadTaskStatus.Completed,
                                                               ingredients : DownloadTaskListTestSeed.Ingredients("GroupA", "简"),
                                                               pendingSavePath : "/downloads/next");
        taskPendingSave.ReplacesTaskId = relatedOld.Id;

        var taskPendingRename = DownloadTaskListTestSeed.NewTask("Task Pending Rename", DownloadTaskListTestSeed.At(2),
                                                                 DownloadTaskStatus.Completed,
                                                                 ingredients : DownloadTaskListTestSeed.Ingredients("GroupB", "繁"));
        taskPendingRename.TorrentFile = [1, 2, 3, 4];
        taskPendingRename.Files.Add(new DownloadTaskFile
        {
            Id                   = Guid.NewGuid(),
            DownloadTaskId       = taskPendingRename.Id,
            FileIndex            = 0,
            OriginalRelativePath = "orig.mkv",
            CurrentRelativePath  = "curr.mkv",
            Size                 = 1024,
            PendingRenameFrom    = "curr.mkv",
            PendingRenameTo      = "next.mkv",
            ClassificationReason = "测试",
            UpdatedAtUtc         = DownloadTaskListTestSeed.At(2)
        });

        var taskClean = DownloadTaskListTestSeed.NewTask("Task Clean", DownloadTaskListTestSeed.At(3),
                                                         DownloadTaskStatus.Downloading);
        taskClean.Files.Add(new DownloadTaskFile
        {
            Id                   = Guid.NewGuid(),
            DownloadTaskId       = taskClean.Id,
            FileIndex            = 0,
            OriginalRelativePath = "clean.mkv",
            CurrentRelativePath  = "clean.mkv",
            Size                 = 2048,
            ClassificationReason = "测试",
            UpdatedAtUtc         = DownloadTaskListTestSeed.At(3)
        });

        await DownloadTaskListTestSeed.SeedAsync(harness, [relatedOld, taskPendingSave, taskPendingRename, taskClean]);

        var queries = CreateQueries(harness);
        var filter  = new DownloadTaskListFilter
        {
            SortColumn     = DownloadTaskSortColumn.CreatedAt,
            SortDescending = true,
            PageSize       = 100
        };

        var index   = await queries.ListIdsAsync(filter);
        var paged   = await queries.ListAsync(filter);
        var reverse = index.TaskIds.Reverse().ToList();
        var missing = Guid.NewGuid();
        var batch   = await queries.GetSummariesByIdsAsync([.. reverse, missing]);
        var empty   = await queries.GetSummariesByIdsAsync([]);

        Assert.Empty(empty);
        Assert.Equal(paged.Items.Select(item => item.Id), index.TaskIds);
        Assert.Equal(paged.Languages, index.Languages);
        Assert.Equal(reverse, batch.Select(item => item.Id).ToList());

        var pendingSaveView   = batch.Single(item => item.Id == taskPendingSave.Id);
        var pendingRenameView = batch.Single(item => item.Id == taskPendingRename.Id);
        var cleanView         = batch.Single(item => item.Id == taskClean.Id);
        Assert.True(pendingSaveView.HasPendingFileMoves);
        Assert.Equal("Old Version", pendingSaveView.ReplacesNaming);
        Assert.True(pendingRenameView.HasPendingFileMoves);
        Assert.False(cleanView.HasPendingFileMoves);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsIndependentProjectionOrNullWhenMissing()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       replaced = DownloadTaskListTestSeed.NewTask("Detail - Replaced", DownloadTaskListTestSeed.At(0),
                                                              DownloadTaskStatus.Replaced);
        var       winner = DownloadTaskListTestSeed.NewTask("Detail - Winner", DownloadTaskListTestSeed.At(1),
                                                            DownloadTaskStatus.Completed,
                                                            ingredients : DownloadTaskListTestSeed.Ingredients("GroupX", "简"));
        winner.ReplacesTaskId = replaced.Id;
        await DownloadTaskListTestSeed.SeedAsync(harness, [replaced, winner]);

        var queries = CreateQueries(harness);
        var detail  = await queries.GetDetailAsync(winner.Id);
        var missing = await queries.GetDetailAsync(Guid.NewGuid());

        Assert.NotNull(detail);
        Assert.Equal(winner.Id, detail.Id);
        Assert.Equal("Detail - Replaced", detail.ReplacesNaming);
        Assert.Equal("GroupX", detail.SubtitleGroup);
        Assert.Equal("简", detail.SubtitleLanguage);
        Assert.Null(missing);
    }

    [Fact]
    public async Task TenThousandTasks_FilterSortAndSliceBenchmark_StaysAccurateAndFast()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       torrent = TestTorrentBytes.Create("Bulk-Benchmark-01");
        var       groups  = new[] { "LoliHouse", "VCB-Studio", "ANi", "SweetSub" };
        var       langs   = new[] { "简", "繁", "简日", "" };

        await using (var db = await harness.CreateDbContextAsync())
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var batch = new List<DownloadTask>(10_000);
            for (var index = 0; index < 10_000; index++)
            {
                var status = (index % 5) switch
                {
                    0 => DownloadTaskStatus.Completed,
                    1 => DownloadTaskStatus.Downloading,
                    2 => DownloadTaskStatus.Paused,
                    3 => DownloadTaskStatus.Failed,
                    _ => DownloadTaskStatus.Replaced
                };
                var lang = langs[index % langs.Length];
                var ingredients = lang.Length == 0
                    ? null
                    : DownloadTaskListTestSeed.Ingredients(groups[index % groups.Length], lang);
                var task = DownloadTaskListTestSeed.NewTask($"BulkTask - {index:00000}",
                                                            DownloadTaskListTestSeed.At(index),
                                                            status,
                                                            userPaused : status == DownloadTaskStatus.Paused,
                                                            ingredients : ingredients);
                task.Episode     = (index % 24) + 1;
                task.Version     = (index % 3)  + 1;
                task.TorrentFile = torrent;
                batch.Add(task);
            }

            db.DownloadTasks.AddRange(batch);
            await db.SaveChangesAsync();
        }

        var queries = CreateQueries(harness);

        var swAll      = Stopwatch.StartNew();
        var allIndex   = await queries.ListIdsAsync(new DownloadTaskListFilter());
        swAll.Stop();

        var filteredFilter = new DownloadTaskListFilter
        {
            SearchText     = "BulkTask - 05",
            StatusGroups   = new HashSet<DownloadStatusFilterGroup> { DownloadStatusFilterGroup.Completed, DownloadStatusFilterGroup.Downloading },
            Languages      = new HashSet<string> { "简", "简日" },
            SortColumn     = DownloadTaskSortColumn.CreatedAt,
            SortDescending = true
        };
        var swFiltered    = Stopwatch.StartNew();
        var filteredIndex = await queries.ListIdsAsync(filteredFilter);
        swFiltered.Stop();

        var tailSliceIds = allIndex.TaskIds.Skip(9900).Take(50).ToArray();
        var swSlice      = Stopwatch.StartNew();
        var tailSlice    = await queries.GetSummariesByIdsAsync(tailSliceIds);
        swSlice.Stop();

        var swDetail = Stopwatch.StartNew();
        var detail   = await queries.GetDetailAsync(tailSliceIds[25]);
        swDetail.Stop();

        output.WriteLine($"10k ListIdsAsync (all): {swAll.ElapsedMilliseconds} ms, total={allIndex.TaskIds.Count}");
        output.WriteLine($"10k ListIdsAsync (filtered+sorted): {swFiltered.ElapsedMilliseconds} ms, matched={filteredIndex.TaskIds.Count}");
        output.WriteLine($"10k GetSummariesByIdsAsync (50 rows at tail 9900..9950): {swSlice.ElapsedMilliseconds} ms");
        output.WriteLine($"10k GetDetailAsync (1 row): {swDetail.ElapsedMilliseconds} ms");

        Assert.Equal(10_000, allIndex.TaskIds.Count);
        Assert.Equal(["简", "简日", "繁", ""], allIndex.Languages);
        Assert.Equal(tailSliceIds, tailSlice.Select(item => item.Id).ToArray());
        Assert.NotNull(detail);
        Assert.Equal(tailSliceIds[25], detail.Id);
        Assert.NotEmpty(filteredIndex.TaskIds);
        Assert.True(filteredIndex.TaskIds.Count < allIndex.TaskIds.Count);

        var filteredSummaries = await queries.GetSummariesByIdsAsync(filteredIndex.TaskIds);
        Assert.All(filteredSummaries, item =>
        {
            Assert.Contains("BulkTask - 05", item.NamingSnapshot, StringComparison.OrdinalIgnoreCase);
            Assert.True(item.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Downloading);
            Assert.True(item.SubtitleLanguage is "简" or "简日");
        });
    }
}
