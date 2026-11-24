namespace Banori.Features.Downloads;

public sealed record DownloadTaskChangedEvent(
    IReadOnlyCollection<Guid> TaskIds,
    bool                      AffectsListOrderOrMembership);

/// <summary>
///     下载任务数据库提交后的轻量变更通知：同一进程内的列表/详情组件据此按需刷新，
///     单个订阅者异常不影响其他订阅者与数据提交线程。
/// </summary>
public sealed class DownloadTaskChangeNotifier
{
    private readonly Lock                                               _lock     = new();
    private readonly Dictionary<long, Action<DownloadTaskChangedEvent>> _handlers = [];
    private          long                                               _nextId;

    public IDisposable Subscribe(Action<DownloadTaskChangedEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        long id;
        lock (_lock)
        {
            id            = ++_nextId;
            _handlers[id] = handler;
        }

        return new Subscription(this, id);
    }

    public void Notify(DownloadTaskChangedEvent changedEvent)
    {
        Action<DownloadTaskChangedEvent>[] snapshot;
        lock (_lock)
        {
            if (_handlers.Count == 0) return;
            snapshot = [.. _handlers.Values];
        }

        foreach (var handler in snapshot)
            try
            {
                handler(changedEvent);
            }
            catch
            {
                // 单个订阅者异常不得影响其他订阅者或打断提交链路。
            }
    }

    public void Notify(IReadOnlyCollection<Guid> taskIds, bool affectsListOrderOrMembership) =>
        Notify(new DownloadTaskChangedEvent(taskIds, affectsListOrderOrMembership));

    public void Notify(Guid taskId, bool affectsListOrderOrMembership) =>
        Notify(new DownloadTaskChangedEvent([taskId], affectsListOrderOrMembership));

    private void Unsubscribe(long id)
    {
        lock (_lock) _handlers.Remove(id);
    }

    private sealed class Subscription(DownloadTaskChangeNotifier owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(id);
        }
    }
}
