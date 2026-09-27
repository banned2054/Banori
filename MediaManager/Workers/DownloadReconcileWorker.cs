using MediaManager.Features.Downloads;
using MediaManager.Features.Subscriptions;

namespace MediaManager.Workers;

/// <summary>
///     周期对账下载任务：提交待提交任务、核对未确认与下载中任务、推进完成与替换。
///     每轮先恢复未落库的同集选择与未登记的下载意图（覆盖“条目已解析但任务尚未创建”的中断窗口）。
///     单轮失败只记录日志，不影响下一轮；下载器未配置时由服务静默跳过。
/// </summary>
public sealed class DownloadReconcileWorker(
    IServiceScopeFactory             scopeFactory,
    ILogger<DownloadReconcileWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CandidateSelectionService>()
                           .RecoverPendingSelectionsAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<DownloadService>()
                           .ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "下载对账轮次发生错误，将在下一轮重试。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
