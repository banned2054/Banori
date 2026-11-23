using Banori.Core.Rules;
using Banori.Integrations.Qbittorrent;
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

/// <summary>
///     界面展示用的工作阶段：从持久状态（任务状态、暂停标志、准备/隔离阶段与校验意图摘要）派生，
///     解释任务当前推进位置与等待依据。只用于展示解释，不参与状态机推进，也不是新的持久化状态。
/// </summary>
public enum DownloadDisplayPhase
{
    /// <summary>已登记意图，等待提交到下载器。</summary>
    WaitingSubmit,

    /// <summary>添加请求已发出（或响应丢失），等待按 Hash 观察到种子与文件清单。</summary>
    WaitingAddConfirm,

    /// <summary>准备暂停但无法归入更细阶段（存量行或阶段字段缺失时的保守展示）。</summary>
    Preparing,

    /// <summary>停止添加后正在应用并核对文件选择、命名与移动。</summary>
    PreparingLayout,

    /// <summary>本轮 recheck 已发出，尚未观察到可归属的 Checking。</summary>
    WaitingCheckStart,

    /// <summary>本轮已观察到 Checking，校验仍在进行。</summary>
    Checking,

    /// <summary>校验已结束，正在跨采样间隔核对逐文件结果的一致性。</summary>
    WaitingResultStable,

    /// <summary>已发出停止请求（到期退出或保护共享数据），等待在下载器端确认生效。</summary>
    WaitingStopConfirm,

    /// <summary>准备完成（复用结果或布局已确认），等待启动确认。</summary>
    WaitingStartConfirm,

    /// <summary>隔离迁移中：删除候选种子并在任务专属根重加为独立下载。</summary>
    IsolationMigrating,

    /// <summary>内容已完成，归档移动或改名尚未全部确认。</summary>
    Archiving,

    /// <summary>下载器内真实下载或做种运行中。</summary>
    Downloading,

    /// <summary>通过本应用页面暂停。</summary>
    PausedManually,

    /// <summary>在下载器端观察到的停止（人工或分享限制等，原因无法确认）。</summary>
    PausedByDownloader,

    /// <summary>暂停但无人工标志（正常不应出现，保守展示）。</summary>
    Paused,

    /// <summary>复用校验未确认：请求可能未送达或未被观察到，结果无法确认；文件保持原样，可重新校验。</summary>
    VerificationUnconfirmed,

    Completed,

    Failed,

    Lost,

    Replaced
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

    /// <summary>
    ///     校验未确认：已多次请求仍未观察到校验阶段、请求连续发送失败、停止后证据不可用或结果长期无法稳定。
    ///     任务保持暂停、不隔离、不删数据；保留候选与意图，等待人工「重新校验」。
    /// </summary>
    public const string Unconfirmed = "Unconfirmed";
}

/// <summary>recheck 请求的发送状态：用于区分「请求可能未被接收」与「已发出但结果未观察」。</summary>
public static class DownloadVerificationRequestState
{
    /// <summary>尚未发出（人工重新校验后等待下一轮对账）。</summary>
    public const string NotSent = "NotSent";

    /// <summary>请求即将发出或发送结果未知：不能据此断言未发送。</summary>
    public const string Unknown = "Unknown";

    /// <summary>请求已成功返回；是否已生效仍由后续快照观察确认。</summary>
    public const string Sent = "Sent";

    /// <summary>请求抛出下载器/网络异常：请求可能已被接收，不能断言未发送。</summary>
    public const string SendFailed = "SendFailed";
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

    /// <summary>本轮 recheck 请求时间；null 表示尚未发出请求（仍在映射阶段），或本轮证据已作废、等待重新发起。</summary>
    public DateTimeOffset? RecheckRequestedAtUtc { get; init; }

    /// <summary>等待观察到 Checking 的截止时间（校验启动窗口）；超时允许有界补发，超时后出现的 Checking 仍可归属本轮。证据作废时一并清空。</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>首次观察到移动的时间；移动等待期限从此独立起算，前面校验的耗时不算入，重复快照不重置。null 表示当前无移动等待。</summary>
    public DateTimeOffset? MovingObservedAtUtc { get; init; }

    /// <summary>累计 recheck 请求次数（跨轮次累计，用于有界重发的终止判断）。</summary>
    public int IssuedCount { get; init; }

    /// <summary>本轮是否已观察到 Checking；新一轮请求时重置为 false。</summary>
    public bool ObservedChecking { get; init; }

    /// <summary>
    ///     本轮请求的发送状态（见 <see cref="DownloadVerificationRequestState" />）。旧意图为 null 时按旧语义处理；
    ///     SendFailed 不消耗 IssuedCount，Unknown 不能断言请求未发送。
    /// </summary>
    public string? RequestState { get; init; }

    /// <summary>连续发送失败次数（成功发出后清零）；达到上限转为校验未确认态。</summary>
    public int SendFailureCount { get; init; }

    /// <summary>最近一次发送失败的异常信息，仅用于诊断，不据此断言请求未发送。</summary>
    public string? LastRequestError { get; init; }

    /// <summary>本轮首次观察到的 qB 原始状态（如 checkingDL/checkingUP/checkingResumeData）；用于区分数据校验与恢复数据校验。</summary>
    public string? ObservedRawState { get; init; }

    /// <summary>
    ///     本轮 recheck 请求尝试锚点：发送前与请求状态一起落库，发送异常也保留。
    ///     请求前基线按同一时间/轮次绑定，补发前重新捕获，完成见证不会串轮。
    /// </summary>
    public DateTimeOffset? RecheckAttemptedAtUtc { get; init; }

    /// <summary>
    ///     本轮请求前快照基线（进度、剩余量、完成时间、原始状态与轮次锚点）。null 表示旧意图
    ///     或请求前无法读取快照：此时不启用完成见证，保持「观察 Checking 或未确认」的旧语义。
    /// </summary>
    public DownloadVerificationBaseline? Baseline { get; init; }

    /// <summary>
    ///     请求前发现下载器已有校验/移动时的等待起点；持续超过校验启动窗口仍无法开始本轮请求时
    ///     转校验未确认（不隔离），避免把外部校验当成本轮证据。
    /// </summary>
    public DateTimeOffset? PreRequestCheckingSinceUtc { get; init; }

    /// <summary>
    ///     仅提示：请求后观察到 checkingResumeData 的时间。恢复数据校验不是内容校验，不设
    ///     ObservedChecking/完成见证，也不参与任何推进判定。
    /// </summary>
    public DateTimeOffset? ResumeDataSeenAtUtc { get; init; }

    /// <summary>
    ///     本轮完成见证种类（见 <see cref="DownloadVerificationCompletionEvidence" />）；null 表示尚未取得。
    ///     见证只证明本轮校验执行后的完整性方向，逐文件复用/缺失结论仍由结果读取与稳定窗口决定。
    /// </summary>
    public string? CompletionEvidenceKind { get; init; }

    /// <summary>本轮完成见证的观察时间；只用于展示与诊断，不参与推进。</summary>
    public DateTimeOffset? CompletionEvidenceAtUtc { get; init; }

    /// <summary>
    ///     创建本意图的 DownloadService 实例 Id。重启后发现意图来自其他实例时，本轮基线/校验观察/
    ///     见证与请求锚点全部作废（保留候选映射），下一轮重新捕获基线并重发——重启后的完成状态可能
    ///     来自旧 resume data，不得凭旧基线判定本轮结果。
    /// </summary>
    public Guid? ObserverInstanceId { get; init; }

    /// <summary>
    ///     recheck 请求后立即查询 pieces，基线 pieces_have 大于 0 而现在掉落（qB 同步清空分片）时记录；
    ///     这是「请求已被该 hash 接收并开始重新校验」的最可靠证据，可替代 Checking 观察作为本轮执行证据。
    /// </summary>
    public DateTimeOffset? RecheckAppliedAtUtc { get; init; }

    /// <summary>本轮首次观察到 Checking 的时间；单轮校验总上限从此起算。</summary>
    public DateTimeOffset? CheckStartedAtUtc { get; init; }

    /// <summary>校验期间最后一次有效进展的时间与进度值；只由有意义的进度变化更新，请求成功或重复快照不算进展。</summary>
    public DateTimeOffset? LastCheckProgressAtUtc { get; init; }

    public double? LastCheckProgress { get; init; }

    /// <summary>
    ///     结果稳定观察的最近一致样本：任务状态与所选文件进度的摘要。样本变化时重置起点与计数；
    ///     新一轮 recheck 请求清空。null 表示尚未取得首个样本。
    /// </summary>
    public string? StabilitySample { get; init; }

    /// <summary>当前一致样本序列的首个样本时间；稳定窗口从此起算。</summary>
    public DateTimeOffset? StabilityStartedAtUtc { get; init; }

    /// <summary>结果稳定观察的起始时间（退出校验后的首个样本）；稳定观察上限从此起算，样本变化不重置。</summary>
    public DateTimeOffset? StabilityObservedAtUtc { get; init; }

    /// <summary>最近一次稳定样本的采样时间；与下一样本的间隔超过稳定窗口表示观察中断，序列重新积累。</summary>
    public DateTimeOffset? StabilitySampleAtUtc { get; init; }

    /// <summary>当前一致样本序列的样本数（含首个样本）。</summary>
    public int StabilitySampleCount { get; init; }

    /// <summary>
    ///     到期退出或轮次作废（人工暂停、移动）的停止请求时间与次数；与共享写入风险的暂停台账相互独立。
    ///     活跃轮次的停止生效后仍走结果评估：校验恰好完成则正常收尾，被中断则以稳定结果确认缺失；
    ///     作废轮次的残留校验停稳后由补发路径重新发起。人工暂停本身计为第一次停止请求。
    /// </summary>
    public DateTimeOffset? TimeoutStopRequestedAtUtc { get; init; }

    public int TimeoutStopAttempts { get; init; }

    /// <summary>校验期对无法解释的运行状态已发出暂停的次数；超过上限转待核对。</summary>
    public int PauseAttempts { get; init; }

    /// <summary>
    ///     最近一次暂停请求时间；确认窗口内不重复发出、不计失败。null 表示旧意图未记录该信息
    ///     或本轮尚未发出暂停——旧意图按“窗口已过期”处理，保持可立即保护共享数据。停止态快照
    ///     确认暂停已生效后清空（次数与风险另记），展示不得把已生效的停止继续解释为待确认。
    /// </summary>
    public DateTimeOffset? PauseRequestedAtUtc { get; init; }

    /// <summary>
    ///     未确认复用前曾观察到可能补写共享数据的下载状态；停止后也不能沿用本轮证据。
    ///     null 表示旧意图未记录该信息，有暂停记录时必须保守处理；false 表示未记录到该风险。
    /// </summary>
    public bool? UnverifiedWriteRisk { get; init; }

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
///     发 recheck 前的请求前快照基线：只用于把「请求后完整性的变化」绑定到本轮尝试，
///     不能单独作为完成证据（已完成种子再次 recheck 可能完全不变）。
/// </summary>
public sealed record DownloadVerificationBaseline(
    double                     Progress,
    long?                      AmountLeft,
    long?                      Completed,
    DateTimeOffset?            CompletionOn,
    long                       TotalSize,
    QbittorrentTorrentCategory Category,
    string?                    RawState,
    bool                       IsMoving,
    DateTimeOffset             CapturedAtUtc,
    DateTimeOffset             AttemptAtUtc,
    int                        Round,
    long?                      PiecesHave = null,
    long?                      PiecesNum  = null);

/// <summary>
///     请求前后的完整性方向：未完成→完成证明本轮校验执行并补齐/确认了数据；
///     完成→完整性下降证明本轮校验确实执行且发现了缺失。两者都不能单独证明逐文件结果。
/// </summary>
public static class DownloadVerificationCompletionEvidence
{
    /// <summary>请求前未完成，请求后停止态完成：可作为「本轮校验已执行并完成」的替代证据。</summary>
    public const string BaselineIncompleteToComplete = "BaselineIncompleteToComplete";

    /// <summary>请求前已完成，请求后停止态完整性下降：证明本轮校验确实执行并发现缺失。</summary>
    public const string BaselineCompleteToIncomplete = "BaselineCompleteToIncomplete";
}

/// <summary>完成/启动恢复的目的：数据完整（恢复做种）或候选已验证（继续下载缺失文件）。</summary>
public static class DownloadRunPurposes
{
    /// <summary>数据完整性已确认（Status=Completed），恢复做种。</summary>
    public const string Seeding = "Seeding";

    /// <summary>候选复用已确认、整包未完成：恢复缺失文件的下载。</summary>
    public const string ContinueDownload = "ContinueDownload";
}

/// <summary>
///     启动恢复子状态：只描述「启动请求是否发出、是否已观察到运行」，绝不改写任务状态、
///     FailedStep/Error/CompletedAtUtc，也绝不触发重新校验、隔离或重新下载。
/// </summary>
public static class DownloadRunStates
{
    /// <summary>无需恢复（不落库：null 即表示没有待恢复的启动）。</summary>
    public const string NotRequired = "NotRequired";

    /// <summary>启动请求已发出，等待观察下载器是否真的运行。</summary>
    public const string AwaitingConfirm = "AwaitingConfirm";

    /// <summary>启动请求抛出异常/响应丢失，可能已生效：先观察，不盲目重发。</summary>
    public const string ResponseUnknown = "ResponseUnknown";

    /// <summary>已观察到 Seeding/Downloading 运行（Seeding 为终值记录；ContinueDownload 观察后清记录）。</summary>
    public const string ConfirmedRunning = "ConfirmedRunning";

    /// <summary>有界自动重试耗尽，可人工恢复（页面「恢复做种/继续下载」）。</summary>
    public const string FailedRetryable = "FailedRetryable";

    /// <summary>用户暂停了任务，不自动恢复。</summary>
    public const string UserPaused = "UserPaused";

    /// <summary>下载器端停止（Sent 后停止、外部停止或种子已消失），不自动拉起。</summary>
    public const string DownloaderStopped = "DownloaderStopped";
}

/// <summary>启动请求的发送结果：用于区分「已确认送达」与「响应丢失，不能断言未发送」。</summary>
public static class DownloadRunRequestOutcomes
{
    /// <summary>请求成功返回；是否已生效仍由快照观察确认。</summary>
    public const string Sent = "Sent";

    /// <summary>请求异常或响应丢失：可能已被接收，先观察再决定是否重试。</summary>
    public const string Unknown = "Unknown";

    /// <summary>下载器明确拒绝（如种子已不存在）。</summary>
    public const string Rejected = "Rejected";
}

/// <summary>
///     完成/验证后的启动恢复子状态 JSON：记录 Resume 请求是否发出、响应是否未知、是否观察到运行、
///     有界重试次数与最后观察。Purpose=Seeding 只出现在 Completed；Purpose=ContinueDownload 只出现在
///     已验证但内容未全部完成的任务。数据完整性由 Status/CompletedAtUtc/VerificationOutcome 表达，
///     本记录的任何失败都不得改写状态或 FailedStep/Error。
/// </summary>
public sealed record DownloadRunRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Purpose { get; init; } = DownloadRunPurposes.Seeding;

    public string State { get; init; } = DownloadRunStates.AwaitingConfirm;

    /// <summary>已发出的 Resume 请求次数（人工恢复与自动重试共用同一预算上限）。</summary>
    public int AttemptCount { get; init; }

    /// <summary>最近一次 Resume 请求时间（journal-first：落库先于发送）。</summary>
    public DateTimeOffset? RequestedAtUtc { get; init; }

    /// <summary>最近一次请求结果（Sent/Unknown/Rejected）；Unknown 不能断言未发送。</summary>
    public string? RequestOutcome { get; init; }

    /// <summary>首次观察到运行（Seeding/Downloading）的时间；非空后永不再自动 Resume。</summary>
    public DateTimeOffset? ObservedRunningAtUtc { get; init; }

    /// <summary>最近一次观察时间（含停止态观察）。</summary>
    public DateTimeOffset? LastObservedAtUtc { get; init; }

    /// <summary>最近一次观察到的下载器端类别。</summary>
    public string? ObservedCategory { get; init; }

    /// <summary>最近一次请求异常信息，仅用于诊断，不据此断言失败。</summary>
    public string? LastRequestError { get; init; }

    /// <summary>进入当前状态的简短原因（外部停止、种子消失、预算耗尽等），供界面解释。</summary>
    public string? Reason { get; init; }

    /// <summary>最近一次 Reannounce 尝试时间（仅 Seeding 确认运行后 best-effort）。</summary>
    public DateTimeOffset? ReannounceAttemptedAtUtc { get; init; }

    /// <summary>最近一次 Reannounce 失败信息；只影响 tracker 可达性，不参与状态转移。</summary>
    public string? LastReannounceError { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DownloadRunRecovery? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DownloadRunRecovery>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>路径决策种类：任务保存根被选择的原因；决策时落库，供诊断与展示（如 .tasks 暂存的由来）。</summary>
public static class DownloadPathDecisionKinds
{
    /// <summary>无候选且无待替换版本：直接在正式根下载。</summary>
    public const string FinalRoot = "FinalRoot";

    /// <summary>存在待替换旧版：先在 .tasks/{taskId} 暂存，完成后统一归档替换。</summary>
    public const string ReplacementStaging = "ReplacementStaging";

    /// <summary>发现可复用候选：在候选根受控映射并校验，再决定复用或隔离。</summary>
    public const string ReuseVerification = "ReuseVerification";

    /// <summary>复用校验放弃：隔离删除重加，在 .tasks/{taskId} 转独立下载。</summary>
    public const string IsolationRecovery = "IsolationRecovery";

    /// <summary>下载器已存在同 Hash 种子：接管其真实保存位置。</summary>
    public const string SameHashAdoption = "SameHashAdoption";

    /// <summary>旧版纯正片布局的一次性迁移：换到媒体根并去掉相对路径中的媒体目录前缀。</summary>
    public const string LegacyLayoutMigration = "LegacyLayoutMigration";

    /// <summary>
    ///     升级前归档日志已确认：最终布局固定为旧版全局根加媒体目录前缀，完成收尾时沿用已发布的
    ///     根与相对路径，不按新版规则重新切到媒体根。
    /// </summary>
    public const string LegacyArchiveFinal = "LegacyArchiveFinal";
}

/// <summary>路径决策记录：保存根为什么这样选。每次新决策覆盖旧记录，保留最近一次以供解释。</summary>
public sealed record DownloadPathDecision
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>决策种类，见 <see cref="DownloadPathDecisionKinds" />。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>决策时的解释说明（含触发条件）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>决策得到的保存根。</summary>
    public string Root { get; init; } = string.Empty;

    /// <summary>关联任务：替换暂存关联被替换的旧任务等；无关联为空。</summary>
    public Guid? RelatedTaskId { get; init; }

    public DateTimeOffset DecidedAtUtc { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DownloadPathDecision? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DownloadPathDecision>(json, JsonOptions);
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
    /// <summary>同集择优落选的失败步骤字面量；与登记写入保持一致，暂不引入常量类。</summary>
    private const string SelectionLossStep = "择优";

    public required Guid               Id               { get; init; }
    public required Guid?              FeedItemId       { get; init; }
    public          string             InfoHash         { get; init; } = string.Empty;
    public          string?            TorrentName      { get; init; }
    public          int?               SubjectId        { get; init; }
    public          string?            SubjectName      { get; init; }
    public          decimal?           Episode          { get; init; }
    public          string?            EpisodeText      { get; init; }
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

    /// <summary>列表集数文本：单段直接展示，多段以首末集加 * 表示存在缺集。</summary>
    public string EpisodeDisplay => EpisodeParts.Display;

    /// <summary>多段缺集的实际集数提示；单段与无集数为空。</summary>
    public string? EpisodeTooltip => EpisodeParts.Tooltip;

    /// <summary>详情面板集数：多段时展示完整区间，不折叠为提示。</summary>
    public string EpisodeDetailText => EpisodeParts.Detail;

    /// <summary>集数优先取命名要素文本，回退单集格式化；三处展示共用同一解析结果。</summary>
    private (string Display, string? Tooltip, string Detail) EpisodeParts
    {
        get
        {
            var source = EpisodeText;
            if (string.IsNullOrWhiteSpace(source))
                source = Episode is { } episode ? ReleaseNamePreviewPolicy.FormatEpisodeText(episode) : null;
            if (string.IsNullOrWhiteSpace(source)) return ("—", null, "—");

            var segments = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(NormalizeEpisodeSegment).ToArray();
            if (segments.Length == 0) return ("—", null, "—");
            if (segments.Length == 1) return (segments[0], null, segments[0]);

            var first   = segments[0].Split('-')[0];
            var last    = segments[^1].Split('-')[^1];
            var detail  = string.Join(", ", segments);
            var display = first == last ? $"{first}*" : $"{first}-{last}*";
            return (display, $"实际包含：{detail}", detail);
        }
    }

    private static string NormalizeEpisodeSegment(string segment)
    {
        var parts = segment.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return "E" + TrimEpisodePrefix(segment);

        var start = TrimEpisodePrefix(parts[0]);
        var end   = TrimEpisodePrefix(parts[1]);
        return start == end ? "E" + start : $"E{start}-E{end}";
    }

    private static string TrimEpisodePrefix(string token) =>
        token.StartsWith('E') || token.StartsWith('e') ? token[1..] : token;

    /// <summary>准备阶段解释（含复用校验的等待位置）；存量任务为 None。</summary>
    public DownloadPreparationStage PreparationStage { get; init; }

    /// <summary>归档受阻原因：文件已完成，但目标占用或共享布局尚未解决。</summary>
    public string? ArchiveReviewReason { get; init; }

    /// <summary>隔离迁移状态；None 之外表示正在删除候选并重加为独立下载。</summary>
    public DownloadIsolationState IsolationState { get; init; }

    /// <summary>复用校验当前子步骤（Mapping/Requested/Observing/ReadyToRun）；无校验意图为空。</summary>
    public string? VerificationStep { get; init; }

    /// <summary>
    ///     本轮校验请求的发送状态（NotSent/Unknown/Sent/SendFailed）；无校验意图为空。
    ///     只用于解释「请求是否可能已送达」，不能据此推断文件状态，也不参与推进。
    /// </summary>
    public string? VerificationRequestState { get; init; }

    /// <summary>复用校验轮次；无意图为 0。</summary>
    public int VerificationRound { get; init; }

    /// <summary>本轮已观察到 Checking：校验确实执行过，是结果判定的前提。</summary>
    public bool ObservedChecking { get; init; }

    /// <summary>存在待确认的停止请求（到期退出停止或共享数据保护暂停）。</summary>
    public bool StopConfirmPending { get; init; }

    /// <summary>退出校验后已开始积累结果稳定样本。</summary>
    public bool ResultStabilizing { get; init; }

    /// <summary>
    ///     本轮完成见证种类（请求前基线 → 请求后完整性的变化方向）；无见证为空。
    ///     只用于解释「校验虽未观察到 Checking，但已取得替代证据」，不参与推进。
    /// </summary>
    public string? CompletionEvidence { get; init; }

    /// <summary>在下载器端观察到外部停止的时间；页面发起的人工暂停不设置。</summary>
    public DateTimeOffset? ExternalStopObservedAtUtc { get; init; }

    /// <summary>启动恢复目的（Seeding/ContinueDownload）；无恢复记录为空。</summary>
    public string? RunPurpose { get; init; }

    /// <summary>
    ///     启动恢复子状态（AwaitingConfirm/ResponseUnknown/ConfirmedRunning/FailedRetryable/
    ///     UserPaused/DownloaderStopped）；无恢复记录为空。只解释启动/做种是否恢复，不参与推进。
    /// </summary>
    public string? RunState { get; init; }

    /// <summary>启动恢复当前状态的简短原因（外部停止、种子消失、预算耗尽等）。</summary>
    public string? RunReason { get; init; }

    /// <summary>最近一次启动恢复观察时间。</summary>
    public DateTimeOffset? RunObservedAtUtc { get; init; }

    /// <summary>启动恢复状态的中文说明：回答「种子是否已恢复/在等什么」。</summary>
    public string RunStateSummary => RunState switch
    {
        DownloadRunStates.AwaitingConfirm   => "已请求启动，等待下载器确认运行",
        DownloadRunStates.ResponseUnknown   => "启动请求响应丢失，正在观察是否已生效",
        DownloadRunStates.ConfirmedRunning  => "已确认运行",
        DownloadRunStates.FailedRetryable   => "启动未恢复，可人工重试",
        DownloadRunStates.UserPaused        => "已按你的暂停停止自动恢复",
        DownloadRunStates.DownloaderStopped => "下载器已停止（不自动拉起）",
        _                                   => RunState ?? string.Empty
    };

    /// <summary>最近一次成功对账下载器的时间（最后观察时间）。</summary>
    public DateTimeOffset? LastSyncedAtUtc { get; init; }

    /// <summary>最近一次对账观察到的下载器端任务类别；普通任务的 qB 端校验据此如实展示。</summary>
    public QbittorrentTorrentCategory? LastObservedCategory { get; init; }

    /// <summary>下一次允许观察/推进本任务的最早时间；null 表示随对账正常处理，终态不展示。</summary>
    public DateTimeOffset? NextObservationAtUtc { get; init; }

    /// <summary>缺种确认时间；非空表示下载器中已连续两次查无该种子。Status 仍为 Completed。</summary>
    public DateTimeOffset? TorrentMissingAtUtc { get; init; }

    /// <summary>种子是否已被确认缺失：仅 Completed 且带缺种确认时间时为真。</summary>
    public bool IsTorrentMissing => Status == DownloadTaskStatus.Completed && TorrentMissingAtUtc != null;

    /// <summary>已发起的提交尝试次数（含隔离重加）；只用于解释等待位置，不参与推进。</summary>
    public int AttemptCount { get; init; }

    /// <summary>
    ///     查询时下载器是否已启用且配置完整；查询层只读一次设置后传给每个投影。
    ///     未注入设置服务时为 false，只用于解释「等待提交」的原因。
    /// </summary>
    public bool DownloaderReady { get; init; }

    /// <summary>路径决策种类（正式/替换暂存/复用候选/隔离/接管）；无决策记录为空。</summary>
    public string? PathDecisionKind { get; init; }

    /// <summary>路径决策的解释说明（决策时写入，含触发条件）。</summary>
    public string? PathDecisionReason { get; init; }

    /// <summary>路径决策关联的任务 Id（如替换暂存关联的旧任务）。</summary>
    public Guid? PathDecisionRelatedTaskId { get; init; }

    /// <summary>路径决策关联任务的命名快照；关联任务已删除时为空。</summary>
    public string? PathDecisionRelatedNaming { get; init; }

    /// <summary>存在未确认完成的保存位置移动或文件改名（完成后的归档移动、准备期的布局请求）。</summary>
    public bool HasPendingFileMoves { get; init; }

    /// <summary>
    ///     从持久状态派生的界面工作阶段：先按任务状态与暂停来源分类，准备暂停下再按隔离、校验意图
    ///     与准备阶段细分等待位置。展示允许「准备/校验 → 完成」，不虚构下载过程。
    /// </summary>
    public DownloadDisplayPhase Phase
    {
        get
        {
            // 校验未确认是独立的暂停原因，不能与人工/外部暂停混为一谈；但人工暂停优先：
            // 用户已明确要求停止时，不得把任务显示成等待「重新校验」的系统暂停。
            if (Status == DownloadTaskStatus.Paused && VerificationStep == DownloadVerificationStep.Unconfirmed)
            {
                if (!IsUserPaused) return DownloadDisplayPhase.VerificationUnconfirmed;
                return ExternalStopObservedAtUtc is null
                    ? DownloadDisplayPhase.PausedManually
                    : DownloadDisplayPhase.PausedByDownloader;
            }

            switch (Status)
            {
                case DownloadTaskStatus.PendingSubmit :
                    return DownloadDisplayPhase.WaitingSubmit;
                case DownloadTaskStatus.Submitting :
                    return DownloadDisplayPhase.WaitingAddConfirm;
                case DownloadTaskStatus.Completed :
                    return HasPendingFileMoves ? DownloadDisplayPhase.Archiving : DownloadDisplayPhase.Completed;
                case DownloadTaskStatus.Failed :
                    return DownloadDisplayPhase.Failed;
                case DownloadTaskStatus.Lost :
                    return DownloadDisplayPhase.Lost;
                case DownloadTaskStatus.Replaced :
                    return DownloadDisplayPhase.Replaced;
                case DownloadTaskStatus.Paused :
                    // 人工/外部暂停的普通任务在 qB 端被重新校验时同样如实显示校验中（复用校验
                    // 任务的展示仍由校验意图驱动，不借用类别）；退出校验后的下一次观察覆盖
                    // 类别、恢复对应的暂停来源展示。
                    if (VerificationStep is null && LastObservedCategory == QbittorrentTorrentCategory.Checking)
                        return DownloadDisplayPhase.Checking;
                    if (!IsUserPaused) return DownloadDisplayPhase.Paused;
                    return ExternalStopObservedAtUtc is null
                        ? DownloadDisplayPhase.PausedManually
                        : DownloadDisplayPhase.PausedByDownloader;
            }

            // Downloading：未准备暂停即真实运行；普通任务最近观察到下载器端校验（qB 端 recheck）时
            // 如实显示校验中（进度前缀随之标注），退出校验后的下一次观察覆盖类别、恢复对应展示。
            if (!IsSystemPaused)
                return LastObservedCategory == QbittorrentTorrentCategory.Checking
                    ? DownloadDisplayPhase.Checking
                    : DownloadDisplayPhase.Downloading;
            if (IsolationState != DownloadIsolationState.None) return DownloadDisplayPhase.IsolationMigrating;
            switch (VerificationStep)
            {
                case null :
                    return PreparationStage switch
                    {
                        DownloadPreparationStage.Planned
                         or DownloadPreparationStage.Applying => DownloadDisplayPhase.PreparingLayout,
                        DownloadPreparationStage.ReadyToRun => DownloadDisplayPhase.WaitingStartConfirm,
                        _                                   => DownloadDisplayPhase.Preparing
                    };
                case DownloadVerificationStep.ReadyToRun :
                    return DownloadDisplayPhase.WaitingStartConfirm;
            }

            if (StopConfirmPending) return DownloadDisplayPhase.WaitingStopConfirm;
            // 已取得完成见证：校验执行已被替代证据证明，展示等待结果稳定，而不是等待校验开始。
            if (CompletionEvidence is not null) return DownloadDisplayPhase.WaitingResultStable;
            if (VerificationStep == DownloadVerificationStep.Mapping) return DownloadDisplayPhase.PreparingLayout;
            if (!ObservedChecking) return DownloadDisplayPhase.WaitingCheckStart;
            return ResultStabilizing ? DownloadDisplayPhase.WaitingResultStable : DownloadDisplayPhase.Checking;
        }
    }

    /// <summary>状态徽章文案：按派生阶段解释当前等待位置。</summary>
    public string StatusText => Phase switch
    {
        DownloadDisplayPhase.WaitingSubmit           => "等待提交",
        DownloadDisplayPhase.WaitingAddConfirm       => "等待添加确认",
        DownloadDisplayPhase.Preparing               => "准备中（未启动下载）",
        DownloadDisplayPhase.PreparingLayout         => "整理路径中",
        DownloadDisplayPhase.WaitingCheckStart       => "等待校验开始",
        DownloadDisplayPhase.Checking                => "校验中",
        DownloadDisplayPhase.WaitingResultStable     => "等待结果稳定",
        DownloadDisplayPhase.WaitingStopConfirm      => "等待停止确认",
        DownloadDisplayPhase.WaitingStartConfirm     => "等待启动确认",
        DownloadDisplayPhase.IsolationMigrating      => "隔离迁移中",
        DownloadDisplayPhase.Archiving               => "归档中",
        DownloadDisplayPhase.Downloading             => "下载中",
        DownloadDisplayPhase.PausedManually          => "已暂停（人工）",
        DownloadDisplayPhase.PausedByDownloader      => "已暂停（下载器端停止）",
        DownloadDisplayPhase.Paused                  => "已暂停",
        DownloadDisplayPhase.VerificationUnconfirmed => "校验未确认",
        DownloadDisplayPhase.Completed               => "已完成",
        DownloadDisplayPhase.Failed                  => "失败",
        DownloadDisplayPhase.Lost                    => "任务丢失",
        DownloadDisplayPhase.Replaced => IsSelectionTombstone
            ? "同集落选（未下载）"
            : "已被替换",
        _ => Phase.ToString()
    };

    /// <summary>
    ///     同集择优落选的终止记录：从未提交或接管下载器资源，不能按「已被替换」解释；
    ///     该记录只承担同资源查重，胜出任务被删除时会重新排队。
    /// </summary>
    public bool IsSelectionTombstone => Status == DownloadTaskStatus.Replaced && FailedStep == SelectionLossStep;

    /// <summary>
    ///     当前在等什么：从派生阶段与就绪/校验标志得到一句话解释。只用于展示，
    ///     不得被任何推进分支读取；新增显示阶段时由文案覆盖测试提示补齐。
    /// </summary>
    public string WaitingForText => Phase switch
    {
        DownloadDisplayPhase.WaitingSubmit => DownloaderReady
            ? "等待提交到下载器"
            : "等待提交：下载器未启用或未配置",
        DownloadDisplayPhase.WaitingAddConfirm   => "等待下载器确认添加",
        DownloadDisplayPhase.Preparing           => "准备文件选择与命名",
        DownloadDisplayPhase.PreparingLayout     => "整理下载路径与文件布局",
        DownloadDisplayPhase.WaitingCheckStart   => "等待下载器开始校验",
        DownloadDisplayPhase.Checking            => "下载器正在校验文件",
        DownloadDisplayPhase.WaitingResultStable => "等待校验结果稳定",
        DownloadDisplayPhase.WaitingStopConfirm  => "等待下载器确认停止",
        DownloadDisplayPhase.WaitingStartConfirm => RunPurpose == DownloadRunPurposes.ContinueDownload
            ? RunState switch
            {
                DownloadRunStates.AwaitingConfirm or
                    DownloadRunStates.ResponseUnknown => "候选已校验；等待确认下载已启动",
                DownloadRunStates.FailedRetryable or
                    DownloadRunStates.DownloaderStopped => "候选已校验；下载启动失败，可重试",
                DownloadRunStates.UserPaused => "候选已校验；下载因你的暂停未启动",
                _                            => "等待启动确认"
            }
            : "等待启动确认",
        DownloadDisplayPhase.IsolationMigrating      => "正在隔离迁移为独立下载",
        DownloadDisplayPhase.VerificationUnconfirmed => "校验结果未确认，文件保持原样",
        DownloadDisplayPhase.Archiving               => "内容已完成，等待归档确认",
        DownloadDisplayPhase.Downloading             => "等待下载或做种进展",
        DownloadDisplayPhase.PausedManually          => "已暂停，等待你恢复",
        DownloadDisplayPhase.PausedByDownloader      => "下载器端已停止，原因未确认",
        DownloadDisplayPhase.Paused                  => "已暂停，原因未记录",
        DownloadDisplayPhase.Completed => RunState switch
        {
            DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown =>
                "内容已完成；等待确认做种是否已恢复",
            DownloadRunStates.FailedRetryable   => "内容已完成；做种未恢复",
            DownloadRunStates.DownloaderStopped => "内容已完成；下载器已停止做种",
            DownloadRunStates.UserPaused        => "内容已完成；做种因你的暂停未启动",
            _                                   => "内容已完成"
        },
        DownloadDisplayPhase.Failed => "任务已失败，等待处理",
        DownloadDisplayPhase.Lost   => "下载器中已找不到任务",
        DownloadDisplayPhase.Replaced => IsSelectionTombstone
            ? "同集落选，未下载任何文件"
            : "已被更新版本替代",
        _ => "状态未识别，请查看任务详情"
    };

    /// <summary>
    ///     下一步会做什么、或用户该做什么：只读静态映射，不推断文件状态、不自动重试，
    ///     也不得被任何推进分支读取。失败步骤的建议见 <see cref="FailedNextStepText" />。
    /// </summary>
    public string NextStepText => Phase switch
    {
        DownloadDisplayPhase.WaitingSubmit => DownloaderReady
            ? "下一轮对账会提交种子，并先以停止状态完成文件选择与命名。"
            : "请在设置页启用下载器并填写地址与保存路径；配置完成后对账会自动提交。",
        DownloadDisplayPhase.WaitingAddConfirm   => "按下载器记录观察添加结果；确认后停在停止状态准备文件选择与命名。",
        DownloadDisplayPhase.Preparing           => "下一轮对账会应用文件选择与命名计划，确认一致后才启动下载。",
        DownloadDisplayPhase.PreparingLayout     => "核对文件选择、命名与移动；一致后进入启动确认。",
        DownloadDisplayPhase.WaitingCheckStart   => "等待校验开始；校验结果确认前不会启动下载。",
        DownloadDisplayPhase.Checking            => "等待校验结束并读取逐文件结果；校验完成前不会启动下载。",
        DownloadDisplayPhase.WaitingResultStable => "跨采样间隔核对逐文件结果，一致后才决定复用或隔离。",
        DownloadDisplayPhase.WaitingStopConfirm  => "停止生效后继续处理；共享数据在确认停止前保持原样。",
        DownloadDisplayPhase.WaitingStartConfirm => RunPurpose == DownloadRunPurposes.ContinueDownload
            ? RunState switch
            {
                DownloadRunStates.FailedRetryable or DownloadRunStates.DownloaderStopped =>
                    "可点击「继续下载」重试；不会重新校验或隔离。",
                _ => "已发出启动请求；对账会继续观察下载器，确认后回到普通下载跟踪。"
            }
            : "文件选择与命名已确认，下一轮对账会恢复下载。",
        DownloadDisplayPhase.IsolationMigrating => "按步骤删除候选种子并在任务目录重新添加，布局确认后继续。",
        DownloadDisplayPhase.VerificationUnconfirmed =>
            VerificationRequestState == DownloadVerificationRequestState.SendFailed
                ? "请求发送失败（可能已送达），未计入请求次数；文件保持原样，可重新校验，不重新下载。"
                : "文件保持原样，可重新校验，不重新下载。",
        DownloadDisplayPhase.Archiving          => "下一步会完成归档移动或改名；目标被占用时会提示归档待核对。",
        DownloadDisplayPhase.Downloading        => "无需操作；对账会持续刷新进度，完成后自动归档。",
        DownloadDisplayPhase.PausedManually     => "需要继续时点击「恢复」；对账不会自动恢复。",
        DownloadDisplayPhase.PausedByDownloader => "如需继续请先核对下载器端状态；对账不会自动恢复。",
        DownloadDisplayPhase.Paused             => "请核对下载器与任务状态，必要时删除本任务记录。",
        DownloadDisplayPhase.Completed => RunState switch
        {
            DownloadRunStates.AwaitingConfirm or DownloadRunStates.ResponseUnknown =>
                "无需操作；对账会继续观察下载器，确认后结束。数据完整性已确认，不受做种影响。",
            DownloadRunStates.FailedRetryable =>
                "可点击「恢复做种」重试，或到下载器手动恢复；不会重新校验或重新下载。",
            DownloadRunStates.DownloaderStopped =>
                "下载器按策略停止了做种；如需继续可点击「恢复做种」或到下载器恢复。",
            DownloadRunStates.UserPaused =>
                "需要继续时先在下载器或任务页恢复；对账不会自动启动。",
            _ => ArchiveReviewReason is null
                ? string.Empty
                : "请核对归档冲突后点击「重新核对归档」。"
        },
        DownloadDisplayPhase.Failed => FailedNextStepText,
        DownloadDisplayPhase.Lost   => "可能已在下载器内手动删除；可点击「重试」重新提交，或删除本任务记录。",
        DownloadDisplayPhase.Replaced => IsSelectionTombstone
            ? "保留该记录用于同资源查重；如需重新下载请删除后重新添加。"
            : string.Empty,
        _ => "未知阶段，请查看错误详情后人工处理。"
    };

    /// <summary>
    ///     失败步骤的静态恢复建议：只按持久化的步骤字符串给操作方向，不据此断言文件是否被写过，
    ///     也不自动重试。未知步骤保留兜底说明，避免新增步骤静默显示原文。
    /// </summary>
    private string FailedNextStepText => FailedStep switch
    {
        "认证"   => "请检查设置页的下载器地址与账号，修正后点击「重试」。",
        "提交"   => "请检查下载器状态后点击「重试」；多次提交仍找不到种子时可删除任务记录。",
        "提交路径" => "目标路径可能被其他任务占用；处理占用后点击「重试」。",
        "对账"   => "请检查下载器中是否手动删除或移动了该任务，确认后点击「重试」。",
        "文件整理" => "请核对下载器中的文件清单与目标路径，处理占用后点击「重试」。",
        "改名"   => "自动改名未能完成，请人工核对文件后点击「重试」。",
        "复用校验" => (VerificationStep, VerificationRequestState) switch
        {
            (DownloadVerificationStep.Unconfirmed, DownloadVerificationRequestState.SendFailed) =>
                "请求发送失败（可能已送达），未计入请求次数；可重新校验，不重新下载。",
            (DownloadVerificationStep.Unconfirmed, _) => "可重新校验，不重新下载。",
            (_, DownloadVerificationRequestState.SendFailed) =>
                "请求发送失败（可能已送达），未计入请求次数；可核对下载器后人工处理。",
            _ => "可点击「重试」重新开始（会放弃复用、转为独立下载），或核对下载器后人工处理。"
        },
        "下载器" => "请检查下载器连接与状态后点击「重试」。",
        "择优"  => "同集落选，不是故障；无需重试。",
        _     => "未知步骤，请查看错误详情后人工处理。"
    };

    /// <summary>是否展示进度：运行、暂停与受阻（失败/丢失）任务保留最后一次观察进度；未获取显示未知，不造 0%。</summary>
    public bool ShowsProgress =>
        Progress is not null &&
        Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused
                                                 or DownloadTaskStatus.Failed or DownloadTaskStatus.Lost;

    /// <summary>进度数值前缀：校验期的总进度是校验进度，不是所选内容完成度。</summary>
    public string? ProgressCaption => Phase == DownloadDisplayPhase.Checking ? "校验 " : null;

    /// <summary>进度旁的说明：复用校验受阻或未确认时，最后进度是观察值，不代表本轮校验已确认。</summary>
    public string? ProgressNote =>
        Phase == DownloadDisplayPhase.VerificationUnconfirmed ||
        (Status is DownloadTaskStatus.Failed && FailedStep == "复用校验")
            ? "最后观察进度，不代表本轮校验已确认"
            : null;

    /// <summary>是否展示最后观察时间：活动与受阻任务展示数据新鲜度；完成与被替换由时间列说明，不重复展示。</summary>
    public bool ShowsLastSync => LastSyncedAtUtc is not null &&
                                 Status is not (DownloadTaskStatus.Completed or DownloadTaskStatus.Replaced);

    /// <summary>
    ///     路径决策说明：暂存/复用/隔离/接管解释保存根由来（含关联任务命名），避免只显示一个 .tasks
    ///     目录让用户猜。正式路径无需解释，终态任务的暂存早已解决、不展示过期理由。
    /// </summary>
    public string? PathDecisionNote
    {
        get
        {
            if (!Status.IsActive()                                              ||
                PathDecisionKind is null or DownloadPathDecisionKinds.FinalRoot ||
                string.IsNullOrWhiteSpace(PathDecisionReason)) return null;

            return PathDecisionRelatedNaming is { } naming
                ? $"{PathDecisionReason}（关联任务：{naming}）"
                : PathDecisionReason;
        }
    }

    /// <summary>状态徽章下的解释行：校验轮次、下载器端停止的原因边界与路径决策。</summary>
    public IReadOnlyList<string> PhaseNotes
    {
        get
        {
            var notes = new List<string>();
            if (VerificationStep is not null && VerificationRound > 0)
                notes.Add($"复用校验第 {VerificationRound} 轮");
            switch (Phase)
            {
                case DownloadDisplayPhase.VerificationUnconfirmed :
                    notes.Add("校验执行或结果未确认；文件保持原样，可「重新校验」再次发起，不重新下载");
                    break;
                case DownloadDisplayPhase.PausedByDownloader :
                    notes.Add("在下载器端观察到停止，原因未记录（可能为下载器内人工暂停或分享限制）");
                    break;
            }

            if (PathDecisionNote is { } decision) notes.Add(decision);
            if (RunState is null) return notes;
            notes.Add(RunPurpose == DownloadRunPurposes.Seeding
                          ? $"数据完整性已确认；做种恢复：{RunStateSummary}"
                          : $"候选复用已确认；启动状态：{RunStateSummary}");
            if (RunState is DownloadRunStates.FailedRetryable or DownloadRunStates.DownloaderStopped)
                notes.Add("仅启动/做种未恢复，文件保持原样；不会自动重新校验或重新下载");

            return notes;
        }
    }
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

/// <summary>状态筛选分组：把派生显示阶段归并成与状态列徽章文案对齐的筛选选项。</summary>
public enum DownloadStatusFilterGroup
{
    /// <summary>已登记意图，等待提交到下载器。</summary>
    WaitingSubmit,

    /// <summary>提交已发起，等待按 Hash 确认添加。</summary>
    WaitingAddConfirm,

    /// <summary>准备暂停下的路径整理、复用校验与各等待确认位置，含下载器端观察到的校验展示。</summary>
    PreparingChecking,

    /// <summary>下载器内真实下载或做种运行中。</summary>
    Downloading,

    /// <summary>人工或下载器端暂停，不在准备流程中。</summary>
    Paused,

    /// <summary>内容已完成，含归档移动尚未全部确认。</summary>
    Completed,

    Failed,

    Lost,

    Replaced
}

/// <summary>
///     状态筛选选项与阶段归组：纯展示映射。DownloadTaskQueries 按同一口径构造 SQL 谓词，
///     由等价性测试锁定两边一致。
/// </summary>
public static class DownloadStatusFilter
{
    public static IReadOnlyList<DownloadStatusFilterGroup> Choices { get; } =
    [
        DownloadStatusFilterGroup.WaitingSubmit,
        DownloadStatusFilterGroup.WaitingAddConfirm,
        DownloadStatusFilterGroup.PreparingChecking,
        DownloadStatusFilterGroup.Downloading,
        DownloadStatusFilterGroup.Paused,
        DownloadStatusFilterGroup.Completed,
        DownloadStatusFilterGroup.Failed,
        DownloadStatusFilterGroup.Lost,
        DownloadStatusFilterGroup.Replaced
    ];

    /// <summary>把派生显示阶段穷举归组；新增显示阶段时由编译器与兜底桶独占性测试提示补齐。</summary>
    public static DownloadStatusFilterGroup GroupOf(DownloadDisplayPhase phase) => phase switch
    {
        DownloadDisplayPhase.WaitingSubmit     => DownloadStatusFilterGroup.WaitingSubmit,
        DownloadDisplayPhase.WaitingAddConfirm => DownloadStatusFilterGroup.WaitingAddConfirm,
        DownloadDisplayPhase.Preparing or
            DownloadDisplayPhase.PreparingLayout or
            DownloadDisplayPhase.WaitingCheckStart or
            DownloadDisplayPhase.Checking or
            DownloadDisplayPhase.WaitingResultStable or
            DownloadDisplayPhase.WaitingStopConfirm or
            DownloadDisplayPhase.WaitingStartConfirm or
            DownloadDisplayPhase.IsolationMigrating => DownloadStatusFilterGroup.PreparingChecking,
        DownloadDisplayPhase.VerificationUnconfirmed => DownloadStatusFilterGroup.PreparingChecking,
        DownloadDisplayPhase.Downloading             => DownloadStatusFilterGroup.Downloading,
        DownloadDisplayPhase.PausedManually or
            DownloadDisplayPhase.PausedByDownloader or
            DownloadDisplayPhase.Paused => DownloadStatusFilterGroup.Paused,
        DownloadDisplayPhase.Completed or
            DownloadDisplayPhase.Archiving => DownloadStatusFilterGroup.Completed,
        DownloadDisplayPhase.Failed   => DownloadStatusFilterGroup.Failed,
        DownloadDisplayPhase.Lost     => DownloadStatusFilterGroup.Lost,
        DownloadDisplayPhase.Replaced => DownloadStatusFilterGroup.Replaced,

        // 兜底仅针对未命名的枚举值；新增命名阶段应由独占性测试提示归组。
        _ => DownloadStatusFilterGroup.Replaced
    };

    /// <summary>全部状态分组：状态筛选默认全部勾选，全不勾选则是明确的“一个都不显示”。</summary>
    public static HashSet<DownloadStatusFilterGroup> CreateAllGroups() => [.. Choices];

    public static string Text(DownloadStatusFilterGroup group) => group switch
    {
        DownloadStatusFilterGroup.WaitingSubmit     => "等待提交",
        DownloadStatusFilterGroup.WaitingAddConfirm => "等待添加确认",
        DownloadStatusFilterGroup.PreparingChecking => "准备与校验中",
        DownloadStatusFilterGroup.Downloading       => "下载中",
        DownloadStatusFilterGroup.Paused            => "已暂停",
        DownloadStatusFilterGroup.Completed         => "已完成（含归档中）",
        DownloadStatusFilterGroup.Failed            => "失败",
        DownloadStatusFilterGroup.Lost              => "丢失",
        _                                           => "已替换"
    };
}

/// <summary>下载任务列表查询条件：搜索关键词、状态分组多选与字幕语言多选；条件之间 AND，同组多选内 OR。</summary>
public sealed record DownloadTaskListFilter
{
    /// <summary>默认每页行数；查询层钳制到 1..200。</summary>
    public const int DefaultPageSize = 100;

    /// <summary>搜索关键词：匹配条目名、种子原始名、字幕组与命名快照；忽略首尾空白，LIKE 通配符按字面处理。</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>
    ///     状态分组多选：集合即需要显示的分组，空集合表示一个都不显示；全部选中与不限制状态等价。
    ///     默认全部勾选，与页面状态筛选的初始值一致。
    /// </summary>
    public IReadOnlySet<DownloadStatusFilterGroup> StatusGroups { get; init; } =
        DownloadStatusFilter.CreateAllGroups();

    /// <summary>字幕语言多选（空字符串哨兵 = 无语言任务）；空集合表示全部语言。</summary>
    public IReadOnlySet<string> Languages { get; init; } = new HashSet<string>();

    /// <summary>1 起始页码；超出有效范围时由查询层钳制并在结果中返回实际页码。</summary>
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;
}

/// <summary>下载任务列表分页结果：当前页行、筛选后的完整总数与可选语言项（空字符串哨兵 = 存在无语言任务）。</summary>
public sealed record DownloadTaskListPage(
    IReadOnlyList<DownloadTaskView> Items,
    int                             Page,
    int                             PageSize,
    int                             TotalCount,
    IReadOnlyList<string>           Languages)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}
