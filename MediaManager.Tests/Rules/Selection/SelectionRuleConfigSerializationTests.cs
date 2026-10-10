using Banned.AniParser.Models.Enums;
using MediaManager.Core.Rules.Selection;

namespace MediaManager.Tests.Rules.Selection;

public sealed class SelectionRuleConfigSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var config = new SelectionRuleConfig
        {
            Mode           = SelectionMode.Combined,
            GroupOrder     = ["LoliHouse", "ANi"],
            LanguageOrder  = [EnumLanguage.JpSc, EnumLanguage.Sc],
            DimensionOrder = SelectionDimensionOrder.LanguageFirst,
            CombinedEntries =
            [
                new CombinedRuleEntry("LoliHouse", EnumLanguage.JpSc),
                new CombinedRuleEntry(null, EnumLanguage.Sc),
                new CombinedRuleEntry("ANi", null)
            ],
            LanguageMatch      = LanguageMatchMode.Contains,
            Unmatched          = UnmatchedPolicy.NeedsReview,
            AllowedResolutions = [EnumResolution.R1080P, EnumResolution.R720P],
            Version            = 7
        };

        var restored = SelectionRuleConfigSerialization.TryDeserialize(
                                                                       SelectionRuleConfigSerialization
                                                                          .Serialize(config));

        Assert.NotNull(restored);
        Assert.Equal(config.Mode, restored.Mode);
        Assert.Equal(config.GroupOrder, restored.GroupOrder);
        Assert.Equal(config.LanguageOrder, restored.LanguageOrder);
        Assert.Equal(config.DimensionOrder, restored.DimensionOrder);
        Assert.Equal(config.CombinedEntries, restored.CombinedEntries);
        Assert.Equal(config.LanguageMatch, restored.LanguageMatch);
        Assert.Equal(config.Unmatched, restored.Unmatched);
        Assert.Equal(config.AllowedResolutions, restored.AllowedResolutions);
        Assert.Equal(config.Version, restored.Version);
    }

    [Fact]
    public void TryDeserialize_CorruptJson_ReturnsNull()
    {
        Assert.Null(SelectionRuleConfigSerialization.TryDeserialize("{not json"));
    }

    [Fact]
    public void TryDeserialize_UnknownEnumValue_ReturnsNull()
    {
        var json = """{"Mode":"NotAMode","Version":1}""";

        Assert.Null(SelectionRuleConfigSerialization.TryDeserialize(json));
    }

    [Fact]
    public void TryDeserialize_MissingVersion_ReturnsNull()
    {
        var json = """{"Mode":"Independent"}""";

        Assert.Null(SelectionRuleConfigSerialization.TryDeserialize(json));
    }

    [Fact]
    public void TryDeserialize_EmptyGroupEntry_ReturnsNull()
    {
        var json = """{"Mode":"Independent","GroupOrder":["ANi"," "],"Version":1}""";

        Assert.Null(SelectionRuleConfigSerialization.TryDeserialize(json));
    }

    [Fact]
    public void TryDeserialize_CombinedModeWithoutEntries_ReturnsNull()
    {
        // 组合模式空条件无法匹配任何候选，等同损坏配置，必须回退默认。
        var json = SelectionRuleConfigSerialization.Serialize(SelectionRuleConfig.CreateDefault())
                                                   .Replace("\"Mode\":\"Independent\"", "\"Mode\":\"Combined\"");

        Assert.Null(SelectionRuleConfigSerialization.TryDeserialize(json));
    }

    [Fact]
    public void TryDeserialize_NumericUnknownEnumValue_ReturnsNull()
    {
        // 回归：JsonStringEnumConverter 默认接受数字枚举值，未定义数字不得当成合法配置。
        var unknownCases = new[]
        {
            ("\"Mode\":\"Independent\"", "\"Mode\":99"),
            ("\"JpSc\"", "99"),
            ("\"R1080P\"", "99"),
            ("\"LanguageMatch\":\"Exact\"", "\"LanguageMatch\":99")
        };
        foreach (var (valid, corrupt) in unknownCases)
        {
            var json = SelectionRuleConfigSerialization.Serialize(SelectionRuleConfig.CreateDefault())
                                                       .Replace(valid, corrupt);
            Assert.Null(SelectionRuleConfigSerialization.TryDeserialize(json));
        }
    }

    [Fact]
    public void TryDeserialize_NumericDefinedEnumValue_IsAccepted()
    {
        // 已定义的数字枚举值语义无歧义，保持可读入。
        var json = SelectionRuleConfigSerialization.Serialize(SelectionRuleConfig.CreateDefault())
                                                   .Replace("\"Mode\":\"Independent\"", "\"Mode\":0");

        var restored = SelectionRuleConfigSerialization.TryDeserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(SelectionMode.Independent, restored.Mode);
    }

    [Fact]
    public void ContentEquals_DifferentVersionOnly_True()
    {
        var config = SelectionRuleConfig.CreateDefault();

        Assert.True(SelectionRuleConfigSerialization.ContentEquals(config, config with { Version = 9 }));
    }

    [Fact]
    public void ContentEquals_GroupCaseDifference_True()
    {
        var config = SelectionRuleConfig.CreateDefault();

        Assert.True(SelectionRuleConfigSerialization.ContentEquals(config,
                                                                   config with { GroupOrder = ["lolihouse", "ani"] }));
    }

    [Fact]
    public void ContentEquals_ResolutionOrderDifference_True()
    {
        var config = SelectionRuleConfig.CreateDefault() with
        {
            AllowedResolutions = [EnumResolution.R1080P, EnumResolution.R720P]
        };

        Assert.True(SelectionRuleConfigSerialization.ContentEquals(config,
                                                                   config with
                                                                   {
                                                                       AllowedResolutions =
                                                                       [EnumResolution.R720P, EnumResolution.R1080P]
                                                                   }));
    }

    [Fact]
    public void ContentEquals_GroupOrderSwap_False()
    {
        var config = SelectionRuleConfig.CreateDefault();

        Assert.False(SelectionRuleConfigSerialization.ContentEquals(config,
                                                                    config with { GroupOrder = ["ANi", "LoliHouse"] }));
    }

    [Fact]
    public void ContentEquals_CombinedEntryWildcardDifference_False()
    {
        var config = SelectionRuleConfig.CreateDefault() with
        {
            CombinedEntries = [new CombinedRuleEntry("ANi", null)]
        };

        Assert.False(SelectionRuleConfigSerialization.ContentEquals(config,
                                                                    config with
                                                                    {
                                                                        CombinedEntries =
                                                                        [new CombinedRuleEntry(null, null)]
                                                                    }));
    }

    [Fact]
    public void Normalize_TrimsGroupNames_AndDropsEmpty()
    {
        var normalized = SelectionRuleConfigSerialization.Normalize(SelectionRuleConfig.CreateDefault() with
        {
            GroupOrder = ["  ANi  ", "", "LoliHouse"],
            CombinedEntries = [new CombinedRuleEntry(" ANi ", EnumLanguage.Sc)]
        });

        Assert.Equal(["ANi", "LoliHouse"], normalized.GroupOrder);
        Assert.Equal("ANi", normalized.CombinedEntries[0].Group);
    }
}
