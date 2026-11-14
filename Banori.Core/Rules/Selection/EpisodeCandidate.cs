using Banned.AniParser.Models.Enums;

namespace Banori.Core.Rules.Selection;

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

    /// <summary>
    ///     逐集裁决前该包的初始候选正片文件数；只数正片，不含字幕与 Seed。跨包落选不会减少该数
    ///     （文件仍保持正片角色），包内落选已归 Seed。不是独立排序维度：同组同语言的分区内与
    ///     版本一起比较，跨组/跨语言同优先级时作为分区冠军之间的决胜输入；不参与跨字幕组/
    ///     跨语言的版本比较。
    /// </summary>
    public int PackageVideoFileCount { get; init; } = 1;

    /// <summary>同优先级的稳定决胜输入之一：更早发布者优先。</summary>
    public DateTimeOffset? PublishedAtUtc { get; init; }

    /// <summary>同优先级时用于稳定决胜的来源标识；不使用 RSS 枚举顺序或数据库 ID。</summary>
    public string StableId { get; init; } = string.Empty;
}
