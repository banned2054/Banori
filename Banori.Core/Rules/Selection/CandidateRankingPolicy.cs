using Banned.AniParser.Models.Enums;

namespace Banori.Core.Rules.Selection;

/// <summary>一个候选在同组内的排名结果；ExcludedReason 非空表示未命中规则或不合格，不参与胜负。</summary>
public sealed record CandidateRank(EpisodeCandidate Candidate, int Priority, string Basis, string? ExcludedReason)
{
    public bool IsExcluded => ExcludedReason is not null;
}

public sealed record GroupSelectionResult(
    int                          SubjectId,
    string                       MediaType,
    decimal                      Episode,
    EpisodeCandidate?            Winner,
    string                       WinnerBasis,
    IReadOnlyList<CandidateRank> Ranking)
{
    public bool HasWinner => Winner is not null;
}

/// <summary>
///     同集组内择优：按规则配置比较候选，选出一组唯一的优胜者。
///     规则优先级（字幕组 / 语言 / 组合条件）最高的候选层内，先按字幕组（忽略大小写）与语言
///     （枚举值全等，null 只与 null 相等）分区：分区内按修订版本 → 包内初始正片数 → 发布时间 →
///     稳定来源标识选版本冠军，分区冠军之间再按包内初始正片数 → 发布时间 → 稳定来源标识比较。
///     版本只在同组同语言的分区内比较；跨组/跨语言同优先级按分区冠军的初始正片数决胜，
///     不按组名/语言名排序。现任只在没有严格更优代表时保留：计数相同时保留现任侧的分区冠军。
///     不使用 RSS 枚举顺序或数据库 ID 决胜。
/// </summary>
public sealed class CandidateRankingPolicy
{
    public GroupSelectionResult Select(IReadOnlyList<EpisodeCandidate> candidates,
                                       SelectionRuleConfig             config,
                                       Guid?                           currentWinnerFeedItemId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(config);
        if (candidates.Count == 0)
            throw new ArgumentException("候选列表不能为空。", nameof(candidates));
        if (config.Mode == SelectionMode.Combined && config.CombinedEntries.Count == 0)
            throw new ArgumentException("组合模式必须配置至少一个条件，可为通配兜底。", nameof(config));

        var subjectId = candidates[0].SubjectId;
        var mediaType = candidates[0].MediaType;
        var episode   = candidates[0].Episode;

        var ranked = new List<CandidateRank>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var (priority, basis, excludedReason) = RankCandidate(candidate, config);
            ranked.Add(new CandidateRank(candidate, priority, basis, excludedReason));
        }

        var               contenders  = ranked.Where(rank => !rank.IsExcluded).ToList();
        EpisodeCandidate? winner      = null;
        var               winnerBasis = string.Empty;
        if (contenders.Count > 0)
        {
            var best = SelectWinner(contenders, currentWinnerFeedItemId);
            winner      = best.Candidate;
            winnerBasis = best.Basis;
        }

        return new GroupSelectionResult(subjectId, mediaType, episode, winner, winnerBasis, ranked);
    }

    /// <summary>
    ///     规则侧严格优于：挑战者处于更高的规则优先级层，或同一优先级层内同分区版本更高、
    ///     初始候选正片数严格更多时成立；同层跨分区只认正片数严格更多。发布时间、稳定标识与
    ///     数据库 ID 只是同层平级的确定性兜底，不构成“规则严格更优”，不得据此建立替换关系。
    ///     任一方被规则排除（如未命中 NeedsReview）时也不成立：排除者没有参与胜负，不能宣称
    ///     挑战者比它更优。
    /// </summary>
    public bool IsStrictlyPreferred(EpisodeCandidate challenger, EpisodeCandidate incumbent,
                                    SelectionRuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(challenger);
        ArgumentNullException.ThrowIfNull(incumbent);
        ArgumentNullException.ThrowIfNull(config);

        var challengerRank = RankCandidate(challenger, config);
        var incumbentRank  = RankCandidate(incumbent, config);
        if (challengerRank.ExcludedReason is not null || incumbentRank.ExcludedReason is not null) return false;
        if (challengerRank.Priority != incumbentRank.Priority) return challengerRank.Priority < incumbentRank.Priority;

        if (PartitionKey(challenger) == PartitionKey(incumbent))
            return challenger.Version > incumbent.Version ||
                   (challenger.Version == incumbent.Version &&
                    challenger.PackageVideoFileCount > incumbent.PackageVideoFileCount);

        return challenger.PackageVideoFileCount > incumbent.PackageVideoFileCount;
    }

    /// <summary>
    ///     同组内选出优胜。现任优胜的保留不进入排序链（排序链要求全序，否则会形成
    ///     “v2 优于 v1、现任 v1 优于他组、他组按组名优于 v2”的循环），而是做一次两层归约：
    ///     取规则优先级最高的候选层，按字幕组（忽略大小写）与语言（枚举值全等）分区，分区内
    ///     选版本冠军；冠军之间按初始正片数 → 发布时间 → 稳定来源标识选全局冠军。版本只在
    ///     同分区内比较，不定义跨分区的成对比较器。现任不在最高层时取全局冠军；在最高层时
    ///     取现任所在分区的冠军，仅当全局冠军的初始正片数严格更多才替换，计数相同保留现任侧。
    /// </summary>
    private static CandidateRank SelectWinner(IReadOnlyList<CandidateRank> contenders, Guid? currentWinnerFeedItemId)
    {
        var minPriority = contenders.Min(rank => rank.Priority);
        var topTier     = contenders.Where(rank => rank.Priority == minPriority).ToList();

        var partitionChampions = topTier.GroupBy(rank => PartitionKey(rank.Candidate))
                                        .Select(partition => OrderWithin(partition).First())
                                        .ToList();
        var overallChampion = OrderAcross(partitionChampions).First();

        var current = currentWinnerFeedItemId is null
            ? null
            : topTier.FirstOrDefault(rank => rank.Candidate.FeedItemId == currentWinnerFeedItemId);
        if (current is null) return overallChampion;

        var incumbentKey      = PartitionKey(current.Candidate);
        var incumbentChampion = partitionChampions.First(champion => PartitionKey(champion.Candidate) == incumbentKey);

        // 冠军之间已按初始正片数降序，全局冠军不会少于现任分区冠军；计数相同说明没有严格更优，
        // 保留现任侧代表（现任本身，或同分区内版本/计数更高的升级者）。
        return overallChampion.Candidate.PackageVideoFileCount > incumbentChampion.Candidate.PackageVideoFileCount
            ? overallChampion
            : incumbentChampion;
    }

    /// <summary>
    ///     分区键：字幕组按忽略大小写的等价类归一，语言按枚举值精确相等；null 语言自成一个分区。
    /// </summary>
    private static (string Group, EnumLanguage? Language) PartitionKey(EpisodeCandidate candidate) =>
        (candidate.Group.ToUpperInvariant(), candidate.Language);

    /// <summary>分区内的全序：版本 → 初始正片数 → 发布时间 → 稳定来源标识 → FeedItemId。</summary>
    private static IOrderedEnumerable<CandidateRank> OrderWithin(IEnumerable<CandidateRank> partition) =>
        partition.OrderByDescending(rank => rank.Candidate.Version)
                 .ThenByDescending(rank => rank.Candidate.PackageVideoFileCount)
                 .ThenBy(rank => rank.Candidate.PublishedAtUtc ?? DateTimeOffset.MaxValue)
                 .ThenBy(rank => rank.Candidate.StableId, StringComparer.Ordinal)
                 .ThenBy(rank => rank.Candidate.FeedItemId);

    /// <summary>分区冠军之间的全序：初始正片数 → 发布时间 → 稳定来源标识 → FeedItemId。</summary>
    private static IOrderedEnumerable<CandidateRank> OrderAcross(IEnumerable<CandidateRank> champions) =>
        champions.OrderByDescending(rank => rank.Candidate.PackageVideoFileCount)
                 .ThenBy(rank => rank.Candidate.PublishedAtUtc ?? DateTimeOffset.MaxValue)
                 .ThenBy(rank => rank.Candidate.StableId, StringComparer.Ordinal)
                 .ThenBy(rank => rank.Candidate.FeedItemId);

    private static (int Priority, string Basis, string? ExcludedReason) RankCandidate(
        EpisodeCandidate candidate, SelectionRuleConfig config)
    {
        if (config.Mode == SelectionMode.Combined) return RankCombined(candidate, config);

        return RankIndependent(candidate, config);
    }

    private static (int, string, string?) RankIndependent(EpisodeCandidate candidate, SelectionRuleConfig config)
    {
        var groupRank    = FindGroupRank(candidate.Group, config);
        var languageRank = FindLanguageRank(candidate.Language, config);

        if (groupRank is null && languageRank is null)
            return DescribeUnmatched(config, "字幕组与语言均未列入规则");

        if (groupRank is null)
            return DescribeUnmatched(config, $"字幕组“{candidate.Group}”未列入规则");

        if (languageRank is null)
            return DescribeUnmatched(config, $"语言 {candidate.Language} 未列入规则");

        var priority = config.DimensionOrder == SelectionDimensionOrder.GroupFirst
            ? groupRank.Value    * 1000 + languageRank.Value
            : languageRank.Value * 1000 + groupRank.Value;
        var basis =
            $"{(config.DimensionOrder == SelectionDimensionOrder.GroupFirst ? "先字幕组" : "先语言")}" +
            $"：字幕组序 {groupRank + 1}（{candidate.Group}），语言序 {languageRank + 1}（{candidate.Language}），修订 v{candidate.Version}";
        return (priority, basis, null);
    }

    private static (int, string, string?) RankCombined(EpisodeCandidate candidate, SelectionRuleConfig config)
    {
        for (var index = 0; index < config.CombinedEntries.Count; index++)
        {
            var entry = config.CombinedEntries[index];
            if (!MatchesGroup(entry.Group, candidate.Group)) continue;
            if (entry.Language is not null &&
                !LanguageComposition.Matches(entry.Language.Value, candidate.Language, config.LanguageMatch))
                continue;

            var basis = $"组合条件第 {index + 1} 项：" +
                        $"{entry.Group ?? "任意字幕组"} + {entry.Language?.ToString() ?? "任意语言"}，修订 v{candidate.Version}";
            return (index, basis, null);
        }

        return DescribeUnmatched(config, "未命中任何组合条件");
    }

    private static (int Priority, string Basis, string? ExcludedReason) DescribeUnmatched(
        SelectionRuleConfig config, string detail)
    {
        if (config.Unmatched == UnmatchedPolicy.NeedsReview)
            return (int.MaxValue, string.Empty, $"{detail}（规则版本 {config.Version}），进入待确认，不参与自动胜负。");

        return (int.MaxValue,
                $"{detail}，按兜底顺序参与竞争（规则版本 {config.Version}）。",
                null);
    }

    /// <summary>字幕组匹配不区分大小写；返回在 GroupOrder 中的序号，未列出时为 null。</summary>
    private static int? FindGroupRank(string group, SelectionRuleConfig config)
    {
        for (var index = 0; index < config.GroupOrder.Count; index++)
            if (string.Equals(config.GroupOrder[index], group, StringComparison.OrdinalIgnoreCase))
                return index;

        return null;
    }

    private static int? FindLanguageRank(EnumLanguage? language, SelectionRuleConfig config)
    {
        if (language is null) return null;

        for (var index = 0; index < config.LanguageOrder.Count; index++)
            if (LanguageComposition.Matches(config.LanguageOrder[index], language, config.LanguageMatch))
                return index;

        return null;
    }

    private static bool MatchesGroup(string? configuredGroup, string candidateGroup) =>
        configuredGroup is null ||
        string.Equals(configuredGroup, candidateGroup, StringComparison.OrdinalIgnoreCase);
}
