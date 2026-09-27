namespace MediaManager.Integrations.Notifications;

/// <summary>通知渠道返回错误或网络失败时抛出；Message 面向用户可直接展示。</summary>
public sealed class NotificationSendException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Telegram Bot API 发送边界；实现按次携带代理配置，测试可用 TCP 桩或替身。</summary>
public interface ITelegramSender
{
    Task SendAsync(string botToken, string chatId, string? proxyUrl, string text,
                   CancellationToken cancellationToken = default);
}

/// <summary>Gotify 发送边界；测试可用 TCP 桩或替身。</summary>
public interface IGotifySender
{
    Task SendAsync(string baseUrl, string appToken, string title, string message,
                   CancellationToken cancellationToken = default);
}
