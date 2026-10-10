namespace MediaManager.Features.Subscriptions;

/// <summary>
///     跨作用域的刷新唤醒信号：入队新工作后立即唤醒刷新工作线程，不必等下一轮 15 秒轮询。
///     单实例假设；脉冲按“至少送达一次”语义投递——有等待者时直接唤醒并同时置位粘性标记
///     （等待者可能已被 WhenAny 抛弃，宁可让下一次等待多重发一轮空批，不可丢脉冲）。
/// </summary>
public sealed class RefreshPulse
{
    private readonly object                      _lock = new();
    private          bool                        _signaled;
    private          TaskCompletionSource<bool>? _waiter;

    public void Pulse()
    {
        TaskCompletionSource<bool>? waiter;
        lock (_lock)
        {
            _signaled = true;
            // 已唤醒的等待者必须摘除：留着会让后续 WaitAsync 拿到已完成的任务，工作线程忙循环。
            waiter  = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult(true);
    }

    /// <summary>等待下一次唤醒；已在等待前发生的唤醒立即返回。</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_signaled)
            {
                _signaled = false;
                return Task.CompletedTask;
            }

            // 每次等待都创建新的等待者，绝不复用：已完成的任务会让等待立即返回。
            _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _waiter.Task.WaitAsync(cancellationToken);
        }
    }
}
