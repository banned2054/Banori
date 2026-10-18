using System.Text.Json;

namespace Banori.Features.Downloads;

/// <summary>只记录恢复下一步所需的状态；不建通用工作流引擎。</summary>
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

/// <summary>
///     新增任务的准备阶段：种子以停止状态添加后，逐步应用文件选择与命名计划，全部确认后才允许启动下载。
///     阶段只用于解释推进位置与恢复边界，实际推进始终以下载器真实状态与计划比对为准。
/// </summary>
public enum DownloadPreparationStage
{
    /// <summary>无需准备：存量任务或接管时已处于可运行状态的种子。</summary>
    None,

    /// <summary>计划已保存（意图先于请求落库），等待应用文件选择与命名。</summary>
    Planned,

    /// <summary>文件选择或命名变更已部分发出，等待与下载器实际状态核对一致。</summary>
    Applying,

    /// <summary>
    ///     复用校验中：受控映射完成后已发出 recheck（或正在补发），等待当前轮次观察到 Checking 且结束并
    ///     取得稳定文件结果。此阶段绝不启动下载——候选路径上的共享数据未经证实不得写入。
    /// </summary>
    AwaitingVerification,

    /// <summary>准备完成：选中文件与路径已确认，允许启动下载。</summary>
    ReadyToRun
}

/// <summary>隔离（放弃复用、转为独立下载）的多步迁移状态；每步以下载器真实状态确认后推进，绝不凭请求成功推进。</summary>
public enum DownloadIsolationState
{
    /// <summary>不在隔离流程中。</summary>
    None = 0,

    /// <summary>待删除：确认种子已停止后以保留文件方式删除（候选路径上的物理数据属于占用方，绝不连带删除）。</summary>
    Deleting,

    /// <summary>待重加：确认 hash 已消失后，以停止状态在任务专属根重新添加。</summary>
    Readding,

    /// <summary>待确认：核对重加后的文件布局与原始身份一致，清理旧改名日志并回到准备流程。</summary>
    Confirming
}

/// <summary>复用校验意图的持久化子步骤：先落库子步骤与次数，再发对应远端请求。</summary>
public static class DownloadVerificationStep
{
    /// <summary>受控映射中：候选文件逐个改名到候选路径（经改名日志与验证映射许可），尚未请求校验。</summary>
    public const string Mapping = "Mapping";

    /// <summary>校验请求已按本轮落库并发出，尚未观察到 Checking。</summary>
    public const string Requested = "Requested";

    /// <summary>本轮已观察到 Checking，等待其结束并读取稳定结果。</summary>
    public const string Observing = "Observing";

    /// <summary>复用结果与启动意图已落库，等待确认 Resume；重启仍须核对同一布局。</summary>
    public const string ReadyToRun = "ReadyToRun";
}

/// <summary>逐文件校验结果。仅描述「该次映射未通过或不能确认」，不证明字节内容不同（可能是边界分片、配套缺失或映射问题）。</summary>
public static class DownloadFileVerificationOutcomes
{
    /// <summary>校验通过：该文件与占用方确认复用同一份数据（交叉做种），凭此结果允许免迁让等路径例外。</summary>
    public const string ReusedVerified = "ReusedVerified";

    /// <summary>该次映射未通过或不能确认：对应目标路径禁止写入，须独立下载。</summary>
    public const string ReuseUnverified = "ReuseUnverified";
}

/// <summary>复用校验的逐文件映射快照：结果绑定规范化完整路径、保存根与文件选择，任一变化即作废。</summary>
public sealed record DownloadVerificationMapping(
    int     FileIndex,
    string  VerificationPath,
    long    Size,
    bool    Selected,
    string? TargetPath,
    bool    IsCandidate);

/// <summary>
///     复用校验意图：候选根添加与受控映射的完整计划。每个远端请求（改名、recheck、暂停）之前先落库
///     对应子步骤、轮次与次数；新一轮请求重置本轮 ObservedChecking，绝不沿用上一轮证据。
/// </summary>
public sealed record DownloadVerificationIntent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>校验轮次；每次重新进入校验递增，用于区分本轮与历史证据。</summary>
    public int Round { get; init; }

    public string Step { get; init; } = DownloadVerificationStep.Mapping;

    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>本轮 recheck 请求时间；null 表示尚未发出请求（仍在映射阶段）。</summary>
    public DateTimeOffset? RecheckRequestedAtUtc { get; init; }

    /// <summary>本轮等待观察到 Checking 的截止时间；超时按未确认处理，不允许据此登记复用。</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>累计 recheck 请求次数（跨轮次累计，用于有界重发的终止判断）。</summary>
    public int IssuedCount { get; init; }

    /// <summary>本轮是否已观察到 Checking；新一轮请求时重置为 false。</summary>
    public bool ObservedChecking { get; init; }

    /// <summary>校验期对无法解释的运行状态已发出暂停的次数；超过上限转待核对。</summary>
    public int PauseAttempts { get; init; }

    /// <summary>本次校验添加所用的候选根（保存根）。</summary>
    public string CandidateRoot { get; init; } = string.Empty;

    /// <summary>逐文件映射快照：文件在候选根下的规范化完整路径、大小与选择状态。</summary>
    public IReadOnlyList<DownloadVerificationMapping> Files { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DownloadVerificationIntent? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DownloadVerificationIntent>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
///     归档受阻记录：仅记录目标占用、共享引用或布局冲突；解决冲突后可重新执行同一归档链。
/// </summary>
public sealed record DownloadArchiveReview
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>待核对原因（展示给用户）。</summary>
    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DownloadArchiveReview? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DownloadArchiveReview>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
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

    /// <summary>系统准备暂停：停止添加后文件选择与命名尚未确认，下载还未启动。</summary>
    public bool IsSystemPaused { get; init; }

    public          double?         Progress         { get; init; }
    public          long?           TotalSize        { get; init; }
    public          string?         NamingSnapshot   { get; init; }
    public          string?         SiteSource       { get; init; }
    public          string?         FailedStep       { get; init; }
    public          string?         Error            { get; init; }
    public required DateTimeOffset  CreatedAtUtc     { get; init; }
    public          DateTimeOffset? SubmittedAtUtc   { get; init; }
    public          DateTimeOffset? CompletedAtUtc   { get; init; }
    public          Guid?           ReplacesTaskId   { get; init; }
    public          string?         ReplacesNaming   { get; init; }
    public          Guid?           ReplacedByTaskId { get; init; }
    public          string?         ReplacedByNaming { get; init; }

    public bool ShowPendingReplacement => ReplacesNaming is not null &&
                                          Status is not (DownloadTaskStatus.Completed or DownloadTaskStatus.Replaced);

    /// <summary>准备阶段解释（含复用校验的等待位置）；存量任务为 None。</summary>
    public DownloadPreparationStage PreparationStage { get; init; }

    /// <summary>归档受阻原因：文件已完成，但目标占用或共享布局尚未解决。</summary>
    public string? ArchiveReviewReason { get; init; }
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
