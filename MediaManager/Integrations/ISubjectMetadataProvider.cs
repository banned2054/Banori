using MediaManager.Features.Subscriptions;
using MediaManager.Features.Anime;

namespace MediaManager.Integrations;

/// <summary>Bangumi Subject 元数据读取边界。</summary>
public interface ISubjectMetadataProvider
{
    Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default);
}
