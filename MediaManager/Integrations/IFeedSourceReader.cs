using MediaManager.Features.Subscriptions;

namespace MediaManager.Integrations;

/// <summary>RSS 源读取边界，便于离线测试与副作用隔离。proxyId 为 null 时直连（沿用系统代理）。</summary>
public interface IFeedSourceReader
{
    Task<IReadOnlyList<FeedSourceItem>> ReadAsync(Uri               feedUri, Guid? proxyId = null,
                                                  CancellationToken cancellationToken = default);
}
