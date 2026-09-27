using MediaManager.Features.Subscriptions;

namespace MediaManager.Integrations;

/// <summary>RSS 源读取边界，便于离线测试与副作用隔离。</summary>
public interface IFeedSourceReader
{
    Task<IReadOnlyList<FeedSourceItem>> ReadAsync(Uri feedUri, CancellationToken cancellationToken = default);
}
