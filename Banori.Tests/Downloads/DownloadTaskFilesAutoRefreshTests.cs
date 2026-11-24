using Banori.Components.Pages.Downloads;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>下面板文件 Tab 的本地秒出 + 静默同步 + 定时轮询行为。</summary>
public sealed class DownloadTaskFilesAutoRefreshTests
{
    [Fact]
    public async Task ActivateFilesTab_LoadsLocalListFirstThenSyncsOnceForActiveTask()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Auto Activate - 01"));
        await SetStatusAsync(harness, id, DownloadTaskStatus.Downloading);
        var service = new RecordingFileService(id);
        var vm      = CreateViewModel(harness, service);
        await vm.LoadAsync();

        await vm.ActivateFilesTabAsync(id);

        Assert.Equal(["List", "Refresh"], service.Calls);
        Assert.Same(service.Refreshed, vm.FileState(id).Details);
        Assert.Null(vm.FileState(id).SilentRefreshError);
    }

    [Fact]
    public async Task AutoRefreshFiles_ActiveTaskPollsOnEveryTick()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Auto Poll - 01"));
        await SetStatusAsync(harness, id, DownloadTaskStatus.Downloading);
        var service = new RecordingFileService(id);
        var vm      = CreateViewModel(harness, service);
        await vm.LoadAsync();
        await vm.ActivateFilesTabAsync(id);

        await vm.AutoRefreshFilesAsync(id);
        await vm.AutoRefreshFilesAsync(id);

        Assert.Equal(3, service.Calls.Count(call => call == "Refresh"));
        Assert.Equal("List", service.Calls[0]);
    }

    [Fact]
    public async Task AutoRefreshFiles_CompletedTaskWithAssociationPollsContinuously_AndSkipsMissingOrTombstone()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Auto Final - 01"));
        await SetStatusAsync(harness, id, DownloadTaskStatus.Completed);
        var service = new RecordingFileService(id);
        var vm      = CreateViewModel(harness, service);
        await vm.LoadAsync();

        await vm.ActivateFilesTabAsync(id);
        await vm.AutoRefreshFilesAsync(id);
        await vm.AutoRefreshFilesAsync(id);

        Assert.Equal("List", service.Calls[0]);
        Assert.Equal(3, service.Calls.Count(call => call == "Refresh"));

        // 确认缺种的已完成任务不再自动同步。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(task => task.Id == id);
            entity.TorrentMissingAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await vm.LoadAsync();
        await vm.AutoRefreshFilesAsync(id);
        Assert.Equal(3, service.Calls.Count(call => call == "Refresh"));
    }

    [Fact]
    public async Task AutoRefreshFiles_SkipsWhenDraftOrFileOperationIsBusy()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Auto Draft - 01"));
        await SetStatusAsync(harness, id, DownloadTaskStatus.Downloading);
        var service = new RecordingFileService(id)
        {
            PreviewGate = new TaskCompletionSource<DownloadFilePreview>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var vm = CreateViewModel(harness, service);
        await vm.LoadAsync();
        await vm.ActivateFilesTabAsync(id);

        vm.BeginFileEdit(id, 0);
        await vm.AutoRefreshFilesAsync(id);
        Assert.Equal(1, service.Calls.Count(call => call == "Refresh"));

        var detailsBefore = vm.FileState(id).Details;
        var preview       = vm.PreviewFileAsync(id, 0);
        Assert.True(vm.FileState(id).Busy);
        await vm.AutoRefreshFilesAsync(id);
        Assert.Equal(1, service.Calls.Count(call => call == "Refresh"));
        Assert.Same(detailsBefore, vm.FileState(id).Details);
        service.PreviewGate.SetResult(service.Preview);
        await preview;
    }

    [Fact]
    public async Task ActivateFilesTab_SilentFailureKeepsExistingDetailsAndRecordsWarning()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Auto Silent Fail - 01"));
        await SetStatusAsync(harness, id, DownloadTaskStatus.Downloading);
        var service = new RecordingFileService(id) { FailRefresh = true };
        var vm      = CreateViewModel(harness, service);
        await vm.LoadAsync();

        await vm.ActivateFilesTabAsync(id);

        Assert.Same(service.Local, vm.FileState(id).Details);
        Assert.Contains("后台同步失败", vm.FileState(id).SilentRefreshError);
        Assert.Null(vm.FileState(id).Error);
        Assert.False(vm.FileState(id).Refreshing);
    }

    private static async Task SetStatusAsync(
        DownloadServiceTests.TestHarness harness, Guid id, DownloadTaskStatus status)
    {
        await using var db = await harness.CreateDbContextAsync();
        var entity = await db.DownloadTasks.SingleAsync(task => task.Id == id);
        entity.Status = status;
        await db.SaveChangesAsync();
    }

    private static DownloadTasksPageViewModel CreateViewModel(
        DownloadServiceTests.TestHarness harness, IDownloadFileService service) =>
        new(harness.Service, harness.Intake,
            new DownloadTaskQueries(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()),
            new DownloadTaskFilterStore(harness.Provider.GetRequiredService<IApplicationSettingStore>()),
            NullLogger<DownloadTasksPageViewModel>.Instance, service);

    private sealed class RecordingFileService(Guid taskId) : IDownloadFileService
    {
        public List<string> Calls { get; } = [];

        public bool FailRefresh { get; set; }

        public TaskCompletionSource<DownloadFilePreview>? PreviewGate { get; init; }

        public DownloadTaskFilesView Local { get; } = new(taskId, "LocalRoot",
        [
            new DownloadFileView(0, "LocalRoot/anime.mkv", "LocalRoot/anime.mkv", DownloadFileKind.Anime,
                                 "解析到集数", 1m, null, null, 1024, true, null, null, null, null)
        ]);

        public DownloadTaskFilesView Refreshed { get; } = new(taskId, "RefreshedRoot",
        [
            new DownloadFileView(0, "LocalRoot/anime.mkv", "RefreshedRoot/anime.mkv", DownloadFileKind.Anime,
                                 "解析到集数", 1m, null, null, 1024, true, null, null, null, null)
        ]);

        public DownloadFilePreview Preview { get; } = new("LocalRoot/anime.mkv", "NewRoot/anime.mkv",
                                                          "LocalRoot", "NewRoot", false, null);

        public Task<DownloadTaskFilesView> ListAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            Calls.Add("List");
            return Task.FromResult(Local);
        }

        public Task<DownloadTaskFilesView> RefreshAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            Calls.Add("Refresh");
            if (FailRefresh) throw new InvalidOperationException("下载器暂时不可用");
            return Task.FromResult(Refreshed);
        }

        public Task<DownloadFilePreview> PreviewAsync(
            Guid id, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default) =>
            PreviewGate?.Task ?? Task.FromResult(Preview);

        public Task<DownloadTaskFilesView> ApplyAsync(
            Guid id, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default,
            string? expectedFileTemplate = null) =>
            throw new InvalidOperationException("该文件替身不承担应用操作。");

        public Task<DownloadTaskFilesView> ObservePendingAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("该文件替身不承担改名确认操作。");

        public Task RetryArchiveAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("该文件替身不承担归档确认。");
    }
}
