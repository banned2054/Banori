using MediaManager.Features.Downloads;

namespace MediaManager.Components.Pages.Downloads;

/// <summary>下载任务页状态：任务列表、上传种子与暂停 / 恢复 / 重试操作。随页面组件创建和释放。</summary>
public sealed class DownloadTasksPageViewModel(
    DownloadService                     downloadService,
    DownloadIntakeService               intakeService,
    DownloadTaskQueries                 taskQueries,
    ILogger<DownloadTasksPageViewModel> logger,
    IDownloadFileService?               fileService = null)
{
    private readonly Dictionary<Guid, DownloadTaskFilesState> _fileStates = [];

    private readonly HashSet<Guid> _selectedIds         = [];
    private          Guid[]        _bulkConfirmationIds = [];
    private          long          _bulkRequestId;

    private bool _deleteFiles;

    /// <summary>面板勾选项：是否连下载器中的种子一起删除。</summary>
    private bool _deleteFromDownloader;

    private long _deleteRequestId;
    private bool _settingDeleteOptions;

    public IReadOnlyList<DownloadTaskView> Tasks { get; private set; } = [];

    public bool    Loaded    { get; private set; }
    public bool    Busy      { get; private set; }
    public string? Message   { get; private set; }
    public bool    Succeeded { get; private set; }

    /// <summary>上传表单的来源站点标注（u2/byrbt/tjupt 等），随下一次上传一起提交；可留空。</summary>
    public string? UploadSiteSource { get; set; }

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

    public bool AllSelected => Tasks.Count > 0 && Tasks.All(task => _selectedIds.Contains(task.Id));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Tasks = await taskQueries.ListAsync(cancellationToken : cancellationToken);
        var selectedBefore = _selectedIds.ToHashSet();
        _selectedIds.IntersectWith(Tasks.Select(task => task.Id));
        if (!selectedBefore.SetEquals(_selectedIds)) InvalidateBulkPanel();
        var taskIds = Tasks.Select(task => task.Id).ToHashSet();
        foreach (var id in _fileStates.Keys.Where(id => !taskIds.Contains(id)).ToArray())
            _fileStates.Remove(id);
        Loaded = true;
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
        if (AllSelected) _selectedIds.Clear();
        else _selectedIds.UnionWith(Tasks.Select(task => task.Id));
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
                await RunCoreAsync(() => intakeService.CreateFromUploadAsync(buffer.ToArray(), UploadSiteSource,
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
