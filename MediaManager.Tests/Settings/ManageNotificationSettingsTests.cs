using MediaManager.Features.Notifications;
using MediaManager.Features.Settings;
using MediaManager.Integrations.Notifications;
using MediaManager.Tests.Downloads;
using MediaManager.Tests.Notifications;

namespace MediaManager.Tests.Settings;

/// <summary>Telegram 通知渠道设置的读写、校验与测试发送入口回归。</summary>
public sealed class ManageTelegramSettingsTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsAndProtectsBotToken()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);

        await settings.SaveAsync(new TelegramSettings("  42  ", "socks5://127.0.0.1:1080", true,
                                                      NotificationEvents.All), "bot-token");

        var loaded = await settings.GetAsync();
        Assert.True(loaded.Enabled);
        Assert.Equal("42", loaded.ChatId);
        Assert.Equal("socks5://127.0.0.1:1080", loaded.ProxyUrl);
        Assert.True(loaded.IsConfigured);
        Assert.True(store.Values[ManageTelegramSettings.BotTokenKey].IsProtected);
        Assert.Equal("DownloadStarted,DownloadFailed",
                     store.Values[ManageTelegramSettings.EventsKey].Value);
    }

    [Fact]
    public async Task Save_WithBlankToken_KeepsStoredValue()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);
        await settings.SaveAsync(new TelegramSettings("42", string.Empty, true, NotificationEvents.All),
                                 "old-token");

        await settings.SaveAsync(new TelegramSettings("100", string.Empty, true, NotificationEvents.All));

        Assert.Equal("old-token", store.Values[ManageTelegramSettings.BotTokenKey].Value);
        Assert.Equal("100", (await settings.GetAsync()).ChatId);
    }

    [Fact]
    public async Task Save_EnabledWithoutAnyToken_Throws()
    {
        var settings = CreateService(new MemoryApplicationSettingStore());
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        settings.SaveAsync(new TelegramSettings("42",
                                                                                    string.Empty, true,
                                                                                    NotificationEvents.All)));
    }

    [Fact]
    public async Task Save_EnabledWithoutChatId_Throws()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        settings.SaveAsync(new TelegramSettings(string.Empty,
                                                                                    string.Empty, true,
                                                                                    NotificationEvents.All),
                                                                           "bot-token"));
    }

    [Fact]
    public async Task Save_WithDisabledChannel_AllowsMissingTokenAndChatId()
    {
        var settings = CreateService(new MemoryApplicationSettingStore());

        await settings.SaveAsync(new TelegramSettings(string.Empty, string.Empty, false,
                                                      NotificationEvents.All));

        Assert.False((await settings.GetAsync()).Enabled);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("not-a-url")]
    public async Task Save_WithInvalidProxyUrl_Throws(string proxyUrl)
    {
        var settings = CreateService(new MemoryApplicationSettingStore());
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        settings.SaveAsync(new TelegramSettings("42", proxyUrl,
                                                                                    true,
                                                                                    NotificationEvents.All),
                                                                           "bot-token"));
    }

    [Fact]
    public async Task TestSend_UsesDraftThenFallsBackToSavedToken()
    {
        var store = new MemoryApplicationSettingStore();
        await store.SetProtectedAsync(ManageTelegramSettings.BotTokenKey, "saved-token");
        var telegram = new FakeTelegramSender();
        var settings = new ManageTelegramSettings(store, telegram);

        // 草稿留空 Token：回落到已保存值。
        var (succeeded, _) =
            await settings.TestSendAsync(new TelegramSettings("42", string.Empty, true, NotificationEvents.All));
        Assert.True(succeeded);
        Assert.Equal("saved-token", Assert.Single(telegram.Calls).BotToken);

        // 草稿填写 Token：优先使用草稿值。
        var (draftSucceeded, _) =
            await settings.TestSendAsync(new TelegramSettings("42", string.Empty, true, NotificationEvents.All),
                                         "draft-token");
        Assert.True(draftSucceeded);
        Assert.Equal("draft-token", telegram.Calls.Last().BotToken);
    }

    [Fact]
    public async Task TestSend_WithoutAnyToken_ReportsMissingToken()
    {
        var settings = CreateService(new MemoryApplicationSettingStore());

        var (succeeded, message) =
            await settings.TestSendAsync(new TelegramSettings("42", string.Empty, true, NotificationEvents.All));

        Assert.False(succeeded);
        Assert.Contains("Bot Token", message);
        var (_, noChatIdMessage) =
            await settings.TestSendAsync(new TelegramSettings(string.Empty, string.Empty, true, NotificationEvents.All),
                                         "bot-token");
        Assert.Contains("Chat ID", noChatIdMessage);
    }

    [Fact]
    public async Task TestSend_ReportsSendFailure()
    {
        var telegram = new FakeTelegramSender { SendException = new NotificationSendException("网络不可达") };
        var settings = new ManageTelegramSettings(new MemoryApplicationSettingStore(), telegram);

        var (succeeded, message) =
            await settings.TestSendAsync(new TelegramSettings("42", string.Empty, true, NotificationEvents.All),
                                         "bot-token");

        Assert.False(succeeded);
        Assert.Contains("网络不可达", message);
    }

    private static ManageTelegramSettings CreateService(MemoryApplicationSettingStore store)
    {
        return new ManageTelegramSettings(store, new FakeTelegramSender());
    }
}

/// <summary>Gotify 通知渠道设置的读写、校验与测试发送入口回归。</summary>
public sealed class ManageGotifySettingsTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsAndProtectsAppToken()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);

        await settings.SaveAsync(new GotifySettings("http://gotify.test:8080/", true, NotificationEvents.All),
                                 "app-token");

        var loaded = await settings.GetAsync();
        Assert.True(loaded.Enabled);
        Assert.Equal("http://gotify.test:8080", loaded.BaseUrl);
        Assert.True(loaded.IsConfigured);
        Assert.True(store.Values[ManageGotifySettings.AppTokenKey].IsProtected);
    }

    [Fact]
    public async Task Save_WithBlankToken_KeepsStoredValue()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);
        await settings.SaveAsync(new GotifySettings("http://gotify.test", true, NotificationEvents.All),
                                 "old-token");

        await settings.SaveAsync(new GotifySettings("http://gotify.test", true, NotificationEvents.All));

        Assert.Equal("old-token", store.Values[ManageGotifySettings.AppTokenKey].Value);
    }

    [Fact]
    public async Task Save_EnabledWithoutAnyToken_Throws()
    {
        var settings = CreateService(new MemoryApplicationSettingStore());
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveAsync(new GotifySettings("http://gotify.test",
                                                                                      true, NotificationEvents.All)));
    }

    [Theory]
    [InlineData("ftp://gotify.test")]
    [InlineData("not-a-url")]
    public async Task Save_WithInvalidBaseUrl_Throws(string baseUrl)
    {
        var settings = CreateService(new MemoryApplicationSettingStore());
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        settings.SaveAsync(new GotifySettings(baseUrl, true,
                                                                                    NotificationEvents.All),
                                                                           "app-token"));
    }

    [Fact]
    public async Task TestSend_ReportsSendFailure()
    {
        var gotify   = new FakeGotifySender { SendException = new NotificationSendException("401 token invalid") };
        var settings = new ManageGotifySettings(new MemoryApplicationSettingStore(), gotify);

        var (succeeded, message) =
            await settings.TestSendAsync(new GotifySettings("http://gotify.test", true, NotificationEvents.All),
                                         "app-token");

        Assert.False(succeeded);
        Assert.Contains("401 token invalid", message);
    }

    private static ManageGotifySettings CreateService(MemoryApplicationSettingStore store)
    {
        return new ManageGotifySettings(store, new FakeGotifySender());
    }
}
