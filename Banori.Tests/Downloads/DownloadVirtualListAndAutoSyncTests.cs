using Banori.Components.Pages.Downloads;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using Xunit.Abstractions;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>第一阶段 B 验收测试：万级无页码虚拟滚动、锚点恢复、变更通知、文件 5 秒持续同步与改名确认链路。</summary>
public sealed class DownloadVirtualListAndAutoSyncTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TenThousandTasks_VirtualWindowInitialLoadAndScrollToEnd()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var batch = new List<DownloadTask>(10_000);
            for (var i = 0; i < 10_000; i++)
            {
                var status = (i % 4) switch
                {
                    0 => DownloadTaskStatus.Completed,
                    1 => DownloadTaskStatus.Downloading,
                    2 => DownloadTaskStatus.Paused,
                    _ => DownloadTaskStatus.Failed
                };
                var task = DownloadTaskListTestSeed.NewTask($"VList-{i:00000}",
                                                            DownloadTaskListTestSeed.At(i),
                                                            status,
                                                            userPaused : status == DownloadTaskStatus.Paused,
                                                            ingredients : DownloadTaskListTestSeed
                                                               .Ingredients("LoliHouse", i % 2 == 0 ? "简" : "繁"));
                task.Episode = (i % 24) + 1;
                task.Version = (i % 3)  + 1;
                batch.Add(task);
            }

            db.DownloadTasks.AddRange(batch);
            await db.SaveChangesAsync();
        }

        var vm = CreateViewModel(harness);
        vm.OverscanCount = 10;

        var sw = Stopwatch.StartNew();
        await vm.LoadAsync();
        sw.Stop();

        var initialMounted = vm.Tasks.Count;
        output.WriteLine($"10k ViewModel initial LoadAsync: {sw.ElapsedMilliseconds} ms, mounted={initialMounted}, total={vm.TotalCount}");

        Assert.Equal(10_000, vm.TotalCount);
        Assert.Equal(DownloadTasksPageViewModel.DefaultViewportCount + vm.OverscanCount, initialMounted);
        Assert.Equal(0, vm.TopSpacerHeightPx);
        Assert.Equal((10_000 - initialMounted) * DownloadTasksPageViewModel.RowHeightPx, vm.BottomSpacerHeightPx);

        // 挂载行数随视口与缓冲区变化。
        vm.OverscanCount = 4;
        await vm.SetViewportAsync(500, 12);
        Assert.Equal(12, vm.VisibleTaskIds.Count);
        Assert.Equal(20, vm.Tasks.Count); // [496, 516)
        Assert.Equal(496 * DownloadTasksPageViewModel.RowHeightPx, vm.TopSpacerHeightPx);
        Assert.Equal((10_000 - 516) * DownloadTasksPageViewModel.RowHeightPx, vm.BottomSpacerHeightPx);

        // 直达列表末尾。
        await vm.SetViewportAsync(9985, 20);
        Assert.Equal(9985, vm.VisibleStartIndex);
        Assert.Equal(10_000, vm.MountedEndIndex);
        Assert.Equal(0, vm.BottomSpacerHeightPx);
        Assert.Equal(vm.OrderedTaskIds[^1], vm.Tasks[^1].Id);

        // 筛选与排序覆盖完整 10,000 条并重置滚动位置到顶部。
        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        Assert.Equal(2_500, vm.TotalCount);
        Assert.Equal(0, vm.VisibleStartIndex);
        Assert.Equal(0, vm.ConsumePendingScrollTopPx());

        await vm.SortByColumnAsync(DownloadTaskSortColumn.CreatedAt);
        Assert.Equal(0, vm.VisibleStartIndex);
        Assert.Equal(0, vm.ConsumePendingScrollTopPx());
        Assert.True(vm.Tasks[0].CreatedAtUtc >= vm.Tasks[1].CreatedAtUtc);
    }

    [Fact]
    public async Task ScrollAndMutations_KeepSelectedDetailDraftsSelectionAndRestoreAnchor()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var tasks = Enumerable.Range(0, 60)
                              .Select(i => DownloadTaskListTestSeed.NewTask($"Item-{i:000}",
                                                                            DownloadTaskListTestSeed.At(i),
                                                                            DownloadTaskStatus.Completed))
                              .ToList();
        await DownloadTaskListTestSeed.SeedAsync(harness, tasks);

        var fileService = new TestFileService(tasks[2].Id);
        var vm          = CreateViewModel(harness, fileService);
        vm.OverscanCount = 3;
        await vm.LoadAsync();
        await vm.SortByColumnAsync(DownloadTaskSortColumn.CreatedAt); // 降序：Item-059 在第 0 行

        // 选中第 2 行任务并打开文件编辑草稿，然后滚出视口到第 40 行。
        var selectedId = vm.OrderedTaskIds[2];
        await vm.SelectTaskAsync(selectedId);
        await vm.ToggleFilesAsync(selectedId);
        vm.BeginFileEdit(selectedId, 0);
        vm.FileState(selectedId).Drafts[0].Episode = 9m;

        await vm.SetViewportAsync(40, 10);
        Assert.DoesNotContain(vm.Tasks, item => item.Id == selectedId);
        Assert.NotNull(vm.SelectedTask);
        Assert.Equal(selectedId, vm.SelectedTask.Id);
        Assert.Equal(9m, vm.FileState(selectedId).Drafts[0].Episode);

        // 筛选变化导致选中行不在当前列表中时，草稿与独立详情依然保留。
        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        Assert.Equal(0, vm.TotalCount);
        Assert.NotNull(vm.SelectedTask);
        Assert.Equal(9m, vm.FileState(selectedId).Drafts[0].Episode);

        await vm.ClearFiltersAsync();
        await vm.SetViewportAsync(30, 8);

        // 批量勾选跨滚动保留，且「勾选可见行」仅覆盖视口内任务（不含缓冲区）。
        vm.ToggleBatchMode();
        vm.ToggleSelectAll();
        Assert.Equal(8, vm.SelectedCount);
        Assert.Equal(vm.VisibleTaskIds.ToHashSet(), vm.SelectedIds.ToHashSet());

        // 后台非排序/成员变更：按 ID 局部更新挂载行与 SelectedTask，不重建有序快照。
        var visibleTargetId = vm.VisibleTaskIds[0];
        await using (var db = await harness.CreateDbContextAsync())
        {
            var row = await db.DownloadTasks.SingleAsync(item => item.Id == visibleTargetId);
            row.Progress = 0.75;
            await db.SaveChangesAsync();
        }

        await vm.RefreshChangedTasksAsync([visibleTargetId], false);
        Assert.Equal(0.75, vm.Tasks.Single(item => item.Id == visibleTargetId).Progress);

        // 后台影响顺序/成员的变更：在当前视口之前新增两条更新的任务，使可见锚点整体下移 2 行。
        var anchorId = vm.VisibleTaskIds[0];
        var newer1   = DownloadTaskListTestSeed.NewTask("Newer-1", DownloadTaskListTestSeed.At(1000));
        var newer2   = DownloadTaskListTestSeed.NewTask("Newer-2", DownloadTaskListTestSeed.At(1001));
        await DownloadTaskListTestSeed.SeedAsync(harness, [newer1, newer2]);

        await vm.RefreshChangedTasksAsync([newer1.Id, newer2.Id], true);
        Assert.Equal(32, vm.VisibleStartIndex);
        Assert.Equal(anchorId, vm.VisibleTaskIds[0]);
        Assert.Equal(32 * DownloadTasksPageViewModel.RowHeightPx, vm.ConsumePendingScrollTopPx());
    }

    [Fact]
    public async Task LateStaleLoadQuery_IsDiscardedByRequestSequence()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        await DownloadTaskListTestSeed.SeedAsync(harness,
        [
            DownloadTaskListTestSeed.NewTask("Alpha-1", DownloadTaskListTestSeed.At(0), DownloadTaskStatus.Completed),
            DownloadTaskListTestSeed.NewTask("Beta-1",  DownloadTaskListTestSeed.At(1), DownloadTaskStatus.Failed)
        ]);

        var settingStore =
            (MemoryApplicationSettingStore)harness.Provider.GetRequiredService<IApplicationSettingStore>();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingStore.GetGate = firstGate.Task;

        var vm        = CreateViewModel(harness);
        var staleLoad = vm.LoadAsync();

        // 在首次 LoadAsync 尚未完成前，用户主动切换状态筛选为仅 Failed。
        await vm.SetStatusFilterAsync([DownloadStatusFilterGroup.Failed]);
        firstGate.SetResult();
        await staleLoad.WaitAsync(Timeout);

        var only = Assert.Single(vm.Tasks);
        Assert.Equal("Beta-1", only.NamingSnapshot);
        Assert.Equal(1, vm.TotalCount);
    }

    [Fact]
    public async Task CompletedTask_FiveSecondPollingRecoversFromTransientFailure_AndUpdatesLastSyncedOnlyOnSuccess()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Completed Sync - 01"));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(task => task.Id == id);
            entity.Status = DownloadTaskStatus.Completed;
            await db.SaveChangesAsync();
        }

        var clock       = new MutableTimeProvider(new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var fileService = new TestFileService(id) { FailRefresh = true };
        var vm          = CreateViewModel(harness, fileService, clock);
        vm.FileAutoRefreshInterval = TimeSpan.FromMilliseconds(15);
        await vm.LoadAsync();
        await vm.SelectTaskAsync(id);

        vm.SetFilePanelActive(true, true, id);
        var state = vm.FileState(id);
        await WaitForAsync(() => state.SilentRefreshError is not null);

        // 初次远端同步失败：保留本地 ListAsync 数据，不推进 LastSyncedAtUtc。
        Assert.NotNull(state.Details);
        Assert.Null(state.LastSyncedAtUtc);
        Assert.Contains("后台同步失败", state.SilentRefreshError);

        // 下一轮 5 秒轮询恢复正常后：清除失败提示并写入 LastSyncedAtUtc。
        clock.Advance(TimeSpan.FromSeconds(5));
        fileService.FailRefresh = false;
        await WaitForAsync(() => state.LastSyncedAtUtc is not null && state.SilentRefreshError is null);
        Assert.Equal(clock.GetUtcNow(), state.LastSyncedAtUtc);
        Assert.Equal("/downloads/Refreshed", state.Details?.CurrentRootPath);

        vm.StopFilePolling();
    }

    [Fact]
    public async Task RenameConfirmation_HandlesImmediateShortIntervalAndBudgetExhaustionFallbackToFiveSeconds()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Rename Confirm - 01"));
        var       clock   = new MutableTimeProvider(new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var fileService = new TestFileService(id);
        var vm          = CreateViewModel(harness, fileService, clock);
        vm.PendingFileRefreshInterval = TimeSpan.FromMilliseconds(10);
        vm.PendingFileRefreshAttempts = 20;
        await vm.LoadAsync();
        await vm.ToggleFilesAsync(id);

        // 1. 立即确认：ApplyAsync 直接返回 HasPendingFileOperation = false。
        vm.BeginFileEdit(id, 0);
        await vm.PreviewFileAsync(id, 0);
        await vm.ApplyFileAsync(id, 0);
        var state = vm.FileState(id);
        Assert.False(state.FileOperationPending);
        Assert.Equal(clock.GetUtcNow(), state.LastSyncedAtUtc);
        Assert.Equal(0, fileService.ObserveCalls);

        // 2. 延迟确认（1 秒短间隔观察），同时保留其他文件的编辑草稿并使旧预览失效。
        clock.Advance(TimeSpan.FromSeconds(10));
        vm.BeginFileEdit(id, 1);
        var otherDraft = state.Drafts[1];
        otherDraft.Episode = 12m;
        await vm.PreviewFileAsync(id, 1);
        Assert.NotNull(otherDraft.Preview);

        vm.BeginFileEdit(id, 0);
        await vm.PreviewFileAsync(id, 0);
        fileService.PendingConfirmAfterObserves = 2;
        await vm.ApplyFileAsync(id, 0);

        Assert.True(state.FileOperationPending);
        await WaitForAsync(() => !state.FileOperationPending);
        Assert.Equal(2, fileService.ObserveCalls);
        Assert.Same(otherDraft, state.Drafts[1]);
        Assert.Equal(12m, otherDraft.Episode);
        Assert.Null(otherDraft.Preview);
        Assert.Equal(clock.GetUtcNow(), state.LastSyncedAtUtc);

        // 3. 补发预算耗尽（DownloadLayoutConfirmationException）：退出 1 秒循环，回到 5 秒观察，
        //    后续 qB 达到目标路径时通过 AutoRefreshFilesAsync（ObservePendingAsync）自动确认。
        clock.Advance(TimeSpan.FromSeconds(10));
        vm.BeginFileEdit(id, 0);
        await vm.PreviewFileAsync(id, 0);
        fileService.ThrowBudgetExhaustedOnObserve = true;
        fileService.PendingConfirmAfterObserves   = int.MaxValue;
        await vm.ApplyFileAsync(id, 0);

        await WaitForAsync(() => state.Error is not null && state.Error.Contains("人工核对"));
        Assert.True(state.FileOperationPending);
        Assert.False(state.PendingFileWaitPaused);
        Assert.Null(state.PendingRefreshCancellation);

        // 回到 5 秒观察后，qB 目标终于就位，AutoRefreshFilesAsync 调用 ObservePendingAsync 确认成功。
        fileService.ThrowBudgetExhaustedOnObserve = false;
        fileService.PendingConfirmAfterObserves   = 1;
        clock.Advance(TimeSpan.FromSeconds(5));
        await vm.AutoRefreshFilesAsync(id);

        Assert.False(state.FileOperationPending);
        Assert.Null(state.Error);
        Assert.Equal(clock.GetUtcNow(), state.LastSyncedAtUtc);
    }

    [Fact]
    public async Task FilePanelLifecycle_StopsPollingAndDiscardsStaleLateResponses()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       idA     = await harness.CreateUploadAsync(TestTorrentBytes.Create("Lifecycle A - 01"));
        var       idB     = await harness.CreateUploadAsync(TestTorrentBytes.Create("Lifecycle B - 01"));
        await using (var db = await harness.CreateDbContextAsync())
        {
            foreach (var entity in await db.DownloadTasks.Where(task => task.Id == idA || task.Id == idB).ToListAsync())
                entity.Status = DownloadTaskStatus.Downloading;
            await db.SaveChangesAsync();
        }

        var serviceA      = new MultiTaskFileService(idA, idB)
        {
            RefreshGateA = new TaskCompletionSource<DownloadTaskFilesView>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var vm = CreateViewModel(harness, serviceA);
        vm.FileAutoRefreshInterval = TimeSpan.FromMilliseconds(10);
        await vm.LoadAsync();
        await vm.SelectTaskAsync(idA);

        vm.SetFilePanelActive(true, true, idA);
        await serviceA.RefreshStartedA.Task.WaitAsync(Timeout);

        // 在任务 A 的远端刷新仍未返回时切到任务 B（或切回详情 Tab / 页面隐藏）：
        // 旧轮询立刻停止，且 A 的迟到响应必须被丢弃，不能写入 A 的刷新结果。
        await vm.SelectTaskAsync(idB);
        vm.SetFilePanelActive(true, true, idB);
        await WaitForAsync(() => serviceA.RefreshCallsB >= 1);

        var staleResult = serviceA.MakeRefreshed(idA, "StaleFromA");
        serviceA.RefreshGateA.SetResult(staleResult);
        await Task.Delay(30);

        Assert.NotSame(staleResult, vm.FileState(idA).Details);

        // 切后台或收起面板停止轮询。
        var callsBeforeHide = serviceA.RefreshCallsB;
        vm.SetFilePanelActive(true, true, idB, isPageHidden : true);
        await Task.Delay(50);
        Assert.Equal(callsBeforeHide, serviceA.RefreshCallsB);

        vm.SetFilePanelActive(true, false, idB, isPageHidden : false);
        vm.SetFilePanelActive(false, true, idB, isPageHidden : false);
        vm.StopFilePolling();
    }

    [Fact]
    public async Task DownloadTaskFiles_RendersLastSyncedTime()
    {
        using var harness     = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       task        = DownloadTaskListTestSeed.NewTask("Render-000", DownloadTaskListTestSeed.At(0));
        await DownloadTaskListTestSeed.SeedAsync(harness, [task]);

        var clock       = new MutableTimeProvider(new(2026, 11, 24, 14, 30, 45, TimeSpan.Zero));
        var fileService = new TestFileService(task.Id);
        var vm          = CreateViewModel(harness, fileService, clock);
        await vm.LoadAsync();
        await vm.ToggleFilesAsync(task.Id);
        await vm.RefreshFilesAsync(task.Id);

        using var       provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var filesHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DownloadTaskFiles>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(DownloadTaskFiles.TaskId)]    = task.Id,
                    [nameof(DownloadTaskFiles.ViewModel)] = vm
                }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
        Assert.Contains("最近同步", filesHtml);
    }

    private static DownloadTasksPageViewModel CreateViewModel(
        DownloadServiceTests.TestHarness harness,
        IDownloadFileService?            fileService  = null,
        TimeProvider?                    timeProvider = null) =>
        new(harness.Service, harness.Intake,
            new DownloadTaskQueries(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()),
            new DownloadTaskFilterStore(harness.Provider.GetRequiredService<IApplicationSettingStore>()),
            NullLogger<DownloadTasksPageViewModel>.Instance,
            fileService,
            timeProvider);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "等待异步状态超时。");
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class TestFileService(Guid taskId) : IDownloadFileService
    {
        private int _observeAttemptsLeft;

        public bool FailRefresh                  { get; set; }
        public bool ThrowBudgetExhaustedOnObserve { get; set; }
        public int  PendingConfirmAfterObserves  { get; set; }
        public int  ObserveCalls                 { get; private set; }

        public DownloadTaskFilesView Details { get; private set; } = new(taskId, "/downloads",
        [
            new DownloadFileView(0, "Orig/ep01.mkv", "Curr/ep01.mkv", DownloadFileKind.Anime, "解析到集数",
                                 1m, null, null, 1024, true, null, null, null, null),
            new DownloadFileView(1, "Orig/ep02.mkv", "Curr/ep02.mkv", DownloadFileKind.Anime, "解析到集数",
                                 2m, null, null, 2048, true, null, null, null, null)
        ]);

        public DownloadTaskFilesView RefreshedDetails => Details with { CurrentRootPath = "/downloads/Refreshed" };

        public Task<DownloadTaskFilesView> ListAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Details);

        public Task<DownloadTaskFilesView> RefreshAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (FailRefresh) throw new InvalidOperationException("下载器连接超时");
            return Task.FromResult(RefreshedDetails);
        }

        public Task<DownloadFilePreview> PreviewAsync(
            Guid id, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadFilePreview("Curr/ep.mkv", "Manual/ep.mkv", "/downloads", "/downloads", false, null));

        public Task<DownloadTaskFilesView> ApplyAsync(
            Guid id, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default,
            string? expectedFileTemplate = null)
        {
            _observeAttemptsLeft = PendingConfirmAfterObserves;
            var pending = PendingConfirmAfterObserves > 0;
            Details = Details with { HasPendingFileOperation = pending };
            return Task.FromResult(Details);
        }

        public Task<DownloadTaskFilesView> ObservePendingAsync(Guid id, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            if (ThrowBudgetExhaustedOnObserve)
                throw new DownloadLayoutConfirmationException("布局请求已发出 3 轮仍未生效，请人工核对下载器。");

            if (_observeAttemptsLeft == int.MaxValue) _observeAttemptsLeft = PendingConfirmAfterObserves;
            if (_observeAttemptsLeft > 1)
            {
                _observeAttemptsLeft--;
                return Task.FromResult(Details with { HasPendingFileOperation = true });
            }

            _observeAttemptsLeft = 0;
            Details = Details with
            {
                HasPendingFileOperation = false,
                CurrentRootPath         = "/downloads/Confirmed"
            };
            return Task.FromResult(Details);
        }

        public Task RetryArchiveAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MultiTaskFileService(Guid taskA, Guid taskB) : IDownloadFileService
    {
        public TaskCompletionSource<DownloadTaskFilesView>? RefreshGateA    { get; init; }
        public TaskCompletionSource                         RefreshStartedA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int                                          RefreshCallsB   { get; private set; }

        public DownloadTaskFilesView MakeRefreshed(Guid id, string root) => new(id, root,
        [
            new DownloadFileView(0, "Orig/ep01.mkv", "Curr/ep01.mkv", DownloadFileKind.Anime, "解析到集数",
                                 1m, null, null, 1024, true, null, null, null, null)
        ]);

        public Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MakeRefreshed(taskId, "Local"));

        public Task<DownloadTaskFilesView> RefreshAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            if (taskId == taskA && RefreshGateA is not null)
            {
                RefreshStartedA.TrySetResult();
                return RefreshGateA.Task;
            }

            if (taskId == taskB) RefreshCallsB++;
            return Task.FromResult(MakeRefreshed(taskId, "Refreshed"));
        }

        public Task<DownloadFilePreview> PreviewAsync(
            Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DownloadTaskFilesView> ApplyAsync(
            Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default,
            string? expectedFileTemplate = null) =>
            throw new NotSupportedException();

        public Task<DownloadTaskFilesView> ObservePendingAsync(
            Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MakeRefreshed(taskId, "Observed"));

        public Task RetryArchiveAsync(Guid taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
