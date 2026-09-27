using MediaManager.Core.Subjects;

namespace MediaManager.Tests.Subjects;

public sealed class SubjectEpisodeCorrectionPolicyTests
{
    private readonly SubjectEpisodeCorrectionPolicy _policy = new();

    [Theory]
    [InlineData(420628, 25, 486347, 25)]
    [InlineData(467461, 13, 529431, 13)]
    [InlineData(484623, 14, 484623, 1)]
    [InlineData(420628, 24, 420628, 24)]
    public void Apply_PreservesLegacyCorrections(
        int subjectId,
        int episode,
        int expectedSubjectId,
        int expectedEpisode)
    {
        var result = _policy.Apply(subjectId, episode);

        Assert.Equal(expectedSubjectId, result.SubjectId);
        Assert.Equal(expectedEpisode, result.Episode);
    }
}
