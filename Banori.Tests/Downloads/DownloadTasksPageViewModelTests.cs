using Banori.Components.Pages.Downloads;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
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
        await DownloadTaskListTestSeed.SeedAsync(harness,
                                                 Enumerable.Range(0, 101)
                                                           .Select(index =>
                                                                       DownloadTaskListTestSeed
                                                                          .NewTask($"Sel - {index:0000}",
                                                                                    DownloadTaskListTestSeed
                                                                                       .At(index))));
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        Assert.Multiple(() => Assert.Equal(101, vm.TotalCount), () => Assert.Equal(100, vm.Tasks.Count));

        vm.ToggleBatchMode();
        vm.ToggleSelectAll();
        Assert.Equal(100, vm.SelectedCount);

        await vm.GoToPageAsync(2);
        Assert.Single(vm.Tasks);
        vm.ToggleSelectAll();
        Assert.Multiple(() => Assert.True(vm.AllSelected), () => Assert.Equal(101, vm.SelectedCount));

        vm.ToggleSelectAll();
        Assert.Multiple(() => Assert.False(vm.AllSelected), () => Assert.Equal(100, vm.SelectedCount));

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

        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        Assert.Multiple(() => Assert.Equal(2, vm.TotalCount), () => Assert.Equal(2, vm.Tasks.Count));
        vm.ToggleSelectAll();
        Assert.Equal(102, vm.SelectedCount);

        await vm.ClearFiltersAsync();
        Assert.Multiple(() => Assert.Equal(102, vm.TotalCount),
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
                                                           .Select(index =>
                                                                       DownloadTaskListTestSeed
                                                                          .NewTask($"Hit - {index:0000}",
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
        Assert.Multiple(() => Assert.Equal(1, vm.PageNumber),
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
                              .Select(index => DownloadTaskListTestSeed.NewTask($"Clamp - {index:0000}",
                                                                                    DownloadTaskListTestSeed.At(index)))
                              .ToList();
        await DownloadTaskListTestSeed.SeedAsync(harness, tasks);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        await vm.GoToPageAsync(2);
        Assert.Multiple(() => Assert.Equal(2, vm.PageNumber), () => Assert.Single(vm.Tasks));

        // 100 条后原第 2 页越界：刷新时查询层钳制回第 1 页。
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.DownloadTasks.Remove(tasks[0]);
            await db.SaveChangesAsync();
        }

        await vm.LoadAsync();
        Assert.Multiple(() => Assert.Equal(1, vm.PageNumber),
                        () => Assert.Equal(100, vm.Tasks.Count),
                        () => Assert.Equal(100, vm.TotalCount));

        // 任务回到 101 条后刷新：内部页码已同步为 1，仍停在第 1 页，不自动跳回第 2 页。
        await DownloadTaskListTestSeed.SeedAsync(harness, [tasks[0]]);
        await vm.LoadAsync();
        Assert.Multiple(() => Assert.Equal(1, vm.PageNumber),
                        () => Assert.Equal(101, vm.TotalCount),
                        () => Assert.Equal(100, vm.Tasks.Count));
    }

    [Fact]
    public async Task ReverifyAsync_UnconfirmedTask_ReloadsResetIntentAndReportsSuccess()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var task = DownloadTaskListTestSeed.NewTask("Page Reverify OK", DownloadTaskListTestSeed.At(0),
                                                    DownloadTaskStatus.Paused,
                                                    verificationIntent : new DownloadVerificationIntent
                                                    {
                                                        CreatedAtUtc = DownloadTaskListTestSeed.Base,
                                                        Step         = DownloadVerificationStep.Unconfirmed
                                                    }.ToJson());
        await DownloadTaskListTestSeed.SeedAsync(harness, [task]);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();

        await vm.ReverifyAsync(task.Id).WaitAsync(Timeout);

        Assert.False(vm.Busy);
        Assert.True(vm.Succeeded);
        Assert.Equal("已重新发起校验，等待下载器确认。", vm.Message);
        var reloaded = await harness.GetTaskAsync(task.Id);
        Assert.Equal(DownloadTaskStatus.Downloading, reloaded.Status);
        Assert.Equal(DownloadVerificationStep.Requested,
                     DownloadVerificationIntent.FromJson(reloaded.VerificationIntent)!.Step);
    }

    [Fact]
    public async Task ReverifyAsync_RejectedTask_ShowsServiceErrorAndClearsBusy()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       taskId  = await harness.CreateUploadAsync(TestTorrentBytes.Create("Page Reverify Error - 01"));
        var       vm      = CreateViewModel(harness);
        await vm.LoadAsync();

        await vm.ReverifyAsync(taskId).WaitAsync(Timeout);

        Assert.False(vm.Busy);
        Assert.False(vm.Succeeded);
        Assert.Equal("只有校验未确认的任务可以重新校验。", vm.Message);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, (await harness.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task StatusFilter_DefaultsToAllGroups_AndUncheckingAllShowsNothing()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness,
        [
            DownloadTaskListTestSeed.NewTask("Filter - 完成", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("Filter - 失败", DownloadTaskListTestSeed.At(1),
                                             DownloadTaskStatus.Failed)
        ]);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        Assert.Multiple(() => Assert.Equal(DownloadStatusFilter.Choices.Count, vm.StatusFilter.Count),
                        () => Assert.False(vm.StatusFilterActive),
                        () => Assert.False(vm.HasActiveFilters),
                        () => Assert.Equal(2, vm.TotalCount));

        // 只取消一个状态：其余分组保持勾选，等于排除该状态，不必把其他分组全勾上。
        await vm.ToggleStatusFilterAsync(DownloadStatusFilterGroup.Failed);
        Assert.Multiple(() => Assert.True(vm.StatusFilterActive),
                        () => Assert.True(vm.HasActiveFilters),
                        () => Assert.Equal(1, vm.TotalCount),
                        () => Assert.Equal("Filter - 完成", Assert.Single(vm.Tasks).NamingSnapshot));

        // 全不勾选就是全都不要，而不是“不筛选”。
        await vm.DeselectAllStatusesAsync();
        Assert.Multiple(() => Assert.Empty(vm.Tasks),
                        () => Assert.Equal(0, vm.TotalCount),
                        () => Assert.True(vm.HasActiveFilters));

        await vm.SelectAllStatusesAsync();
        Assert.Multiple(() => Assert.Equal(2, vm.TotalCount),
                        () => Assert.False(vm.HasActiveFilters));
    }

    [Fact]
    public async Task StatusFilter_PersistsAcrossPageReload_IncludingEmptySelection()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, [
            DownloadTaskListTestSeed.NewTask("Reload - 完成", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("Reload - 失败", DownloadTaskListTestSeed.At(1),
                                             DownloadTaskStatus.Failed)
        ]);
        var first = CreateViewModel(harness);
        await first.LoadAsync();
        await first.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        Assert.Equal("Reload - 失败", Assert.Single(first.Tasks).NamingSnapshot);

        // 刷新页面 = 新页面状态重新加载：恢复已保存的勾选，而不是回到全选。
        var reloaded = CreateViewModel(harness);
        await reloaded.LoadAsync();
        Assert.Multiple(() => Assert.True(reloaded.StatusFilter.SetEquals([DownloadStatusFilterGroup.Failed])),
                        () => Assert.True(reloaded.StatusFilterActive),
                        () => Assert.Equal("Reload - 失败", Assert.Single(reloaded.Tasks).NamingSnapshot));

        // “全不勾选”也是需要保存的明确状态，刷新后不能退化成“不筛选”。
        await reloaded.DeselectAllStatusesAsync();
        var emptyReload = CreateViewModel(harness);
        await emptyReload.LoadAsync();
        Assert.Multiple(() => Assert.Empty(emptyReload.StatusFilter),
                        () => Assert.True(emptyReload.StatusFilterActive),
                        () => Assert.Equal(0, emptyReload.TotalCount));

        // 清除筛选写回全选：再次刷新回到全部任务。
        await emptyReload.ClearFiltersAsync();
        var cleared = CreateViewModel(harness);
        await cleared.LoadAsync();
        Assert.Multiple(() => Assert.Equal(DownloadStatusFilter.Choices.Count, cleared.StatusFilter.Count),
                        () => Assert.Equal(2, cleared.TotalCount));
    }

    [Fact]
    public async Task StatusFilter_StoreFailureKeepsCurrentSelectionAndListing()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, [
            DownloadTaskListTestSeed.NewTask("Broken - 完成", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("Broken - 失败", DownloadTaskListTestSeed.At(1),
                                             DownloadTaskStatus.Failed)
        ]);
        var settingStore =
            (MemoryApplicationSettingStore)harness.Provider.GetRequiredService<IApplicationSettingStore>();
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        settingStore.ThrowOnSet = new InvalidOperationException("模拟设置存储故障。");

        await vm.ToggleStatusFilterAsync(DownloadStatusFilterGroup.Failed);

        Assert.Multiple(() => Assert.True(vm.StatusFilterActive),
                        () => Assert.Equal("Broken - 完成", Assert.Single(vm.Tasks).NamingSnapshot));
    }

    [Fact]
    public async Task StatusFilter_ToggleWhileStoredFilterInFlight_KeepsUserSelection()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, [
            DownloadTaskListTestSeed.NewTask("Race - 完成", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("Race - 失败", DownloadTaskListTestSeed.At(1), DownloadTaskStatus.Failed)
        ]);
        var settingStore =
            (MemoryApplicationSettingStore)harness.Provider.GetRequiredService<IApplicationSettingStore>();
        await new DownloadTaskFilterStore(settingStore).SaveStatusGroupsAsync(new HashSet<DownloadStatusFilterGroup>
        {
            DownloadStatusFilterGroup.Completed
        });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingStore.GetGate = gate.Task;
        var vm        = CreateViewModel(harness);
        var firstLoad = vm.LoadAsync();

        // 已保存筛选的读取还在途时用户改了勾选：慢请求晚到不得把新选择覆盖回旧值。
        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        gate.SetResult();
        await firstLoad.WaitAsync(Timeout);

        Assert.Multiple(() => Assert.True(vm.StatusFilter.SetEquals([DownloadStatusFilterGroup.Failed])),
                        () => Assert.Equal("Race - 失败", Assert.Single(vm.Tasks).NamingSnapshot));
    }

    [Fact]
    public async Task StatusFilter_ConcurrentLoadsWhileStoredFilterInFlight_UseRestoredSelection()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, [
            DownloadTaskListTestSeed.NewTask("Load - 完成", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("Load - 失败", DownloadTaskListTestSeed.At(1), DownloadTaskStatus.Failed)
        ]);
        var settingStore =
            (MemoryApplicationSettingStore)harness.Provider.GetRequiredService<IApplicationSettingStore>();
        await new DownloadTaskFilterStore(settingStore).SaveStatusGroupsAsync(new HashSet<DownloadStatusFilterGroup>
        {
            DownloadStatusFilterGroup.Failed
        });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingStore.GetGate = gate.Task;
        var vm = CreateViewModel(harness);

        // 首次加载还在读已保存筛选时，上传完成会再触发一次加载：两次都必须等到恢复完成再查询，
        // 后到的加载不能把“读取已开始”当成“已恢复”，用默认全选先查一遍。
        var firstLoad  = vm.LoadAsync();
        var secondLoad = vm.LoadAsync();
        gate.SetResult();
        await Task.WhenAll(firstLoad, secondLoad).WaitAsync(Timeout);

        Assert.Multiple(() => Assert.True(vm.StatusFilter.SetEquals([DownloadStatusFilterGroup.Failed])),
                        () => Assert.Equal("Load - 失败", Assert.Single(vm.Tasks).NamingSnapshot));
    }

    [Fact]
    public async Task StatusFilter_ConsecutiveToggles_WriteSelectionsInOrder()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness, [
            DownloadTaskListTestSeed.NewTask("Order - 完成", DownloadTaskListTestSeed.At(0)),
            DownloadTaskListTestSeed.NewTask("Order - 失败", DownloadTaskListTestSeed.At(1), DownloadTaskStatus.Failed)
        ]);
        var settingStore =
            (MemoryApplicationSettingStore)harness.Provider.GetRequiredService<IApplicationSettingStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingStore.SetGates[DownloadTaskFilterStore.StatusGroupsKey] = (1, release.Task);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();

        // 第一次写入未完成时继续勾选：第二次保存必须排在后面，较早的选择不能晚于较新的选择落库。
        var first  = vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        var second = vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Lost]);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        var writes = settingStore.SetValues.Where(entry => entry.Key == DownloadTaskFilterStore.StatusGroupsKey)
                                 .Select(entry => entry.Value).ToArray();
        Assert.Multiple(() => Assert.Equal(2, writes.Length),
                        () => Assert.Contains("Failed", writes[0]),
                        () => Assert.Contains("Lost", writes[1]));
    }

    [Fact]
    public async Task MissingTorrentTask_LostFilterIncludesIt_AndNoDuplicateCountWhenCompletedAlsoSelected()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       missing = DownloadTaskListTestSeed.NewTask("Page Missing", DownloadTaskListTestSeed.At(0));
        missing.TorrentMissingAtUtc = DownloadTaskListTestSeed.At(1);
        var ordinary = DownloadTaskListTestSeed.NewTask("Page Ordinary", DownloadTaskListTestSeed.At(2));
        await DownloadTaskListTestSeed.SeedAsync(harness, [missing, ordinary]);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();

        // 缺种任务保留 Completed，但必须能只勾「丢失」找到；普通完成任务不混入。
        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Lost]);
        Assert.Multiple(() => Assert.Equal(1, vm.TotalCount),
                        () => Assert.Equal(missing.Id, Assert.Single(vm.Tasks).Id));

        // 同一任务同时命中 Completed 与 Lost 两个 OR 分支，列表与总数都只能算一次。
        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Completed]);
        Assert.Equal(2, vm.TotalCount);

        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Completed, DownloadStatusFilterGroup.Lost]);
        Assert.Multiple(() => Assert.Equal(2, vm.TotalCount),
                        () => Assert.Equal(2, vm.Tasks.Count),
                        () => Assert.Equal(2, vm.Tasks.Select(task => task.Id).Distinct().Count()),
                        () => Assert.Contains(vm.Tasks, task => task.Id == missing.Id && task.IsTorrentMissing));
    }

    [Fact]
    public async Task PreviewFileAsync_TorrentMissing_UsesFixedMessage()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       taskId  = Guid.NewGuid();
        var fileService = new StubFileService
        {
            FilesView = new DownloadTaskFilesView(taskId, "/downloads",
            [
                new DownloadFileView(0, "01.mkv", "01.mkv", DownloadFileKind.Anime, "测试", 1, null, null,
                                     1024, true, null, null, null, null)
            ]),
            PreviewException = new TorrentMissingException()
        };
        var vm = CreateViewModel(harness, fileService);

        await vm.LoadFilesAsync(taskId);
        vm.BeginFileEdit(taskId, 0);
        await vm.PreviewFileAsync(taskId, 0);

        var state = vm.FileState(taskId);
        Assert.Multiple(() => Assert.Equal("qBittorrent 中已找不到该种子", state.Error),
                        () => Assert.Null(state.Drafts[0].Preview),
                        () => Assert.False(state.Busy));
    }

    [Fact]
    public async Task RedownloadAsync_CompletedWithMissingTorrent_ResubmitsKeepsHistoryAndRefreshes()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       task    = DownloadTaskListTestSeed.NewTask("Page Redownload", DownloadTaskListTestSeed.At(0));
        task.CompletedAtUtc      = DownloadTaskListTestSeed.At(1);
        task.TorrentMissingAtUtc = DownloadTaskListTestSeed.At(2);
        await DownloadTaskListTestSeed.SeedAsync(harness, [task]);
        var vm = CreateViewModel(harness);
        await vm.LoadAsync();
        Assert.True(vm.Tasks.Single().IsTorrentMissing);

        await vm.RedownloadAsync(task.Id).WaitAsync(Timeout);

        Assert.Multiple(() => Assert.False(vm.Busy),
                        () => Assert.True(vm.Succeeded),
                        () => Assert.Equal("已重新提交下载，等待下载器确认。", vm.Message),
                        () => Assert.False(vm.Tasks.Single().IsTorrentMissing));
        var reloaded = await harness.GetTaskAsync(task.Id);
        Assert.Multiple(() => Assert.Equal(DownloadTaskStatus.PendingSubmit, reloaded.Status),
                        () => Assert.Equal(DownloadTaskListTestSeed.At(1), reloaded.CompletedAtUtc),
                        () => Assert.Null(reloaded.TorrentMissingAtUtc));
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
            new DownloadTaskFilterStore(harness.Provider.GetRequiredService<IApplicationSettingStore>()),
            NullLogger<DownloadTasksPageViewModel>.Instance);

    private static DownloadTasksPageViewModel CreateViewModel(
        DownloadServiceTests.TestHarness harness, IDownloadFileService fileService) =>
        new(harness.Service, harness.Intake,
            new DownloadTaskQueries(harness.Provider.GetRequiredService<
                                        IDbContextFactory<ApplicationDbContext>>()),
            new DownloadTaskFilterStore(harness.Provider.GetRequiredService<IApplicationSettingStore>()),
            NullLogger<DownloadTasksPageViewModel>.Instance,
            fileService);

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

    private sealed class StubFileService : IDownloadFileService
    {
        public required DownloadTaskFilesView FilesView        { get; init; }
        public          Exception?            PreviewException { get; set; }

        public Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(FilesView);

        public Task<DownloadFilePreview> PreviewAsync(
            Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default) =>
            PreviewException is null
                ? Task.FromResult(new DownloadFilePreview("原路径", "目标路径", "/downloads", "/downloads", false, null))
                : Task.FromException<DownloadFilePreview>(PreviewException);

        public Task<DownloadTaskFilesView> ApplyAsync(
            Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(FilesView);

        public Task RetryArchiveAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
