using MediaManager.Features.Notifications;
using MediaManager.Features.Settings;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Components.Pages.Settings;

/// <summary>设置页状态：常规、下载器与通知渠道设置草稿及保存操作。随页面组件创建和释放。</summary>
public sealed class SettingsPageViewModel(
    ManageGeneralSettings          settingsService,
    ManageQbittorrentSettings      qbittorrentSettings,
    ManageTelegramSettings         telegramSettings,
    ManageGotifySettings           gotifySettings,
    ILogger<SettingsPageViewModel> logger)
{
    public GeneralSettingsDraft     Draft      { get; } = new();
    public QbittorrentSettingsDraft Downloader { get; } = new();
    public TelegramSettingsDraft    Telegram   { get; } = new();
    public GotifySettingsDraft      Gotify     { get; } = new();

    public bool    Loaded        { get; private set; }
    public bool    Saving        { get; private set; }
    public bool    Testing       { get; private set; }
    public bool    SaveSucceeded { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? TestMessage   { get; private set; }
    public bool    TestSucceeded { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        Draft.InstanceName = settings.InstanceName;

        var downloader = await qbittorrentSettings.GetAsync(cancellationToken);
        Downloader.BaseUrl  = downloader.BaseUrl;
        Downloader.Username = downloader.Username;
        Downloader.SavePath = downloader.SavePath;
        Downloader.Enabled  = downloader.Enabled;

        var telegram = await telegramSettings.GetAsync(cancellationToken);
        Telegram.ChatId   = telegram.ChatId;
        Telegram.ProxyUrl = telegram.ProxyUrl;
        Telegram.Enabled  = telegram.Enabled;
        Telegram.NotifyDownloadStarted = telegram.Events.Contains(NotificationEventType.DownloadStarted);
        Telegram.NotifyDownloadFailed  = telegram.Events.Contains(NotificationEventType.DownloadFailed);

        var gotify = await gotifySettings.GetAsync(cancellationToken);
        Gotify.BaseUrl = gotify.BaseUrl;
        Gotify.Enabled = gotify.Enabled;
        Gotify.NotifyDownloadStarted = gotify.Events.Contains(NotificationEventType.DownloadStarted);
        Gotify.NotifyDownloadFailed  = gotify.Events.Contains(NotificationEventType.DownloadFailed);

        Loaded = true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Saving        = true;
        StatusMessage = null;
        try
        {
            await settingsService.SaveAsync(new GeneralSettings(Draft.InstanceName), cancellationToken);
            await qbittorrentSettings.SaveAsync(new QbittorrentSettings(Downloader.BaseUrl  ?? string.Empty,
                                                                        Downloader.Username ?? string.Empty,
                                                                        Downloader.SavePath ?? string.Empty,
                                                                        Downloader.Enabled),
                                                string.IsNullOrWhiteSpace(Downloader.Password)
                                                    ? null
                                                    : Downloader.Password,
                                                cancellationToken);
            await telegramSettings.SaveAsync(Telegram.ToSettings(),
                                             string.IsNullOrWhiteSpace(Telegram.BotToken)
                                                 ? null
                                                 : Telegram.BotToken,
                                             cancellationToken);
            await gotifySettings.SaveAsync(Gotify.ToSettings(),
                                           string.IsNullOrWhiteSpace(Gotify.AppToken)
                                               ? null
                                               : Gotify.AppToken,
                                           cancellationToken);
            SaveSucceeded       = true;
            StatusMessage       = "设置已保存。";
            Downloader.Password = null;
            Telegram.BotToken   = null;
            Gotify.AppToken     = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存设置失败。");
            SaveSucceeded = false;
            StatusMessage = exception is ArgumentException
                ? exception.Message
                : "保存失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    /// <summary>用当前草稿测试下载器连接；密码留空时使用已保存密码。</summary>
    public async Task TestDownloaderAsync(CancellationToken cancellationToken = default)
    {
        Testing     = true;
        TestMessage = null;
        try
        {
            var (succeeded, message) = await qbittorrentSettings.TestConnectionAsync(
                 new QbittorrentSettings(Downloader.BaseUrl  ?? string.Empty, Downloader.Username ?? string.Empty,
                                         Downloader.SavePath ?? string.Empty, true),
                 string.IsNullOrWhiteSpace(Downloader.Password) ? null : Downloader.Password,
                 cancellationToken);
            TestSucceeded = succeeded;
            TestMessage   = message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            Testing = false;
        }
    }

    /// <summary>用当前草稿发送 Telegram 测试消息；Token 留空时使用已保存值。</summary>
    public async Task TestTelegramAsync(CancellationToken cancellationToken = default)
    {
        Testing     = true;
        TestMessage = null;
        try
        {
            var (succeeded, message) = await telegramSettings.TestSendAsync(
                Telegram.ToSettings(),
                string.IsNullOrWhiteSpace(Telegram.BotToken) ? null : Telegram.BotToken,
                cancellationToken);
            TestSucceeded = succeeded;
            TestMessage   = message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            Testing = false;
        }
    }

    /// <summary>用当前草稿发送 Gotify 测试消息；Token 留空时使用已保存值。</summary>
    public async Task TestGotifyAsync(CancellationToken cancellationToken = default)
    {
        Testing     = true;
        TestMessage = null;
        try
        {
            var (succeeded, message) = await gotifySettings.TestSendAsync(
                Gotify.ToSettings(),
                string.IsNullOrWhiteSpace(Gotify.AppToken) ? null : Gotify.AppToken,
                cancellationToken);
            TestSucceeded = succeeded;
            TestMessage   = message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            Testing = false;
        }
    }
}

/// <summary>常规设置的编辑模型。</summary>
public sealed class GeneralSettingsDraft
{
    [Required(ErrorMessage = "请输入实例名称。")]
    [StringLength(80, MinimumLength = 1, ErrorMessage = "实例名称长度必须为 1 到 80 个字符。")]
    public string InstanceName { get; set; } = GeneralSettings.DefaultInstanceName;
}

/// <summary>qBittorrent 下载器的编辑模型。密码留空表示沿用已保存值。</summary>
public sealed class QbittorrentSettingsDraft
{
    [StringLength(500, ErrorMessage = "下载器地址不能超过 500 个字符。")]
    public string? BaseUrl { get; set; }

    [StringLength(100, ErrorMessage = "用户名不能超过 100 个字符。")]
    public string? Username { get; set; }

    [DataType(DataType.Password)]
    public string? Password { get; set; }

    [StringLength(500, ErrorMessage = "保存路径不能超过 500 个字符。")]
    public string? SavePath { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>Telegram 通知渠道的编辑模型。Bot Token 留空表示沿用已保存值。</summary>
public sealed class TelegramSettingsDraft
{
    [StringLength(200, ErrorMessage = "Chat ID 不能超过 200 个字符。")]
    public string? ChatId { get; set; }

    [DataType(DataType.Password)]
    public string? BotToken { get; set; }

    [StringLength(500, ErrorMessage = "代理地址不能超过 500 个字符。")]
    public string? ProxyUrl { get; set; }

    public bool Enabled { get; set; }

    public bool NotifyDownloadStarted { get; set; } = true;

    public bool NotifyDownloadFailed { get; set; } = true;

    internal TelegramSettings ToSettings()
    {
        return new TelegramSettings(ChatId ?? string.Empty, ProxyUrl ?? string.Empty, Enabled,
                                    NotificationEvents.FromFlags(NotifyDownloadStarted, NotifyDownloadFailed));
    }
}

/// <summary>Gotify 通知渠道的编辑模型。应用 Token 留空表示沿用已保存值。</summary>
public sealed class GotifySettingsDraft
{
    [StringLength(500, ErrorMessage = "Gotify 地址不能超过 500 个字符。")]
    public string? BaseUrl { get; set; }

    [DataType(DataType.Password)]
    public string? AppToken { get; set; }

    public bool Enabled { get; set; }

    public bool NotifyDownloadStarted { get; set; } = true;

    public bool NotifyDownloadFailed { get; set; } = true;

    internal GotifySettings ToSettings()
    {
        return new GotifySettings(BaseUrl ?? string.Empty, Enabled,
                                  NotificationEvents.FromFlags(NotifyDownloadStarted, NotifyDownloadFailed));
    }
}
