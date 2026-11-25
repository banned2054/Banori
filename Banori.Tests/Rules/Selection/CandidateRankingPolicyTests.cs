using Banned.AniParser.Models.Enums;
using Banori.Core.Rules.Selection;

namespace Banori.Tests.Rules.Selection;

/// <summary>同集组内择优规则的样本矩阵：排列不变性、维度顺序、组合匹配、版本边界、跨组/跨语言同优先级的计数决胜与稳定决胜。</summary>
public sealed class CandidateRankingPolicyTests
{
    private static readonly Guid CurrentItemId = Guid.NewGuid();

    private static EpisodeCandidate Candidate(string          group, EnumLanguage? language, int version = 1,
                                              EnumResolution  resolution     = EnumResolution.R1080P,
                                              DateTimeOffset? publishedAtUtc = null,
                                              string?         stableId       = null, Guid? itemId = null,
                                              int             packageVideoFileCount = 1) => new()
    {
        FeedItemId            = itemId ?? Guid.NewGuid(),
        SubjectId             = 100,
        MediaType             = "SingleEpisode",
        Episode               = 3,
        Group                 = group,
        Language              = language,
        Resolution            = resolution,
        Version               = version,
        PackageVideoFileCount = packageVideoFileCount,
        PublishedAtUtc        = publishedAtUtc,
        StableId              = stableId ?? $"guid:{group}-{language}-v{version}"
    };

    private static SelectionRuleConfig IndependentConfig(
        SelectionDimensionOrder order         = SelectionDimensionOrder.GroupFirst,
        LanguageMatchMode       match         = LanguageMatchMode.Exact,
        UnmatchedPolicy         unmatched     = UnmatchedPolicy.LowestPriority,
        EnumLanguage[]?         languageOrder = null,
        int                     version       = 1) => new()
    {
        Mode               = SelectionMode.Independent,
        GroupOrder         = ["LoliHouse", "ANi"],
        LanguageOrder      = languageOrder ?? [EnumLanguage.JpSc, EnumLanguage.Sc],
        DimensionOrder     = order,
        CombinedEntries    = [],
        LanguageMatch      = match,
        Unmatched          = unmatched,
        AllowedResolutions = [EnumResolution.R1080P],
        Version            = version
    };

    private static SelectionRuleConfig CombinedConfig(
        IReadOnlyList<CombinedRuleEntry> entries,
        LanguageMatchMode                match     = LanguageMatchMode.Exact,
        UnmatchedPolicy                  unmatched = UnmatchedPolicy.NeedsReview) => new()
    {
        Mode               = SelectionMode.Combined,
        GroupOrder         = [],
        LanguageOrder      = [],
        DimensionOrder     = SelectionDimensionOrder.GroupFirst,
        CombinedEntries    = entries,
        LanguageMatch      = match,
        Unmatched          = unmatched,
        AllowedResolutions = [EnumResolution.R1080P]
    };

    /// <summary>组合通配把所有候选放进同一规则优先级层，用于验证层内按字幕组+语言分区后的冠军归约。</summary>
    private static SelectionRuleConfig SameTierConfig() => CombinedConfig([new CombinedRuleEntry(null, null)]);

    [Fact]
    public void Select_AnivalsHigherGroup_AnyInputOrderPicksSameWinner()
    {
        var anibaba   = Candidate("ANi", EnumLanguage.JpSc);
        var loliHouse = Candidate("LoliHouse", EnumLanguage.JpSc);
        var policy    = new CandidateRankingPolicy();

        var orders = new[]
        {
            new[] { anibaba, loliHouse },
            new[] { loliHouse, anibaba },
            new[] { anibaba, Candidate("Sakurato", EnumLanguage.Sc), loliHouse }
        };

        foreach (var candidates in orders)
        {
            var result = policy.Select(candidates, IndependentConfig());
            Assert.True(result.HasWinner);
            Assert.Equal("LoliHouse", result.Winner!.Group);
        }
    }

    [Fact]
    public void Select_DimensionOrder_ChangesWinnerBetweenGroupFirstAndLanguageFirst()
    {
        var loliHouseSc = Candidate("LoliHouse", EnumLanguage.Sc);
        var aniJpSc     = Candidate("ANi", EnumLanguage.JpSc);
        var policy      = new CandidateRankingPolicy();

        var groupFirst = policy.Select([loliHouseSc, aniJpSc], IndependentConfig());
        var languageFirst = policy.Select([loliHouseSc, aniJpSc],
                                          IndependentConfig(SelectionDimensionOrder.LanguageFirst));

        Assert.Equal("LoliHouse", groupFirst.Winner!.Group);
        Assert.Equal("ANi", languageFirst.Winner!.Group);
    }

    [Fact]
    public void Select_Combined_FirstMatchWinsAndWildcardCatchesRest()
    {
        var config = CombinedConfig(
        [
            new CombinedRuleEntry("LoliHouse", EnumLanguage.JpSc),
            new CombinedRuleEntry("ANi", EnumLanguage.JpScTc),
            new CombinedRuleEntry("ANi", null)
        ]);
        var policy = new CandidateRankingPolicy();

        var aniJpScTc = policy.Select([Candidate("ANi", EnumLanguage.JpScTc)], config);
        var aniJp     = policy.Select([Candidate("ANi", EnumLanguage.Jp)], config);
        var aniTc     = policy.Select([Candidate("ANi", EnumLanguage.Tc)], config);

        Assert.Contains("第 2 项", aniJpScTc.WinnerBasis, StringComparison.Ordinal);
        Assert.Contains("第 3 项", aniJp.WinnerBasis, StringComparison.Ordinal);
        Assert.True(aniTc.HasWinner);
        Assert.Contains("第 3 项", aniTc.WinnerBasis, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_Combined_UnmatchedFollowsPolicy()
    {
        var entries = new List<CombinedRuleEntry> { new("ANi", EnumLanguage.Sc) };
        var policy  = new CandidateRankingPolicy();

        var needsReview = policy.Select([Candidate("LoliHouse", EnumLanguage.Sc)],
                                        CombinedConfig(entries, unmatched : UnmatchedPolicy.NeedsReview));
        var lowest = policy.Select([Candidate("LoliHouse", EnumLanguage.Sc), Candidate("ANi", EnumLanguage.Sc)],
                                   CombinedConfig(entries, unmatched : UnmatchedPolicy.LowestPriority));

        Assert.False(needsReview.HasWinner);
        Assert.All(needsReview.Ranking, rank => Assert.True(rank.IsExcluded));
        Assert.True(lowest.HasWinner);
        Assert.Equal("ANi", lowest.Winner!.Group);
    }

    [Fact]
    public void Select_LanguageMatch_ExactVersusContains()
    {
        var scTc   = Candidate("LoliHouse", EnumLanguage.ScTc, stableId : "guid:sctc");
        var sc     = Candidate("LoliHouse", EnumLanguage.Sc, stableId : "guid:sc");
        var policy = new CandidateRankingPolicy();

        var exact    = policy.Select([scTc, sc], IndependentConfig(match : LanguageMatchMode.Exact));
        var contains = policy.Select([scTc, sc], IndependentConfig(match : LanguageMatchMode.Contains));

        Assert.Equal("guid:sc", exact.Winner!.StableId);
        Assert.Contains("兜底", exact.Ranking.Single(r => r.Candidate.StableId == "guid:sctc").Basis,
                        StringComparison.Ordinal);
        Assert.All(contains.Ranking, rank => Assert.False(rank.IsExcluded));
        Assert.Contains("语言序 2", contains.WinnerBasis, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_UnknownGroupAndLanguage_FollowUnmatchedPolicy()
    {
        var unknownGroup    = Candidate("Sakurato", EnumLanguage.JpSc);
        var unknownLanguage = Candidate("LoliHouse", null);
        var policy          = new CandidateRankingPolicy();

        var lowest = policy.Select([unknownGroup, unknownLanguage], IndependentConfig());
        var review = policy.Select([unknownGroup, unknownLanguage],
                                   IndependentConfig(unmatched : UnmatchedPolicy.NeedsReview));

        Assert.True(lowest.HasWinner);
        Assert.Equal("LoliHouse", lowest.Winner!.Group);
        Assert.False(review.HasWinner);
        Assert.All(review.Ranking, rank => Assert.True(rank.IsExcluded));
    }

    [Fact]
    public void Select_Independent_PerDimensionFallback_RespectsMatchedDimensionAndFirstComeFirstServed()
    {
        var policy = new CandidateRankingPolicy();

        // 语言优先：未知组 + 简日（语言在榜）严格优于 未知组 + 繁中（语言不在榜）。
        var unknownGroupJpSc = Candidate("UnknownA", EnumLanguage.JpSc, stableId : "guid:unknown-a-jpsc");
        var unknownGroupTc   = Candidate("UnknownB", EnumLanguage.Tc, stableId : "guid:unknown-b-tc");
        var langFirst        = policy.Select([unknownGroupTc, unknownGroupJpSc],
                                             IndependentConfig(SelectionDimensionOrder.LanguageFirst));
        Assert.Equal(unknownGroupJpSc.FeedItemId, langFirst.Winner!.FeedItemId);

        // 语言优先且语言同为简日时：在榜字幕组 > 不在榜字幕组；都不在榜则先到先得（保留现任）。
        var listedGroupJpSc = Candidate("ANi", EnumLanguage.JpSc, stableId : "guid:ani-jpsc");
        var listedWins = policy.Select([unknownGroupJpSc, listedGroupJpSc],
                                       IndependentConfig(SelectionDimensionOrder.LanguageFirst),
                                       unknownGroupJpSc.FeedItemId);
        Assert.Equal(listedGroupJpSc.FeedItemId, listedWins.Winner!.FeedItemId);

        var otherUnknownJpScV2 = Candidate("UnknownC", EnumLanguage.JpSc, version : 2,
                                           stableId : "guid:unknown-c-jpsc-v2");
        var bothUnlistedKeepIncumbent = policy.Select([otherUnknownJpScV2, unknownGroupJpSc],
                                                      IndependentConfig(SelectionDimensionOrder.LanguageFirst),
                                                      unknownGroupJpSc.FeedItemId);
        // 都不在榜且跨组：版本不跨组生效，保留现任（先到先得）。
        Assert.Equal(unknownGroupJpSc.FeedItemId, bothUnlistedKeepIncumbent.Winner!.FeedItemId);

        // 字幕组优先：在榜字幕组 + 不在榜语言 严格优于 不在榜字幕组 + 在榜语言。
        var listedGroupUnknownLang = Candidate("ANi", EnumLanguage.Tc, stableId : "guid:ani-tc");
        var groupFirst = policy.Select([unknownGroupJpSc, listedGroupUnknownLang],
                                       IndependentConfig(SelectionDimensionOrder.GroupFirst));
        Assert.Equal(listedGroupUnknownLang.FeedItemId, groupFirst.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SamePriority_KeepsCurrentWinner()
    {
        var existing = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:existing",
                                 publishedAtUtc : new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                                 itemId : CurrentItemId);
        var challenger = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:challenger",
                                   publishedAtUtc : new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([challenger, existing], IndependentConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SamePriorityAcrossGroups_KeepsCurrentWinnerOverAlphabeticalReplacement()
    {
        // 两个未列入规则的字幕组同处兜底层：本例计数相同，没有严格更优代表，新候选不得按组名
        // 字典序替换现任；若挑战者计数更大，跨组平级应按计数替换。
        var currentAni = Candidate("ANi", EnumLanguage.Sc, stableId : "guid:current-ani", itemId : CurrentItemId);
        var alphabeticalFirst = Candidate("ASakura", EnumLanguage.Sc, stableId : "guid:asakura");
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([alphabeticalFirst, currentAni], IndependentConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SameGroupRevisionUpgrade_OverridesCurrentWinner()
    {
        // 现任优胜是 v1 时，同字幕组的 v2 是严格升级，必须胜出；现任保留不得阻止版本升级。
        var currentV1 = Candidate("LoliHouse", EnumLanguage.JpSc,
                                  stableId : "guid:current-v1", itemId : CurrentItemId);
        var newV2  = Candidate("LoliHouse", EnumLanguage.JpSc, 2, stableId : "guid:new-v2");
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([currentV1, newV2], IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SameGroupUpgrade_IgnoresGroupCase()
    {
        // 字幕组匹配不区分大小写：现任 ANi v1 与新候选 ani v2 是同组修订升级，不得视为跨组保留现任。
        var currentV1 = Candidate("ANi", EnumLanguage.JpSc, stableId : "guid:current-ani-v1",
                                  itemId : CurrentItemId);
        var newV2  = Candidate("ani", EnumLanguage.JpSc, 2, stableId : "guid:ani-lower-v2");
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([currentV1, newV2], IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SameGroupDifferentLanguage_HigherVersion_KeepsCurrentWinner()
    {
        // 默认规则下未列入的字幕组同处兜底层：版本不跨语言，本例计数相同才保留现任 Sc v1；
        // Tc 合集计数更大时应按计数替换。
        var currentScV1 = Candidate("Sakurato", EnumLanguage.Sc, stableId : "guid:current-sc-v1",
                                    itemId : CurrentItemId);
        var newTcV2  = Candidate("Sakurato", EnumLanguage.Tc, 2, stableId : "guid:new-tc-v2");
        var policy   = new CandidateRankingPolicy();

        var result = policy.Select([newTcV2, currentScV1], IndependentConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
        Assert.Equal(EnumLanguage.Sc, result.Winner.Language);
    }

    [Fact]
    public void Select_SameGroupDifferentLanguage_ContainsMatch_HigherVersion_KeepsCurrentWinner()
    {
        // Contains 命中只说明规则优先级相同，不代表同语言，版本不跨语言比较；本例计数相同才保留
        // 现任 Sc v1，ScTc 合集计数更大时应按计数替换。
        var config = CombinedConfig([new CombinedRuleEntry("LoliHouse", EnumLanguage.Sc)],
                                    match : LanguageMatchMode.Contains);
        var currentScV1 = Candidate("LoliHouse", EnumLanguage.Sc, stableId : "guid:current-sc-v1",
                                    itemId : CurrentItemId);
        var newScTcV2 = Candidate("LoliHouse", EnumLanguage.ScTc, 2, stableId : "guid:new-sctc-v2");
        var policy    = new CandidateRankingPolicy();

        var result = policy.Select([newScTcV2, currentScV1], config, CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
        Assert.Equal(EnumLanguage.Sc, result.Winner.Language);
    }

    [Fact]
    public void Select_DifferentGroup_HigherVersion_KeepsCurrentWinner()
    {
        // 组合通配让两个字幕组同层，但修订版本不得跨字幕组升级；本例计数相同才保留现任，
        // 挑战者计数更大时应按计数替换。
        var config = CombinedConfig([new CombinedRuleEntry(null, null)]);
        var currentLoliHouseV1 = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-loli-v1",
                                          itemId : CurrentItemId);
        var newAniV9 = Candidate("ANi", EnumLanguage.JpSc, 9, stableId : "guid:new-ani-v9");
        var policy   = new CandidateRankingPolicy();

        var result = policy.Select([newAniV9, currentLoliHouseV1], config, CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
        Assert.Equal("LoliHouse", result.Winner.Group);
    }

    [Fact]
    public void Select_SameGroupSameLanguage_SameVersion_MoreVideosWins()
    {
        // 同组同语言同版本：单集（1 个正片）对合集（4 个正片）保留合集。
        var currentSingle = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-single",
                                      itemId : CurrentItemId, packageVideoFileCount : 1);
        var newBatch = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:new-batch",
                                 packageVideoFileCount : 4);
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([currentSingle, newBatch], IndependentConfig(), CurrentItemId);

        Assert.Equal(newBatch.FeedItemId, result.Winner!.FeedItemId);
        Assert.Equal(4, result.Winner.PackageVideoFileCount);
    }

    [Fact]
    public void Select_SameGroupSameLanguage_SameVersion_FewerVideosKeepsCurrentWinner()
    {
        // 同一分区内，包内正片数在版本相同时比较：现任更多时保留现任，少者不能凭其他平级候选翻转。
        var currentBatch = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-batch",
                                     itemId : CurrentItemId, packageVideoFileCount : 4);
        var newSingle = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:new-single",
                                  packageVideoFileCount : 1);
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([newSingle, currentBatch], IndependentConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
        Assert.Equal(4, result.Winner.PackageVideoFileCount);
    }

    [Fact]
    public void Select_VersionTakesPrecedenceOverPackageVideoCount()
    {
        // 版本先于文件数：即使同组同语言的 v1 大包更多正片，也不能压过 v2 小包。
        var currentV1Big = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-v1-big",
                                     itemId : CurrentItemId, packageVideoFileCount : 4);
        var sameVersionMore = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:v1-more",
                                        packageVideoFileCount : 5);
        var newV2Small = Candidate("LoliHouse", EnumLanguage.JpSc, 2, stableId : "guid:new-v2-small",
                                   packageVideoFileCount : 2);
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([currentV1Big, sameVersionMore, newV2Small], IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2Small.FeedItemId, result.Winner!.FeedItemId);
        Assert.Equal(2, result.Winner.Version);
    }

    [Fact]
    public void Select_WithoutCurrent_CrossGroupOrLanguage_DecidesByPackageCount()
    {
        // 同一优先级层内跨组不比较版本，也不按组名字典序：按包初始正片数，多者胜。
        var alphaV1 = Candidate("Alpha", EnumLanguage.Sc, stableId : "guid:alpha-v1", packageVideoFileCount : 1);
        var zuluV9  = Candidate("Zulu", EnumLanguage.Sc, 9, stableId : "guid:zulu-v9",
                                packageVideoFileCount : 9);
        var policy = new CandidateRankingPolicy();

        var byGroup = policy.Select([zuluV9, alphaV1], IndependentConfig());
        Assert.Equal("Zulu", byGroup.Winner!.Group);

        // 跨语言同理（同处于未命中或通配同级层内）：单集不得靠语言枚举名字典序压过同组其他语言合集。
        var scV1 = Candidate("Sakurato", EnumLanguage.Sc, stableId : "guid:sc-v1", packageVideoFileCount : 1);
        var tcV9 = Candidate("Sakurato", EnumLanguage.Tc, 9, stableId : "guid:tc-v9",
                             packageVideoFileCount : 9);
        var byLanguage = policy.Select([tcV9, scV1], SameTierConfig());
        Assert.Equal(EnumLanguage.Tc, byLanguage.Winner!.Language);

        // 版本仍只在同组同语言的分区内比较：Zulu 分区内 v9 小包压过 v2 大包，不与 Alpha 的分区混排。
        var zuluV2Big = Candidate("Zulu", EnumLanguage.Sc, 2, stableId : "guid:zulu-v2",
                                  packageVideoFileCount : 9);
        var withinPartition = policy.Select([alphaV1, zuluV2Big, zuluV9], IndependentConfig());
        Assert.Equal(zuluV9.FeedItemId, withinPartition.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_CurrentAcrossGroups_LargerCollectionReplacesCurrent()
    {
        // U1：同层不同字幕组，版本关系不适用；现任 1 正片单集被 4 正片合集按计数替换。
        var currentAni = Candidate("ANi", EnumLanguage.JpSc, stableId : "guid:current-ani",
                                   itemId : CurrentItemId, packageVideoFileCount : 1);
        var loliHouseBatch = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:loli-batch",
                                       packageVideoFileCount : 4);
        var result = new CandidateRankingPolicy().Select([currentAni, loliHouseBatch],
                                                         SameTierConfig(), CurrentItemId);

        Assert.Equal(loliHouseBatch.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_CurrentSameGroupDifferentLanguage_LargerCollectionReplacesCurrent()
    {
        // U2：同组不同语言同层，版本不跨语言比较；即使挑战者版本更高，也只按包计数 4 > 1 替换。
        var config = CombinedConfig([new CombinedRuleEntry("LoliHouse", null)]);
        var currentJpSc = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-jpsc",
                                    itemId : CurrentItemId, packageVideoFileCount : 1);
        var jpTcBatch = Candidate("LoliHouse", EnumLanguage.JpTc, 2, stableId : "guid:jptc-batch",
                                  packageVideoFileCount : 4);
        var result = new CandidateRankingPolicy().Select([currentJpSc, jpTcBatch], config, CurrentItemId);

        Assert.Equal(jpTcBatch.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_WithoutCurrent_CrossGroups_DecidesByPackageCount()
    {
        // U3：无现任、两个未列入规则的字幕组同处兜底层，按包计数而非组名选 Zulu 合集。
        var alpha = Candidate("Alpha", EnumLanguage.JpSc, stableId : "guid:alpha", packageVideoFileCount : 1);
        var zulu  = Candidate("Zulu", EnumLanguage.JpSc, stableId : "guid:zulu", packageVideoFileCount : 4);
        var result = new CandidateRankingPolicy().Select([alpha, zulu], IndependentConfig());

        Assert.Equal(zulu.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_WithoutCurrent_ContainsSamePriority_DifferentLanguagesDecideByPackageCount()
    {
        // U4：Contains 让 Sc 与 ScTc 命中同一语言序，但语言不同、版本不比较，按计数选 ScTc 合集。
        var config = IndependentConfig(match : LanguageMatchMode.Contains,
                                       languageOrder : [EnumLanguage.Sc]);
        var sc = Candidate("LoliHouse", EnumLanguage.Sc, stableId : "guid:sc", packageVideoFileCount : 1);
        var scTc = Candidate("LoliHouse", EnumLanguage.ScTc, 2, stableId : "guid:sctc",
                             packageVideoFileCount : 4);
        var result = new CandidateRankingPolicy().Select([sc, scTc], config);

        Assert.Equal(scTc.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_CurrentAcrossGroups_EqualPackageCountKeepsCurrent()
    {
        // U5：跨组同层、双方各 1 正片，没有严格更优代表，保留现任。
        var currentAni = Candidate("ANi", EnumLanguage.JpSc, stableId : "guid:current-ani",
                                   itemId : CurrentItemId, packageVideoFileCount : 1);
        var loliHouseSingle = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:loli-single",
                                        packageVideoFileCount : 1);
        var result = new CandidateRankingPolicy().Select([currentAni, loliHouseSingle],
                                                         SameTierConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SamePartitionHigherVersion_ReplacesCurrent()
    {
        // U7：同组同语言分区内版本升级仍有效，计相同也由更高版本顶替现任。
        var currentV1 = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-v1",
                                  itemId : CurrentItemId, packageVideoFileCount : 1);
        var newV2 = Candidate("LoliHouse", EnumLanguage.JpSc, 2, stableId : "guid:new-v2",
                              packageVideoFileCount : 1);
        var result = new CandidateRankingPolicy().Select([currentV1, newV2], IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SamePartitionHigherVersion_BeatsLargerPackageCount()
    {
        // U8：同分区版本优先于包计数：现任 v1/4 正片被 v2/2 正片替换。
        var currentV1Big = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:current-v1-big",
                                     itemId : CurrentItemId, packageVideoFileCount : 4);
        var newV2Small = Candidate("LoliHouse", EnumLanguage.JpSc, 2, stableId : "guid:new-v2-small",
                                   packageVideoFileCount : 2);
        var result = new CandidateRankingPolicy().Select([currentV1Big, newV2Small],
                                                         IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2Small.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SamePartitionUpgrade_WinsTieAgainstOtherGroupChampion()
    {
        // U9：同分区升级 v2 成为现任侧代表 C；他组 Alpha 同为 1 正片，即使稳定标识更小（成为全局
        // 冠军 W），计数相同也保留 C。
        var currentZuluV1 = Candidate("Zulu", EnumLanguage.Sc, stableId : "guid:current-zulu-v1",
                                      itemId : CurrentItemId);
        var zuluV2 = Candidate("Zulu", EnumLanguage.Sc, 2, stableId : "guid:zulu-v2");
        var alpha  = Candidate("Alpha", EnumLanguage.Sc, stableId : "guid:aaa-alpha");
        var result = new CandidateRankingPolicy().Select([currentZuluV1, zuluV2, alpha],
                                                         IndependentConfig(), CurrentItemId);

        Assert.Equal(zuluV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_LargerOtherGroupChampion_BeatsIncumbentPartitionUpgrade()
    {
        // U10：现任 ANi v2/C5 在同分区被 v3 淘汰，ANi 代表变成 v3/C1；LoliHouse 冠军 C3 > 1，
        // 他组合集胜出。跨区只比代表包计数，旧 v2/C5 不再参与。
        var currentAniV2 = Candidate("ANi", EnumLanguage.JpSc, 2, stableId : "guid:current-ani-v2",
                                     itemId : CurrentItemId, packageVideoFileCount : 5);
        var aniV3 = Candidate("ANi", EnumLanguage.JpSc, 3, stableId : "guid:ani-v3",
                              packageVideoFileCount : 1);
        var loliHouseV1 = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:loli-house-v1",
                                    packageVideoFileCount : 3);
        var result = new CandidateRankingPolicy().Select([currentAniV2, aniV3, loliHouseV1],
                                                         SameTierConfig(), CurrentItemId);

        Assert.Equal(loliHouseV1.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_NonChampionPackageCount_DoesNotChallengeOtherPartitions()
    {
        // U11：ANi v1/C100 不是分区冠军（同组 v2/C5 版本更高），不得凭 100 个正片跨区挑战；
        // LoliHouse C3 也小于 ANi 代表 C5，保留现任。
        var currentAniV2 = Candidate("ANi", EnumLanguage.JpSc, 2, stableId : "guid:current-ani-v2",
                                     itemId : CurrentItemId, packageVideoFileCount : 5);
        var aniV1Big = Candidate("ANi", EnumLanguage.JpSc, 1, stableId : "guid:ani-v1-big",
                                 packageVideoFileCount : 100);
        var loliHouseV1 = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:loli-house-v1",
                                    packageVideoFileCount : 3);
        var result = new CandidateRankingPolicy().Select([currentAniV2, aniV1Big, loliHouseV1],
                                                         SameTierConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_EqualCountOtherGroupChampion_KeepsIncumbentPartitionChampion()
    {
        // U12：ANi 分区冠军 v3/C1 与 LoliHouse C1 计数相同；即使 LoliHouse 稳定标识更小成为全局
        // 冠军 W，也保留现任侧代表 C（ANi v3）。
        var currentAniV2 = Candidate("ANi", EnumLanguage.JpSc, 2, stableId : "guid:current-ani-v2",
                                     itemId : CurrentItemId, packageVideoFileCount : 5);
        var aniV3 = Candidate("ANi", EnumLanguage.JpSc, 3, stableId : "guid:z-ani-v3",
                              packageVideoFileCount : 1);
        var loliHouseV1 = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:a-loli-house",
                                    packageVideoFileCount : 1);
        var result = new CandidateRankingPolicy().Select([currentAniV2, aniV3, loliHouseV1],
                                                         SameTierConfig(), CurrentItemId);

        Assert.Equal(aniV3.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_CrossPartitionPermutations_Agree()
    {
        // U15：无现任、三个跨组分区计数 1/3/2；六种输入排列都应由计数 3 的 Zulu 合集胜出。
        var alpha = Candidate("Alpha", EnumLanguage.JpSc, stableId : "guid:alpha", packageVideoFileCount : 1);
        var zulu  = Candidate("Zulu", EnumLanguage.JpSc, stableId : "guid:zulu", packageVideoFileCount : 3);
        var sakurato = Candidate("Sakurato", EnumLanguage.JpSc, stableId : "guid:sakurato",
                                 packageVideoFileCount : 2);
        var policy = new CandidateRankingPolicy();
        var orders = new[]
        {
            new[] { alpha, zulu, sakurato },
            new[] { alpha, sakurato, zulu },
            new[] { zulu, alpha, sakurato },
            new[] { zulu, sakurato, alpha },
            new[] { sakurato, alpha, zulu },
            new[] { sakurato, zulu, alpha }
        };

        foreach (var candidates in orders)
        {
            var result = policy.Select(candidates, IndependentConfig());
            Assert.Equal(zulu.FeedItemId, result.Winner!.FeedItemId);
        }
    }

    [Fact]
    public void Select_CrossGroupPairwiseCycle_ResolvesByPartitionChampion()
    {
        // U16：A G1/v2/C1、B G1/v1/C3、C G2/v1/C2 的成对关系会成环；两层归约先取 G1 的版本冠军
        // A（C1）与 G2 的冠军 C（C2），再按计数选 C，不实现跨分区的成对比较器。
        var a = Candidate("G1", EnumLanguage.Sc, 2, stableId : "guid:g1-v2", packageVideoFileCount : 1);
        var b = Candidate("G1", EnumLanguage.Sc, 1, stableId : "guid:g1-v1", packageVideoFileCount : 3);
        var c = Candidate("G2", EnumLanguage.Sc, 1, stableId : "guid:g2-v1", packageVideoFileCount : 2);
        var result = new CandidateRankingPolicy().Select([a, b, c], IndependentConfig());

        Assert.Equal(c.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_PartitionKeyIgnoresGroupCase_UpgradesCurrent()
    {
        // U17：分区键忽略字幕组大小写，现任 ANi v1 与新候选 ani v2 同分区，版本升级顶替现任。
        var currentAniV1 = Candidate("ANi", EnumLanguage.JpSc, stableId : "guid:ani-v1",
                                     itemId : CurrentItemId);
        var aniV2 = Candidate("ani", EnumLanguage.JpSc, 2, stableId : "guid:ani-v2");
        var result = new CandidateRankingPolicy().Select([currentAniV1, aniV2], IndependentConfig(), CurrentItemId);

        Assert.Equal(aniV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_NullLanguageIsOwnPartition_LargerCollectionReplacesCurrent()
    {
        // U18：null 语言自成一个分区；同组不同语言（含 null）同层时按包计数，合集替换现任单集。
        var config = CombinedConfig([new CombinedRuleEntry("LoliHouse", null)]);
        var currentNoLanguage = Candidate("LoliHouse", null, stableId : "guid:current-null",
                                          itemId : CurrentItemId, packageVideoFileCount : 1);
        var scBatch = Candidate("LoliHouse", EnumLanguage.Sc, stableId : "guid:sc-batch",
                                packageVideoFileCount : 4);
        var result = new CandidateRankingPolicy().Select([currentNoLanguage, scBatch], config, CurrentItemId);

        Assert.Equal(scBatch.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_CombinedContains_DifferentLanguagesSamePriority_DecidesByPackageCount()
    {
        // U19：组合条件 + Contains 让 Sc 与 ScTc 命中同一项，但语言不同、版本不比较，按计数选 ScTc 合集。
        var config = CombinedConfig([new CombinedRuleEntry("LoliHouse", EnumLanguage.Sc)],
                                    match : LanguageMatchMode.Contains);
        var sc = Candidate("LoliHouse", EnumLanguage.Sc, stableId : "guid:sc", packageVideoFileCount : 1);
        var scTc = Candidate("LoliHouse", EnumLanguage.ScTc, 9, stableId : "guid:sctc",
                             packageVideoFileCount : 4);
        var result = new CandidateRankingPolicy().Select([sc, scTc], config);

        Assert.Equal(scTc.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_AllInputPermutations_Agree()
    {
        // 同组同语言、无现任：版本降序 → 包内正片数降序；六种输入排列必须一致。
        var v1Big = Candidate("Sakurato", EnumLanguage.Sc, 1, stableId : "guid:v1-big",
                              packageVideoFileCount : 9);
        var v2Small = Candidate("Sakurato", EnumLanguage.Sc, 2, stableId : "guid:v2-small",
                                packageVideoFileCount : 1);
        var v2Big = Candidate("Sakurato", EnumLanguage.Sc, 2, stableId : "guid:v2-big",
                              packageVideoFileCount : 5);
        var policy = new CandidateRankingPolicy();
        var orders = new[]
        {
            new[] { v1Big, v2Small, v2Big },
            new[] { v1Big, v2Big, v2Small },
            new[] { v2Small, v1Big, v2Big },
            new[] { v2Small, v2Big, v1Big },
            new[] { v2Big, v1Big, v2Small },
            new[] { v2Big, v2Small, v1Big }
        };

        foreach (var candidates in orders)
        {
            var result = policy.Select(candidates, IndependentConfig());
            Assert.Equal(v2Big.FeedItemId, result.Winner!.FeedItemId);
        }
    }

    [Fact]
    public void Select_PriorityTierWithUpgradeAndOtherGroup_NoCyclicOrdering()
    {
        // 同优先级兜底层内：现任 Zulu v1、候选 Zulu v2、候选 Alpha v1，计数均为 1。
        // 分区归约先取 Zulu 冠军 v2 与 Alpha 冠军 v1，计数相同保留现任侧代表 v2；
        // 不定义成对比较器，因此不会形成循环，结果与输入顺序无关。
        var currentZuluV1 = Candidate("Zulu", EnumLanguage.Sc,
                                      stableId : "guid:zulu-v1", itemId : CurrentItemId);
        var zuluV2  = Candidate("Zulu", EnumLanguage.Sc, 2, stableId : "guid:zulu-v2");
        var alphaV1 = Candidate("Alpha", EnumLanguage.Sc, stableId : "guid:alpha-v1");
        var policy  = new CandidateRankingPolicy();

        var orders = new[]
        {
            new[] { currentZuluV1, zuluV2, alphaV1 },
            new[] { alphaV1, currentZuluV1, zuluV2 },
            new[] { zuluV2, alphaV1, currentZuluV1 }
        };

        foreach (var candidates in orders)
        {
            var result = policy.Select(candidates, IndependentConfig(), CurrentItemId);
            Assert.Equal(zuluV2.FeedItemId, result.Winner!.FeedItemId);
        }
    }

    [Fact]
    public void Select_SamePriorityWithoutCurrent_DecidesByPublicationThenStableId()
    {
        var later = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:b-later",
                              publishedAtUtc : new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var earlier = Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:a-earlier",
                                publishedAtUtc : new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var sameTime = new CandidateRankingPolicy().Select(
        [
            Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:zzz"),
            Candidate("LoliHouse", EnumLanguage.JpSc, stableId : "guid:aaa")
        ], IndependentConfig());
        var policy = new CandidateRankingPolicy();

        var byPublication = policy.Select([later, earlier], IndependentConfig());

        Assert.Equal("guid:a-earlier", byPublication.Winner!.StableId);
        Assert.Equal("guid:aaa", sameTime.Winner!.StableId);
    }

    [Fact]
    public void Select_VersionUpgradesOnlyWithinSameGroup()
    {
        var aniV9     = Candidate("ANi", EnumLanguage.JpSc, 9);
        var loliHouse = Candidate("LoliHouse", EnumLanguage.JpSc);
        var policy    = new CandidateRankingPolicy();

        var crossGroup = policy.Select([aniV9, loliHouse], IndependentConfig());
        var sameGroup = policy.Select(
        [
            Candidate("LoliHouse", EnumLanguage.JpSc),
            Candidate("LoliHouse", EnumLanguage.JpSc, 2)
        ], IndependentConfig());

        Assert.Equal("LoliHouse", crossGroup.Winner!.Group);
        Assert.Equal(2, sameGroup.Winner!.Version);
    }

    [Fact]
    public void IsStrictlyPreferred_HigherPriorityTierWinsAcrossGroupAndLanguage()
    {
        var policy   = new CandidateRankingPolicy();
        var config   = IndependentConfig();
        var loliJpSc = Candidate("LoliHouse", EnumLanguage.JpSc);
        var aniJpSc  = Candidate("ANi", EnumLanguage.JpSc);

        Assert.True(policy.IsStrictlyPreferred(loliJpSc, aniJpSc, config));
        Assert.False(policy.IsStrictlyPreferred(aniJpSc, loliJpSc, config));
    }

    [Fact]
    public void IsStrictlyPreferred_SamePartitionRequiresHigherVersionOrMoreVideos()
    {
        var policy = new CandidateRankingPolicy();
        var config = IndependentConfig();

        Assert.True(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc, 2),
                                               Candidate("LoliHouse", EnumLanguage.JpSc),
                                               config));
        Assert.False(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc),
                                                Candidate("LoliHouse", EnumLanguage.JpSc, 2),
                                                config));
        Assert.True(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc,
                                                         packageVideoFileCount : 2),
                                               Candidate("LoliHouse", EnumLanguage.JpSc),
                                               config));
        Assert.False(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc),
                                                Candidate("LoliHouse", EnumLanguage.JpSc),
                                                config));
    }

    [Fact]
    public void IsStrictlyPreferred_SameTierCrossPartitionNeedsStrictlyMoreVideos()
    {
        var policy = new CandidateRankingPolicy();
        var config = SameTierConfig();

        Assert.True(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc,
                                                         packageVideoFileCount : 2),
                                               Candidate("ANi", EnumLanguage.JpSc),
                                               config));
        Assert.False(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc,
                                                          packageVideoFileCount : 2),
                                                Candidate("ANi", EnumLanguage.JpSc,
                                                          packageVideoFileCount : 3),
                                                config));
        Assert.False(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc),
                                                Candidate("ANi", EnumLanguage.JpSc),
                                                config));
    }

    [Fact]
    public void IsStrictlyPreferred_TieBreakAndExcludedCandidatesDoNotCount()
    {
        var policy = new CandidateRankingPolicy();
        var config = IndependentConfig();

        // 同分区同版本同计数：仅发布时间 / 稳定标识更早，不构成规则严格更优。
        Assert.False(policy.IsStrictlyPreferred(Candidate("LoliHouse", EnumLanguage.JpSc,
                                                          publishedAtUtc : new DateTimeOffset(2026, 9, 1, 0, 0, 0,
                                                                                              TimeSpan.Zero),
                                                          stableId : "guid:a"),
                                                Candidate("LoliHouse", EnumLanguage.JpSc,
                                                          publishedAtUtc : new DateTimeOffset(2026, 9, 2, 0, 0, 0,
                                                                                              TimeSpan.Zero),
                                                          stableId : "guid:b"),
                                                config));

        // NeedsReview 排除者没有参与胜负：不能宣称任何一方比它严格更优。
        var reviewConfig = IndependentConfig(unmatched : UnmatchedPolicy.NeedsReview);
        var sakuratoJpSc = Candidate("Sakurato", EnumLanguage.JpSc);
        var loliJpSc     = Candidate("LoliHouse", EnumLanguage.JpSc);
        Assert.False(policy.IsStrictlyPreferred(sakuratoJpSc, loliJpSc, reviewConfig));
        Assert.False(policy.IsStrictlyPreferred(loliJpSc, sakuratoJpSc, reviewConfig));
    }

    [Fact]
    public void Select_EmptyCandidates_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
                                             new CandidateRankingPolicy().Select([], IndependentConfig()));
    }

    [Fact]
    public void Select_CombinedWithoutEntries_Throws()
    {
        var config = CombinedConfig([]);
        Assert.Throws<ArgumentException>(() =>
                                             new CandidateRankingPolicy().Select([Candidate("ANi", EnumLanguage.Sc)],
                                                      config));
    }
}
