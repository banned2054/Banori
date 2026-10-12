using Banned.AniParser.Models.Enums;

namespace MediaManager.Core.Rules.Selection;

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
///     比较顺序：规则优先级（字幕组 / 语言 / 组合条件）→ 字幕组名 → 修订版本（仅同字幕组内生效）→
///     同优先级保留现任优胜 → 发布时间早者优先 → 稳定来源标识。不使用 RSS 枚举顺序或数据库 ID 决胜。
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
    ///     同组内选出优胜。现任优胜的保留不进入排序链（排序链要求全序，否则会形成
    ///     “v2 优于 v1、现任 v1 优于他组、他组按组名优于 v2”的循环），而是显式判定：
    ///     取规则优先级最高的候选层；现任在该层内时，只有同字幕组的更高修订版本构成严格升级
    ///     （计划：不同字幕组不凭版本号跨越优先级），其余情况保留现任；现任不在该层时按
    ///     组名 → 版本 → 发布时间 → 稳定来源标识的基础全序取第一。
    /// </summary>
    private static CandidateRank SelectWinner(IReadOnlyList<CandidateRank> contenders, Guid? currentWinnerFeedItemId)
    {
        var minPriority = contenders.Min(rank => rank.Priority);
        var topTier     = contenders.Where(rank => rank.Priority == minPriority).ToList();
        var current = currentWinnerFeedItemId is null
            ? null
            : topTier.FirstOrDefault(rank => rank.Candidate.FeedItemId == currentWinnerFeedItemId);
        if (current is null)
            return topTier
                  .OrderBy(rank => rank.Candidate.Group, StringComparer.Ordinal)
                  .ThenByDescending(rank => rank.Candidate.Version)
                  .ThenBy(rank => rank.Candidate.PublishedAtUtc ?? DateTimeOffset.MaxValue)
                  .ThenBy(rank => rank.Candidate.StableId, StringComparer.Ordinal)
                  .ThenBy(rank => rank.Candidate.FeedItemId)
                  .First();

        // 同字幕组判断与规则匹配（FindGroupRank / MatchesGroup）一致不区分大小写：
        // 现任 ANi 与新候选 ani 是同组修订升级，不得视为跨组而保留现任。
        var upgrade = topTier
                     .Where(rank => string.Equals(rank.Candidate.Group, current.Candidate.Group,
                                                  StringComparison.OrdinalIgnoreCase) &&
                                    rank.Candidate.Version > current.Candidate.Version)
                     .OrderByDescending(rank => rank.Candidate.Version)
                     .ThenBy(rank => rank.Candidate.PublishedAtUtc ?? DateTimeOffset.MaxValue)
                     .ThenBy(rank => rank.Candidate.StableId, StringComparer.Ordinal)
                     .ThenBy(rank => rank.Candidate.FeedItemId)
                     .FirstOrDefault();
        return upgrade ?? current;
    }

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
