namespace MediaManager.Integrations;

/// <summary>从来源条目页面推断 Bangumi Subject ID 的边界。proxyId 为订阅配置的代理（null 直连）。</summary>
public interface ISubjectLinkResolver
{
    Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = null,
                                     CancellationToken cancellationToken = default);
}
