using Banned.AniParser.Models.Enums;

namespace MediaManager.Core.Rules.Selection;

/// <summary>
///     参与同集择优的一个候选。字段来自条目已保存的解析快照，Core 不接触 EF 实体与数据库。
/// </summary>
public sealed record EpisodeCandidate
{
    public required Guid FeedItemId { get; init; }

    public required int SubjectId { get; init; }

    /// <summary>分组的集类型；多集合集不参与择优，由调用方与硬性过滤共同排除。</summary>
    public required string MediaType { get; init; }

    public required decimal Episode { get; init; }

    /// <summary>字幕组名；解析不到时为空字符串，按未命中规则处理。</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>解析得到的语言组合；无法识别时为 null，按未命中规则处理。</summary>
    public EnumLanguage? Language { get; init; }

    /// <summary>分辨率无法识别时为 Unknown，由硬性过滤判断。</summary>
    public required EnumResolution Resolution { get; init; }

    public required int Version { get; init; }

    /// <summary>同优先级的稳定决胜输入之一：更早发布者优先。</summary>
    public DateTimeOffset? PublishedAtUtc { get; init; }

    /// <summary>同优先级时用于稳定决胜的来源标识；不使用 RSS 枚举顺序或数据库 ID。</summary>
    public string StableId { get; init; } = string.Empty;
}
