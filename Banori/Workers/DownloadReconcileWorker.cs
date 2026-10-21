using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Banori.Workers;

/// <summary>
///     周期对账下载任务：提交待提交任务、核对未确认与下载中任务、推进完成与替换。
///     每轮先恢复未落库的同集选择与未登记的下载意图（覆盖“条目已解析但任务尚未创建”的中断窗口）。
///     唤醒时机 = 空闲间隔、最早到期任务的观察时间、qBittorrent 钩子脉冲三者取最早；
///     两轮之间保持最短间隔，重复脉冲不得形成忙循环。单轮失败只记录日志，不影响下一轮；
///     下载器未配置时由服务静默跳过。
/// </summary>
public sealed class DownloadReconcileWorker(
    IServiceScopeFactory                    scopeFactory,
    ReconcilePulse                          reconcilePulse,
    TimeProvider                            timeProvider,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    DownloadTimingPolicy?                   timingPolicy = null,
    ILogger<DownloadReconcileWorker>        logger       = null) : BackgroundService
{
    private readonly DownloadTimingPolicy timing = timingPolicy ?? DownloadTimingPolicy.Default;

    private DateTimeOffset _lastRoundAtUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _lastRoundAtUtc = timeProvider.GetUtcNow();
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
                logger?.LogError(exception, "下载对账轮次发生错误，将在下一轮重试。");
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

    /// <summary>等待下一轮：按最早到期任务或空闲间隔计算唤醒时间，被对账脉冲立即唤醒；进程停止时返回。</summary>
    private async Task WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? earliestDue = null;
        try
        {
            earliestDue = await GetEarliestNextObservationAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // 调度查询失败退回空闲间隔，不影响对账本身。
            logger?.LogWarning(exception, "查询最早到期观察时间失败，退回空闲间隔。");
        }

        var delay = ComputeWakeDelay(timeProvider.GetUtcNow(), earliestDue, _lastRoundAtUtc, timing);
        while (true)
        {
            using var linked    = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var       delayTask = Task.Delay(delay, timeProvider, linked.Token);
            var       pulseTask = reconcilePulse.WaitAsync(cancellationToken);
            var       completed = await Task.WhenAny(delayTask, pulseTask);
            if (completed == pulseTask) await linked.CancelAsync();
            await completed;

            // pulse 不越过最短轮间隔：提前唤醒时继续睡剩余时间。不再重复等待新脉冲——
            // ReconcilePulse 是“至少一次”粘性语义，最短间隔后的下一轮自然会消费期间到达的脉冲。
            var remaining = timing.MinReconcileRoundInterval - (timeProvider.GetUtcNow() - _lastRoundAtUtc);
            if (remaining <= TimeSpan.Zero) return;
            delay = remaining;
        }
    }

    /// <summary>
    ///     计算下一轮唤醒延迟：空闲间隔、最早到期任务与两轮最短间隔的共同约束。
    ///     最早到期任务让等待提前（到期任务尽快被观察）；最短间隔让重复脉冲不形成忙循环。
    /// </summary>
    internal static TimeSpan ComputeWakeDelay(DateTimeOffset now,            DateTimeOffset?      earliestDue,
                                              DateTimeOffset lastRoundAtUtc, DownloadTimingPolicy timing)
    {
        var wake = timing.IdleReconcileInterval;
        if (earliestDue is { } due)
        {
            var untilDue              = due - now;
            if (untilDue < wake) wake = untilDue < TimeSpan.Zero ? TimeSpan.Zero : untilDue;
        }

        // 脉冲不能越过最短轮间隔：上一轮刚结束时连续脉冲最多把下一轮提前到最短间隔之后。
        var throttle              = timing.MinReconcileRoundInterval - (now - lastRoundAtUtc);
        if (throttle > wake) wake = throttle > timing.IdleReconcileInterval ? timing.IdleReconcileInterval : throttle;
        return wake;
    }

    /// <summary>
    ///     活动任务（含带未确认文件操作的终态任务）中最早的下一次观察时间。SQLite 不支持
    ///     DateTimeOffset 服务端聚合，取回候选列后在内存求最小值；已过期的时间不再触发提前唤醒，
    ///     残留的过期值只会随下一轮对账自然处理（worker 恢复空闲节奏）。
    /// </summary>
    private async Task<DateTimeOffset?> GetEarliestNextObservationAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var times = await db.DownloadTasks.AsNoTracking()
                            .Where(task => task.NextObservationAtUtc != null &&
                                           (task.Status          == DownloadTaskStatus.PendingSubmit ||
                                            task.Status          == DownloadTaskStatus.Submitting    ||
                                            task.Status          == DownloadTaskStatus.Downloading   ||
                                            task.Status          == DownloadTaskStatus.Paused        ||
                                            task.PendingSavePath != null                             ||
                                            task.Files.Any(file => file.PendingRenameTo != null)))
                            .Select(task => task.NextObservationAtUtc!.Value)
                            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        times = times.Where(value => value > now).ToList();
        return times.Count == 0 ? null : times.Min();
    }
}
