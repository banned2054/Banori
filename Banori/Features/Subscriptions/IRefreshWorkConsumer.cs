namespace Banori.Features.Subscriptions;

/// <summary>
///     订阅刷新的后台消费入口：启动恢复遗留工作，再逐项领取并执行。
///     Worker 依赖该接口调用刷新服务，测试可注入替身隔离恢复与消费行为。
/// </summary>
public interface IRefreshWorkConsumer
{
    /// <summary>启动恢复：消费开始前，把上次进程遗留的执行中工作恢复为待执行。</summary>
    Task RecoverInterruptedWorksAsync(CancellationToken cancellationToken = default);

    /// <summary>领取并执行一项刷新工作；没有待执行工作时返回 null。</summary>
    Task<RefreshResult?> ProcessNextAsync(CancellationToken cancellationToken = default);
}
