using Banned.AniParser.Models.Enums;
using Banori.Core.Rules.Selection;

namespace Banori.Tests.Rules.Selection;

public sealed class CandidateEligibilityPolicyTests
{
    private static EpisodeCandidate Candidate(string         mediaType  = "SingleEpisode",
                                              EnumResolution resolution = EnumResolution.R1080P,
                                              decimal        episode    = 3) => new()
    {
        FeedItemId = Guid.NewGuid(),
        SubjectId  = 100,
        MediaType  = mediaType,
        Episode    = episode,
        Group      = "ANi",
        Language   = EnumLanguage.JpSc,
        Resolution = resolution,
        Version    = 1
    };

    private static SelectionRuleConfig Config(params EnumResolution[] allowed)
    {
        var config = SelectionRuleConfig.CreateDefault();
        return config with { AllowedResolutions = allowed };
    }

    [Fact]
    public void Evaluate_MultipleEpisode_NeedsReview()
    {
        var decision = new CandidateEligibilityPolicy()
           .Evaluate(Candidate("MultipleEpisode"), Config(EnumResolution.R1080P));

        Assert.Equal(EligibilityStatus.NeedsReview, decision.Status);
    }

    [Fact]
    public void Evaluate_ResolutionOutsideAllowList_Ineligible()
    {
        var decision = new CandidateEligibilityPolicy().Evaluate(Candidate(resolution : EnumResolution.R720P),
                                                                 Config(EnumResolution.R1080P));

        Assert.Equal(EligibilityStatus.Ineligible, decision.Status);
        Assert.Contains("分辨率", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_EmptyAllowList_DoesNotRestrictResolution()
    {
        var decision = new CandidateEligibilityPolicy().Evaluate(Candidate(resolution : EnumResolution.R4K),
                                                                 Config());

        Assert.Equal(EligibilityStatus.Eligible, decision.Status);
    }

    [Fact]
    public void Evaluate_UnknownResolution_TreatedLikeAnyOtherValue()
    {
        var allowed = new CandidateEligibilityPolicy()
           .Evaluate(Candidate(resolution : EnumResolution.Unknown), Config(EnumResolution.R1080P));
        var permitted = new CandidateEligibilityPolicy()
           .Evaluate(Candidate(resolution : EnumResolution.Unknown), Config(EnumResolution.Unknown));

        Assert.Equal(EligibilityStatus.Ineligible, allowed.Status);
        Assert.Equal(EligibilityStatus.Eligible, permitted.Status);
    }
}
