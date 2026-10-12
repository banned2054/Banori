using Banned.AniParser.Models.Enums;

namespace MediaManager.Core.Rules.Selection;

public enum SelectionMode
{
    /// <summary>字幕组顺序与语言组合顺序独立配置，按维度顺序依次比较。</summary>
    Independent,

    /// <summary>按有序的“字幕组 + 语言”条件列表匹配，首个命中的条件确定优先级。</summary>
    Combined
}

public enum SelectionDimensionOrder
{
    /// <summary>先比较字幕组顺序，再比较语言组合顺序。</summary>
    GroupFirst,

    /// <summary>先比较语言组合顺序，再比较字幕组顺序。</summary>
    LanguageFirst
}

public enum LanguageMatchMode
{
    /// <summary>候选语言组合必须与配置项完全一致。</summary>
    Exact,

    /// <summary>配置项的基础语言全部出现在候选语言组合中即命中，例如配置“简日”可命中“简繁日”。</summary>
    Contains
}

public enum UnmatchedPolicy
{
    /// <summary>未命中规则的字幕组 / 语言 / 组合进入待确认，不参与自动胜负。</summary>
    NeedsReview,

    /// <summary>未命中者仍参与竞争，但排在所有已配置项之后。</summary>
    LowestPriority
}

/// <summary>组合模式的一个有序条件；Group 或 Language 为 null 表示该维度通配。</summary>
public sealed record CombinedRuleEntry(string? Group, EnumLanguage? Language);

public sealed record SelectionRuleConfig
{
    public required SelectionMode Mode { get; init; }

    /// <summary>独立模式：字幕组优先顺序，越靠前优先级越高；匹配不区分大小写。</summary>
    public required IReadOnlyList<string> GroupOrder { get; init; }

    /// <summary>独立模式：语言组合优先顺序，越靠前优先级越高。</summary>
    public required IReadOnlyList<EnumLanguage> LanguageOrder { get; init; }

    /// <summary>独立模式：两个维度的比较先后顺序。</summary>
    public required SelectionDimensionOrder DimensionOrder { get; init; }

    /// <summary>组合模式：有序条件列表；可在末尾显式放置通配兜底。</summary>
    public required IReadOnlyList<CombinedRuleEntry> CombinedEntries { get; init; }

    public required LanguageMatchMode LanguageMatch { get; init; }

    /// <summary>独立模式下未列出的字幕组 / 语言，或组合模式未命中任何条件时的处理方式。</summary>
    public required UnmatchedPolicy Unmatched { get; init; }

    /// <summary>硬性过滤：允许参与竞争的分辨率；空列表表示不限制。</summary>
    public required IReadOnlyList<EnumResolution> AllowedResolutions { get; init; }

    /// <summary>规则版本；配置每次修改递增，择优结果与下载意图记录当时版本，同批前后不混用标准。</summary>
    public int Version { get; init; } = 1;

    /// <summary>
    ///     内置默认规则：LoliHouse 先于 ANi，简日先于简中，仅接受 1080p。
    ///     未列出的字幕组与语言参与竞争但排最后，不提前排除。
    /// </summary>
    public static SelectionRuleConfig CreateDefault() =>
        new()
        {
            Mode               = SelectionMode.Independent,
            GroupOrder         = ["LoliHouse", "ANi"],
            LanguageOrder      = [EnumLanguage.JpSc, EnumLanguage.Sc],
            DimensionOrder     = SelectionDimensionOrder.GroupFirst,
            CombinedEntries    = [],
            LanguageMatch      = LanguageMatchMode.Exact,
            Unmatched          = UnmatchedPolicy.LowestPriority,
            AllowedResolutions = [EnumResolution.R1080P]
        };
}
