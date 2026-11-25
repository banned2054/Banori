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
using System.Net;

namespace Banori.Tests.Downloads;

public sealed class DownloadTaskFilesTests
{
    [Fact]
    public async Task Expand_LazilyLoadsOnceAndPreservesDraftAcrossSummaryRefreshAndCollapse()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Draft - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.LoadAsync();
        Assert.Equal(0, service.ListCalls);

        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 1);
        var state = vm.FileState(id);
        var draft = state.Drafts[1];
        draft.ReidentifyOriginalName = true;
        draft.Episode                = 12.5m;
        draft.EpisodeEnd             = 13.5m;
        draft.AnimeRelativePath      = "Manual/episode.ass";
        await vm.LoadAsync();
        await vm.ToggleFilesAsync(id);
        await vm.ToggleFilesAsync(id);

        Assert.Same(state, vm.FileState(id));
        Assert.True(state.Expanded);
        Assert.Same(draft, state.Drafts[1]);
        Assert.Equal(DownloadFileKind.Anime, draft.Kind);
        Assert.Equal(12.5m, draft.Episode);
        Assert.Equal(13.5m, draft.EpisodeEnd);
        Assert.Equal("Manual/episode.ass", draft.AnimeRelativePath);
        Assert.True(draft.ReidentifyOriginalName);
        Assert.Equal(1, service.ListCalls);
    }

    [Fact]
    public async Task Reidentify_ClearsOldOverridesAndAllowsExplicitDecimalCorrections()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Reidentify - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        var draft = vm.FileState(id).Drafts[0];
        draft.AnimeRelativePath = "OldOverride.mkv";
        await vm.PreviewFileAsync(id, 0);

        draft.ReidentifyOriginalName = true;
        Assert.Null(draft.Episode);
        Assert.Null(draft.EpisodeEnd);
        Assert.Null(draft.AnimeRelativePath);
        Assert.False(draft.CanApply);
        await vm.PreviewFileAsync(id, 0);
        Assert.Equal(new DownloadFileEditRequest(DownloadFileKind.Anime, true), service.LastRequest);
        draft.Episode           = 5.5m;
        draft.EpisodeEnd        = 6.5m;
        draft.AnimeRelativePath = "Manual/05.5-06.5.mkv";
        await vm.PreviewFileAsync(id, 0);
        Assert.Equal(new DownloadFileEditRequest(DownloadFileKind.Anime, true, 5.5m, 6.5m,
                                                 "Manual/05.5-06.5.mkv"), service.LastRequest);
    }

    [Fact]
    public async Task Expand_PendingLoadIsNotDuplicatedAndDoesNotReopenCollapsedTask()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Load Race - 01"));
        var service = new FakeFileService(id)
        {
            ListGate =
                new TaskCompletionSource<DownloadTaskFilesView>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var vm = CreateViewModel(harness, service);
        await vm.LoadAsync();
        var pending = vm.ToggleFilesAsync(id);
        Assert.True(vm.FileState(id).Loading);
        await vm.ToggleFilesAsync(id);
        await vm.ToggleFilesAsync(id);
        await vm.ToggleFilesAsync(id);
        await vm.LoadAsync();
        service.ListGate.SetResult(service.Details);
        await pending;
        Assert.False(vm.FileState(id).Expanded);
        Assert.False(vm.FileState(id).Loading);
        Assert.Equal(1, service.ListCalls);
        await vm.ToggleFilesAsync(id);
        Assert.Equal(1, service.ListCalls);
    }

    [Fact]
    public async Task Preview_ChangingDraftInvalidatesPreviewAndApplyRequiresNewPreview()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Preview - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        var draft = vm.FileState(id).Drafts[0];
        await vm.ApplyFileAsync(id, 0);
        Assert.Equal(0, service.ApplyCalls);
        await vm.PreviewFileAsync(id, 0);
        Assert.True(draft.CanApply);

        draft.Episode = 2.5m;
        Assert.Null(draft.Preview);
        await vm.ApplyFileAsync(id, 0);
        Assert.Equal(0, service.ApplyCalls);
        await vm.PreviewFileAsync(id, 0);
        Assert.Equal(2.5m, service.LastRequest?.Episode);
        await vm.ApplyFileAsync(id, 0);

        Assert.Equal(1, service.ApplyCalls);
        Assert.Empty(vm.FileState(id).Drafts);
        Assert.Equal("NewRoot/renamed.mkv", vm.FileState(id).Details!.Files[0].CurrentPath);
        Assert.Null(vm.FileState(id).Message);
    }

    [Fact]
    public async Task Preview_LateResultDoesNotResurrectCancelledOrChangedDraft()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Race - 01"));
        var service = new FakeFileService(id)
        {
            PreviewGate =
                new TaskCompletionSource<DownloadFilePreview>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var vm = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        var oldDraft = vm.FileState(id).Drafts[0];
        var pending  = vm.PreviewFileAsync(id, 0);
        Assert.True(vm.FileState(id).Busy);
        oldDraft.Episode = 9m;
        service.PreviewGate.SetResult(service.Preview);
        await pending;
        Assert.Null(oldDraft.Preview);
        Assert.False(oldDraft.CanApply);
        vm.CancelFileEdit(id, 0);
        Assert.Empty(vm.FileState(id).Drafts);
        Assert.Equal(0, service.ApplyCalls);
    }

    [Fact]
    public async Task Apply_RefreshesDetailsButPreservesOtherDraftAndInvalidatesItsRootPreview()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Other - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        vm.BeginFileEdit(id, 2);
        var other = vm.FileState(id).Drafts[2];
        other.Kind    = DownloadFileKind.Anime;
        other.Episode = 8.5m;
        await vm.PreviewFileAsync(id, 2);
        await vm.PreviewFileAsync(id, 0);
        await vm.ApplyFileAsync(id, 0);

        Assert.Same(other, vm.FileState(id).Drafts[2]);
        Assert.Equal(8.5m, other.Episode);
        Assert.Null(other.Preview);
        Assert.True(vm.FileState(id).Expanded);
    }

    [Fact]
    public async Task LoadFailure_CanRetryAndApplyFailureRetainsDraftWithoutStalePreview()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Failure - 01"));
        var       service = new FakeFileService(id) { FailList = true };
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        Assert.Contains("无法读取", vm.FileState(id).Error);
        Assert.False(vm.FileState(id).Loading);
        service.FailList = false;
        await vm.LoadFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        var draft = vm.FileState(id).Drafts[0];
        await vm.PreviewFileAsync(id, 0);
        service.FailApply = true;
        await vm.ApplyFileAsync(id, 0);

        Assert.Same(draft, vm.FileState(id).Drafts[0]);
        Assert.Contains("下载器改名失败", vm.FileState(id).Error);
        Assert.False(vm.FileState(id).Busy);
        Assert.False(draft.CanApply);
        vm.CancelFileEdit(id, 0);
        Assert.Empty(vm.FileState(id).Drafts);
    }

    [Fact]
    public async Task ReloadFiles_PreservesDraftValuesButInvalidatesPreview()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Reload - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        var draft = vm.FileState(id).Drafts[0];
        draft.Episode           = 7.5m;
        draft.AnimeRelativePath = "Manual/episode.mkv";
        await vm.PreviewFileAsync(id, 0);
        await vm.LoadFilesAsync(id);

        Assert.Equal(2, service.ListCalls);
        Assert.Same(draft, vm.FileState(id).Drafts[0]);
        Assert.Equal(7.5m, draft.Episode);
        Assert.Equal("Manual/episode.mkv", draft.AnimeRelativePath);
        Assert.False(draft.CanApply);
    }

    [Fact]
    public async Task RefreshFiles_UsesExplicitRefreshEntry_PreservesDraftsAndInvalidatesPreview()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Refresh - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        Assert.Equal(1, service.ListCalls);
        vm.BeginFileEdit(id, 0);
        var draft = vm.FileState(id).Drafts[0];
        draft.Episode = 7.5m;
        await vm.PreviewFileAsync(id, 0);
        Assert.True(draft.CanApply);

        service.RefreshedDetails = service.Details with
        {
            Files = service.Details.Files.Select(file => file with
            {
                CurrentPath = $"Refreshed/Renamed{file.Index}.mkv"
            }).ToArray()
        };
        await vm.RefreshFilesAsync(id);

        Assert.Equal(1, service.RefreshCalls);
        Assert.Equal(1, service.ListCalls); // 显式刷新没有退回 ListAsync。
        Assert.Same(service.RefreshedDetails, vm.FileState(id).Details);
        Assert.Contains("Refreshed/Renamed0.mkv", vm.FileState(id).Details!.Files[0].CurrentPath);
        Assert.Same(draft, vm.FileState(id).Drafts[0]);
        Assert.Equal(7.5m, draft.Episode);
        Assert.Null(draft.Preview);
        Assert.False(draft.CanApply);
    }

    [Fact]
    public async Task RefreshFiles_FailureKeepsExistingDetailsAndReportsReason()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Refresh Fail - 01"));
        var       service = new FakeFileService(id) { FailRefresh = true };
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        var details = vm.FileState(id).Details;
        await vm.RefreshFilesAsync(id);

        Assert.Equal(1, service.RefreshCalls);
        Assert.Same(details, vm.FileState(id).Details);
        Assert.Contains("刷新失败", vm.FileState(id).Error);
        Assert.False(vm.FileState(id).Loading);
    }

    [Fact]
    public async Task Apply_PendingOperationWaitsAndAutoRefreshesOnceConfirmed()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Pending Auto - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        vm.PendingFileRefreshInterval = TimeSpan.FromMilliseconds(5);
        vm.PendingFileRefreshAttempts = 40;
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        await vm.PreviewFileAsync(id, 0);
        service.Details = service.Details with { HasPendingFileOperation = true };
        await vm.ApplyFileAsync(id, 0);

        var state = vm.FileState(id);
        Assert.True(state.FileOperationPending);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Contains("等待下载器确认", state.Message);

        var confirmed = service.Details with { HasPendingFileOperation = false, CurrentRootPath = "ConfirmedRoot" };
        service.ConfirmedDetails = confirmed;
        service.ConfirmPending   = true;
        await WaitForAsync(() => !state.FileOperationPending);

        Assert.True(service.ObserveCalls >= 1);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Same(confirmed, state.Details);
        Assert.Null(state.Error);
        Assert.Null(state.Message);
        Assert.False(state.PendingFileWaitPaused);

        // 确认并直接采用 ObservePendingAsync 的布局后，显式刷新入口即恢复可用。
        await vm.RefreshFilesAsync(id);
        Assert.Equal(1, service.RefreshCalls);
    }

    [Fact]
    public async Task Apply_PendingNeverConfirmedKeepsRefreshLockedUntilResumed()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Pending Timeout - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        vm.PendingFileRefreshInterval = TimeSpan.FromMilliseconds(5);
        vm.PendingFileRefreshAttempts = 3;
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        await vm.PreviewFileAsync(id, 0);
        service.Details = service.Details with { HasPendingFileOperation = true };
        await vm.ApplyFileAsync(id, 0);

        var state = vm.FileState(id);
        Assert.True(state.FileOperationPending);
        await WaitForAsync(() => state.PendingFileWaitPaused);

        Assert.Equal(0, service.RefreshCalls);
        Assert.Contains("仍未被下载器确认", state.Error);
        Assert.Contains("继续等待确认", state.Error);
        Assert.Null(state.Message);

        // 超时不等于确认：手动刷新与再次编辑继续锁住，界面给出恢复入口。
        await vm.RefreshFilesAsync(id);
        vm.BeginFileEdit(id, 1);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Empty(state.Drafts);
        Assert.Contains("继续等待确认", await RenderAsync(vm, id));

        var confirmed = service.Details with { HasPendingFileOperation = false, CurrentRootPath = "ConfirmedRoot" };
        service.ConfirmedDetails = confirmed;
        service.ConfirmPending   = true;
        vm.ContinueWaitingForFileOperation(id);
        await WaitForAsync(() => !state.FileOperationPending);

        Assert.True(service.ObserveCalls >= 4);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Same(confirmed, state.Details);
        Assert.Null(state.Error);
        Assert.Null(state.Message);
        Assert.False(state.PendingFileWaitPaused);
    }

    [Fact]
    public async Task Apply_PendingListFailureKeepsRefreshLockedAndCanResume()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Pending Error - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        vm.PendingFileRefreshInterval = TimeSpan.FromMilliseconds(5);
        vm.PendingFileRefreshAttempts = 40;
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        await vm.PreviewFileAsync(id, 0);
        service.Details = service.Details with { HasPendingFileOperation = true };
        await vm.ApplyFileAsync(id, 0);
        service.FailObserve = true;

        var state = vm.FileState(id);
        await WaitForAsync(() => state.PendingFileWaitPaused);

        Assert.True(state.FileOperationPending);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Contains("等待下载器确认失败", state.Error);
        Assert.Contains("无法核对改名状态", state.Error);
        Assert.Contains("继续等待确认", await RenderAsync(vm, id));
        await vm.RefreshFilesAsync(id);
        Assert.Equal(0, service.RefreshCalls);

        service.FailObserve = false;
        var confirmed = service.Details with { HasPendingFileOperation = false, CurrentRootPath = "ConfirmedRoot" };
        service.ConfirmedDetails = confirmed;
        service.ConfirmPending   = true;
        vm.ContinueWaitingForFileOperation(id);
        await WaitForAsync(() => !state.FileOperationPending);

        Assert.Same(confirmed, state.Details);
        Assert.Null(state.Error);
        Assert.Null(state.Message);
        Assert.False(state.PendingFileWaitPaused);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "等待状态变化超时。");
    }

    [Fact]
    public async Task Render_AfterRefreshShowsNewCurrentPath()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Refresh Render - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        service.RefreshedDetails = service.Details with
        {
            Files = service.Details.Files.Select(file => file with
            {
                CurrentPath = $"RefreshedPath/Renamed{file.Index}.mkv"
            }).ToArray()
        };
        await vm.RefreshFilesAsync(id);
        var html = await RenderAsync(vm, id);

        Assert.Contains("RefreshedPath/Renamed0.mkv", html);
        Assert.DoesNotContain("OldRoot/renamed.mkv", html);
    }

    [Fact]
    public async Task SummaryRefresh_RemovesFileStateForDeletedTasks()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Delete - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.LoadAsync();
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 0);
        var oldState = vm.FileState(id);
        await harness.Service.DeleteAsync(id, false, false);
        await vm.LoadAsync();
        Assert.NotSame(oldState, vm.FileState(id));
        Assert.False(vm.FileState(id).Expanded);
        Assert.Empty(vm.FileState(id).Drafts);
    }

    [Fact]
    public async Task Render_ShowsTwoGroupsAllPathsReasonsAndEditButtons()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Render - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        var html = await RenderAsync(vm, id);

        Assert.Contains("动画与字幕", html);
        Assert.Contains("Seed / 未识别与被过滤文件", html);
        Assert.Contains("OldRoot/[Group] Anime - 01.mkv", html);
        Assert.Contains("OldRoot/renamed.mkv", html);
        Assert.Contains("OldRoot/[Group] Anime - 01.ja.zh-Hans.ass", html);
        Assert.Contains("字幕", html);
        Assert.Contains("解析到集数", html);
        Assert.Contains("特殊文件已过滤", html);
        Assert.Contains("集数：1.5–2.5", html);
        Assert.Equal(3, Count(html, "aria-label=\"修改 "));
        Assert.DoesNotContain("file-edit", html);
    }

    [Fact]
    public async Task Render_EditorHasOnlyAnimeSeedAndPreviewShowsRootRenameAndCompletePaths()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Editor - 01"));
        var       service = new FakeFileService(id);
        var       vm      = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        vm.BeginFileEdit(id, 1);
        var before = await RenderAsync(vm, id);
        Assert.Contains("从原始文件名重新识别", before);
        Assert.Contains("手工动画相对路径（相对动画根目录，可留空）", before);
        Assert.Contains("作品目录/文件名.mkv", before);
        Assert.Contains("无需重复添加动画根目录", before);
        Assert.Contains("复合语言后缀", before);
        Assert.Contains("value=\"Anime\"", before);
        Assert.Contains("value=\"Seed\"", before);
        Assert.DoesNotContain("value=\"Subtitle\"", before);
        Assert.Contains("disabled>应用</button>", before);
        await vm.PreviewFileAsync(id, 1);
        var html = await RenderAsync(vm, id);
        Assert.Contains("需要更改 torrent 根目录", html);
        Assert.Contains("当前完整路径", html);
        Assert.Contains("/downloads/OldRoot/renamed.mkv", html);
        Assert.Contains("/downloads/NewRoot/renamed.mkv", html);
        Assert.Contains("当前根目录", html);
        Assert.Contains("目标根目录", html);
        Assert.Contains("预览警告", html);
        Assert.Contains(">应用</button>", html);
        Assert.DoesNotContain("disabled>应用</button>", html);
    }

    [Fact]
    public async Task Render_HidesRoutineParsedReasonButKeepsDiagnosticReasons()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Reason - 01"));
        var service = new FakeFileService(id, new DownloadTaskFilesView(id, "Root",
        [
            new DownloadFileView(0, "Root/[Group] Anime - 01.mkv", "Root/E01.mkv", DownloadFileKind.Anime,
                                 "AniParser 已解析到集数", 1m, null, null, 3L * 1024 * 1024, true, null, null, null,
                                 null),
            new DownloadFileView(1, "Root/NCOP.mkv", "Root/Seed/NCOP.mkv", DownloadFileKind.Seed,
                                 "命中过滤词", null, null, null, 0, false, null, null, null, null)
        ]));
        var vm = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        var html = await RenderAsync(vm, id);

        Assert.DoesNotContain("分类原因：AniParser 已解析到集数", html);
        Assert.Contains("分类原因：命中过滤词", html);
    }

    [Fact]
    public async Task Render_ExplainsUnselectedSupersededReuseAndReadableSize()
    {
        using var harness          = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       id               = await harness.CreateUploadAsync(TestTorrentBytes.Create("Files Explain - 01"));
        var       namedSuccessor   = Guid.Parse("0123456789abcdef0123456789abcdef");
        var       unnamedSuccessor = Guid.Parse("fedcba9876543210fedcba9876543210");
        var service = new FakeFileService(id, new DownloadTaskFilesView(id, "Root",
        [
            new DownloadFileView(0, "Root/[Group] Anime - 01.mkv", "Root/E01.mkv", DownloadFileKind.Anime,
                                 "解析到集数", 1m, null, null, 3L * 1024 * 1024, true, null, null, null,
                                 DownloadFileVerificationOutcomes.ReusedVerified),
            new DownloadFileView(1, "Root/[Group] Anime - 02.mkv", "Root/E02.mkv", DownloadFileKind.Anime,
                                 "解析到集数", 2m, null, null, 2L * 1024 * 1024 * 1024, true, null, null, null,
                                 DownloadFileVerificationOutcomes.ReuseUnverified),
            new DownloadFileView(2, "Root/[Group] Anime - 01.ja.ass", "Root/E01.ja.ass", DownloadFileKind.Subtitle,
                                 "已识别字幕", 1m, null, null, 1536, false, "已被更优任务替代：Other Anime",
                                 namedSuccessor, "Other Anime", null),
            new DownloadFileView(3, "Root/NCOP.mkv", "Root/Seed/NCOP.mkv", DownloadFileKind.Seed,
                                 "特殊文件已过滤", null, null, null, 0, false, "同集包内择优落选", null, null, null),
            new DownloadFileView(4, "Root/notes.txt", "Root/Seed/notes.txt", DownloadFileKind.Seed,
                                 "未识别", null, null, null, 2048, false, "已被更优任务替代：已删除任务",
                                 unnamedSuccessor, null, null)
        ]));
        var vm = CreateViewModel(harness, service);
        await vm.ToggleFilesAsync(id);
        var html = await RenderAsync(vm, id);

        Assert.Equal(3, Count(html, ">不下载</span>"));
        Assert.Contains("不下载原因：同集包内择优落选", html);
        Assert.Contains("已被 Other Anime 承接", html);
        Assert.Contains("已被 fedcba98 承接", html);
        Assert.DoesNotContain("复用校验：复用已证实", html);
        Assert.Contains("复用校验：复用未证实（该次映射未通过/未确认）", html);
        Assert.Contains("大小：3 MB", html);
        Assert.Contains("大小：2 GB", html);
        Assert.Contains("大小：1.5 KB", html);
        Assert.Contains("大小：0 B", html);
        Assert.Contains("大小：2 KB", html);
    }

    private static DownloadTasksPageViewModel CreateViewModel(
        DownloadServiceTests.TestHarness harness, IDownloadFileService service) =>
        new(harness.Service, harness.Intake,
            new DownloadTaskQueries(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()),
            new DownloadTaskFilterStore(harness.Provider.GetRequiredService<IApplicationSettingStore>()),
            NullLogger<DownloadTasksPageViewModel>.Instance, service);

    private static async Task<string> RenderAsync(DownloadTasksPageViewModel vm, Guid id)
    {
        using var       provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DownloadTaskFiles>(ParameterView.FromDictionary(
                          new Dictionary<string, object?>
                          {
                              [nameof(DownloadTaskFiles.TaskId)]    = id,
                              [nameof(DownloadTaskFiles.ViewModel)] = vm
                          }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    private static int Count(string text, string value) => text.Split(value).Length - 1;

    private sealed class FakeFileService(Guid taskId, DownloadTaskFilesView? details = null) : IDownloadFileService
    {
        public int                                          ListCalls        { get; private set; }
        public int                                          ApplyCalls       { get; private set; }
        public int                                          RefreshCalls     { get; private set; }
        public int                                          ObserveCalls     { get; private set; }
        public bool                                         FailList         { get; set; }
        public bool                                         FailApply        { get; set; }
        public bool                                         FailRefresh      { get; set; }
        public bool                                         FailObserve      { get; set; }
        public bool                                         ConfirmPending   { get; set; }
        public DownloadTaskFilesView?                       ConfirmedDetails { get; set; }
        public DownloadFileEditRequest?                     LastRequest  { get; private set; }
        public TaskCompletionSource<DownloadTaskFilesView>? ListGate     { get; init; }
        public TaskCompletionSource<DownloadFilePreview>?   PreviewGate  { get; init; }

        /// <summary>注入刷新后的详情；未设置时与 Details 相同，用于断言显式刷新走独立入口。</summary>
        public DownloadTaskFilesView? RefreshedDetails { get; set; }

        public DownloadFilePreview Preview { get; } = new("/downloads/OldRoot/renamed.mkv",
                                                          "/downloads/NewRoot/renamed.mkv", "OldRoot", "NewRoot", true,
                                                          "预览警告");

        public DownloadTaskFilesView Details { get; set; } = details ??
                                                                     new DownloadTaskFilesView(taskId, "OldRoot",
                                                                     [
                                                                         new DownloadFileView(0,
                                                                                  "OldRoot/[Group] Anime - 01.mkv",
                                                                                  "OldRoot/renamed.mkv",
                                                                                  DownloadFileKind.Anime,
                                                                                  "解析到集数", 1.5m, 2.5m, null, 1024, true,
                                                                                  null, null, null, null),
                                                                         new DownloadFileView(1,
                                                                                  "OldRoot/[Group] Anime - 01.ja.zh-Hans.ass",
                                                                                  "OldRoot/renamed.ja.zh-Hans.ass",
                                                                                  DownloadFileKind.Subtitle, "已识别字幕",
                                                                                  1m, null, null, 2048, true, null,
                                                                                  null,
                                                                                  null, null),
                                                                         new DownloadFileView(2, "OldRoot/NCOP.mkv",
                                                                                  "OldRoot/Seed/NCOP.mkv",
                                                                                  DownloadFileKind.Seed,
                                                                                  "特殊文件已过滤", null, null, null, 0, true,
                                                                                  null, null, null, null)
                                                                     ]);

        public Task<DownloadTaskFilesView> ListAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            ListCalls++;
            if (FailList) throw new InvalidOperationException("无法读取文件列表");
            if (ListGate is not null) return ListGate.Task;
            return Task.FromResult(ConfirmPending && ConfirmedDetails is not null ? ConfirmedDetails : Details);
        }

        public Task<DownloadTaskFilesView> RefreshAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            RefreshCalls++;
            if (FailRefresh) throw new InvalidOperationException("下载器刷新失败");
            return Task.FromResult(RefreshedDetails ?? Details);
        }

        public Task<DownloadTaskFilesView> ObservePendingAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            ObserveCalls++;
            if (FailObserve) throw new InvalidOperationException("无法核对改名状态");
            return Task.FromResult(ConfirmPending && ConfirmedDetails is not null ? ConfirmedDetails : Details);
        }

        public Task RetryArchiveAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("该替身不承担归档确认用例。");

        public Task<DownloadFilePreview> PreviewAsync(Guid              id, int index, DownloadFileEditRequest request,
                                                      CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            LastRequest = request;
            return PreviewGate?.Task ?? Task.FromResult(Preview);
        }

        public Task<DownloadTaskFilesView> ApplyAsync(Guid              id, int index, DownloadFileEditRequest request,
                                                      CancellationToken cancellationToken = default,
                                                      string?           expectedFileTemplate = null)
        {
            Assert.Equal(taskId, id);
            ApplyCalls++;
            LastRequest = request;
            if (FailApply) throw new InvalidOperationException("下载器改名失败");
            Details = Details with
            {
                CurrentRootPath = "NewRoot",
                Files = Details.Files.Select(file => file with
                {
                    CurrentPath = file.CurrentPath.Replace("OldRoot/", "NewRoot/", StringComparison.Ordinal)
                }).ToArray()
            };
            return Task.FromResult(Details);
        }
    }
}
