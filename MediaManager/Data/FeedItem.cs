using MediaManager.Core.Rules;
using MediaManager.Features.Subscriptions;

namespace MediaManager.Data;

public sealed class FeedItem
{
    public Guid                   Id             { get; set; }
    public Guid                   SubscriptionId { get; set; }
    public MediaSubscription      Subscription   { get; set; } = null!;
    public string                 StableId       { get; set; } = string.Empty;
    public string?                SourceItemId   { get; set; }
    public string                 Title          { get; set; } = string.Empty;
    public string?                Link           { get; set; }
    public string?                TorrentUrl     { get; set; }
    public DateTimeOffset?        PublishedAtUtc { get; set; }
    public DateTimeOffset         FirstSeenAtUtc { get; set; }
    public DateTimeOffset?        EvaluatedAtUtc { get; set; }
    public FeedItemStatus         Status         { get; set; }
    public string?                ParsedTitle    { get; set; }
    public string?                MediaType      { get; set; }
    public decimal?               Episode        { get; set; }
    public int?                   StartEpisode   { get; set; }
    public int?                   EndEpisode     { get; set; }
    public int                    Version        { get; set; }
    public string?                Group          { get; set; }
    public string?                GroupType      { get; set; }
    public string?                Language       { get; set; }
    public string?                SubtitleType   { get; set; }
    public string?                Resolution     { get; set; }
    public string?                Source         { get; set; }
    public string?                WebSource      { get; set; }
    public string?                VideoCodec     { get; set; }
    public string?                AudioCodec     { get; set; }
    public int?                   ColorBitDepth  { get; set; }
    public int?                   SubjectId      { get; set; }
    public MediaSubject?          Subject        { get; set; }
    public ReleaseDecisionStatus? DecisionStatus { get; set; }
    public string?                Preference     { get; set; }
    public string                 Reason         { get; set; } = "等待处理。";
    public string?                NamingPreview  { get; set; }

    /// <summary>命名要素 JSON：生成 NamingPreview 时的模板与令牌值；登记下载任务时随任务落库。</summary>
    public string? NamingIngredients { get; set; }

    public SelectionOutcome SelectionOutcome { get; set; }
}
