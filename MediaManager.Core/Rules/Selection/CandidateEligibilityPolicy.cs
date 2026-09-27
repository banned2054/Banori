namespace MediaManager.Core.Rules.Selection;

public enum EligibilityStatus
{
    /// <summary>允许参与同集竞争。</summary>
    Eligible,

    /// <summary>无法可靠判定（如多集合集），进入待确认，不自动处理。</summary>
    NeedsReview,

    /// <summary>硬性条件不满足，记录原因后排除。</summary>
    Ineligible
}

public sealed record EligibilityDecision(EligibilityStatus Status, string Reason);

/// <summary>
///     硬性过滤：决定候选是否允许参与同集竞争。被排除者记录原因；
///     排名较低的字幕组或语言不在此排除，仍参与竞争。
/// </summary>
public sealed class CandidateEligibilityPolicy
{
    public EligibilityDecision Evaluate(EpisodeCandidate candidate, SelectionRuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(config);

        if (string.Equals(candidate.MediaType, "MultipleEpisode", StringComparison.Ordinal))
            return new EligibilityDecision(EligibilityStatus.NeedsReview, "首版不自动处理多集合集。");

        if (candidate.Episode < 0)
            return new EligibilityDecision(EligibilityStatus.NeedsReview, "集数无法可靠判定，需要人工确认。");

        if (config.AllowedResolutions.Count > 0 &&
            !config.AllowedResolutions.Contains(candidate.Resolution))
            return new EligibilityDecision(EligibilityStatus.Ineligible,
                                           $"分辨率 {candidate.Resolution} 不在允许范围内。");

        return new EligibilityDecision(EligibilityStatus.Eligible, "通过硬性过滤。");
    }
}
