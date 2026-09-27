using MediaManager.Features.Settings;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Components.Pages.Settings;

/// <summary>设置页状态：常规与下载器设置草稿及保存操作。随页面组件创建和释放。</summary>
public sealed class SettingsPageViewModel(
    ManageGeneralSettings          settingsService,
    ManageQbittorrentSettings      qbittorrentSettings,
    ILogger<SettingsPageViewModel> logger)
{
    public GeneralSettingsDraft     Draft      { get; } = new();
    public QbittorrentSettingsDraft Downloader { get; } = new();

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
        Loaded              = true;
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
            SaveSucceeded       = true;
            StatusMessage       = "设置已保存。";
            Downloader.Password = null;
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
