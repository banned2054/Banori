using Banned.AniParser.Models.Enums;
using MediaManager.Core.Rules.Selection;

namespace MediaManager.Tests.Rules.Selection;

/// <summary>同集组内择优规则的样本矩阵：排列不变性、维度顺序、组合匹配、版本边界与稳定决胜。</summary>
public sealed class CandidateRankingPolicyTests
{
    private static readonly Guid CurrentItemId = Guid.NewGuid();

    private static EpisodeCandidate Candidate(string group, EnumLanguage? language, int version = 1,
                                              EnumResolution resolution = EnumResolution.R1080P,
                                              DateTimeOffset? publishedAtUtc = null,
                                              string? stableId = null, Guid? itemId = null)
    {
        return new EpisodeCandidate
        {
            FeedItemId     = itemId ?? Guid.NewGuid(),
            SubjectId      = 100,
            MediaType      = "SingleEpisode",
            Episode        = 3,
            Group          = group,
            Language       = language,
            Resolution     = resolution,
            Version        = version,
            PublishedAtUtc = publishedAtUtc,
            StableId       = stableId ?? $"guid:{group}-{language}-v{version}"
        };
    }

    private static SelectionRuleConfig IndependentConfig(
        SelectionDimensionOrder order = SelectionDimensionOrder.GroupFirst,
        LanguageMatchMode match = LanguageMatchMode.Exact,
        UnmatchedPolicy unmatched = UnmatchedPolicy.LowestPriority,
        EnumLanguage[]? languageOrder = null,
        int version = 1)
    {
        return new SelectionRuleConfig
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
    }

    private static SelectionRuleConfig CombinedConfig(
        IReadOnlyList<CombinedRuleEntry> entries,
        LanguageMatchMode match = LanguageMatchMode.Exact,
        UnmatchedPolicy unmatched = UnmatchedPolicy.NeedsReview)
    {
        return new SelectionRuleConfig
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
    }

    [Fact]
    public void Select_AnivalsHigherGroup_AnyInputOrderPicksSameWinner()
    {
        var anibaba  = Candidate("ANi", EnumLanguage.JpSc);
        var loliHouse = Candidate("LoliHouse", EnumLanguage.JpSc);
        var policy   = new CandidateRankingPolicy();

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
                                          IndependentConfig(order: SelectionDimensionOrder.LanguageFirst));

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
                                        CombinedConfig(entries, unmatched: UnmatchedPolicy.NeedsReview));
        var lowest = policy.Select([Candidate("LoliHouse", EnumLanguage.Sc), Candidate("ANi", EnumLanguage.Sc)],
                                   CombinedConfig(entries, unmatched: UnmatchedPolicy.LowestPriority));

        Assert.False(needsReview.HasWinner);
        Assert.All(needsReview.Ranking, rank => Assert.True(rank.IsExcluded));
        Assert.True(lowest.HasWinner);
        Assert.Equal("ANi", lowest.Winner!.Group);
    }

    [Fact]
    public void Select_LanguageMatch_ExactVersusContains()
    {
        var scTc = Candidate("LoliHouse", EnumLanguage.ScTc, stableId: "guid:sctc");
        var sc   = Candidate("LoliHouse", EnumLanguage.Sc, stableId: "guid:sc");
        var policy = new CandidateRankingPolicy();

        var exact = policy.Select([scTc, sc], IndependentConfig(match: LanguageMatchMode.Exact));
        var contains = policy.Select([scTc, sc], IndependentConfig(match: LanguageMatchMode.Contains));

        Assert.Equal("guid:sc", exact.Winner!.StableId);
        Assert.Contains("兜底", exact.Ranking.Single(r => r.Candidate.StableId == "guid:sctc").Basis,
                        StringComparison.Ordinal);
        Assert.All(contains.Ranking, rank => Assert.False(rank.IsExcluded));
        Assert.Contains("语言序 2", contains.WinnerBasis, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_UnknownGroupAndLanguage_FollowUnmatchedPolicy()
    {
        var unknownGroup = Candidate("Sakurato", EnumLanguage.JpSc);
        var unknownLanguage = Candidate("LoliHouse", null);
        var policy = new CandidateRankingPolicy();

        var lowest = policy.Select([unknownGroup, unknownLanguage], IndependentConfig());
        var review = policy.Select([unknownGroup, unknownLanguage],
                                   IndependentConfig(unmatched: UnmatchedPolicy.NeedsReview));

        Assert.True(lowest.HasWinner);
        Assert.Equal("LoliHouse", lowest.Winner!.Group);
        Assert.False(review.HasWinner);
        Assert.All(review.Ranking, rank => Assert.True(rank.IsExcluded));
    }

    [Fact]
    public void Select_SamePriority_KeepsCurrentWinner()
    {
        var existing = Candidate("LoliHouse", EnumLanguage.JpSc, stableId: "guid:existing",
                                 publishedAtUtc: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                                 itemId: CurrentItemId);
        var challenger = Candidate("LoliHouse", EnumLanguage.JpSc, stableId: "guid:challenger",
                                   publishedAtUtc: new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([challenger, existing], IndependentConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SamePriorityAcrossGroups_KeepsCurrentWinnerOverAlphabeticalReplacement()
    {
        // 两个未列入规则的字幕组同处兜底桶：字母序更小的新候选不得按名字替换现任优胜。
        var currentAni = Candidate("ANi", EnumLanguage.Sc, stableId: "guid:current-ani", itemId: CurrentItemId);
        var alphabeticalFirst = Candidate("ASakura", EnumLanguage.Sc, stableId: "guid:asakura");
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([alphabeticalFirst, currentAni], IndependentConfig(), CurrentItemId);

        Assert.Equal(CurrentItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SameGroupRevisionUpgrade_OverridesCurrentWinner()
    {
        // 现任优胜是 v1 时，同字幕组的 v2 是严格升级，必须胜出；现任保留不得阻止版本升级。
        var currentV1 = Candidate("LoliHouse", EnumLanguage.JpSc, version: 1,
                                  stableId: "guid:current-v1", itemId: CurrentItemId);
        var newV2 = Candidate("LoliHouse", EnumLanguage.JpSc, version: 2, stableId: "guid:new-v2");
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([currentV1, newV2], IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_SameGroupUpgrade_IgnoresGroupCase()
    {
        // 字幕组匹配不区分大小写：现任 ANi v1 与新候选 ani v2 是同组修订升级，不得视为跨组保留现任。
        var currentV1 = Candidate("ANi", EnumLanguage.JpSc, version: 1, stableId: "guid:current-ani-v1",
                                  itemId: CurrentItemId);
        var newV2 = Candidate("ani", EnumLanguage.JpSc, version: 2, stableId: "guid:ani-lower-v2");
        var policy = new CandidateRankingPolicy();

        var result = policy.Select([currentV1, newV2], IndependentConfig(), CurrentItemId);

        Assert.Equal(newV2.FeedItemId, result.Winner!.FeedItemId);
    }

    [Fact]
    public void Select_PriorityTierWithUpgradeAndOtherGroup_NoCyclicOrdering()
    {
        // 同优先级兜底桶内：现任 Zulu v1、候选 Zulu v2、候选 Alpha v1。
        // 若现任保留进入排序链会形成 v2>v1、现任>Alpha、Alpha>Zulu v2 的循环，导致结果随输入顺序变化。
        var currentZuluV1 = Candidate("Zulu", EnumLanguage.Sc, version: 1,
                                      stableId: "guid:zulu-v1", itemId: CurrentItemId);
        var zuluV2 = Candidate("Zulu", EnumLanguage.Sc, version: 2, stableId: "guid:zulu-v2");
        var alphaV1 = Candidate("Alpha", EnumLanguage.Sc, version: 1, stableId: "guid:alpha-v1");
        var policy = new CandidateRankingPolicy();

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
        var later = Candidate("LoliHouse", EnumLanguage.JpSc, stableId: "guid:b-later",
                              publishedAtUtc: new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var earlier = Candidate("LoliHouse", EnumLanguage.JpSc, stableId: "guid:a-earlier",
                                publishedAtUtc: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var sameTime = new CandidateRankingPolicy().Select(
        [
            Candidate("LoliHouse", EnumLanguage.JpSc, stableId: "guid:zzz"),
            Candidate("LoliHouse", EnumLanguage.JpSc, stableId: "guid:aaa")
        ], IndependentConfig());
        var policy = new CandidateRankingPolicy();

        var byPublication = policy.Select([later, earlier], IndependentConfig());

        Assert.Equal("guid:a-earlier", byPublication.Winner!.StableId);
        Assert.Equal("guid:aaa", sameTime.Winner!.StableId);
    }

    [Fact]
    public void Select_VersionUpgradesOnlyWithinSameGroup()
    {
        var aniV9     = Candidate("ANi", EnumLanguage.JpSc, version: 9);
        var loliHouse = Candidate("LoliHouse", EnumLanguage.JpSc, version: 1);
        var policy    = new CandidateRankingPolicy();

        var crossGroup = policy.Select([aniV9, loliHouse], IndependentConfig());
        var sameGroup  = policy.Select(
        [
            Candidate("LoliHouse", EnumLanguage.JpSc, version: 1),
            Candidate("LoliHouse", EnumLanguage.JpSc, version: 2)
        ], IndependentConfig());

        Assert.Equal("LoliHouse", crossGroup.Winner!.Group);
        Assert.Equal(2, sameGroup.Winner!.Version);
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
            new CandidateRankingPolicy().Select([Candidate("ANi", EnumLanguage.Sc)], config));
    }
}
