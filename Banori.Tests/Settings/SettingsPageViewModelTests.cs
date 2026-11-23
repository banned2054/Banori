using Banori.Components.Pages.Settings;
using Banori.Features.Downloads;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Tests.Downloads;
using Banori.Tests.Notifications;
using Banori.Tests.Proxies;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Banori.Tests.Settings;

/// <summary>设置页按卡片保存：隔离持久化与草稿，保留空白密钥的已保存值。</summary>
public sealed class SettingsPageViewModelTests
{
    public enum Card
    {
        Downloader,
        Naming,
        Telegram,
        Gotify
    }

    [Theory]
    [InlineData(Card.Downloader, false)]
    [InlineData(Card.Naming, false)]
    [InlineData(Card.Telegram, false)]
    [InlineData(Card.Gotify, false)]
    [InlineData(Card.Downloader, true)]
    [InlineData(Card.Naming, true)]
    [InlineData(Card.Telegram, true)]
    [InlineData(Card.Gotify, true)]
    public async Task Save_OnlyPersistsOwnCardAndClearsOwnSecret(Card card, bool invalidOtherDrafts)
    {
        var (vm, store) = await CreateAsync();
        if (invalidOtherDrafts)
            foreach (var other in Enum.GetValues<Card>().Where(other => other != card))
                InvalidateDraft(vm, other);
        var expected = store.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var change in SavedDraftValues(card)) expected[change.Key] = change.Value;
        var drafts = Enum.GetValues<Card>().Select(other => SnapshotDraft(vm, other, other == card)).ToArray();

        await SaveAsync(vm, card);

        Assert.True(vm.SaveSucceeded);
        Assert.False(vm.Saving);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.Equal(expected.OrderBy(pair => pair.Key), store.Values.OrderBy(pair => pair.Key));
        Assert.Equal(drafts, Enum.GetValues<Card>().Select(other => SnapshotDraft(vm, other)).ToArray());
    }

    [Theory]
    [InlineData(Card.Downloader, "下载器地址")]
    [InlineData(Card.Naming, "相对目录")]
    [InlineData(Card.Telegram, "Chat ID")]
    [InlineData(Card.Gotify, "Gotify 地址")]
    public async Task Save_WithInvalidOwnDraft_ReportsFailureWithoutChangingSettingsOrDrafts(Card card, string error)
    {
        var (vm, store) = await CreateAsync();
        await SaveAsync(vm, card);
        Assert.True(vm.SaveSucceeded);
        if (card != Card.Naming) SetSecret(vm, card, "retry-secret");
        InvalidateDraft(vm, card);
        var saved  = store.Values.OrderBy(pair => pair.Key).ToArray();
        var drafts = Enum.GetValues<Card>().Select(other => SnapshotDraft(vm, other)).ToArray();

        await SaveAsync(vm, card);

        Assert.False(vm.SaveSucceeded);
        Assert.False(vm.Saving);
        Assert.Contains(error, vm.StatusMessage);
        Assert.Equal(saved, store.Values.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(drafts, Enum.GetValues<Card>().Select(other => SnapshotDraft(vm, other)).ToArray());
    }

    [Theory]
    [InlineData(Card.Downloader, null)]
    [InlineData(Card.Downloader, "")]
    [InlineData(Card.Downloader, " \t ")]
    [InlineData(Card.Telegram, null)]
    [InlineData(Card.Telegram, "")]
    [InlineData(Card.Telegram, " \t ")]
    [InlineData(Card.Gotify, null)]
    [InlineData(Card.Gotify, "")]
    [InlineData(Card.Gotify, " \t ")]
    public async Task Save_WithBlankSecret_KeepsStoredSecretAndOtherCards(Card card, string? secret)
    {
        var (vm, store) = await CreateAsync();
        SetSecret(vm, card, secret);
        var expected = store.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var change in SavedDraftValues(card))
            if (change.Key != SecretKey(card))
                expected[change.Key] = change.Value;
        var drafts = Enum.GetValues<Card>().Select(other => SnapshotDraft(vm, other, other == card)).ToArray();

        await SaveAsync(vm, card);

        Assert.True(vm.SaveSucceeded);
        Assert.False(vm.Saving);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.Equal(expected.OrderBy(pair => pair.Key), store.Values.OrderBy(pair => pair.Key));
        Assert.Equal(drafts, Enum.GetValues<Card>().Select(other => SnapshotDraft(vm, other)).ToArray());
    }

    [Fact]
    public async Task NamingPreview_RendersTemplatesAndHandlesInvalidDirectoryGracefully()
    {
        var (vm, _) = await CreateAsync();
        vm.Naming.NamingTemplate           = "[{ani.group}] {bgm.nameCn} E{ani.episode:ee}";
        vm.Naming.CollectionNamingTemplate = "[{ani.group}] {bgm.nameCn} {episode:Eee-Eee}";
        vm.Naming.FileNamingTemplate       = "{bgm.nameCn}/S01E{ani.episode:ee}";
        vm.Naming.MediaDirectory           = "Media/Anime";

        Assert.Equal("[LoliHouse] 葬送的芙莉莲 E05", vm.PreviewTorrentName());
        Assert.Equal("[LoliHouse] 葬送的芙莉莲 E01-E02,E04-E06", vm.PreviewCollectionName());
        Assert.Equal("Media/Anime/葬送的芙莉莲/S01E05.mkv", vm.PreviewFilePath());

        vm.Naming.CollectionNamingTemplate = " ";
        Assert.Equal("[LoliHouse] 葬送的芙莉莲 E01-E02,04-E06", vm.PreviewCollectionName());

        vm.Naming.MediaDirectory = "../invalid";
        Assert.EndsWith("/葬送的芙莉莲/S01E05.mkv", vm.PreviewFilePath());
    }

    private static async Task<(SettingsPageViewModel ViewModel, MemoryApplicationSettingStore Store)> CreateAsync()
    {
        var store = new MemoryApplicationSettingStore();
        var downloader = new ManageQbittorrentSettings(store,
                                                       new FakeQbittorrentSessionFactory(new FakeTorrentStore()));
        var telegram     = new ManageTelegramSettings(store, new FakeTelegramSender());
        var gotify       = new ManageGotifySettings(store, new FakeGotifySender());
        var naming       = new NamingTemplateStore(store);
        var directories  = new DownloadDirectorySettingsStore(store);
        var savedProxyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await downloader.SaveAsync(new QbittorrentSettings("http://saved-qb.test", "saved-user", "/saved", false,
                                                           savedProxyId), "saved-password");
        await downloader.SaveHookBaseUrlAsync("http://saved-banori.test");
        await store.SetProtectedAsync(ManageQbittorrentSettings.HookTokenKey, "saved-hook-token");
        await naming.SaveAsync("saved-torrent-template");
        await naming.SaveFileTemplateAsync("saved-file-template");
        await naming.SaveCollectionTemplateAsync("saved-collection-template");
        await directories.SaveAsync("SavedMedia", "SavedSeed");
        await telegram.SaveAsync(new TelegramSettings("saved-chat", savedProxyId, false, NotificationEvents.All),
                                 "saved-bot-token");
        await gotify.SaveAsync(new GotifySettings("http://saved-gotify.test", savedProxyId, false,
                                                  NotificationEvents.All), "saved-app-token");
        var vm = new SettingsPageViewModel(downloader, naming, directories, telegram, gotify,
                                           ProxyServiceTestFactory.CreateEphemeral(),
                                           NullLogger<SettingsPageViewModel>.Instance);
        // 不调用 LoadAsync：ephemeral 代理库没有表，直接构造与已保存值不同的四张卡片草稿。
        vm.Downloader.BaseUrl            = "http://draft-qb.test";
        vm.Downloader.Username           = "draft-user";
        vm.Downloader.Password           = "draft-password";
        vm.Downloader.SavePath           = "/draft";
        vm.Downloader.Enabled            = true;
        vm.Downloader.HookBaseUrl        = "http://draft-banori.test";
        vm.Naming.NamingTemplate         = "draft-torrent-template";
        vm.Naming.FileNamingTemplate     = "draft-file-template";
        vm.Naming.CollectionNamingTemplate = "draft-collection-template";
        vm.Naming.MediaDirectory         = "DraftMedia";
        vm.Naming.SeedDirectory          = "DraftSeed";
        vm.Telegram.ChatId               = "draft-chat";
        vm.Telegram.BotToken             = "draft-bot-token";
        vm.Telegram.Enabled              = true;
        vm.Telegram.NotifyDownloadStarted = false;
        vm.Gotify.BaseUrl                = "http://draft-gotify.test";
        vm.Gotify.AppToken               = "draft-app-token";
        vm.Gotify.Enabled                = true;
        vm.Gotify.NotifyDownloadFailed   = false;
        return (vm, store);
    }

    private static Task SaveAsync(SettingsPageViewModel vm, Card card) => card switch
    {
        Card.Downloader => vm.SaveDownloaderAsync(),
        Card.Naming     => vm.SaveNamingAsync(),
        Card.Telegram   => vm.SaveTelegramAsync(),
        Card.Gotify     => vm.SaveGotifyAsync(),
        _               => throw new ArgumentOutOfRangeException(nameof(card))
    };

    private static void InvalidateDraft(SettingsPageViewModel vm, Card card)
    {
        switch (card)
        {
            case Card.Downloader :
                vm.Downloader.BaseUrl = "not-a-url";
                break;
            case Card.Naming :
                vm.Naming.MediaDirectory = "../outside";
                break;
            case Card.Telegram :
                vm.Telegram.ChatId = " ";
                break;
            case Card.Gotify :
                vm.Gotify.BaseUrl = "not-a-url";
                break;
            default :
                throw new ArgumentOutOfRangeException(nameof(card));
        }
    }

    private static void SetSecret(SettingsPageViewModel vm, Card card, string? secret)
    {
        switch (card)
        {
            case Card.Downloader :
                vm.Downloader.Password = secret;
                break;
            case Card.Telegram :
                vm.Telegram.BotToken = secret;
                break;
            case Card.Gotify :
                vm.Gotify.AppToken = secret;
                break;
            default :
                throw new ArgumentOutOfRangeException(nameof(card));
        }
    }

    private static string SecretKey(Card card) => card switch
    {
        Card.Downloader => ManageQbittorrentSettings.PasswordKey,
        Card.Telegram   => ManageTelegramSettings.BotTokenKey,
        Card.Gotify     => ManageGotifySettings.AppTokenKey,
        _               => throw new ArgumentOutOfRangeException(nameof(card))
    };

    private static object SnapshotDraft(SettingsPageViewModel vm, Card card, bool clearedSecret = false) => card switch
    {
        Card.Downloader => (vm.Downloader.BaseUrl, vm.Downloader.Username,
                            clearedSecret ? null : vm.Downloader.Password, vm.Downloader.SavePath,
                            vm.Downloader.Enabled, vm.Downloader.ProxyId, vm.Downloader.HookBaseUrl),
        Card.Naming => (vm.Naming.NamingTemplate, vm.Naming.FileNamingTemplate,
                        vm.Naming.CollectionNamingTemplate, vm.Naming.MediaDirectory, vm.Naming.SeedDirectory),
        Card.Telegram => (vm.Telegram.ChatId, clearedSecret ? null : vm.Telegram.BotToken,
                          vm.Telegram.Enabled, vm.Telegram.ProxyId,
                          vm.Telegram.NotifyDownloadStarted, vm.Telegram.NotifyDownloadFailed),
        Card.Gotify => (vm.Gotify.BaseUrl, clearedSecret ? null : vm.Gotify.AppToken,
                        vm.Gotify.Enabled, vm.Gotify.ProxyId,
                        vm.Gotify.NotifyDownloadStarted, vm.Gotify.NotifyDownloadFailed),
        _ => throw new ArgumentOutOfRangeException(nameof(card))
    };

    private static Dictionary<string, (string Value, bool IsProtected)> SavedDraftValues(Card card) => card switch
    {
        Card.Downloader => new Dictionary<string, (string Value, bool IsProtected)>
        {
            [ManageQbittorrentSettings.EnabledKey]     = ("true", false),
            [ManageQbittorrentSettings.BaseUrlKey]     = ("http://draft-qb.test", false),
            [ManageQbittorrentSettings.UsernameKey]    = ("draft-user", false),
            [ManageQbittorrentSettings.PasswordKey]    = ("draft-password", true),
            [ManageQbittorrentSettings.SavePathKey]    = ("/draft", false),
            [ManageQbittorrentSettings.ProxyIdKey]     = (string.Empty, false),
            [ManageQbittorrentSettings.HookBaseUrlKey] = ("http://draft-banori.test", false)
        },
        Card.Naming => new Dictionary<string, (string Value, bool IsProtected)>
        {
            [NamingTemplateStore.Key]           = ("draft-torrent-template", false),
            [NamingTemplateStore.FileKey]       = ("draft-file-template", false),
            [NamingTemplateStore.CollectionKey] = ("draft-collection-template", false),
            [DownloadDirectorySettingsStore.Key] =
                (JsonSerializer.Serialize(new DownloadDirectorySettings("DraftMedia", "DraftSeed")), false)
        },
        Card.Telegram => new Dictionary<string, (string Value, bool IsProtected)>
        {
            [ManageTelegramSettings.EnabledKey]  = ("true", false),
            [ManageTelegramSettings.ChatIdKey]   = ("draft-chat", false),
            [ManageTelegramSettings.BotTokenKey] = ("draft-bot-token", true),
            [ManageTelegramSettings.ProxyIdKey]  = (string.Empty, false),
            [ManageTelegramSettings.EventsKey]   = ("DownloadFailed", false)
        },
        Card.Gotify => new Dictionary<string, (string Value, bool IsProtected)>
        {
            [ManageGotifySettings.EnabledKey]  = ("true", false),
            [ManageGotifySettings.BaseUrlKey]  = ("http://draft-gotify.test", false),
            [ManageGotifySettings.AppTokenKey] = ("draft-app-token", true),
            [ManageGotifySettings.ProxyIdKey]  = (string.Empty, false),
            [ManageGotifySettings.EventsKey]   = ("DownloadStarted", false)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(card))
    };
}
