using Banori.Data;
using Banori.Features.Settings;

namespace Banori.Features.Notifications;

/// <summary>
///     通知投递意图入队：读取渠道设置，为每个启用且订阅该事件的渠道向给定上下文追加投递行，
///     随调用方的 SaveChangesAsync 在同一本地事务落库（与下载状态变更同事务）。
///     入队自身的任何异常都只记日志，绝不外泄，绝不影响下载主流程。
/// </summary>
public sealed class NotificationEnqueuer(
    ManageTelegramSettings        telegramSettings,
    ManageGotifySettings          gotifySettings,
    TimeProvider                  timeProvider,
    ILogger<NotificationEnqueuer> logger)
{
    public const int TitleMaxLength   = 200;
    public const int MessageMaxLength = 2000;

    public async Task TryEnqueueAsync(ApplicationDbContext db,    NotificationEventType eventType,
                                      string               title, string                message,
                                      CancellationToken    cancellationToken = default)
    {
        try
        {
            var telegram = await telegramSettings.GetAsync(cancellationToken);
            if (telegram.Enabled && telegram.IsConfigured && telegram.Events.Contains(eventType))
                db.NotificationDeliveries.Add(CreateDelivery(NotificationChannel.Telegram, eventType,
                                                             title, message));

            var gotify = await gotifySettings.GetAsync(cancellationToken);
            if (gotify.Enabled && gotify.IsConfigured && gotify.Events.Contains(eventType))
                db.NotificationDeliveries.Add(CreateDelivery(NotificationChannel.Gotify, eventType,
                                                             title, message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "记录 {EventType} 通知投递意图失败，本条通知跳过。", eventType);
        }
    }

    private NotificationDelivery CreateDelivery(NotificationChannel channel, NotificationEventType eventType,
                                                string              title,   string                message) =>
        new()
        {
            Id           = Guid.NewGuid(),
            Channel      = channel,
            EventType    = eventType,
            Title        = Truncate(title, TitleMaxLength),
            Message      = Truncate(message, MessageMaxLength),
            Status       = NotificationDeliveryStatus.Pending,
            CreatedAtUtc = timeProvider.GetUtcNow()
        };

    private static string Truncate(string text, int maxLength)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
