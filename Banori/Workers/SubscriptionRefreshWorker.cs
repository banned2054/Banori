using Banori.Features.Subscriptions;

namespace Banori.Workers;

/// <summary>
///     周期调度订阅刷新：每轮先补齐到期工作，再串行消费，直到没有待执行工作。
///     入队新工作（订阅添加、手动刷新）时被唤醒立即处理，否则按空闲间隔轮询。
///     启动时先把上次进程遗留的执行中工作恢复为待执行，恢复成功后才开始消费；
///     恢复失败时退避等待并重试，直到成功或进程停止。
/// </summary>
public sealed class SubscriptionRefreshWorker(
    IServiceScopeFactory               scopeFactory,
    RefreshPulse                       refreshPulse,
    TimeProvider                       timeProvider,
    ILogger<SubscriptionRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan InitialRecoveryRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRecoveryRetryDelay     = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdlePollInterval          = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedWorksAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunBatchAsync(stoppingToken);
            try
            {
                await WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>等待下一轮：被刷新脉冲立即唤醒，或空闲间隔到期；进程停止时返回。</summary>
    private async Task WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        using var linked    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       delay     = Task.Delay(IdlePollInterval, timeProvider, linked.Token);
        var       pulseTask = refreshPulse.WaitAsync(cancellationToken);
        var       completed = await Task.WhenAny(delay, pulseTask);
        if (completed == pulseTask) await linked.CancelAsync();
        await completed;
    }

    /// <summary>
    ///     遗留执行中工作既不能被消费者领取，又会阻止同一订阅重新入队，
    ///     因此恢复必须成功后才进入消费循环；临时数据库错误导致失败时有界退避等待后重试。
    /// </summary>
    private async Task RecoverInterruptedWorksAsync(CancellationToken cancellationToken)
    {
        var retryDelay = InitialRecoveryRetryDelay;
        while (true)
        {
            try
            {
                await using var scope          = scopeFactory.CreateAsyncScope();
                var             refreshService = scope.ServiceProvider.GetRequiredService<IRefreshWorkConsumer>();
                await refreshService.RecoverInterruptedWorksAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "启动恢复遗留刷新工作失败，{RetryDelaySeconds} 秒后重试。",
                                retryDelay.TotalSeconds);
            }

            await Task.Delay(retryDelay, timeProvider, cancellationToken);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2,
                                                       MaxRecoveryRetryDelay.TotalSeconds));
        }
    }

    private async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using (var schedulingScope = scopeFactory.CreateAsyncScope())
            {
                var subscriptionService =
                    schedulingScope.ServiceProvider.GetRequiredService<SubscriptionService>();
                await subscriptionService.EnqueueDueRefreshesAsync(cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var processingScope = scopeFactory.CreateAsyncScope();
                var refreshService =
                    processingScope.ServiceProvider.GetRequiredService<IRefreshWorkConsumer>();
                var result = await refreshService.ProcessNextAsync(cancellationToken);
                if (result is null) break;
                if (result.Error is null)
                    logger.LogInformation("刷新工作 {WorkId} 完成：新增 {Added}，重复 {Duplicates}，条目失败 {Failures}。",
                                          result.WorkId, result.AddedItems, result.DuplicateItems, result.FailedItems);
                else
                    logger.LogWarning("刷新工作 {WorkId} 失败：{Error}。新增 {Added}，重复 {Duplicates}，条目失败 {Failures}。",
                                      result.WorkId, result.Error, result.AddedItems, result.DuplicateItems,
                                      result.FailedItems);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "订阅刷新批次发生错误，将在下一轮重试。");
        }
    }
}
