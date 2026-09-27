using MediaManager.Features.Notifications;

namespace MediaManager.Workers;

/// <summary>
///     周期投递待发通知：按渠道独立发送并记录结果，渠道未启用或未配置时跳过保留待发。
///     投递记录持久化在数据库中，重启后自动续投；单轮失败只记录日志，不影响下一轮。
/// </summary>
public sealed class NotificationDeliveryWorker(
    IServiceScopeFactory                scopeFactory,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NotificationDeliveryService>()
                           .DeliverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "通知投递轮次发生错误，将在下一轮重试。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
