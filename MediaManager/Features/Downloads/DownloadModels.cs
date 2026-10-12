namespace MediaManager.Features.Downloads;

/// <summary>只记录恢复下一步所需的状态；不建通用状态机。</summary>
public enum DownloadTaskStatus
{
    /// <summary>已登记意图，等待提交到下载器。</summary>
    PendingSubmit,

    /// <summary>已发起提交但结果未确认（响应丢失或下载器暂时错误），等待按 Hash 对账。</summary>
    Submitting,

    Downloading,

    /// <summary>人工暂停（页面或下载器内暂停），对账绝不自动恢复。</summary>
    Paused,

    Completed,

    Failed,

    /// <summary>下载器中已找不到该任务（可能在下载器内被手动删除）。</summary>
    Lost,

    /// <summary>被更新的资源版本取代，旧任务与文件默认保留。</summary>
    Replaced
}

public static class DownloadTaskStatusExtensions
{
    public static bool IsActive(this DownloadTaskStatus status) =>
        status is DownloadTaskStatus.PendingSubmit
               or DownloadTaskStatus.Submitting or DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused;

    public static bool IsFinal(this DownloadTaskStatus status) =>
        status is DownloadTaskStatus.Completed
               or DownloadTaskStatus.Failed or DownloadTaskStatus.Lost or DownloadTaskStatus.Replaced;
}

/// <summary>下载任务列表投影。</summary>
public sealed record DownloadTaskView
{
    public required Guid               Id               { get; init; }
    public required Guid?              FeedItemId       { get; init; }
    public          string?            TorrentName      { get; init; }
    public          int?               SubjectId        { get; init; }
    public          string?            SubjectName      { get; init; }
    public          decimal?           Episode          { get; init; }
    public          int                Version          { get; init; }
    public          string?            SubtitleGroup    { get; init; }
    public          string?            SubtitleLanguage { get; init; }
    public required DownloadTaskStatus Status           { get; init; }
    public          bool               IsUserPaused     { get; init; }
    public          double?            Progress         { get; init; }
    public          long?              TotalSize        { get; init; }
    public          string?            NamingSnapshot   { get; init; }
    public          string?            SiteSource       { get; init; }
    public          string?            FailedStep       { get; init; }
    public          string?            Error            { get; init; }
    public required DateTimeOffset     CreatedAtUtc     { get; init; }
    public          DateTimeOffset?    SubmittedAtUtc   { get; init; }
    public          DateTimeOffset?    CompletedAtUtc   { get; init; }
    public          Guid?              ReplacesTaskId   { get; init; }
    public          string?            ReplacesNaming   { get; init; }
    public          Guid?              ReplacedByTaskId { get; init; }
    public          string?            ReplacedByNaming { get; init; }
}

/// <summary>RSS 链接下载中单个条目的登记结果。</summary>
public enum RssDownloadItemOutcome
{
    /// <summary>已登记为新的下载任务。</summary>
    Created,

    /// <summary>同 hash 任务已存在（进行中或已完成），跳过。</summary>
    Duplicate,

    /// <summary>同集择优落选：批内同集分组未选中，或统一裁决输给既有更优任务。</summary>
    Superseded,

    /// <summary>登记失败（种子下载失败、种子无效等）。</summary>
    Failed
}

/// <summary>RSS 链接下载中单个条目的处理明细。</summary>
public sealed record RssDownloadItemResult(
    string                 Title,
    RssDownloadItemOutcome Outcome,
    Guid?                  TaskId,
    string?                Error);

/// <summary>RSS 链接下载的整批结果：逐条尽力登记，汇总各类计数。</summary>
public sealed record RssDownloadResult(
    int                                  CreatedCount,
    int                                  DuplicateCount,
    int                                  SupersededCount,
    int                                  FailedCount,
    IReadOnlyList<RssDownloadItemResult> Items)
{
    public bool HasChanges => CreatedCount > 0 || DuplicateCount > 0;
}

/// <summary>批量删除面板预检结果：现存任务数、其中在下载器里能找到种子的数量、下载器状态是否成功确认。</summary>
public sealed record BulkDeletePrecheck(
    int  TaskCount,
    int  TorrentsInDownloader,
    bool DownloaderChecked);
