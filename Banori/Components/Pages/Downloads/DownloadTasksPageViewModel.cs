using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;

namespace Banori.Components.Pages.Downloads;

/// <summary>下载任务页状态：无页码虚拟列表、按任务 ID 独立的详情/草稿与文件同步。</summary>
public sealed class DownloadTasksPageViewModel(
    DownloadService                     downloadService,
    DownloadIntakeService               intakeService,
    DownloadTaskQueries                 taskQueries,
    DownloadTaskFilterStore             filterStore,
    ILogger<DownloadTasksPageViewModel> logger,
    IDownloadFileService?               fileService  = null,
    TimeProvider?                       timeProvider = null) : IDisposable
{
    public const int RowHeightPx          = 38;
    public const int DefaultViewportCount = 30;

    private readonly Dictionary<Guid, DownloadTaskFilesState> _fileStates     = [];
    private readonly Dictionary<Guid, Task>                   _inFlightSyncs  = [];
    private readonly HashSet<string>                          _languageFilter = [];
    private readonly HashSet<Guid>                            _selectedIds    = [];
    private readonly Dictionary<Guid, DownloadTaskView>       _summaryCache   = [];
    private readonly TimeProvider                             _timeProvider   = timeProvider ?? TimeProvider.System;

    // 状态筛选默认全部勾选：全不勾选表示一个状态都不显示，不再用空集合表达“不筛选”。
    private readonly HashSet<DownloadStatusFilterGroup> _statusFilter = DownloadStatusFilter.CreateAllGroups();

    private Guid?                    _activeFilePollTaskId;
    private Guid[]                   _bulkConfirmationIds = [];
    private long                     _bulkRequestId;
    private bool                     _deleteFiles;
    private bool                     _deleteFromDownloader;
    private long                     _deleteRequestId;
    private long                         _detailRequestId;
    private CancellationTokenSource?     _filePollCts;
    private long                         _filePollGeneration;
    private bool                         _isPageHidden;
    private QbittorrentMainDataSnapshot? _latestMainDataSnapshot;
    private long                         _loadRequestId;
    private int?                     _pendingScrollTopPx;
    private int                      _requestedVisibleStart;
    private CancellationTokenSource? _searchDebounceCts;
    private string                   _searchText = string.Empty;
    private bool                     _settingDeleteOptions;
    private long                     _snapshotVersion;
    private int                      _viewportCount = DefaultViewportCount;

    // 已保存的筛选只在页面首次加载时读取，之后由勾选维护，定时刷新不回读；并发加载共用同一次读取。
    private Task? _statusFilterLoadTask;

    // 勾选串行写入同一设置项：保存链条的尾部保证较早的选择不会晚于较新的选择落库。
    private Task _statusFilterSave = Task.CompletedTask;

    // 用户在读取已保存筛选期间改过勾选：读取结果作废，不能把新选择覆盖成旧值。
    private bool _statusFilterTouched;

    /// <summary>当前挂载在 DOM 中的可见区与上下缓冲区任务摘要。</summary>
    public IReadOnlyList<DownloadTaskView> Tasks { get; private set; } = [];

    /// <summary>当前筛选与排序下的完整有序任务 ID 快照。</summary>
    public IReadOnlyList<Guid> OrderedTaskIds { get; private set; } = [];

    /// <summary>当前视口内可见的任务 ID 切片（不含上下缓冲区，用于「勾选可见行」）。</summary>
    public IReadOnlyList<Guid> VisibleTaskIds { get; private set; } = [];

    /// <summary>当前选中的任务 ID（跨滚动与筛选独立保留）。</summary>
    public Guid? SelectedTaskId { get; private set; }

    /// <summary>当前选中任务的独立详情投影；选中行滚出视口后仍可供详情与文件面板使用。</summary>
    public DownloadTaskView? SelectedTask { get; private set; }

    public int OverscanCount { get; set; } = 10;

    public int VisibleStartIndex { get; private set; }
    public int VisibleEndIndex   { get; private set; }
    public int MountedStartIndex { get; private set; }
    public int MountedEndIndex   { get; private set; }

    public int TopSpacerHeightPx    => MountedStartIndex * RowHeightPx;
    public int BottomSpacerHeightPx => Math.Max(0, TotalCount - MountedEndIndex) * RowHeightPx;

    /// <summary>
    ///     搜索关键词输入：每次变化重置防抖定时器，停顿后回到顶部重新查询；空白视为不过滤。
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

    /// <summary>测试注入：待确认文件操作的轮询间隔与最大轮数，避免用例真实等待。</summary>
    internal TimeSpan PendingFileRefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    internal int PendingFileRefreshAttempts { get; set; } = 120;

    /// <summary>展开的文件面板自动同步间隔；生产默认 5 秒，测试可缩短。</summary>
    internal TimeSpan FileAutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>状态筛选分组多选：默认全部勾选（等于不限制），全不勾选表示一个状态都不显示。</summary>
    public IReadOnlySet<DownloadStatusFilterGroup> StatusFilter => _statusFilter;

    /// <summary>状态筛选是否正在收窄：全选是默认的“不限制”，不计入生效中的筛选。</summary>
    public bool StatusFilterActive => _statusFilter.Count != DownloadStatusFilter.Choices.Count;

    /// <summary>字幕语言筛选多选（空字符串哨兵 = 无语言任务）；空集合表示全部语言。</summary>
    public IReadOnlySet<string> LanguageFilter => _languageFilter;

    /// <summary>是否有生效中的筛选，用于展示清除入口与空态文案。</summary>
    public bool HasActiveFilters => _searchText.Trim().Length > 0 ||
                                    StatusFilterActive            ||
                                    _languageFilter.Count > 0;

    /// <summary>当前主排序列；默认为动画名。</summary>
    public DownloadTaskSortColumn SortColumn { get; private set; } = DownloadTaskSortColumn.AnimeName;

    /// <summary>当前主排序列是否降序。</summary>
    public bool SortDescending { get; private set; }

    /// <summary>点击表头排序：切到新列时采用该列默认方向（点击动画名即恢复默认排序），再点同列则反序。</summary>
    public Task SortByColumnAsync(DownloadTaskSortColumn column)
    {
        if (SortColumn == column)
            SortDescending = !SortDescending;
        else
        {
            SortColumn     = column;
            SortDescending = DownloadTaskQueries.DefaultSortDescending(column);
        }

        return ReloadFromTopAsync();
    }

    public int TotalCount => OrderedTaskIds.Count;

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

    /// <summary>当前勾选的任务 Id；跨滚动保留，仅在任务确实删除或用户取消勾选时移除。</summary>
    public IReadOnlyCollection<Guid> SelectedIds => _selectedIds;

    public int SelectedCount => _selectedIds.Count;

    /// <summary>表头复选框状态：仅反映当前视口内可见行是否全部选中（不含缓冲区）。</summary>
    public bool AllSelected => VisibleTaskIds.Count > 0 && VisibleTaskIds.All(_selectedIds.Contains);

    /// <summary>页面组件释放时取消未触发的防抖定时、文件自动轮询与进行中的文件确认等待。</summary>
    public void Dispose()
    {
        CancelSearchDebounce();
        StopFilePolling();
        foreach (var state in _fileStates.Values) state.PendingRefreshCancellation?.Cancel();
    }

    /// <summary>后台加载（防抖搜索、视口加载与文件轮询）完成后的界面刷新回调。</summary>
    public event Action? ResultsChanged;

    public int? ConsumePendingScrollTopPx()
    {
        var value = _pendingScrollTopPx;
        _pendingScrollTopPx = null;
        return value;
    }

    public DownloadTaskView? GetTaskView(Guid taskId)
    {
        if (SelectedTask?.Id == taskId) return SelectedTask;
        return _summaryCache.GetValueOrDefault(taskId) ?? Tasks.FirstOrDefault(task => task.Id == taskId);
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(false, cancellationToken);

    public Task RefreshFallbackAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(true, cancellationToken);

    /// <summary>
    ///     应用下载器 <c>sync/maindata</c> 实时进度快照：按任务 ID 原地刷新已挂载行与当前选中任务的展示进度，
    ///     不重查数据库、不改变排序与列表归属、不触发滚动位置重算，并严守业务终态/隔离/缺失边界。
    /// </summary>
    public void ApplyMainDataSnapshot(QbittorrentMainDataSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latestMainDataSnapshot = snapshot;
        if (!Loaded) return;

        var mountedSet   = SliceIds(MountedStartIndex, MountedEndIndex).ToHashSet();
        var mountedDirty = false;
        foreach (var id in _summaryCache.Keys.ToArray())
        {
            var current = _summaryCache[id];
            var updated = OverlayMainDataProgress(current, snapshot);
            if (ReferenceEquals(updated, current)) continue;
            _summaryCache[id] = updated;
            if (mountedSet.Contains(id)) mountedDirty = true;
        }

        if (mountedDirty) RebuildMountedTasks();

        var selectedDirty = false;
        if (SelectedTask is { } selected)
        {
            var updatedSelected = OverlayMainDataProgress(selected, snapshot);
            if (!ReferenceEquals(updatedSelected, selected))
            {
                SelectedTask  = updatedSelected;
                selectedDirty = true;
            }
        }

        if (mountedDirty || selectedDirty) ResultsChanged?.Invoke();
    }

    public async Task RefreshChangedTasksAsync(
        IReadOnlyCollection<Guid> taskIds, bool affectsListOrderOrMembership,
        CancellationToken cancellationToken = default)
    {
        if (affectsListOrderOrMembership || !Loaded)
        {
            await LoadCoreAsync(true, cancellationToken);
            return;
        }

        if (taskIds.Count == 0) return;

        var mountedSet       = SliceIds(MountedStartIndex, MountedEndIndex).ToHashSet();
        var targetMountedIds = taskIds.Where(mountedSet.Contains).Distinct().ToArray();
        var updatedSelected  = false;

        if (targetMountedIds.Length > 0)
        {
            var snapshotVersion = _snapshotVersion;
            var updated         = await taskQueries.GetSummariesByIdsAsync(targetMountedIds, cancellationToken);
            if (snapshotVersion != _snapshotVersion) return;

            foreach (var rawItem in updated)
            {
                var item = OverlayMainDataProgress(rawItem);
                _summaryCache[item.Id] = item;
                if (SelectedTaskId == item.Id)
                {
                    SelectedTask    = item;
                    updatedSelected = true;
                }
            }

            RebuildMountedTasks();
        }

        if (!updatedSelected && SelectedTaskId is { } selectedId && taskIds.Contains(selectedId))
            await RefreshSelectedDetailAsync(cancellationToken);

        ResultsChanged?.Invoke();
    }

    public async Task SetViewportAsync(
        int visibleStartIndex, int visibleCount, CancellationToken cancellationToken = default)
    {
        _requestedVisibleStart = Math.Max(0, visibleStartIndex);
        _viewportCount         = Math.Max(0, visibleCount);
        if (!Loaded) return;

        var prevMountedStart = MountedStartIndex;
        var prevMountedEnd   = MountedEndIndex;
        var prevVisibleStart = VisibleStartIndex;
        var prevVisibleEnd   = VisibleEndIndex;
        RecomputeWindowIndices();

        var mountedIds = SliceIds(MountedStartIndex, MountedEndIndex);
        var missingIds = mountedIds.Where(id => !_summaryCache.ContainsKey(id)).ToArray();
        if (missingIds.Length == 0)
        {
            if (prevMountedStart != MountedStartIndex || prevMountedEnd != MountedEndIndex ||
                prevVisibleStart != VisibleStartIndex || prevVisibleEnd != VisibleEndIndex)
            {
                RebuildMountedTasks();
                ResultsChanged?.Invoke();
            }

            return;
        }

        var snapshotVersion = _snapshotVersion;
        var fetched         = await taskQueries.GetSummariesByIdsAsync(missingIds, cancellationToken);
        if (snapshotVersion != _snapshotVersion) return;

        foreach (var rawItem in fetched)
        {
            var item = OverlayMainDataProgress(rawItem);
            _summaryCache[item.Id] = item;
        }

        TrimSummaryCacheIfNeeded();
        RebuildMountedTasks();
        ResultsChanged?.Invoke();
    }

    public async Task SelectTaskAsync(Guid? taskId, CancellationToken cancellationToken = default)
    {
        SelectedTaskId = taskId;
        var detailRequestId = ++_detailRequestId;
        if (taskId is not { } id)
        {
            SelectedTask = null;
            ResultsChanged?.Invoke();
            return;
        }

        if (_summaryCache.TryGetValue(id, out var cached))
            SelectedTask = OverlayMainDataProgress(cached);

        var detail = await taskQueries.GetDetailAsync(id, cancellationToken);
        if (detailRequestId != _detailRequestId || SelectedTaskId != id) return;

        if (detail is null)
        {
            SelectedTaskId = null;
            SelectedTask   = null;
            RemoveFileState(id);
        }
        else
        {
            SelectedTask = OverlayMainDataProgress(detail);
        }

        ResultsChanged?.Invoke();
    }

    private async Task LoadCoreAsync(bool preserveAnchor, CancellationToken cancellationToken)
    {
        // 请求序号防止慢查询晚到覆盖较新的结果；防抖取消令牌同时中止过期的数据库查询。
        var requestId      = ++_loadRequestId;
        var anchorIds      = preserveAnchor ? VisibleTaskIds.ToArray() : [];
        var prevStartIndex = VisibleStartIndex;

        await EnsureStatusFilterLoadedAsync(cancellationToken);
        var index = await taskQueries.ListIdsAsync(BuildFilter(), cancellationToken);
        if (requestId != _loadRequestId) return;

        var idsChanged = !OrderedTaskIds.SequenceEqual(index.TaskIds);
        var nextStart  = _requestedVisibleStart;
        int? nextScrollTop = null;

        if (preserveAnchor && idsChanged && Loaded)
        {
            var anchorNewIndex = FindFirstAnchorIndex(index.TaskIds, anchorIds);
            if (anchorNewIndex >= 0)
            {
                nextStart = anchorNewIndex;
                if (anchorNewIndex != prevStartIndex) nextScrollTop = anchorNewIndex * RowHeightPx;
            }
            else
            {
                nextStart     = index.TaskIds.Count == 0 ? 0 : Math.Clamp(prevStartIndex, 0, index.TaskIds.Count - 1);
                nextScrollTop = nextStart * RowHeightPx;
            }
        }

        var window     = ComputeWindow(index.TaskIds, nextStart, _viewportCount, OverscanCount);
        var mountedIds = SliceIds(index.TaskIds, window.MountedStart, window.MountedEnd);
        IReadOnlyList<DownloadTaskView> summaries = mountedIds.Length > 0
            ? await taskQueries.GetSummariesByIdsAsync(mountedIds, cancellationToken)
            : [];
        if (requestId != _loadRequestId) return;

        OrderedTaskIds         = index.TaskIds;
        AvailableLanguages     = index.Languages;
        _snapshotVersion++;
        _summaryCache.Clear();
        foreach (var rawItem in summaries)
        {
            var item = OverlayMainDataProgress(rawItem);
            _summaryCache[item.Id] = item;
        }

        _requestedVisibleStart = nextStart;
        if (nextScrollTop is { } scrollTop) _pendingScrollTopPx = scrollTop;
        VisibleStartIndex      = window.VisibleStart;
        VisibleEndIndex        = window.VisibleEnd;
        MountedStartIndex      = window.MountedStart;
        MountedEndIndex        = window.MountedEnd;
        VisibleTaskIds         = SliceIds(index.TaskIds, window.VisibleStart, window.VisibleEnd);
        RebuildMountedTasks();
        PruneDeletedStateWhenUnfiltered();
        await RefreshSelectedDetailAsync(cancellationToken);
        if (requestId != _loadRequestId) return;

        Loaded = true;
        ResultsChanged?.Invoke();
    }

    private async Task RefreshSelectedDetailAsync(CancellationToken cancellationToken)
    {
        if (SelectedTaskId is not { } selectedId)
        {
            SelectedTask = null;
            return;
        }

        if (_summaryCache.TryGetValue(selectedId, out var cached))
        {
            SelectedTask = OverlayMainDataProgress(cached);
            return;
        }

        var detailRequestId = ++_detailRequestId;
        var detail          = await taskQueries.GetDetailAsync(selectedId, cancellationToken);
        if (detailRequestId != _detailRequestId || SelectedTaskId != selectedId) return;

        if (detail is null)
        {
            SelectedTaskId = null;
            SelectedTask   = null;
            RemoveFileState(selectedId);
            return;
        }

        SelectedTask = OverlayMainDataProgress(detail);
    }

    private DownloadTaskView OverlayMainDataProgress(
        DownloadTaskView view, QbittorrentMainDataSnapshot? snapshot = null)
    {
        snapshot ??= _latestMainDataSnapshot;
        if (snapshot is null || snapshot.Torrents.Count == 0) return view;
        if (!CanOverlayLiveProgress(view)) return view;

        var normalizedHash = view.InfoHash.Trim().ToLowerInvariant();
        if (!snapshot.Torrents.TryGetValue(normalizedHash, out var torrent)) return view;
        if (torrent.IsMoving ||
            torrent.Category is not (QbittorrentTorrentCategory.Downloading
                                    or QbittorrentTorrentCategory.Stopped
                                    or QbittorrentTorrentCategory.Seeding) ||
            !double.IsFinite(torrent.Progress))
            return view;

        var liveProgress = Math.Clamp(torrent.Progress, 0d, 1d);
        if (view.Progress is { } currentProgress && Math.Abs(currentProgress - liveProgress) <= 1e-6)
            return view;

        return view with { Progress = liveProgress };
    }

    private static bool CanOverlayLiveProgress(DownloadTaskView view) =>
        view.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused &&
        !string.IsNullOrWhiteSpace(view.InfoHash)                                  &&
        !view.IsSelectionTombstone                                                 &&
        !view.IsTorrentMissing                                                     &&
        !view.IsSystemPaused                                                       &&
        view.IsolationState       == DownloadIsolationState.None                   &&
        view.VerificationStep     is null                                          &&
        view.LastObservedCategory != QbittorrentTorrentCategory.Checking;

    private void RecomputeWindowIndices()
    {
        var window = ComputeWindow(OrderedTaskIds, _requestedVisibleStart, _viewportCount, OverscanCount);
        VisibleStartIndex = window.VisibleStart;
        VisibleEndIndex   = window.VisibleEnd;
        MountedStartIndex = window.MountedStart;
        MountedEndIndex   = window.MountedEnd;
        VisibleTaskIds    = SliceIds(OrderedTaskIds, VisibleStartIndex, VisibleEndIndex);
    }

    private static (int VisibleStart, int VisibleEnd, int MountedStart, int MountedEnd) ComputeWindow(
        IReadOnlyList<Guid> orderedIds, int requestedStart, int viewportCount, int overscanCount)
    {
        var total = orderedIds.Count;
        if (total == 0) return (0, 0, 0, 0);

        var visibleStart = Math.Clamp(requestedStart, 0, total - 1);
        var visibleEnd   = Math.Min(total, visibleStart + Math.Max(0, viewportCount));
        var mountedStart = Math.Max(0, visibleStart - Math.Max(0, overscanCount));
        var mountedEnd   = Math.Min(total, visibleEnd + Math.Max(0, overscanCount));
        return (visibleStart, visibleEnd, mountedStart, mountedEnd);
    }

    private Guid[] SliceIds(int start, int end) => SliceIds(OrderedTaskIds, start, end);

    private static Guid[] SliceIds(IReadOnlyList<Guid> source, int start, int end)
    {
        var count = Math.Max(0, end - start);
        if (count == 0) return [];
        var result = new Guid[count];
        for (var i = 0; i < count; i++) result[i] = source[start + i];
        return result;
    }

    private void RebuildMountedTasks()
    {
        var mountedIds = SliceIds(MountedStartIndex, MountedEndIndex);
        if (mountedIds.Length == 0)
        {
            Tasks = [];
            return;
        }

        var items = new List<DownloadTaskView>(mountedIds.Length);
        foreach (var id in mountedIds)
            if (_summaryCache.TryGetValue(id, out var view))
                items.Add(view);
        Tasks = items;
    }

    private void TrimSummaryCacheIfNeeded()
    {
        if (_summaryCache.Count <= 500) return;
        var keep = SliceIds(MountedStartIndex, MountedEndIndex).ToHashSet();
        if (SelectedTaskId is { } selectedId) keep.Add(selectedId);
        foreach (var id in _summaryCache.Keys.Where(id => !keep.Contains(id)).ToArray())
            _summaryCache.Remove(id);
    }

    private void PruneDeletedStateWhenUnfiltered()
    {
        if (HasActiveFilters) return;
        var existing = OrderedTaskIds.ToHashSet();
        foreach (var id in _fileStates.Keys.Where(id => !existing.Contains(id)).ToArray())
            RemoveFileState(id);
        _selectedIds.RemoveWhere(id => !existing.Contains(id));
    }

    private void RemoveFileState(Guid taskId)
    {
        if (!_fileStates.Remove(taskId, out var state)) return;
        state.SyncGeneration++;
        state.PendingRefreshCancellation?.Cancel();
        if (_activeFilePollTaskId == taskId) StopFilePolling();
    }

    private static int FindFirstAnchorIndex(IReadOnlyList<Guid> orderedIds, IReadOnlyList<Guid> anchorIds)
    {
        if (orderedIds.Count == 0 || anchorIds.Count == 0) return -1;
        var indexById = new Dictionary<Guid, int>(orderedIds.Count);
        for (var i = 0; i < orderedIds.Count; i++) indexById.TryAdd(orderedIds[i], i);

        foreach (var anchorId in anchorIds)
            if (indexById.TryGetValue(anchorId, out var index))
                return index;

        return -1;
    }

    private DownloadTaskListFilter BuildFilter() => new()
    {
        SearchText     = _searchText,
        StatusGroups   = _statusFilter,
        Languages      = _languageFilter,
        SortColumn     = SortColumn,
        SortDescending = SortDescending
    };

    /// <summary>
    ///     页面生命周期内只恢复一次已保存的状态筛选：之后由用户勾选维护，
    ///     15 秒自动刷新不回读，避免中途覆盖当前页面的选择。读取失败按全部勾选继续。
    /// </summary>
    private async Task EnsureStatusFilterLoadedAsync(CancellationToken cancellationToken)
    {
        // 并发加载共用同一次读取：后到的加载不能把“读取已开始”当成“已恢复”，
        // 否则它会先用默认全选查一遍，恢复后的勾选要等下一次刷新才反映到列表。
        _statusFilterLoadTask ??= LoadStatusFilterAsync();
        // 用户已经改过勾选，这次读取的结果注定作废，不必等它结束再查询。
        if (_statusFilterTouched) return;
        await _statusFilterLoadTask.WaitAsync(cancellationToken);
    }

    /// <summary>读取已保存筛选并恢复勾选；读取本身不随某次页面加载取消，结果供所有等待者共用。</summary>
    private async Task LoadStatusFilterAsync()
    {
        try
        {
            var stored = await filterStore.GetStatusGroupsAsync();
            // 用户在读取期间改过勾选时以用户选择为准，读取结果作废但不影响其他加载等待这一轮读取。
            if (_statusFilterTouched) return;
            _statusFilter.Clear();
            _statusFilter.UnionWith(stored);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "读取下载任务状态筛选失败，按全部分组显示。");
        }
    }

    /// <summary>整体替换状态筛选勾选：与当前勾选一致时不重复查询。</summary>
    public async Task SetStatusFilterAsync(IEnumerable<DownloadStatusFilterGroup> groups)
    {
        var target = groups.ToHashSet();
        if (target.SetEquals(_statusFilter)) return;
        _statusFilterTouched = true;
        _statusFilter.Clear();
        _statusFilter.UnionWith(target);
        await SaveStatusFilterAsync();
        await ReloadFromTopAsync();
    }

    public Task ToggleStatusFilterAsync(DownloadStatusFilterGroup group)
    {
        var target = new HashSet<DownloadStatusFilterGroup>(_statusFilter);
        if (!target.Remove(group)) target.Add(group);
        return SetStatusFilterAsync(target);
    }

    /// <summary>状态筛选全选：等于不限制状态，也是从全不勾选恢复的入口。</summary>
    public Task SelectAllStatusesAsync() => SetStatusFilterAsync(DownloadStatusFilter.Choices);

    /// <summary>状态筛选全不选：明确的“一个都不显示”，列表随即为空。</summary>
    public Task DeselectAllStatusesAsync() => SetStatusFilterAsync([]);

    /// <summary>保存勾选供刷新后恢复；按调用顺序串行写入，保存失败只记录日志，当前页面仍按已勾选过滤。</summary>
    private async Task SaveStatusFilterAsync()
    {
        // 先快照当前勾选：串行化的是写入次数与顺序，后一次保存必须仍是调用时的选择。
        var groups = _statusFilter.ToHashSet();
        var save   = SaveStatusFilterCoreAsync(_statusFilterSave, groups);
        _statusFilterSave = save;
        await save;
    }

    private async Task SaveStatusFilterCoreAsync(Task previousSave, HashSet<DownloadStatusFilterGroup> groups)
    {
        // 连续勾选可能并发发出多次保存：前一次写完再写，避免较早的选择晚于较新的选择落库。
        await previousSave;
        try
        {
            await filterStore.SaveStatusGroupsAsync(groups);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "保存下载任务状态筛选失败，本次选择仅在当前页面生效。");
        }
    }

    public async Task ToggleLanguageFilterAsync(string language)
    {
        if (!_languageFilter.Remove(language)) _languageFilter.Add(language);
        await ReloadFromTopAsync();
    }

    /// <summary>清空搜索与全部多选筛选，状态回到全选（不限制），回到顶部全量展示。</summary>
    public async Task ClearFiltersAsync()
    {
        var resetStatus = StatusFilterActive;
        _searchText          = string.Empty;
        _statusFilterTouched = true;
        _statusFilter.Clear();
        _statusFilter.UnionWith(DownloadStatusFilter.Choices);
        _languageFilter.Clear();
        // 状态筛选同样持久化：清除后写回全选，刷新不会恢复成旧的收窄选择。
        if (resetStatus) await SaveStatusFilterAsync();
        await ReloadFromTopAsync();
    }

    private async Task ReloadFromTopAsync()
    {
        _requestedVisibleStart = 0;
        _pendingScrollTopPx    = 0;
        CancelSearchDebounce();
        await LoadSafelyAsync(CancellationToken.None);
    }

    /// <summary>搜索输入防抖：停顿后重新查询；新输入取消未触发的旧定时与进行中的查询（配合请求序号双保险）。</summary>
    private void ScheduleSearchReload()
    {
        _requestedVisibleStart = 0;
        _pendingScrollTopPx    = 0;
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
        if (state.Loading || state.Busy || state.Refreshing) return;
        state.Loading            = true;
        state.Error              = null;
        state.SilentRefreshError = null;
        var stateGen = state.SyncGeneration;
        try
        {
            var details = await RequireFileService().ListAsync(taskId, cancellationToken);
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
            {
                state.Details                     = details;
                state.FileOperationPending        = details.HasPendingFileOperation;
                state.PendingFileWaitPaused       = false;
                state.ConfirmationBudgetExhausted = false;
                foreach (var draft in state.Drafts.Values) draft.InvalidatePreview();
                StartPendingFileRefresh(taskId, state);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件列表加载失败。", taskId);
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
                state.Error = exception.Message;
        }
        finally
        {
            state.Loading = false;
        }
    }

    /// <summary>
    ///     控制文件面板的 5 秒串行自动同步：展开且处于文件列表 Tab、页面可见时启动（先立即同步再每 5 秒轮询）；
    ///     收起、切 Tab、切换任务或页面隐藏时立即取消旧轮询并推进代次，丢弃迟到响应。
    /// </summary>
    public void SetFilePanelActive(bool expanded, bool isFilesTab, Guid? taskId, bool isPageHidden = false)
    {
        _isPageHidden = isPageHidden;
        if (!expanded || !isFilesTab || isPageHidden || taskId is not { } targetId)
        {
            StopFilePolling();
            return;
        }

        if (_activeFilePollTaskId == targetId && _filePollCts is { IsCancellationRequested: false }) return;

        StopFilePolling();
        _activeFilePollTaskId = targetId;
        var cts        = _filePollCts = new CancellationTokenSource();
        var generation = _filePollGeneration;
        _ = PollFilesLoopAsync(targetId, generation, cts.Token);
    }

    public void StopFilePolling()
    {
        _activeFilePollTaskId = null;
        _filePollGeneration++;
        _filePollCts?.Cancel();
        _filePollCts?.Dispose();
        _filePollCts = null;
    }

    private async Task PollFilesLoopAsync(Guid taskId, long generation, CancellationToken cancellationToken)
    {
        try
        {
            await ActivateFilesTabAsync(taskId, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FileAutoRefreshInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested ||
                    _activeFilePollTaskId != taskId           ||
                    _filePollGeneration   != generation)
                    return;
                await AutoRefreshFilesAsync(taskId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件自动轮询循环异常。", taskId);
        }
    }

    /// <summary>
    ///     切入文件列表 Tab 或在该 Tab 下切换选中任务时调用：若尚无本地清单先调 ListAsync 秒出展示，
    ///     若允许远端同步则随后静默触发一次远端同步；同一任务 ID 在途读取合并。
    /// </summary>
    public Task ActivateFilesTabAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        RunCoalescedFileSyncAsync(taskId, true, cancellationToken);

    /// <summary>
    ///     5 秒定时静默同步（含已完成且有明确关联的任务与预算耗尽后的待确认回退观察）：
    ///     同一任务 ID 在途读取合并，上一次完成后再安排下一次。
    /// </summary>
    public Task AutoRefreshFilesAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        RunCoalescedFileSyncAsync(taskId, false, cancellationToken);

    private async Task RunCoalescedFileSyncAsync(
        Guid taskId, bool isActivation, CancellationToken cancellationToken)
    {
        if (_inFlightSyncs.TryGetValue(taskId, out var inFlight) && !inFlight.IsCompleted)
        {
            await inFlight.WaitAsync(cancellationToken);
            return;
        }

        var syncTask = RunFileSyncCoreAsync(taskId, isActivation, cancellationToken);
        _inFlightSyncs[taskId] = syncTask;
        try
        {
            await syncTask;
        }
        finally
        {
            if (_inFlightSyncs.GetValueOrDefault(taskId) == syncTask) _inFlightSyncs.Remove(taskId);
        }
    }

    private async Task RunFileSyncCoreAsync(
        Guid taskId, bool isActivation, CancellationToken cancellationToken)
    {
        var panelGen        = _filePollGeneration;
        var fromActivePanel = _activeFilePollTaskId == taskId;
        var task            = await FindTaskOrDetailAsync(taskId, cancellationToken);
        if (task is null) return;
        if (IsStaleFileSyncTarget(taskId, fromActivePanel, panelGen)) return;

        var state = FileState(taskId);
        if (ShouldSkipAutoFileSync(state)) return;

        if (state.Details is null)
        {
            await LoadFilesAsync(taskId, cancellationToken);
            ResultsChanged?.Invoke();
            if (IsStaleFileSyncTarget(taskId, fromActivePanel, panelGen)) return;
            task = await FindTaskOrDetailAsync(taskId, cancellationToken);
            if (task is null || !IsCurrentFileState(taskId, state) || state.Details is null) return;
            if (ShouldSkipAutoFileSync(state)) return;
        }

        if (isActivation && !CanSyncFilesRemotely(task) && !state.FileOperationPending) return;
        if (!isActivation && !CanSyncFilesRemotely(task) && !state.FileOperationPending) return;

        await RefreshFilesSilentlyAsync(taskId, state, fromActivePanel, panelGen, cancellationToken);
    }

    /// <summary>刷新文件：实时查询下载器并按原始种子核对新路径，与展开面板的 ListAsync 加载分开。</summary>
    public Task RefreshFilesAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var state = FileState(taskId);
        // 待确认期间手动刷新会被服务端的 HasPending 拒绝；入口先锁住，避免误以为刷新成功。
        if (state.FileOperationPending || state.Loading || state.Busy || state.Refreshing) return Task.CompletedTask;
        return RefreshFilesCoreAsync(taskId, state, cancellationToken);
    }

    private async Task RefreshFilesCoreAsync(
        Guid taskId, DownloadTaskFilesState state, CancellationToken cancellationToken)
    {
        state.Loading            = true;
        state.Error              = null;
        state.SilentRefreshError = null;
        var stateGen = state.SyncGeneration;
        try
        {
            var details = await RequireFileService().RefreshAsync(taskId, cancellationToken);
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
            {
                state.Details                     = details;
                state.FileOperationPending        = details.HasPendingFileOperation;
                state.PendingFileWaitPaused       = false;
                state.ConfirmationBudgetExhausted = false;
                state.LastSyncedAtUtc             = _timeProvider.GetUtcNow();
                // 刷新后来源路径可能变化，旧预览基于旧路径生成，必须失效。
                foreach (var draft in state.Drafts.Values) draft.InvalidatePreview();
                StartPendingFileRefresh(taskId, state);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TorrentMissingException)
        {
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
                state.Error = "qBittorrent 中已找不到该种子";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件刷新失败。", taskId);
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
                state.Error = exception.Message;
        }
        finally
        {
            state.Loading = false;
        }
    }

    private async Task RefreshFilesSilentlyAsync(
        Guid taskId, DownloadTaskFilesState state, bool fromActivePanel, long panelGen,
        CancellationToken cancellationToken)
    {
        var stateGen        = state.SyncGeneration;
        var observingPending = state.FileOperationPending;
        state.Refreshing         = true;
        state.SilentRefreshError = null;
        ResultsChanged?.Invoke();
        try
        {
            var details = observingPending
                ? await RequireFileService().ObservePendingAsync(taskId, cancellationToken)
                : await RequireFileService().RefreshAsync(taskId, cancellationToken);
            if (!IsCurrentFileState(taskId, state) || stateGen != state.SyncGeneration ||
                IsStaleFileSyncTarget(taskId, fromActivePanel, panelGen))
                return;

            // 同步期间用户可能开始编辑或触发了其他非待确认文件操作；绝不覆盖或冲掉普通编辑草稿。
            if (!observingPending && ShouldSkipApplyingSilentRefresh(state)) return;

            state.Details               = details;
            state.FileOperationPending  = details.HasPendingFileOperation;
            state.PendingFileWaitPaused = false;
            state.Error                 = null;
            state.LastSyncedAtUtc       = _timeProvider.GetUtcNow();

            if (observingPending && !details.HasPendingFileOperation)
            {
                state.ConfirmationBudgetExhausted = false;
                foreach (var draft in state.Drafts.Values) draft.InvalidatePreview();
                state.Message = "文件操作已确认，路径已自动刷新。";
            }
            else if (details.HasPendingFileOperation)
            {
                StartPendingFileRefresh(taskId, state);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DownloadLayoutConfirmationException exception)
        {
            if (!IsCurrentFileState(taskId, state) || stateGen != state.SyncGeneration ||
                IsStaleFileSyncTarget(taskId, fromActivePanel, panelGen))
                return;
            state.FileOperationPending  = true;
            state.PendingFileWaitPaused = false;
            state.Error                 = exception.Message;
        }
        catch (TorrentMissingException)
        {
            if (!IsCurrentFileState(taskId, state) || stateGen != state.SyncGeneration ||
                IsStaleFileSyncTarget(taskId, fromActivePanel, panelGen))
                return;
            const string missingMessage = "qBittorrent 中已找不到该种子";
            if (state.Details is null)
                state.Error = missingMessage;
            else
                state.SilentRefreshError = missingMessage;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件后台静默刷新失败。", taskId);
            if (!IsCurrentFileState(taskId, state) || stateGen != state.SyncGeneration ||
                IsStaleFileSyncTarget(taskId, fromActivePanel, panelGen))
                return;
            if (state.Details is null)
                state.Error = exception.Message;
            else
                state.SilentRefreshError = $"后台同步失败：{exception.Message}";
        }
        finally
        {
            state.Refreshing = false;
            ResultsChanged?.Invoke();
        }
    }

    private async Task<DownloadTaskView?> FindTaskOrDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (GetTaskView(taskId) is { } cached) return cached;
        return await taskQueries.GetDetailAsync(taskId, cancellationToken);
    }

    private bool IsStaleFileSyncTarget(Guid taskId, bool fromActivePanel, long panelGen) =>
        (fromActivePanel && (_activeFilePollTaskId != taskId || _filePollGeneration != panelGen)) ||
        (!fromActivePanel && _activeFilePollTaskId is { } activeId && activeId != taskId);

    private static bool ShouldSkipAutoFileSync(DownloadTaskFilesState state) =>
        state.Loading                                                    ||
        state.Busy                                                       ||
        state.Refreshing                                                 ||
        state.PendingFileWaitPaused                                      ||
        state.PendingRefreshCancellation is { IsCancellationRequested: false } ||
        (!state.FileOperationPending && state.Drafts.Count > 0);

    private static bool ShouldSkipApplyingSilentRefresh(DownloadTaskFilesState state) =>
        state.Drafts.Count > 0 ||
        state.Loading          ||
        state.Busy             ||
        state.FileOperationPending;

    private static bool CanSyncFilesRemotely(DownloadTaskView task) =>
        !task.IsTorrentMissing &&
        !task.IsSelectionTombstone &&
        task.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused or DownloadTaskStatus.Completed;

    public void BeginFileEdit(Guid taskId, int fileIndex)
    {
        var state = FileState(taskId);
        if (state.Busy || state.Loading || state.FileOperationPending || state.Drafts.ContainsKey(fileIndex)) return;
        var file = state.Details?.Files.SingleOrDefault(file => file.Index == fileIndex);
        if (file is null) return;
        state.Drafts[fileIndex]  = new DownloadFileEditDraft(file);
        state.Error              = null;
        state.SilentRefreshError = null;
        state.Message            = null;
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
        if (state.Busy || state.Loading || state.FileOperationPending ||
            !state.Drafts.TryGetValue(fileIndex, out var draft)) return;
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
        catch (TorrentMissingException)
        {
            // 种子在下载器中已丢失：按固定文案提示重新下载，不记警告日志。
            if (IsCurrentFileState(taskId, state)) state.Error = "qBittorrent 中已找不到该种子";
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
        if (state.Busy || state.Loading || state.FileOperationPending ||
            !state.Drafts.TryGetValue(fileIndex, out var draft) || !draft.CanApply) return;
        var request = draft.Request;
        state.Busy               = true;
        state.Error              = null;
        state.SilentRefreshError = null;
        state.Message            = null;
        try
        {
            var details = await RequireFileService().ApplyAsync(taskId, fileIndex, request, cancellationToken,
                                                                draft.Preview?.FileTemplate);
            if (!IsCurrentFileState(taskId, state)) return;
            state.PendingRefreshCancellation?.Cancel();
            state.PendingRefreshCancellation = null;
            state.Details                     = details;
            state.FileOperationPending        = details.HasPendingFileOperation;
            state.PendingFileWaitPaused       = false;
            state.ConfirmationBudgetExhausted = false;
            if (!state.FileOperationPending) state.LastSyncedAtUtc = _timeProvider.GetUtcNow();
            state.Drafts.Remove(fileIndex);
            // 根目录迁移会影响其他文件的完整路径，需要重新预览，但保留其编辑值。
            foreach (var otherDraft in state.Drafts.Values) otherDraft.InvalidatePreview();
            state.Message = state.FileOperationPending
                ? "修改已提交，正在等待下载器确认，确认后会自动刷新。"
                : "修改已提交，文件位置以下载器确认结果为准。";
            StartPendingFileRefresh(taskId, state);
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

    private void StartPendingFileRefresh(Guid taskId, DownloadTaskFilesState state)
    {
        if (!state.FileOperationPending            ||
            state.ConfirmationBudgetExhausted      ||
            state.PendingRefreshCancellation is { IsCancellationRequested: false })
            return;
        _ = WatchPendingFileOperationAsync(taskId, state);
    }

    /// <summary>自动等待暂停后的显式恢复：仍待确认时才重新轮询，不绕过服务端的确认闸门。</summary>
    public void ContinueWaitingForFileOperation(Guid taskId)
    {
        var state = FileState(taskId);
        if (!state.FileOperationPending || !state.PendingFileWaitPaused) return;
        state.PendingFileWaitPaused       = false;
        state.ConfirmationBudgetExhausted = false;
        state.Error                       = null;
        state.Message                     = "修改已提交，正在等待下载器确认，确认后会自动刷新。";
        StartPendingFileRefresh(taskId, state);
        ResultsChanged?.Invoke();
    }

    /// <summary>改名/移动待确认的约 1 秒短间隔观察循环：直接消费 ObservePendingAsync 的确认结果；预算耗尽后回退 5 秒观察。</summary>
    private async Task WatchPendingFileOperationAsync(Guid taskId, DownloadTaskFilesState state)
    {
        var cancellation = state.PendingRefreshCancellation = new CancellationTokenSource();
        var stateGen     = state.SyncGeneration;
        try
        {
            for (var attempt = 0; attempt < PendingFileRefreshAttempts; attempt++)
            {
                await Task.Delay(PendingFileRefreshInterval, cancellation.Token);
                if (!IsCurrentFileState(taskId, state) || stateGen != state.SyncGeneration ||
                    !state.FileOperationPending)
                    return;

                var details = await RequireFileService().ObservePendingAsync(taskId, cancellation.Token);
                if (!IsCurrentFileState(taskId, state) || stateGen != state.SyncGeneration ||
                    !state.FileOperationPending)
                    return;

                state.Details         = details;
                state.LastSyncedAtUtc = _timeProvider.GetUtcNow();
                if (details.HasPendingFileOperation)
                {
                    ResultsChanged?.Invoke();
                    continue;
                }

                // 后端确认完整计划后，直接采用返回的新布局并开放再次编辑与刷新；保留既有草稿并失效其旧预览。
                state.FileOperationPending        = false;
                state.PendingFileWaitPaused       = false;
                state.ConfirmationBudgetExhausted = false;
                state.Error                       = null;
                state.SilentRefreshError          = null;
                foreach (var draft in state.Drafts.Values) draft.InvalidatePreview();
                state.Message = "文件操作已确认，路径已自动刷新。";
                ResultsChanged?.Invoke();
                return;
            }

            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration && state.FileOperationPending)
                PauseFileOperationWait(state,
                    "文件操作仍未被下载器确认，自动等待已暂停；请检查下载器后点击“继续等待确认”。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (DownloadLayoutConfirmationException exception)
        {
            // 补发预算耗尽：展示待核对原因并退出 1 秒短间隔，交由 5 秒自动同步继续调用 ObservePendingAsync 观察。
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
            {
                state.FileOperationPending        = true;
                state.PendingFileWaitPaused       = false;
                state.ConfirmationBudgetExhausted = true;
                state.Error                       = exception.Message;
                state.Message                     = null;
                ResultsChanged?.Invoke();
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 文件操作确认等待失败。", taskId);
            if (IsCurrentFileState(taskId, state) && stateGen == state.SyncGeneration)
                PauseFileOperationWait(state,
                    $"等待下载器确认失败：{exception.Message}。自动等待已暂停，请点击“继续等待确认”重试。");
        }
        finally
        {
            if (state.PendingRefreshCancellation == cancellation) state.PendingRefreshCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>未确认不是已确认：保持待处理状态锁住刷新与编辑，只暂停自动等待并给出恢复入口。</summary>
    private void PauseFileOperationWait(DownloadTaskFilesState state, string error)
    {
        state.FileOperationPending = true;
        state.PendingFileWaitPaused = true;
        state.Error                 = error;
        state.Message               = null;
        ResultsChanged?.Invoke();
    }

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
        var visibleIds = VisibleTaskIds;
        if (visibleIds.Count == 0) return;
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
            foreach (var id in ids)
            {
                RemoveFileState(id);
                if (SelectedTaskId == id)
                {
                    SelectedTaskId = null;
                    SelectedTask   = null;
                }
            }
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

        await RefreshFallbackAsync(cancellationToken);
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
        await RunOperationAsync(taskId, () => downloadService
                                   .DeleteAsync(taskId, deleteFromDownloader, deleteFiles, cancellationToken),
                                "任务已删除。", cancellationToken);
        // 单删成功后同步收敛选择集与文件状态，批量计数不再包含已删任务。
        if (Succeeded)
        {
            _selectedIds.Remove(taskId);
            RemoveFileState(taskId);
            if (SelectedTaskId == taskId)
            {
                SelectedTaskId = null;
                SelectedTask   = null;
            }
        }
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
                await RunCoreAsync(() => intakeService.CreateFromUploadAsync(buffer.ToArray(), null, UploadSubjectId,
                                                                             cancellationToken),
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

    /// <summary>接收统一登记组件的反馈并按需刷新任务列表；成功时不展示提示框，仅清空旧提示。</summary>
    public async Task ApplyIntakeFeedbackAsync(
        bool succeeded, string message, bool shouldReload, CancellationToken cancellationToken = default)
    {
        Succeeded = succeeded;
        Message   = succeeded ? null : message;
        if (shouldReload) await RefreshFallbackAsync(cancellationToken);
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

    /// <summary>种子丢失后重新下载：沿用原决策与命名快照重新提交，完成记录与逐集归属不变。</summary>
    public async Task RedownloadAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.RetryAsync(taskId, cancellationToken),
                                "已重新提交下载，等待下载器确认。", cancellationToken);
    }

    /// <summary>重新校验：仅作用于校验未确认的任务；保留候选与文件，只重置本轮证据并重发 recheck。</summary>
    public async Task ReverifyAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.ReverifyAsync(taskId, cancellationToken),
                                "已重新发起校验，等待下载器确认。", cancellationToken);
    }

    /// <summary>
    ///     人工恢复启动：完整任务恢复做种、部分完成继续下载。响应丢失先观察、有界重试，
    ///     不盲目重发；不重新校验、不隔离、不重新下载。
    /// </summary>
    public async Task ResumeRunAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.ResumeRunAsync(taskId, cancellationToken),
                                "恢复请求已发出，等待下载器确认。", cancellationToken);
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

        await RefreshFallbackAsync(cancellationToken);
    }
}

/// <summary>仅属于页面生命周期的文件详情与编辑草稿。</summary>
public sealed class DownloadTaskFilesState
{
    public bool            Expanded             { get; internal set; }
    public bool            Loading              { get; internal set; }
    public bool            Refreshing           { get; internal set; }
    public bool            Busy                 { get; internal set; }
    public string?         Error                { get; internal set; }
    public string?         SilentRefreshError   { get; internal set; }
    public string?         Message              { get; internal set; }
    public bool            FileOperationPending { get; internal set; }
    public DateTimeOffset? LastSyncedAtUtc      { get; internal set; }

    /// <summary>超时或轮询失败后自动等待暂停；仍待确认时界面提供“继续等待确认”入口。</summary>
    public bool PendingFileWaitPaused { get; internal set; }

    internal bool                     ConfirmationBudgetExhausted { get; set; }
    internal long                     SyncGeneration              { get; set; }
    internal CancellationTokenSource? PendingRefreshCancellation { get; set; }

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
