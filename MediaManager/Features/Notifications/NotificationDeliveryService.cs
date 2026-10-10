using MediaManager.Data;
using MediaManager.Features.Proxies;
using MediaManager.Features.Settings;
using MediaManager.Integrations.Notifications;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Notifications;

/// <summary>
///     投递待发通知：按渠道独立发送与记录结果，渠道未启用或未配置时跳过并保留 Pending。
///     单条失败进入指数退避重试，重试用尽置为 Failed 终态（投递记录页可人工重试）。
///     远端已接收而响应丢失时会重复投递（at-least-once），这是如实记录的设计边界而非缺陷。
/// </summary>
public sealed class NotificationDeliveryService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ManageTelegramSettings                  telegramSettings,
    ManageGotifySettings                    gotifySettings,
    ITelegramSender                         telegramSender,
    IGotifySender                           gotifySender,
    ProxyService                            proxyService,
    TimeProvider                            timeProvider,
    ILogger<NotificationDeliveryService>    logger)
{
    /// <summary>自动重试上限：达到后置为 Failed 终态，等待人工重试。</summary>
    public const int MaxAttempts = 10;

    /// <summary>退避封顶分钟数。</summary>
    public const int BackoffCapMinutes = 30;

    /// <summary>单轮最多投递条数，避免一轮长时间占用。</summary>
    public const int TakeLimit = 100;

    public async Task DeliverPendingAsync(CancellationToken cancellationToken = default)
    {
        List<NotificationDelivery> deliveries;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var pending = await db.NotificationDeliveries.AsNoTracking()
                                  .Where(item => item.Status == NotificationDeliveryStatus.Pending)
                                  .ToListAsync(cancellationToken);
            // SQLite 不支持 DateTimeOffset 服务端比较与排序：到期过滤与先后排序在内存完成。
            deliveries = pending.Where(item => item.NextAttemptAtUtc is null ||
                                               item.NextAttemptAtUtc <= timeProvider.GetUtcNow())
                                .OrderBy(item => item.CreatedAtUtc)
                                .Take(TakeLimit)
                                .ToList();
        }

        if (deliveries.Count == 0) return;

        var telegram = await telegramSettings.GetAsync(cancellationToken);
        var gotify   = await gotifySettings.GetAsync(cancellationToken);
        // 渠道代理按批解析一次：代理被删除时回退 null（直连），不让失效引用阻塞投递。
        var telegramProxyUrl = await proxyService.ResolveProxyUrlAsync(telegram.ProxyId, cancellationToken);
        var gotifyProxyUrl   = await proxyService.ResolveProxyUrlAsync(gotify.ProxyId, cancellationToken);

        foreach (var delivery in deliveries)
        {
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                await DeliverOneAsync(delivery, telegram, gotify, telegramProxyUrl, gotifyProxyUrl,
                                      cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "投递 {DeliveryId} 失败（第 {Attempt} 次）。",
                                  delivery.Id, delivery.AttemptCount + 1);
                await RecordFailureAsync(delivery.Id, exception.Message, cancellationToken);
            }
        }
    }

    /// <summary>人工重试：Failed 的投递重新进入待发队列，沿用原内容并清零尝试计数。</summary>
    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.NotificationDeliveries
                               .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                    ?? throw new KeyNotFoundException("找不到指定投递记录。");
        if (delivery.Status != NotificationDeliveryStatus.Failed)
            throw new InvalidOperationException("只有重试用尽的失败记录可以人工重试。");

        delivery.Status           = NotificationDeliveryStatus.Pending;
        delivery.AttemptCount     = 0;
        delivery.NextAttemptAtUtc = null;
        delivery.LastError        = null;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("投递 {DeliveryId} 已重新进入待发队列。", id);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ListAsync(int               take              = 100,
                                                                     CancellationToken cancellationToken = default)
    {
        await using var db         = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             deliveries = await db.NotificationDeliveries.AsNoTracking().ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序并截取。
        return deliveries.OrderByDescending(item => item.CreatedAtUtc)
                         .Take(Math.Clamp(take, 1, 500))
                         .ToList();
    }

    private async Task DeliverOneAsync(NotificationDelivery delivery,       TelegramSettings  telegram,
                                       GotifySettings       gotify,         string?           telegramProxyUrl,
                                       string?              gotifyProxyUrl, CancellationToken cancellationToken)
    {
        switch (delivery.Channel)
        {
            case NotificationChannel.Telegram :
                // 渠道关闭或未配置属于配置问题而非发送失败：跳过并保留 Pending，重新启用后自动续投。
                if (!telegram.Enabled || !telegram.IsConfigured) return;
                var botToken = await telegramSettings.GetBotTokenAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(botToken))
                {
                    await RecordFailureAsync(delivery.Id, "Bot Token 未配置，请到设置页填写。", cancellationToken);
                    return;
                }

                await telegramSender.SendAsync(botToken, telegram.ChatId, telegramProxyUrl,
                                               FormatText(delivery), cancellationToken);
                break;

            case NotificationChannel.Gotify :
                if (!gotify.Enabled || !gotify.IsConfigured) return;
                var appToken = await gotifySettings.GetAppTokenAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(appToken))
                {
                    await RecordFailureAsync(delivery.Id, "应用 Token 未配置，请到设置页填写。", cancellationToken);
                    return;
                }

                await gotifySender.SendAsync(gotify.BaseUrl, appToken, gotifyProxyUrl, delivery.Title,
                                             delivery.Message, cancellationToken);
                break;

            default :
                await RecordFailureAsync(delivery.Id, "未知的通知渠道。", cancellationToken);
                return;
        }

        await RecordDeliveredAsync(delivery.Id, cancellationToken);
    }

    private async Task RecordDeliveredAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.NotificationDeliveries
                               .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (delivery is null || delivery.Status != NotificationDeliveryStatus.Pending) return;

        delivery.AttemptCount   += 1;
        delivery.Status         =  NotificationDeliveryStatus.Delivered;
        delivery.DeliveredAtUtc =  timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await db.NotificationDeliveries
                               .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (delivery is null || delivery.Status != NotificationDeliveryStatus.Pending) return;

        delivery.AttemptCount += 1;
        delivery.LastError    =  error.Length > 1000 ? error[..1000] : error;
        if (delivery.AttemptCount >= MaxAttempts)
        {
            delivery.Status           = NotificationDeliveryStatus.Failed;
            delivery.NextAttemptAtUtc = null;
        }
        else
        {
            delivery.NextAttemptAtUtc = timeProvider.GetUtcNow()
                                                    .AddMinutes(BackoffMinutes(delivery.AttemptCount));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>指数退避：第 n 次失败后等待 2^(n-1) 分钟，封顶 30 分钟。</summary>
    internal static int BackoffMinutes(int attemptCount)
    {
        return (int)Math.Min(Math.Pow(2, attemptCount - 1), BackoffCapMinutes);
    }

    private static string FormatText(NotificationDelivery delivery)
    {
        return string.IsNullOrWhiteSpace(delivery.Title)
            ? delivery.Message
            : $"{delivery.Title}\n{delivery.Message}";
    }
}
