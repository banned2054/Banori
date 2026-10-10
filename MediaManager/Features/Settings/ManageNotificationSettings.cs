using MediaManager.Features.Notifications;
using MediaManager.Integrations.Notifications;

namespace MediaManager.Features.Settings;

/// <summary>
///     Telegram 通知渠道设置的读取、校验与保存。BotToken 使用受保护存储；
///     保存时不回传明文，token 参数为 null 或空白表示沿用原值。测试发送用草稿设置执行。
/// </summary>
public sealed class ManageTelegramSettings(IApplicationSettingStore settingStore, ITelegramSender sender)
{
    public const string EnabledKey  = "Notification.Telegram.Enabled";
    public const string BotTokenKey = "Notification.Telegram.BotToken";
    public const string ChatIdKey   = "Notification.Telegram.ChatId";
    public const string ProxyIdKey  = "Notification.Telegram.ProxyId";
    public const string EventsKey   = "Notification.Telegram.Events";

    public async Task<TelegramSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var enabledText = await settingStore.GetAsync(EnabledKey, cancellationToken);
        var chatId      = await settingStore.GetAsync(ChatIdKey, cancellationToken) ?? string.Empty;
        var events      = NotificationEvents.Parse(await settingStore.GetAsync(EventsKey, cancellationToken));
        return new TelegramSettings(chatId.Trim(),
                                    ParseProxyId(await settingStore.GetAsync(ProxyIdKey, cancellationToken)),
                                    string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase), events);
    }

    private static Guid? ParseProxyId(string? stored)
    {
        return Guid.TryParse(stored, out var proxyId) ? proxyId : null;
    }

    /// <summary>已保存的 Bot Token；投递与测试发送时使用，不进入设置模型。</summary>
    public async Task<string?> GetBotTokenAsync(CancellationToken cancellationToken = default)
    {
        return await settingStore.GetAsync(BotTokenKey, cancellationToken);
    }

    /// <summary>保存设置；botToken 为 null 或空白表示不修改已保存 Token。</summary>
    public async Task SaveAsync(
        TelegramSettings settings, string? botToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var chatId = settings.ChatId.Trim();

        if (chatId.Length > 200) throw new ArgumentException("Chat ID 不能超过 200 个字符。", nameof(settings));

        if (settings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                throw new ArgumentException("启用 Telegram 通知时必须填写发送目标 Chat ID。", nameof(settings));

            var savedToken = await settingStore.GetAsync(BotTokenKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(botToken) && string.IsNullOrWhiteSpace(savedToken))
                throw new ArgumentException("启用 Telegram 通知时必须填写 Bot Token。", nameof(settings));
        }

        await settingStore.SetAsync(EnabledKey, settings.Enabled ? "true" : "false", cancellationToken);
        await settingStore.SetAsync(ChatIdKey, chatId, cancellationToken);
        await settingStore.SetAsync(ProxyIdKey, settings.ProxyId?.ToString() ?? string.Empty, cancellationToken);
        await settingStore.SetAsync(EventsKey, NotificationEvents.Format(settings.Events), cancellationToken);
        if (!string.IsNullOrWhiteSpace(botToken))
            await settingStore.SetProtectedAsync(BotTokenKey, botToken, cancellationToken);
    }

    /// <summary>
    ///     用传入设置发送测试消息；token 留空时使用已保存值。proxyUrl 由调用方按 ProxyId 解析
    ///     （ProxyService.ResolveProxyUrlAsync），成功与否都返回可直接展示的文本。
    /// </summary>
    public async Task<(bool Succeeded, string Message)> TestSendAsync(
        TelegramSettings  settings, string? botToken = null, string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = string.IsNullOrWhiteSpace(botToken)
                ? await settingStore.GetAsync(BotTokenKey, cancellationToken)
                : botToken;
            if (string.IsNullOrWhiteSpace(effectiveToken))
                return (false, "尚未填写 Bot Token，请填写后再测试。");
            if (string.IsNullOrWhiteSpace(settings.ChatId))
                return (false, "尚未填写发送目标 Chat ID，请填写后再测试。");

            await sender.SendAsync(effectiveToken, settings.ChatId, proxyUrl,
                                   "MediaManager 测试消息：Telegram 通知渠道工作正常。", cancellationToken);
            return (true, "测试消息已发送，请到 Telegram 查收。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (false, $"发送失败：{exception.Message}");
        }
    }
}

/// <summary>
///     Gotify 通知渠道设置的读取、校验与保存。AppToken 使用受保护存储；
///     保存时不回传明文，token 参数为 null 或空白表示沿用原值。测试发送用草稿设置执行。
/// </summary>
public sealed class ManageGotifySettings(IApplicationSettingStore settingStore, IGotifySender sender)
{
    public const string EnabledKey  = "Notification.Gotify.Enabled";
    public const string BaseUrlKey  = "Notification.Gotify.BaseUrl";
    public const string AppTokenKey = "Notification.Gotify.AppToken";
    public const string ProxyIdKey  = "Notification.Gotify.ProxyId";
    public const string EventsKey   = "Notification.Gotify.Events";

    private static Guid? ParseProxyId(string? stored)
    {
        return Guid.TryParse(stored, out var proxyId) ? proxyId : null;
    }

    public async Task<GotifySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var enabledText = await settingStore.GetAsync(EnabledKey, cancellationToken);
        var baseUrl     = await settingStore.GetAsync(BaseUrlKey, cancellationToken) ?? string.Empty;
        var events      = NotificationEvents.Parse(await settingStore.GetAsync(EventsKey, cancellationToken));
        return new GotifySettings(baseUrl.Trim(),
                                  ParseProxyId(await settingStore.GetAsync(ProxyIdKey, cancellationToken)),
                                  string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase), events);
    }

    /// <summary>已保存的 App Token；投递与测试发送时使用，不进入设置模型。</summary>
    public async Task<string?> GetAppTokenAsync(CancellationToken cancellationToken = default)
    {
        return await settingStore.GetAsync(AppTokenKey, cancellationToken);
    }

    /// <summary>保存设置；appToken 为 null 或空白表示不修改已保存 Token。</summary>
    public async Task SaveAsync(
        GotifySettings settings, string? appToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var baseUrl = settings.BaseUrl.Trim();
        if (baseUrl.Length > 500) throw new ArgumentException("Gotify 地址不能超过 500 个字符。", nameof(settings));

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("Gotify 地址必须是有效的 http(s) URL。", nameof(settings));

            baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        if (settings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("启用 Gotify 通知时必须填写服务地址。", nameof(settings));

            var savedToken = await settingStore.GetAsync(AppTokenKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(appToken) && string.IsNullOrWhiteSpace(savedToken))
                throw new ArgumentException("启用 Gotify 通知时必须填写应用 Token。", nameof(settings));
        }

        await settingStore.SetAsync(EnabledKey, settings.Enabled ? "true" : "false", cancellationToken);
        await settingStore.SetAsync(BaseUrlKey, baseUrl, cancellationToken);
        await settingStore.SetAsync(ProxyIdKey, settings.ProxyId?.ToString() ?? string.Empty, cancellationToken);
        await settingStore.SetAsync(EventsKey, NotificationEvents.Format(settings.Events), cancellationToken);
        if (!string.IsNullOrWhiteSpace(appToken))
            await settingStore.SetProtectedAsync(AppTokenKey, appToken, cancellationToken);
    }

    /// <summary>
    ///     用传入设置发送测试消息；token 留空时使用已保存值。proxyUrl 由调用方按 ProxyId 解析
    ///     （ProxyService.ResolveProxyUrlAsync），成功与否都返回可直接展示的文本。
    /// </summary>
    public async Task<(bool Succeeded, string Message)> TestSendAsync(
        GotifySettings    settings, string? appToken = null, string? proxyUrl = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = string.IsNullOrWhiteSpace(appToken)
                ? await settingStore.GetAsync(AppTokenKey, cancellationToken)
                : appToken;
            if (string.IsNullOrWhiteSpace(effectiveToken))
                return (false, "尚未填写应用 Token，请填写后再测试。");
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
                return (false, "尚未填写 Gotify 服务地址，请填写后再测试。");

            await sender.SendAsync(settings.BaseUrl, effectiveToken, proxyUrl, "MediaManager 测试消息",
                                   "Gotify 通知渠道工作正常。", cancellationToken);
            return (true, "测试消息已发送，请到 Gotify 客户端查收。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (false, $"发送失败：{exception.Message}");
        }
    }
}
