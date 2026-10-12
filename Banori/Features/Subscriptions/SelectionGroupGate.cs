namespace Banori.Features.Subscriptions;

/// <summary>
///     同集组级串行化门闸：进程内单例，让同一组的“计划 + 提交”跨 DI 作用域互斥。
///     择优服务本身是 Scoped（刷新 worker 与 Blazor 电路各持一个实例），门闸若随实例
///     创建就只对单个作用域生效，跨作用域的同组并发仍会用过期快照互相覆盖，
///     因此门闸必须独立为单例注入。
///     组键（作品 × 集数）随订阅内容无限增长，空闲组的信号量不能常驻：等待与持有
///     都计入引用，最后一个引用释放时移除该组的信号量。移除只发生在计数归零时，
///     归零后新进入者会创建新信号量，互斥不被破坏。
/// </summary>
public sealed class SelectionGroupGate
{
    private readonly Dictionary<SelectionGroupKey, GatedGroup> _groups = [];
    private readonly object                                    _sync   = new();

    /// <summary>当前仍在字典中登记（被持有、被等待或待回收）的组数；供测试与观测。</summary>
    public int TrackedGroupCount
    {
        get
        {
            lock (_sync)
            {
                return _groups.Count;
            }
        }
    }

    /// <summary>
    ///     进入指定组的串行区。等待与持有期间该组保持登记；未取得信号量就被取消时只回收
    ///     本次引用（不归还信号量——从未取得就无所谓归还），不会遗留空计数条目。
    /// </summary>
    public async Task WaitAsync(SelectionGroupKey key, CancellationToken cancellationToken = default)
    {
        GatedGroup group;
        lock (_sync)
        {
            if (!_groups.TryGetValue(key, out group!))
                _groups[key] = group = new GatedGroup();
            group.RefCount++;
        }

        try
        {
            await group.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Abandon(group, key);
            throw;
        }
    }

    /// <summary>
    ///     离开指定组的串行区：先归还信号量再回收引用。归还必须在递减之前——递减归零
    ///     会移除并 Dispose 信号量，且被唤醒的等待者仍持引用、条目不会被移除。
    ///     未成功进入就调用（或重复调用）会抛 SemaphoreFullException，按误用暴露。
    /// </summary>
    public void Release(SelectionGroupKey key)
    {
        GatedGroup group;
        lock (_sync)
        {
            if (!_groups.TryGetValue(key, out group!)) return;
        }

        // 自身引用未归还前条目不可能被移除，此处拿到的信号量不会被并发 Dispose。
        group.Semaphore.Release();
        Abandon(group, key);
    }

    /// <summary>回收一次引用；归零时移除条目并释放信号量。等待者各自持引用，归零即无人再用。</summary>
    private void Abandon(GatedGroup group, SelectionGroupKey key)
    {
        lock (_sync)
        {
            if (--group.RefCount > 0) return;
            _groups.Remove(key);
            group.Semaphore.Dispose();
        }
    }

    private sealed class GatedGroup
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        public int RefCount;
    }
}
