using MediaManager.Core.Rules;
using MediaManager.Features.Downloads;
using MediaManager.Features.Notifications;
using MediaManager.Features.Proxies;
using MediaManager.Features.Settings;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Components.Pages.Settings;

/// <summary>设置页状态：下载器与通知渠道设置草稿及保存操作。随页面组件创建和释放。</summary>
public sealed class SettingsPageViewModel(
    ManageQbittorrentSettings      qbittorrentSettings,
    NamingTemplateStore            namingTemplateStore,
    ManageTelegramSettings         telegramSettings,
    ManageGotifySettings           gotifySettings,
    ProxyService                   proxyService,
    ILogger<SettingsPageViewModel> logger)
{
    private readonly List<ProxyView> _proxies = [];

    private string? _hookToken;

    public QbittorrentSettingsDraft Downloader { get; } = new();
    public TelegramSettingsDraft    Telegram   { get; } = new();
    public GotifySettingsDraft      Gotify     { get; } = new();

    /// <summary>默认命名模板，供输入框占位与“恢复默认”提示展示。</summary>
    public string DefaultNamingTemplate => ReleaseNamePreviewPolicy.DefaultTemplate;

    /// <summary>命名代理列表，供各卡片的代理下拉展示；代理变动后由 ReloadProxiesAsync 刷新。</summary>
    public IReadOnlyList<ProxyView> Proxies => _proxies;

    /// <summary>粘贴到 qBittorrent“运行外部程序”的两条命令；尚未生成令牌时为 null。</summary>
    public (string Added, string Completed)? HookCommands { get; private set; }

    public bool    Loaded        { get; private set; }
    public bool    Saving        { get; private set; }
    public bool    Testing       { get; private set; }
    public bool    SaveSucceeded { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? TestMessage   { get; private set; }
    public bool    TestSucceeded { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var downloader = await qbittorrentSettings.GetAsync(cancellationToken);
        Downloader.BaseUrl  = downloader.BaseUrl;
        Downloader.Username = downloader.Username;
        Downloader.SavePath = downloader.SavePath;
        Downloader.Enabled  = downloader.Enabled;
        Downloader.ProxyId  = downloader.ProxyId?.ToString();
        Downloader.NamingTemplate = await namingTemplateStore.GetAsync(cancellationToken);

        var hookBaseUrl = await qbittorrentSettings.GetHookBaseUrlAsync(cancellationToken);
        Downloader.HookBaseUrl = hookBaseUrl;
        _hookToken             = await qbittorrentSettings.GetHookTokenAsync(cancellationToken);
        HookCommands           = BuildHookCommands(hookBaseUrl, _hookToken);

        var telegram = await telegramSettings.GetAsync(cancellationToken);
        Telegram.ChatId                = telegram.ChatId;
        Telegram.ProxyId               = telegram.ProxyId?.ToString();
        Telegram.Enabled               = telegram.Enabled;
        Telegram.NotifyDownloadStarted = telegram.Events.Contains(NotificationEventType.DownloadStarted);
        Telegram.NotifyDownloadFailed  = telegram.Events.Contains(NotificationEventType.DownloadFailed);

        var gotify = await gotifySettings.GetAsync(cancellationToken);
        Gotify.BaseUrl               = gotify.BaseUrl;
        Gotify.ProxyId               = gotify.ProxyId?.ToString();
        Gotify.Enabled               = gotify.Enabled;
        Gotify.NotifyDownloadStarted = gotify.Events.Contains(NotificationEventType.DownloadStarted);
        Gotify.NotifyDownloadFailed  = gotify.Events.Contains(NotificationEventType.DownloadFailed);

        _proxies.Clear();
        _proxies.AddRange(await proxyService.ListAsync(cancellationToken));

        Loaded = true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Saving        = true;
        StatusMessage = null;
        try
        {
            await qbittorrentSettings.SaveAsync(new QbittorrentSettings(Downloader.BaseUrl  ?? string.Empty,
                                                                        Downloader.Username ?? string.Empty,
                                                                        Downloader.SavePath ?? string.Empty,
                                                                        Downloader.Enabled,
                                                                        Downloader.ParseProxyId()),
                                                string.IsNullOrWhiteSpace(Downloader.Password)
                                                    ? null
                                                    : Downloader.Password,
                                                cancellationToken);
            await qbittorrentSettings.SaveHookBaseUrlAsync(Downloader.HookBaseUrl, cancellationToken);
            HookCommands = BuildHookCommands(await qbittorrentSettings.GetHookBaseUrlAsync(cancellationToken),
                                             _hookToken);
            await namingTemplateStore.SaveAsync(Downloader.NamingTemplate, cancellationToken);
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

    /// <summary>代理增删改后重新加载代理列表，供同页卡片的代理下拉刷新。</summary>
    public async Task ReloadProxiesAsync(CancellationToken cancellationToken = default)
    {
        _proxies.Clear();
        _proxies.AddRange(await proxyService.ListAsync(cancellationToken));
    }

    /// <summary>重新生成钩子令牌并刷新命令行展示；旧令牌立即失效。</summary>
    public async Task RegenerateHookTokenAsync(CancellationToken cancellationToken = default)
    {
        _hookToken   = await qbittorrentSettings.GenerateHookTokenAsync(cancellationToken);
        HookCommands = BuildHookCommands(Downloader.HookBaseUrl, _hookToken);
    }

    private static (string Added, string Completed)? BuildHookCommands(string? baseUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var host = string.IsNullOrWhiteSpace(baseUrl)
            ? "<MediaManager 地址>"
            : baseUrl.Trim().TrimEnd('/');
        var added     = $"curl -s \"{host}/hooks/qbittorrent?token={token}&hash=%I&event=added\"";
        var completed = $"curl -s \"{host}/hooks/qbittorrent?token={token}&hash=%I&event=completed\"";
        return (added, completed);
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
                                     Downloader.SavePath ?? string.Empty, true,
                                     Downloader.ParseProxyId()),
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
            var proxyUrl = await proxyService.ResolveProxyUrlAsync(Telegram.ParseProxyId(), cancellationToken);
            var (succeeded, message) = await telegramSettings.TestSendAsync(
                                                                            Telegram.ToSettings(),
                                                                            string.IsNullOrWhiteSpace(Telegram.BotToken)
                                                                                ? null
                                                                                : Telegram.BotToken,
                                                                            proxyUrl,
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
            var proxyUrl = await proxyService.ResolveProxyUrlAsync(Gotify.ParseProxyId(), cancellationToken);
            var (succeeded, message) = await gotifySettings.TestSendAsync(
                                                                          Gotify.ToSettings(),
                                                                          string.IsNullOrWhiteSpace(Gotify.AppToken)
                                                                              ? null
                                                                              : Gotify.AppToken,
                                                                          proxyUrl,
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

    /// <summary>代理下拉的绑定值（代理 Id 的字符串形式）；空表示直连。</summary>
    public string? ProxyId { get; set; }

    /// <summary>钩子回调的对外可达基础地址；空表示未配置。</summary>
    public string? HookBaseUrl { get; set; }

    /// <summary>订阅条目的命名模板；留空表示恢复默认模板。</summary>
    [StringLength(200, ErrorMessage = "命名模板不能超过 200 个字符。")]
    public string? NamingTemplate { get; set; }

    public bool Enabled { get; set; }

    internal Guid? ParseProxyId()
    {
        return Guid.TryParse(ProxyId, out var proxyId) ? proxyId : null;
    }
}

/// <summary>Telegram 通知渠道的编辑模型。Bot Token 留空表示沿用已保存值。</summary>
public sealed class TelegramSettingsDraft
{
    [StringLength(200, ErrorMessage = "Chat ID 不能超过 200 个字符。")]
    public string? ChatId { get; set; }

    [DataType(DataType.Password)]
    public string? BotToken { get; set; }

    /// <summary>代理下拉的绑定值（代理 Id 的字符串形式）；空表示直连。</summary>
    public string? ProxyId { get; set; }

    public bool Enabled { get; set; }

    public bool NotifyDownloadStarted { get; set; } = true;

    public bool NotifyDownloadFailed { get; set; } = true;

    internal Guid? ParseProxyId()
    {
        return Guid.TryParse(ProxyId, out var proxyId) ? proxyId : null;
    }

    internal TelegramSettings ToSettings()
    {
        return new TelegramSettings(ChatId ?? string.Empty, ParseProxyId(), Enabled,
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

    /// <summary>代理下拉的绑定值（代理 Id 的字符串形式）；空表示直连。</summary>
    public string? ProxyId { get; set; }

    public bool Enabled { get; set; }

    public bool NotifyDownloadStarted { get; set; } = true;

    public bool NotifyDownloadFailed { get; set; } = true;

    internal Guid? ParseProxyId()
    {
        return Guid.TryParse(ProxyId, out var proxyId) ? proxyId : null;
    }

    internal GotifySettings ToSettings()
    {
        return new GotifySettings(BaseUrl ?? string.Empty, ParseProxyId(), Enabled,
                                  NotificationEvents.FromFlags(NotifyDownloadStarted, NotifyDownloadFailed));
    }
}
