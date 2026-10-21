using Banori.Core.Rules;

namespace Banori.Features.Subscriptions;

public enum FeedItemStatus
{
    Pending,
    Accepted,
    Rejected,
    NeedsReview,
    Failed
}

/// <summary>条目在同集择优中的结果，与解析 / 硬性过滤状态（FeedItemStatus）分开表示。</summary>
public enum SelectionOutcome
{
    /// <summary>尚未参与同集择优（含历史数据，等待该组下一次重选）。</summary>
    None,

    /// <summary>当前是所在组的优胜候选。</summary>
    Winner,

    /// <summary>组内落选；解析快照与原因保留，不因此永久拒绝，规则或候选变化后可重新参与。</summary>
    RunnerUp
}

public sealed record FeedSourceItem(
    string          StableId,
    string?         SourceItemId,
    string          Title,
    Uri?            Link,
    Uri?            TorrentUri,
    DateTimeOffset? PublishedAtUtc,
    /// <summary>来源 feed 直接提供的 Bangumi Subject ID（如 AniBT 的 bgmId）；feed 未提供时为 null。</summary>
    int?            SourceSubjectId = null);

/// <summary>
///     一次评估产生的完整结果。失败与待处理分支只填写可用的字段，Reason 始终给出可读解释。
/// </summary>
public sealed record FeedItemAnalysis
{
    public required FeedItemStatus         Status         { get; init; }
    public          string?                ParsedTitle    { get; init; }
    public          string?                MediaType      { get; init; }
    public          decimal?               Episode        { get; init; }
    public          int?                   StartEpisode   { get; init; }
    public          int?                   EndEpisode     { get; init; }
    public          int                    Version        { get; init; } = 1;
    public          string?                Group          { get; init; }
    public          string?                GroupType      { get; init; }
    public          string?                Language       { get; init; }
    public          string?                SubtitleType   { get; init; }
    public          string?                Resolution     { get; init; }
    public          string?                Source         { get; init; }
    public          string?                WebSource      { get; init; }
    public          string?                VideoCodec     { get; init; }
    public          string?                AudioCodec     { get; init; }
    public          int?                   ColorBitDepth  { get; init; }
    public          int?                   SubjectId      { get; init; }
    public          string?                SubjectName    { get; init; }
    public          ReleaseDecisionStatus? DecisionStatus { get; init; }
    public          string?                Preference     { get; init; }
    public required string                 Reason         { get; init; }
    public          string?                NamingPreview  { get; init; }

    /// <summary>命名要素 JSON：与 NamingPreview 同源生成；登记下载任务时随任务落库，供完成改名按文件集数重渲染。</summary>
    public string? NamingIngredients { get; init; }

    public required DateTimeOffset EvaluatedAtUtc { get; init; }
}

public sealed record FeedItemView
{
    public required Guid                   Id               { get; init; }
    public required Guid                   SubscriptionId   { get; init; }
    public required string                 SubscriptionName { get; init; }
    public required string                 Title            { get; init; }
    public          string?                Link             { get; init; }
    public          string?                TorrentUrl       { get; init; }
    public          DateTimeOffset?        PublishedAtUtc   { get; init; }
    public required DateTimeOffset         FirstSeenAtUtc   { get; init; }
    public required FeedItemStatus         Status           { get; init; }
    public          string?                ParsedTitle      { get; init; }
    public          string?                MediaType        { get; init; }
    public          decimal?               Episode          { get; init; }
    public          int                    Version          { get; init; }
    public          string?                Group            { get; init; }
    public          string?                Language         { get; init; }
    public          string?                Resolution       { get; init; }
    public          int?                   SubjectId        { get; init; }
    public          string?                SubjectName      { get; init; }
    public          ReleaseDecisionStatus? DecisionStatus   { get; init; }
    public          string?                Preference       { get; init; }
    public required string                 Reason           { get; init; }
    public          string?                NamingPreview    { get; init; }

    /// <summary>条目在同集择优中的结果；None 表示尚未参与择优（含历史数据）。</summary>
    public SelectionOutcome SelectionOutcome { get; init; }
}
