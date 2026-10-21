using Banned.AniParser.Models.Enums;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banori.Core.Rules.Selection;

/// <summary>
///     SelectionRuleConfig 的 JSON 序列化与内容比较。设置存储把整份配置作为单个键的 JSON 持久化；
///     反序列化遇到损坏内容或未知枚举值时返回 null，由调用方回退到内置默认规则。
/// </summary>
public static class SelectionRuleConfigSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(SelectionRuleConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>反序列化并做基本有效性校验；内容损坏、缺字段或版本非法时返回 null。</summary>
    public static SelectionRuleConfig? TryDeserialize(string json)
    {
        SelectionRuleConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SelectionRuleConfig>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        return config is not null && IsValid(config) ? config : null;
    }

    /// <summary>保存前规范化：字幕组名去首尾空白并剔除空项；组合条件同理。其余字段按用户输入保留。</summary>
    public static SelectionRuleConfig Normalize(SelectionRuleConfig config) => config with
    {
        GroupOrder = [.. config.GroupOrder.Select(group => group.Trim()).Where(group => group.Length > 0)],
        CombinedEntries = [.. config.CombinedEntries.Select(entry => entry with { Group = entry.Group?.Trim() })]
    };

    /// <summary>
    ///     内容相等：忽略 Version 与字幕组大小写（规则匹配不区分大小写，仅大小写不同的配置语义相同），
    ///     分辨率按集合比较（硬性过滤只关心是否允许，与顺序无关）。用于判断保存是否真的修改了规则。
    /// </summary>
    public static bool ContentEquals(SelectionRuleConfig left, SelectionRuleConfig right)
    {
        if (left.Mode           != right.Mode           ||
            left.DimensionOrder != right.DimensionOrder ||
            left.LanguageMatch  != right.LanguageMatch  ||
            left.Unmatched      != right.Unmatched)
            return false;

        if (!SequenceEquals(left.GroupOrder, right.GroupOrder,
                            (a, b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!left.LanguageOrder.SequenceEqual(right.LanguageOrder)) return false;

        if (!SequenceEquals(left.CombinedEntries, right.CombinedEntries, EntryEquals)) return false;

        return new HashSet<EnumResolution>(left.AllowedResolutions)
           .SetEquals(right.AllowedResolutions);
    }

    private static bool IsValid(SelectionRuleConfig config)
    {
        if (config.Version < 1) return false;
        if (config.GroupOrder is null      ||
            config.LanguageOrder is null   ||
            config.CombinedEntries is null ||
            config.AllowedResolutions is null)
            return false;
        if (config.GroupOrder.Any(group => string.IsNullOrWhiteSpace(group))) return false;
        if (config.CombinedEntries.Any(entry => entry.Group is not null &&
                                                string.IsNullOrWhiteSpace(entry.Group)))
            return false;

        // 组合模式空条件列表无法匹配任何候选（CandidateRankingPolicy 拒绝），等同损坏配置。
        if (config.Mode == SelectionMode.Combined && config.CombinedEntries.Count == 0) return false;

        // JSON 反序列化接受数字形式的枚举值，未定义的数字会被当成合法值读入；
        // 未知值必须视为损坏回退默认，而不是让所有候选判不合格。
        if (!Enum.IsDefined(config.Mode)           ||
            !Enum.IsDefined(config.DimensionOrder) ||
            !Enum.IsDefined(config.LanguageMatch)  ||
            !Enum.IsDefined(config.Unmatched))
            return false;
        if (config.LanguageOrder.Any(language => !Enum.IsDefined(language))) return false;
        if (config.CombinedEntries.Any(entry => entry.Language is not null &&
                                                !Enum.IsDefined(entry.Language.Value)))
            return false;
        if (config.AllowedResolutions.Any(resolution => !Enum.IsDefined(resolution))) return false;

        return true;
    }

    private static bool SequenceEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, T, bool> equals) =>
        left.Count == right.Count && left.Zip(right).All(pair => equals(pair.First, pair.Second));

    private static bool EntryEquals(CombinedRuleEntry left, CombinedRuleEntry right)
    {
        var sameGroup = (left.Group, right.Group) switch
        {
            (null, null)   => true,
            (null, _)      => false,
            (_, null)      => false,
            ({ } a, { } b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)
        };
        return sameGroup && left.Language == right.Language;
    }
}
