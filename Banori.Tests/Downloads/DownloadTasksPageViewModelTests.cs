using Banori.Components.Pages.Downloads;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Downloads;

public sealed class DownloadTasksPageViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PrepareBulkDelete_SelectionChangesWhilePrecheckPending_DoesNotOpenPanel()
    {
        using var harness  = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       firstId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page A - 01"));
        var       secondId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page B - 01"));
        await harness.ReconcileAsync();
        var secondHash = TestTorrentBytes.InfoHashOf("Page B - 01");
        Assert.True(harness.Store.Torrents.ContainsKey(secondHash));
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        vm.ToggleBatchMode();
        vm.ToggleSelected(firstId);
        var (started, release) = InstallFindGate(harness.Factory, "Page A - 01");

        var prepare = vm.PrepareBulkDeleteAsync();
        await started.Task.WaitAsync(Timeout);
        vm.DeleteFromDownloader = true;
        vm.DeleteFromDownloader = false;
        vm.ToggleSelected(firstId);
        vm.ToggleSelected(secondId);
        release.TrySetResult();
        await prepare.WaitAsync(Timeout);

        Assert.False(vm.BulkDeletePanelOpen);
        Assert.Null(vm.BulkPrecheck);
        Assert.Equal([secondId], vm.SelectedIds);
        Assert.True(vm.DeleteOptionsTouched);
        Assert.False(vm.DeleteFromDownloader);
        Assert.Null(vm.BulkPrecheck);
        Assert.NotNull(await harness.GetTaskAsync(secondId));
        Assert.True(harness.Store.Torrents.ContainsKey(secondHash));
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Fact]
    public async Task PrepareBulkDelete_LeavingBatchModeWhilePending_DiscardsLateResult()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Exit - 01"));
        var       vm      = CreateViewModel(harness);
        await vm.LoadAsync();
        vm.ToggleBatchMode();
        vm.ToggleSelected(taskId);
        var (started, release) = InstallFindGate(harness.Factory, "Page Exit - 01");

        var prepare = vm.PrepareBulkDeleteAsync();
        await started.Task.WaitAsync(Timeout);
        vm.ToggleBatchMode();
        release.TrySetResult();
        await prepare.WaitAsync(Timeout);

        Assert.False(vm.BulkDeletePanelOpen);
        Assert.Null(vm.BulkPrecheck);
        Assert.False(vm.BatchMode);
        Assert.NotNull(await harness.GetTaskAsync(taskId));
    }

    [Fact]
    public async Task PrepareBulkDelete_CancelWhilePrecheckPending_DiscardsLateResult()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Cancel - 01"));
        var       vm      = CreateViewModel(harness);
        await vm.LoadAsync();
        vm.ToggleBatchMode();
        vm.ToggleSelected(taskId);
        var (started, release) = InstallFindGate(harness.Factory, "Page Cancel - 01");

        var prepare = vm.PrepareBulkDeleteAsync();
        await started.Task.WaitAsync(Timeout);
        vm.CancelBulkDelete();
        release.TrySetResult();
        await prepare.WaitAsync(Timeout);

        Assert.False(vm.BulkDeletePanelOpen);
        Assert.Null(vm.BulkPrecheck);
        Assert.NotNull(await harness.GetTaskAsync(taskId));
    }

    [Fact]
    public async Task ConfirmBulkDelete_UsesImmutablePrecheckSelection()
    {
        using var harness  = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       firstId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Confirm A - 01"));
        var       secondId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Confirm B - 01"));
        await harness.ReconcileAsync();
        var firstHash  = TestTorrentBytes.InfoHashOf("Page Confirm A - 01");
        var secondHash = TestTorrentBytes.InfoHashOf("Page Confirm B - 01");
        var vm         = CreateViewModel(harness);
        await vm.LoadAsync();
        vm.ToggleBatchMode();
        vm.ToggleSelected(firstId);
        var (started, release) = InstallFindGate(harness.Factory, "Page Confirm A - 01");
        var prepare = vm.PrepareBulkDeleteAsync();
        await started.Task.WaitAsync(Timeout);
        release.TrySetResult();
        await prepare.WaitAsync(Timeout);
        // 模拟确认面板打开后其他 UI 状态改动了选择集合；执行仍必须使用预检时不可变快照。
        ((HashSet<Guid>)vm.SelectedIds).Clear();
        ((HashSet<Guid>)vm.SelectedIds).Add(secondId);

        await vm.ConfirmBulkDeleteAsync().WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.GetTaskAsync(firstId));
        Assert.NotNull(await harness.GetTaskAsync(secondId));
        Assert.False(harness.Store.Torrents.ContainsKey(firstHash));
        Assert.True(harness.Store.Torrents.ContainsKey(secondHash));
        Assert.Contains(harness.Store.DeleteCalls, call => call.Hash       == firstHash);
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.Hash == secondHash);
    }

    [Fact]
    public async Task PrepareDelete_UserChoiceAndReopenedTargetAreNotOverwrittenByLatePrecheck()
    {
        using var harness  = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       firstId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Delete A - 01"));
        var       secondId = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Delete B - 01"));
        await harness.ReconcileAsync();
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        var first  = vm.Tasks.Single(task => task.Id == firstId);
        var second = vm.Tasks.Single(task => task.Id == secondId);
        var (started, release) = InstallFindGate(harness.Factory, "Page Delete A - 01");

        var preparingFirst = vm.PrepareDeleteAsync(first);
        await started.Task.WaitAsync(Timeout);
        vm.DeleteFromDownloader = false;
        vm.CancelDelete();
        var preparingSecond = vm.PrepareDeleteAsync(second);
        release.TrySetResult();
        await Task.WhenAll(preparingFirst, preparingSecond).WaitAsync(Timeout);

        Assert.Equal(secondId, vm.DeleteTarget?.Id);
        Assert.True(vm.DeleteFromDownloader);
        Assert.True(vm.TorrentInDownloader);
        Assert.NotNull(await harness.GetTaskAsync(firstId));
        Assert.NotNull(await harness.GetTaskAsync(secondId));
    }

    [Fact]
    public async Task PrepareDelete_SameTargetLatePrecheckPreservesExplicitUserChoice()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Same - 01"));
        await harness.ReconcileAsync();
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        var target = vm.Tasks.Single(task => task.Id == taskId);
        var (started, release) = InstallFindGate(harness.Factory, "Page Same - 01");

        var preparing = vm.PrepareDeleteAsync(target);
        await started.Task.WaitAsync(Timeout);
        vm.DeleteFromDownloader = true;
        vm.DeleteFromDownloader = false;
        release.TrySetResult();
        await preparing.WaitAsync(Timeout);

        Assert.Equal(taskId, vm.DeleteTarget?.Id);
        Assert.False(vm.DeleteFromDownloader);
        Assert.True(vm.TorrentInDownloader);
        await vm.DeleteAsync(taskId).WaitAsync(Timeout);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.True(harness.Store.Torrents.ContainsKey(TestTorrentBytes.InfoHashOf("Page Same - 01")));
    }

    [Fact]
    public async Task SelectAll_OperatesOnCurrentPageOnly_AndKeepsOtherPagesSelection()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, Enumerable.Range(0, 101)
                                                                    .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                             $"Sel - {index:0000}",
                                                                             DownloadTaskListTestSeed.At(index))));
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        Assert.Multiple(
                        () => Assert.Equal(101, vm.TotalCount),
                        () => Assert.Equal(100, vm.Tasks.Count));

        vm.ToggleBatchMode();
        vm.ToggleSelectAll();
        Assert.Equal(100, vm.SelectedCount);

        await vm.GoToPageAsync(2);
        Assert.Single(vm.Tasks);
        vm.ToggleSelectAll();
        Assert.Multiple(
                        () => Assert.True(vm.AllSelected),
                        () => Assert.Equal(101, vm.SelectedCount));

        vm.ToggleSelectAll();
        Assert.Multiple(
                        () => Assert.False(vm.AllSelected),
                        () => Assert.Equal(100, vm.SelectedCount));

        await vm.GoToPageAsync(1);
        Assert.True(vm.AllSelected);
        vm.ToggleSelectAll();
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task SelectAll_WithActiveFilters_OnlyTogglesVisibleTasks()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var failedTasks = new[]
        {
            DownloadTaskListTestSeed.NewTask("Sel - 失败甲", DownloadTaskListTestSeed.At(-2),
                                             DownloadTaskStatus.Failed),
            DownloadTaskListTestSeed.NewTask("Sel - 失败乙", DownloadTaskListTestSeed.At(-1),
                                             DownloadTaskStatus.Failed)
        };
        await DownloadTaskListTestSeed.SeedAsync(harness,
                                                 Enumerable.Range(0, 100)
                                                           .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                    $"Sel - {index:0000}",
                                                                    DownloadTaskListTestSeed.At(index)))
                                                           .Concat(failedTasks));
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        Assert.Equal(102, vm.TotalCount);

        vm.ToggleBatchMode();
        vm.ToggleSelectAll();
        Assert.Equal(100, vm.SelectedCount);

        await vm.ToggleStatusFilterAsync(DownloadStatusFilterGroup.Failed);
        Assert.Multiple(
                        () => Assert.Equal(2, vm.TotalCount),
                        () => Assert.Equal(2, vm.Tasks.Count));
        vm.ToggleSelectAll();
        Assert.Equal(102, vm.SelectedCount);

        await vm.ClearFiltersAsync();
        Assert.Multiple(
                        () => Assert.Equal(102, vm.TotalCount),
                        () => Assert.Equal(100, vm.Tasks.Count),
                        // 第一页的 100 个完成任务仍在选中集内，表头全选如实反映当前页。
                        () => Assert.True(vm.AllSelected));
        vm.ToggleSelectAll();
        // 取消全选只移除当前页的 100 个；筛选隐藏期间选中的 2 个失败任务保留。
        Assert.True(failedTasks.Select(task => task.Id).ToHashSet().SetEquals(vm.SelectedIds));
    }

    [Fact]
    public async Task SearchText_Debounces_ResetsToFirstPage_AndCoversAllHistory()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        // 目标任务是 501 条中最旧的一条：旧的 500 条截断实现会被本用例击穿。
        await DownloadTaskListTestSeed.SeedAsync(harness,
                                                 Enumerable.Range(1, 500)
                                                           .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                    $"Hit - {index:0000}",
                                                                    DownloadTaskListTestSeed.At(index)))
                                                           .Prepend(DownloadTaskListTestSeed.NewTask("Old - 0001",
                                                                        DownloadTaskListTestSeed.At(0))));
        var vm = CreateViewModel(harness);
        vm.SearchDebounceDelay = TimeSpan.FromMilliseconds(1);
        await vm.LoadAsync();
        Assert.Multiple(
                        () => Assert.Equal(501, vm.TotalCount),
                        () => Assert.Equal(6, vm.TotalPages));

        // 最旧的一条落在第 6 页：分页确实覆盖最新 500 条之外的历史。
        await vm.GoToPageAsync(6);
        Assert.Equal("Old - 0001", Assert.Single(vm.Tasks).NamingSnapshot);

        await vm.GoToPageAsync(2);
        Assert.Equal(2, vm.PageNumber);

        vm.SearchText = "Old - 0001";
        await WaitForFilterAsync(vm, () => vm.TotalCount == 1);
        Assert.Multiple(
                        () => Assert.Equal(1, vm.PageNumber),
                        () => Assert.Equal(1, vm.TotalCount),
                        () => Assert.Equal("Old - 0001", Assert.Single(vm.Tasks).NamingSnapshot));

        vm.SearchText = "   ";
        await WaitForFilterAsync(vm, () => vm.TotalCount == 501);
        Assert.Equal(1, vm.PageNumber);
    }

    [Fact]
    public async Task LoadAsync_ClampedPageNumber_SyncsInternalState()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var tasks = Enumerable.Range(0, 101)
                              .Select(index => DownloadTaskListTestSeed.NewTask(
                                                                                $"Clamp - {index:0000}",
                                                                                DownloadTaskListTestSeed.At(index)))
                              .ToList();
        await DownloadTaskListTestSeed.SeedAsync(harness, tasks);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        await vm.GoToPageAsync(2);
        Assert.Multiple(
                        () => Assert.Equal(2, vm.PageNumber),
                        () => Assert.Single(vm.Tasks));

        // 100 条后原第 2 页越界：刷新时查询层钳制回第 1 页。
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.DownloadTasks.Remove(tasks[0]);
            await db.SaveChangesAsync();
        }

        await vm.LoadAsync();
        Assert.Multiple(
                        () => Assert.Equal(1, vm.PageNumber),
                        () => Assert.Equal(100, vm.Tasks.Count),
                        () => Assert.Equal(100, vm.TotalCount));

        // 任务回到 101 条后刷新：内部页码已同步为 1，仍停在第 1 页，不自动跳回第 2 页。
        await DownloadTaskListTestSeed.SeedAsync(harness, [tasks[0]]);
        await vm.LoadAsync();
        Assert.Multiple(
                        () => Assert.Equal(1, vm.PageNumber),
                        () => Assert.Equal(101, vm.TotalCount),
                        () => Assert.Equal(100, vm.Tasks.Count));
    }

    private static async Task WaitForFilterAsync(DownloadTasksPageViewModel vm, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "等待筛选结果超时。");
    }

    private static DownloadTasksPageViewModel CreateViewModel(DownloadServiceTests.TestHarness harness) =>
        new(harness.Service, harness.Intake,
            new DownloadTaskQueries(harness.Provider.GetRequiredService<
                                        IDbContextFactory<ApplicationDbContext>>()),
            NullLogger<DownloadTasksPageViewModel>.Instance);

    private static (TaskCompletionSource Started, TaskCompletionSource Release) InstallFindGate(
        FakeQbittorrentSessionFactory factory, string targetName)
    {
        var targetHash = TestTorrentBytes.InfoHashOf(targetName);
        var started    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasPaused  = 0;
        factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = async hashes =>
        {
            if (!hashes.Contains(targetHash, StringComparer.OrdinalIgnoreCase) ||
                Interlocked.Exchange(ref hasPaused, 1) != 0) return;
            started.TrySetResult();
            await release.Task.WaitAsync(Timeout);
        };
        return (started, release);
    }
}
