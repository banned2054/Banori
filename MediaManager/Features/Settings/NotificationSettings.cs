using MediaManager.Features.Notifications;

namespace MediaManager.Features.Settings;

/// <summary>Telegram 通知渠道设置。BotToken 仅在保存时写入，读取路径不携带明文。</summary>
public sealed record TelegramSettings(
    string                               ChatId,
    string                               ProxyUrl,
    bool                                 Enabled,
    IReadOnlyList<NotificationEventType> Events)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>Gotify 通知渠道设置。AppToken 仅在保存时写入，读取路径不携带明文。</summary>
public sealed record GotifySettings(
    string                               BaseUrl,
    bool                                 Enabled,
    IReadOnlyList<NotificationEventType> Events)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
