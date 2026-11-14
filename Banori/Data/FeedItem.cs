using Banori.Core.Rules;
using Banori.Core.Torrents;
using Banori.Features.Subscriptions;

namespace Banori.Data;

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

    /// <summary>
    ///     来源作用域：由订阅 RSS 地址推导，同来源的条目才在同一竞争组内比较。
    ///     迁移前的旧行保持 <see cref="SourceScopes.Legacy" />，回填服务证明来源后才迁出。
    /// </summary>
    public string SourceScope { get; set; } = SourceScopes.Legacy;

    public SelectionOutcome SelectionOutcome { get; set; }
}
