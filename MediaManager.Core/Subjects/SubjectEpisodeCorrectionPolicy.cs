namespace MediaManager.Core.Subjects;

public sealed record SubjectEpisodeCorrection(int? SubjectId, decimal Episode, string Reason);

public sealed class SubjectEpisodeCorrectionPolicy
{
    public SubjectEpisodeCorrection Apply(int? subjectId, decimal episode)
    {
        return subjectId switch
        {
            420628 when episode > 24 => new SubjectEpisodeCorrection(486347, episode, "旧版规则：超过 24 集后映射到后续 Subject。"),
            467461 when episode > 12 => new SubjectEpisodeCorrection(529431, episode, "旧版规则：超过 12 集后映射到后续 Subject。"),
            484623 when episode > 13 => new SubjectEpisodeCorrection(subjectId, episode - 13, "旧版规则：第二季集数减去 13。"),
            _                        => new SubjectEpisodeCorrection(subjectId, episode, "未应用集数修正规则。")
        };
    }
}
