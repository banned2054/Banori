using Banori.Features.Downloads;

namespace Banori.Components.Pages.Downloads;

/// <summary>下载任务页状态：任务列表、上传种子与暂停 / 恢复 / 重试操作。随页面组件创建和释放。</summary>
public sealed class DownloadTasksPageViewModel(
    DownloadService                     downloadService,
    DownloadIntakeService               intakeService,
    DownloadTaskQueries                 taskQueries,
    ILogger<DownloadTasksPageViewModel> logger,
    IDownloadFileService?               fileService = null) : IDisposable
{
    private readonly Dictionary<Guid, DownloadTaskFilesState> _fileStates     = [];
    private readonly HashSet<string>                          _languageFilter = [];

    private readonly HashSet<Guid> _selectedIds = [];

    private readonly HashSet<DownloadStatusFilterGroup> _statusFilter        = [];
    private          Guid[]                             _bulkConfirmationIds = [];
    private          long                               _bulkRequestId;

    private bool _deleteFiles;

    /// <summary>面板勾选项：是否连下载器中的种子一起删除。</summary>
    private bool _deleteFromDownloader;

    private long                     _deleteRequestId;
    private long                     _loadRequestId;
    private int                      _pageNumber = 1;
    private CancellationTokenSource? _searchDebounceCts;
    private string                   _searchText = string.Empty;
    private bool                     _settingDeleteOptions;

    public IReadOnlyList<DownloadTaskView> Tasks { get; private set; } = [];

    /// <summary>
    ///     搜索关键词输入：每次变化重置防抖定时器，停顿后回到第一页重新查询；空白视为不过滤。
    ///     数据库查询在停顿前不会发出，新输入会取消未触发的定时与进行中的查询。
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(text, _searchText, StringComparison.Ordinal)) return;
            _searchText = text;
            ScheduleSearchReload();
        }
    }

    /// <summary>测试注入：缩短防抖等待，避免用例真实挂起数百毫秒。</summary>
    internal TimeSpan SearchDebounceDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>状态筛选分组多选；空集合表示全部状态。</summary>
    public IReadOnlySet<DownloadStatusFilterGroup> StatusFilter => _statusFilter;

    /// <summary>字幕语言筛选多选（空字符串哨兵 = 无语言任务）；空集合表示全部语言。</summary>
    public IReadOnlySet<string> LanguageFilter => _languageFilter;

    /// <summary>是否有生效中的筛选，用于展示清除入口与空态文案。</summary>
    public bool HasActiveFilters => _searchText.Trim().Length > 0 ||
                                    _statusFilter.Count       > 0 ||
                                    _languageFilter.Count     > 0;

    /// <summary>分页状态：页码为查询层钳制后的实际值，TotalCount 是筛选后的完整总数而不只当前页。</summary>
    public int PageNumber { get; private set; } = 1;

    public int PageSize   { get; private set; } = DownloadTaskListFilter.DefaultPageSize;
    public int TotalCount { get; private set; }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => PageNumber < TotalPages;

    /// <summary>可选字幕语言（覆盖全部历史任务，空字符串哨兵 = 存在无语言任务），按出现次数降序。</summary>
    public IReadOnlyList<string> AvailableLanguages { get; private set; } = [];

    public bool    Loaded    { get; private set; }
    public bool    Busy      { get; private set; }
    public string? Message   { get; private set; }
    public bool    Succeeded { get; private set; }

    /// <summary>上传表单的 Bangumi Subject ID：标题映射未命中时必须提供，否则登记被拒绝；可留空。</summary>
    public int? UploadSubjectId { get; set; }

    /// <summary>删除确认面板的目标任务；null 表示面板关闭。</summary>
    public DownloadTaskView? DeleteTarget { get; private set; }

    /// <summary>删除面板预检：下载器中是否存在该种子；null 表示未知或尚未加载。</summary>
    public bool? TorrentInDownloader { get; private set; }

    /// <summary>删除器连删勾选被用户明确触碰时置位；用于确认异步预检不得覆盖用户选择。</summary>
    internal bool DeleteOptionsTouched { get; private set; }

    public bool DeleteFromDownloader
    {
        get => _deleteFromDownloader;
        set
        {
            _deleteFromDownloader = value;
            if (!_settingDeleteOptions) DeleteOptionsTouched = true;
        }
    }

    /// <summary>面板勾选项：删除下载器种子时是否连带删除已下载的文件。</summary>
    public bool DeleteFiles
    {
        get => _deleteFiles;
        set
        {
            _deleteFiles = value;
            if (!_settingDeleteOptions) DeleteOptionsTouched = true;
        }
    }

    /// <summary>批量选择模式：开启后行首出现选择框，行内操作按钮让位。</summary>
    public bool BatchMode { get; private set; }

    /// <summary>批量确认面板是否打开。</summary>
    public bool BulkDeletePanelOpen { get; private set; }

    /// <summary>批量删除预检结果；面板打开时非空。</summary>
    public BulkDeletePrecheck? BulkPrecheck { get; private set; }

    /// <summary>当前勾选的任务 Id；随列表刷新与现存任务取交集，不保留悬挂选择。</summary>
    public IReadOnlyCollection<Guid> SelectedIds => _selectedIds;

    public int SelectedCount => _selectedIds.Count;

    // 表头全选只反映当前页：勾选只添加当前页 Id，取消只移除当前页 Id，
    // 其他页与被筛选隐藏任务的已选状态不受影响；批量操作仍按明确选中的 Id 执行。
    public bool AllSelected => Tasks.Count > 0 && Tasks.All(task => _selectedIds.Contains(task.Id));

    /// <summary>页面组件释放时取消未触发的防抖定时与进行中的防抖查询。</summary>
    public void Dispose() => CancelSearchDebounce();

    /// <summary>后台加载（防抖搜索）完成后的界面刷新回调；页面组件订阅后转 StateHasChanged。</summary>
    public event Action? ResultsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 请求序号防止慢查询晚到覆盖较新的结果；防抖取消令牌同时中止过期的数据库查询。
        // 选择集只由用户操作维护，不与当前页求交集：翻页或筛选隐藏的任务保留已选状态。
        var requestId = ++_loadRequestId;
        var page      = await taskQueries.ListAsync(BuildFilter(), cancellationToken);
        if (requestId != _loadRequestId) return;
        // 查询层钳制越界页后把实际页码同步回内部状态；过期请求已在上方返回，不会覆盖新状态。
        Tasks              = page.Items;
        PageNumber         = page.Page;
        _pageNumber        = page.Page;
        PageSize           = page.PageSize;
        TotalCount         = page.TotalCount;
        AvailableLanguages = page.Languages;
        var taskIds = Tasks.Select(task => task.Id).ToHashSet();
        foreach (var id in _fileStates.Keys.Where(id => !taskIds.Contains(id)).ToArray())
            _fileStates.Remove(id);
        Loaded = true;
        ResultsChanged?.Invoke();
    }

    private DownloadTaskListFilter BuildFilter() => new()
    {
        SearchText   = _searchText,
        StatusGroups = _statusFilter,
        Languages    = _languageFilter,
        Page         = _pageNumber
    };

    public async Task ToggleStatusFilterAsync(DownloadStatusFilterGroup group)
    {
        if (!_statusFilter.Remove(group)) _statusFilter.Add(group);
        await ReloadFromFirstPageAsync();
    }

    public async Task ToggleLanguageFilterAsync(string language)
    {
        if (!_languageFilter.Remove(language)) _languageFilter.Add(language);
        await ReloadFromFirstPageAsync();
    }

    /// <summary>清空搜索与全部多选筛选，回到第一页全量展示。</summary>
    public async Task ClearFiltersAsync()
    {
        _searchText = string.Empty;
        _statusFilter.Clear();
        _languageFilter.Clear();
        await ReloadFromFirstPageAsync();
    }

    public async Task GoToPageAsync(int page)
    {
        var target = Math.Clamp(page, 1, TotalPages);
        if (target == _pageNumber && Loaded) return;
        _pageNumber = target;
        CancelSearchDebounce();
        await LoadSafelyAsync(CancellationToken.None);
    }

    private async Task ReloadFromFirstPageAsync()
    {
        _pageNumber = 1;
        CancelSearchDebounce();
        await LoadSafelyAsync(CancellationToken.None);
    }

    /// <summary>搜索输入防抖：停顿后重新查询；新输入取消未触发的旧定时与进行中的查询（配合请求序号双保险）。</summary>
    private void ScheduleSearchReload()
    {
        _pageNumber = 1;
        CancelSearchDebounce();
        _searchDebounceCts = new CancellationTokenSource();
        _                  = ReloadAfterDelayAsync(_searchDebounceCts.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDebounceDelay, cancellationToken);
            await LoadSafelyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelSearchDebounce()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;
    }

    private async Task LoadSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务列表加载失败。");
            Succeeded = false;
            Message   = "加载下载任务列表失败，请重试。";
            ResultsChanged?.Invoke();
        }
    }

    /// <summary>详情以任务 Id 保存，摘要刷新不重新加载文件，也不重建正在编辑的草稿。</summary>
    public DownloadTaskFilesState FileState(Guid taskId)
    {
        if (!_fileStates.TryGetValue(taskId, out var state))
            _fileStates[taskId] = state = new DownloadTaskFilesState();
        return state;
    }

    public async Task ToggleFilesAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var state = FileState(taskId);
        state.Expanded = !state.Expanded;
        if (state is { Expanded: true, Details: null })
            await LoadFilesAsync(taskId, cancellationToken);
    }

    public async Task LoadFilesAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var state = FileState(taskId);
        if (state.Loading || state.Busy) return;
        state.Loading = true;
        state.Error   = null;
        try
        {
            var details = await RequireFileService().ListAsync(taskId, cancellationToken);
            if (IsCurrentFileState(taskId, state))
            {
                state.Details = details;
                foreach (var draft in state.Drafts.Values) draft.InvalidatePreview();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件列表加载失败。", taskId);
            if (IsCurrentFileState(taskId, state)) state.Error = exception.Message;
        }
        finally
        {
            state.Loading = false;
        }
    }

    public void BeginFileEdit(Guid taskId, int fileIndex)
    {
        var state = FileState(taskId);
        if (state.Busy || state.Loading || state.Drafts.ContainsKey(fileIndex)) return;
        var file = state.Details?.Files.SingleOrDefault(file => file.Index == fileIndex);
        if (file is null) return;
        state.Drafts[fileIndex] = new DownloadFileEditDraft(file);
        state.Error             = null;
        state.Message           = null;
    }

    public void CancelFileEdit(Guid taskId, int fileIndex)
    {
        var state = FileState(taskId);
        // 应用已发出后无法用取消按钮撤销下载器变更。
        if (!state.Busy) state.Drafts.Remove(fileIndex);
    }

    public async Task PreviewFileAsync(Guid taskId, int fileIndex, CancellationToken cancellationToken = default)
    {
        var state = FileState(taskId);
        if (state.Busy || state.Loading || !state.Drafts.TryGetValue(fileIndex, out var draft)) return;
        var request = draft.Request;
        draft.Preview        = null;
        draft.PreviewRequest = null;
        state.Busy           = true;
        state.Error          = null;
        state.Message        = null;
        try
        {
            var preview = await RequireFileService().PreviewAsync(taskId, fileIndex, request, cancellationToken);
            if (IsCurrentFileState(taskId, state)                  &&
                state.Drafts.GetValueOrDefault(fileIndex) == draft &&
                draft.Request                             == request)
            {
                draft.Preview        = preview;
                draft.PreviewRequest = request;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件 {FileIndex} 修改预览失败。", taskId, fileIndex);
            if (IsCurrentFileState(taskId, state)) state.Error = exception.Message;
        }
        finally
        {
            state.Busy = false;
        }
    }

    public async Task ApplyFileAsync(Guid taskId, int fileIndex, CancellationToken cancellationToken = default)
    {
        var state = FileState(taskId);
        if (state.Busy || state.Loading || !state.Drafts.TryGetValue(fileIndex, out var draft) ||
            !draft.CanApply) return;
        var request = draft.Request;
        state.Busy    = true;
        state.Error   = null;
        state.Message = null;
        try
        {
            var details = await RequireFileService().ApplyAsync(taskId, fileIndex, request, cancellationToken);
            if (!IsCurrentFileState(taskId, state)) return;
            state.Details = details;
            state.Drafts.Remove(fileIndex);
            // 根目录迁移会影响其他文件的完整路径，需要重新预览，但保留其编辑值。
            foreach (var otherDraft in state.Drafts.Values) otherDraft.InvalidatePreview();
            state.Message = "修改已提交，文件位置以下载器确认结果为准。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件 {FileIndex} 修改失败。", taskId, fileIndex);
            if (IsCurrentFileState(taskId, state))
            {
                state.Error = exception.Message;
                draft.InvalidatePreview();
            }
        }
        finally
        {
            state.Busy = false;
        }
    }

    private IDownloadFileService RequireFileService() =>
        fileService ?? throw new InvalidOperationException("文件详情服务尚未启用。");

    private bool IsCurrentFileState(Guid taskId, DownloadTaskFilesState state) =>
        _fileStates.GetValueOrDefault(taskId) == state;

    public void ToggleBatchMode()
    {
        BatchMode            = !BatchMode;
        BulkDeletePanelOpen  = false;
        _bulkConfirmationIds = [];
        _bulkRequestId++;
        _selectedIds.Clear();
    }

    public void ToggleSelected(Guid taskId)
    {
        if (!_selectedIds.Remove(taskId)) _selectedIds.Add(taskId);
        InvalidateBulkPanel();
    }

    public bool IsSelected(Guid taskId) => _selectedIds.Contains(taskId);

    public void ToggleSelectAll()
    {
        var visibleIds = Tasks.Select(task => task.Id).ToList();
        if (AllSelected) _selectedIds.ExceptWith(visibleIds);
        else _selectedIds.UnionWith(visibleIds);
        InvalidateBulkPanel();
    }

    /// <summary>打开批量删除面板：预检下载器中存在多少种子，全部存在时默认勾选连删种子。</summary>
    public async Task PrepareBulkDeleteAsync(CancellationToken cancellationToken = default)
    {
        if (!BatchMode || _selectedIds.Count == 0) return;

        var requestId = ++_bulkRequestId;
        var ids       = _selectedIds.ToArray();
        BulkDeletePanelOpen  = false;
        BulkPrecheck         = null;
        _bulkConfirmationIds = [];
        var precheck = await downloadService.PrecheckBulkDeleteAsync(ids, cancellationToken);
        if (requestId != _bulkRequestId || !BatchMode || cancellationToken.IsCancellationRequested ||
            !_selectedIds.SetEquals(ids)) return;

        BulkPrecheck         = precheck;
        _bulkConfirmationIds = ids;
        SetDeleteOptions(precheck.DownloaderChecked && precheck.TorrentsInDownloader == precheck.TaskCount, false);
        BulkDeletePanelOpen = true;
    }

    public void CancelBulkDelete()
    {
        _bulkRequestId++;
        _bulkConfirmationIds = [];
        BulkDeletePanelOpen  = false;
        BulkPrecheck         = null;
    }

    private void InvalidateBulkPanel()
    {
        _bulkRequestId++;
        _bulkConfirmationIds = [];
        BulkDeletePanelOpen  = false;
        BulkPrecheck         = null;
    }

    private void SetDeleteOptions(bool deleteTorrent, bool deleteFiles)
    {
        _settingDeleteOptions = true;
        try
        {
            DeleteFromDownloader = deleteTorrent;
            DeleteFiles          = deleteFiles;
        }
        finally
        {
            _settingDeleteOptions = false;
        }
    }

    /// <summary>确认批量删除：面板在发起前关闭，删除多少个由结果消息汇报。</summary>
    public async Task ConfirmBulkDeleteAsync(CancellationToken cancellationToken = default)
    {
        var ids = _bulkConfirmationIds.ToArray();
        if (ids.Length == 0 || !BulkDeletePanelOpen) return;
        var deleteFromDownloader = DeleteFromDownloader;
        var deleteFiles          = deleteFromDownloader && DeleteFiles;
        BulkDeletePanelOpen  = false;
        _bulkConfirmationIds = [];
        _bulkRequestId++;
        Busy    = true;
        Message = null;
        try
        {
            var deleted = await downloadService.DeleteManyAsync(ids, deleteFromDownloader, deleteFiles,
                                                                cancellationToken);
            Succeeded = true;
            Message   = $"已删除 {deleted} 个任务。";
            _selectedIds.Clear();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "批量删除下载任务失败。");
            Succeeded = false;
            Message = exception.Message.Contains("下载器", StringComparison.Ordinal) ||
                      exception is InvalidOperationException
                ? exception.Message
                : "删除失败，请查看服务日志。";
        }
        finally
        {
            Busy = false;
        }

        await LoadAsync(cancellationToken);
    }

    /// <summary>打开删除面板：记录目标任务并预检下载器中是否存在同 hash 种子。</summary>
    public async Task PrepareDeleteAsync(DownloadTaskView task, CancellationToken cancellationToken = default)
    {
        var requestId = ++_deleteRequestId;
        DeleteTarget         = task;
        TorrentInDownloader  = null;
        DeleteOptionsTouched = false;
        SetDeleteOptions(false, false);
        try
        {
            var inDownloader = await downloadService.TorrentInDownloaderAsync(task.Id, cancellationToken);
            if (requestId != _deleteRequestId || DeleteTarget?.Id != task.Id ||
                cancellationToken.IsCancellationRequested)
                return;

            TorrentInDownloader = inDownloader;
            if (!DeleteOptionsTouched && inDownloader == true) SetDeleteOptions(true, false);
        }
        catch (KeyNotFoundException)
        {
            if (requestId != _deleteRequestId || DeleteTarget?.Id != task.Id) return;
            DeleteTarget        = null;
            TorrentInDownloader = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requestId != _deleteRequestId || DeleteTarget?.Id != task.Id) throw;
            DeleteTarget        = null;
            TorrentInDownloader = null;

            throw;
        }
        catch
        {
            if (requestId == _deleteRequestId && DeleteTarget?.Id == task.Id)
                TorrentInDownloader = null;
        }
    }

    public void CancelDelete()
    {
        _deleteRequestId++;
        DeleteTarget        = null;
        TorrentInDownloader = null;
    }

    /// <summary>确认删除：按面板勾选项连下载器种子与文件一起处理；面板在发起前关闭，结果走页面提示条。</summary>
    public async Task DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var deleteFromDownloader = DeleteFromDownloader;
        var deleteFiles          = deleteFromDownloader && DeleteFiles;
        DeleteTarget = null;
        await RunOperationAsync(taskId,
                                () => downloadService.DeleteAsync(taskId, deleteFromDownloader, deleteFiles,
                                                                  cancellationToken), "任务已删除。", cancellationToken);
        // 单删成功后同步收敛选择集，批量计数不再包含已删任务；选择集不随刷新重建，靠这里保持准确。
        if (Succeeded) _selectedIds.Remove(taskId);
    }

    /// <summary>上传种子登记：读取浏览器流后交给服务校验与去重，创建即等待提交。</summary>
    public async Task UploadTorrentAsync(Stream torrentStream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await torrentStream.CopyToAsync(buffer, cancellationToken);
        switch (buffer.Length)
        {
            case 0 :
                NoteFailure("所选文件为空，请重新选择。");
                return;
            case > DownloadIntakeService.MaxTorrentUploadBytes :
                NoteFailure("种子文件超过 10 MB，已拒绝。");
                return;
            default :
                await RunCoreAsync(() => intakeService.CreateFromUploadAsync(buffer.ToArray(), null,
                                                                             UploadSubjectId, cancellationToken),
                                   "上传种子已登记，等待提交下载器。", cancellationToken,
                                   exception => logger.LogWarning(exception, "上传种子登记失败。"));
                break;
        }
    }

    /// <summary>浏览器文件读取失败（超过传输上限或流中断）时直接提示，不触发服务调用。</summary>
    public void NoteFailure(string message)
    {
        Succeeded = false;
        Message   = message;
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.PauseAsync(taskId, cancellationToken),
                                "任务已暂停。", cancellationToken);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.ResumeAsync(taskId, cancellationToken),
                                "任务已恢复。", cancellationToken);
    }

    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.RetryAsync(taskId, cancellationToken),
                                "任务已重新排队，等待提交。", cancellationToken);
    }

    /// <summary>重新核对：仅作用于已完成但归档受阻的任务；占用或身份异常仍会被拒绝并提示原因。</summary>
    public async Task RetryArchiveAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (fileService is null)
        {
            NoteFailure("文件整理服务未启用，无法重新核对归档。");
            return;
        }

        await RunOperationAsync(taskId, () => fileService.RetryArchiveAsync(taskId, cancellationToken),
                                "归档请求已处理，请查看当前归档状态。", cancellationToken);
    }

    private async Task RunOperationAsync(
        Guid taskId, Func<Task> operation, string successMessage, CancellationToken cancellationToken)
    {
        await RunCoreAsync(operation, successMessage, cancellationToken,
                           exception => logger.LogWarning(exception, "下载任务 {TaskId} 操作失败。", taskId));
    }

    private async Task RunCoreAsync(
        Func<Task> operation, string successMessage, CancellationToken cancellationToken, Action<Exception> logFailure)
    {
        Busy    = true;
        Message = null;
        try
        {
            await operation();
            Succeeded = true;
            Message   = successMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logFailure(exception);
            Succeeded = false;
            Message = exception.Message.Contains("下载器", StringComparison.Ordinal) ||
                      exception is InvalidOperationException
                ? exception.Message
                : "下载器操作失败，请查看服务日志。";
        }
        finally
        {
            Busy = false;
        }

        await LoadAsync(cancellationToken);
    }
}

/// <summary>仅属于页面生命周期的文件详情与编辑草稿。</summary>
public sealed class DownloadTaskFilesState
{
    public bool    Expanded { get; internal set; }
    public bool    Loading  { get; internal set; }
    public bool    Busy     { get; internal set; }
    public string? Error    { get; internal set; }
    public string? Message  { get; internal set; }

    public DownloadTaskFilesView?                 Details { get; internal set; }
    public Dictionary<int, DownloadFileEditDraft> Drafts  { get; } = [];
}

public sealed class DownloadFileEditDraft(DownloadFileView file)
{
    private string?  _animeRelativePath = file.AnimeRelativePath;
    private decimal? _episode           = file.Episode;
    private decimal? _episodeEnd        = file.EpisodeEnd;

    private DownloadFileKind
        _kind = file.Kind == DownloadFileKind.Seed ? DownloadFileKind.Seed : DownloadFileKind.Anime;

    private bool _reidentifyOriginalName;

    public DownloadFileKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            InvalidatePreview();
        }
    }

    public bool ReidentifyOriginalName
    {
        get => _reidentifyOriginalName;
        set
        {
            if (_reidentifyOriginalName == value) return;
            _reidentifyOriginalName = value;
            if (value)
            {
                // 既有分类可能来自已改名路径；不要用旧人工值盖掉原始名重新解析的结果。
                _episode           = null;
                _episodeEnd        = null;
                _animeRelativePath = null;
            }

            InvalidatePreview();
        }
    }

    public decimal? Episode
    {
        get => _episode;
        set
        {
            if (_episode == value) return;
            _episode = value;
            InvalidatePreview();
        }
    }

    public decimal? EpisodeEnd
    {
        get => _episodeEnd;
        set
        {
            if (_episodeEnd == value) return;
            _episodeEnd = value;
            InvalidatePreview();
        }
    }

    public string? AnimeRelativePath
    {
        get => _animeRelativePath;
        set
        {
            if (_animeRelativePath == value) return;
            _animeRelativePath = value;
            InvalidatePreview();
        }
    }

    public DownloadFileEditRequest Request => new(Kind, ReidentifyOriginalName, Episode, EpisodeEnd,
                                                  string.IsNullOrWhiteSpace(AnimeRelativePath)
                                                      ? null
                                                      : AnimeRelativePath);

    public   DownloadFilePreview?     Preview        { get; internal set; }
    internal DownloadFileEditRequest? PreviewRequest { get; set; }

    public bool CanApply => Preview is not null && PreviewRequest == Request;

    public void InvalidatePreview()
    {
        Preview        = null;
        PreviewRequest = null;
    }
}
