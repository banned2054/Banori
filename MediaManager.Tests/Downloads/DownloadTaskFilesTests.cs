using MediaManager.Components.Pages.Downloads;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace MediaManager.Tests.Downloads;

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
        Assert.Equal("修改已提交，文件位置以下载器确认结果为准。", vm.FileState(id).Message);
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

    private static DownloadTasksPageViewModel CreateViewModel(DownloadServiceTests.TestHarness harness,
                                                              IDownloadFileService             service)
    {
        return new DownloadTasksPageViewModel(harness.Service, harness.Intake,
                                              new DownloadTaskQueries(harness.Provider
                                                                             .GetRequiredService<
                                                                                  IDbContextFactory<
                                                                                      ApplicationDbContext>>()),
                                              NullLogger<DownloadTasksPageViewModel>.Instance, service);
    }

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

    private static int Count(string text, string value)
    {
        return text.Split(value).Length - 1;
    }

    private sealed class FakeFileService(Guid taskId) : IDownloadFileService
    {
        public int                                          ListCalls   { get; private set; }
        public int                                          ApplyCalls  { get; private set; }
        public bool                                         FailList    { get; set; }
        public bool                                         FailApply   { get; set; }
        public DownloadFileEditRequest?                     LastRequest { get; private set; }
        public TaskCompletionSource<DownloadTaskFilesView>? ListGate    { get; init; }
        public TaskCompletionSource<DownloadFilePreview>?   PreviewGate { get; init; }

        public DownloadFilePreview Preview { get; } = new("/downloads/OldRoot/renamed.mkv",
                                                          "/downloads/NewRoot/renamed.mkv", "OldRoot", "NewRoot", true,
                                                          "预览警告");

        public DownloadTaskFilesView Details { get; private set; } = new(taskId, "OldRoot",
        [
            new DownloadFileView(0, "OldRoot/[Group] Anime - 01.mkv", "OldRoot/renamed.mkv", DownloadFileKind.Anime,
                                 "解析到集数", 1.5m, 2.5m, null),
            new DownloadFileView(1, "OldRoot/[Group] Anime - 01.ja.zh-Hans.ass", "OldRoot/renamed.ja.zh-Hans.ass",
                                 DownloadFileKind.Subtitle, "已识别字幕", 1m, null, null),
            new DownloadFileView(2, "OldRoot/NCOP.mkv", "OldRoot/Seed/NCOP.mkv", DownloadFileKind.Seed,
                                 "特殊文件已过滤", null, null, null)
        ]);

        public Task<DownloadTaskFilesView> ListAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            ListCalls++;
            if (FailList) throw new InvalidOperationException("无法读取文件列表");
            return ListGate?.Task ?? Task.FromResult(Details);
        }

        public Task<DownloadFilePreview> PreviewAsync(Guid              id, int index, DownloadFileEditRequest request,
                                                      CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            LastRequest = request;
            return PreviewGate?.Task ?? Task.FromResult(Preview);
        }

        public Task<DownloadTaskFilesView> ApplyAsync(Guid              id, int index, DownloadFileEditRequest request,
                                                      CancellationToken cancellationToken = default)
        {
            Assert.Equal(taskId, id);
            ApplyCalls++;
            LastRequest = request;
            if (FailApply) throw new InvalidOperationException("下载器改名失败");
            Details = Details with
            {
                CurrentRootPath = "NewRoot", Files = Details.Files.Select(file =>
                                                                              file with
                                                                              {
                                                                                  CurrentPath =
                                                                                  file.CurrentPath
                                                                                     .Replace("OldRoot/",
                                                                                          "NewRoot/",
                                                                                          StringComparison
                                                                                             .Ordinal)
                                                                              })
                                                            .ToArray()
            };
            return Task.FromResult(Details);
        }
    }
}
