using MediaManager.Integrations.Qbittorrent;
using System.Security.Cryptography;

namespace MediaManager.Features.Settings;

/// <summary>
///     qBittorrent 设置的读取、校验与保存。密码使用受保护存储；
///     保存时不回传明文，password 参数为 null 表示沿用原值。连接测试用草稿设置执行。
///     钩子令牌（供 qBittorrent“运行外部程序”回调本应用）同样受保护存储。
/// </summary>
public sealed class ManageQbittorrentSettings(
    IApplicationSettingStore   settingStore,
    IQbittorrentSessionFactory sessionFactory)
{
    public const string EnabledKey     = "Qbittorrent.Enabled";
    public const string BaseUrlKey     = "Qbittorrent.BaseUrl";
    public const string UsernameKey    = "Qbittorrent.Username";
    public const string PasswordKey    = "Qbittorrent.Password";
    public const string SavePathKey    = "Qbittorrent.SavePath";
    public const string ProxyIdKey     = "Qbittorrent.ProxyId";
    public const string HookBaseUrlKey = "Qbittorrent.HookBaseUrl";
    public const string HookTokenKey   = "Qbittorrent.HookToken";

    /// <summary>钩子回调的对外可达基础地址；由用户按部署形态填写（如容器名地址）。</summary>
    public async Task<string> GetHookBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        return (await settingStore.GetAsync(HookBaseUrlKey, cancellationToken) ?? string.Empty).Trim().TrimEnd('/');
    }

    public async Task SaveHookBaseUrlAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        await settingStore.SetAsync(HookBaseUrlKey, (baseUrl ?? string.Empty).Trim().TrimEnd('/'), cancellationToken);
    }

    /// <summary>已保存的钩子令牌（解密）；未生成过时为 null。端点校验与设置页展示共用。</summary>
    public async Task<string?> GetHookTokenAsync(CancellationToken cancellationToken = default)
    {
        return await settingStore.GetAsync(HookTokenKey, cancellationToken);
    }

    /// <summary>生成 256 位随机令牌并覆盖旧值；返回明文供页面立即展示。</summary>
    public async Task<string> GenerateHookTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await settingStore.SetProtectedAsync(HookTokenKey, token, cancellationToken);
        return token;
    }

    public async Task<QbittorrentSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var enabledText = await settingStore.GetAsync(EnabledKey, cancellationToken);
        var baseUrl     = await settingStore.GetAsync(BaseUrlKey, cancellationToken)  ?? string.Empty;
        var username    = await settingStore.GetAsync(UsernameKey, cancellationToken) ?? string.Empty;
        var savePath    = await settingStore.GetAsync(SavePathKey, cancellationToken) ?? string.Empty;
        var proxyId = Guid.TryParse(await settingStore.GetAsync(ProxyIdKey, cancellationToken), out var parsed)
            ? (Guid?)parsed
            : null;
        return new QbittorrentSettings(baseUrl.Trim(), username, savePath.Trim(),
                                       string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase),
                                       proxyId);
    }

    /// <summary>保存设置；password 为 null 或空白表示不修改已保存密码。</summary>
    public async Task SaveAsync(QbittorrentSettings settings, string? password = null,
                                CancellationToken   cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var baseUrl  = settings.BaseUrl.Trim();
        var savePath = settings.SavePath.Trim();
        var username = settings.Username.Trim();

        if (settings.Enabled || !string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("下载器地址必须是有效的 http(s) URL。", nameof(settings));

            baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(savePath) && settings.Enabled)
            throw new ArgumentException("启用下载器时必须填写保存路径。", nameof(settings));

        if (savePath.Length > 500) throw new ArgumentException("保存路径不能超过 500 个字符。", nameof(settings));

        await settingStore.SetAsync(EnabledKey, settings.Enabled ? "true" : "false", cancellationToken);
        await settingStore.SetAsync(BaseUrlKey, baseUrl, cancellationToken);
        await settingStore.SetAsync(UsernameKey, username, cancellationToken);
        await settingStore.SetAsync(SavePathKey, savePath, cancellationToken);
        await settingStore.SetAsync(ProxyIdKey, settings.ProxyId?.ToString() ?? string.Empty, cancellationToken);
        if (!string.IsNullOrWhiteSpace(password))
            await settingStore.SetProtectedAsync(PasswordKey, password, cancellationToken);
    }

    /// <summary>用传入设置建立会话测试连接；成功返回下载器版本，失败返回原因文本。</summary>
    public async Task<(bool Succeeded, string Message)> TestConnectionAsync(
        QbittorrentSettings settings, string? password = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await sessionFactory.CreateAsync(settings, password, cancellationToken);
            return (true, $"连接成功，qBittorrent 版本 {session.ServerVersion}。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (false, $"连接失败：{exception.Message}");
        }
    }
}
