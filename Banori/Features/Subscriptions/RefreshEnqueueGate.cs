namespace Banori.Features.Subscriptions;

/// <summary>
///     单实例入队协调：串行化手动请求与到期调度共用的“检查无活动工作 → 插入工作”过程，
///     防止两种入口并发交错时为同一订阅创建多个活动工作。
///     进程内单例，依赖单实例部署假设，不提供跨实例保证。
/// </summary>
public sealed class RefreshEnqueueGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
