namespace MediaManager.Integrations;

/// <summary>从 Mikan 条目页面推断 Bangumi Subject ID 的边界。</summary>
public interface ISubjectLinkResolver
{
    Task<int?> ResolveSubjectIdAsync(Uri episodeUri, CancellationToken cancellationToken = default);
}
