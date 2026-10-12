using MediaManager.Components.Pages.Downloads;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Downloads;

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
