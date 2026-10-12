using Banori.Features.Downloads;
using Banori.Features.Subscriptions;

namespace Banori.Workers;

/// <summary>
///     周期对账下载任务：提交待提交任务、核对未确认与下载中任务、推进完成与替换。
///     每轮先恢复未落库的同集选择与未登记的下载意图（覆盖“条目已解析但任务尚未创建”的中断窗口）。
///     qBittorrent 钩子可通过 ReconcilePulse 立即唤醒一轮；单轮失败只记录日志，不影响下一轮；
///     下载器未配置时由服务静默跳过。
/// </summary>
public sealed class DownloadReconcileWorker(
    IServiceScopeFactory             scopeFactory,
    ReconcilePulse                   reconcilePulse,
    TimeProvider                     timeProvider,
    ILogger<DownloadReconcileWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
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

            try
            {
                await WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>等待下一轮：被对账脉冲立即唤醒，或空闲间隔到期；进程停止时返回。</summary>
    private async Task WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        using var linked    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       delay     = Task.Delay(IdlePollInterval, timeProvider, linked.Token);
        var       pulseTask = reconcilePulse.WaitAsync(cancellationToken);
        var       completed = await Task.WhenAny(delay, pulseTask);
        if (completed == pulseTask) await linked.CancelAsync();
        await completed;
    }
}
