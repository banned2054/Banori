using Banned.Qbittorrent;
using Banned.Qbittorrent.Models;
using MediaManager.Features.Proxies;
using MediaManager.Features.Settings;

namespace MediaManager.Integrations.Qbittorrent;

/// <summary>按设置创建独立登录的会话；未配置时给出可直接展示的错误。代理从命名代理配置解析，已删除的代理回退直连。</summary>
public sealed class QbittorrentSessionFactory(
    IApplicationSettingStore settingStore,
    ProxyService             proxyService) : IQbittorrentSessionFactory
{
    public async Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var enabledText = await settingStore.GetAsync(ManageQbittorrentSettings.EnabledKey, cancellationToken);
        if (!string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("qBittorrent 下载器尚未启用，请先在设置页完成配置。");

        var baseUrl  = await settingStore.GetAsync(ManageQbittorrentSettings.BaseUrlKey, cancellationToken);
        var username = await settingStore.GetAsync(ManageQbittorrentSettings.UsernameKey, cancellationToken);
        var password = await settingStore.GetAsync(ManageQbittorrentSettings.PasswordKey, cancellationToken);
        var savePath = await settingStore.GetAsync(ManageQbittorrentSettings.SavePathKey, cancellationToken);
        var proxyId  = Guid.TryParse(await settingStore.GetAsync(ManageQbittorrentSettings.ProxyIdKey, cancellationToken),
                                     out var parsed)
            ? (Guid?)parsed
            : null;
        return await CreateAsync(new QbittorrentSettings(baseUrl  ?? string.Empty, username ?? string.Empty,
                                                         savePath ?? string.Empty, true, proxyId), password,
                                 cancellationToken);
    }

    public async Task<IQbittorrentSession> CreateAsync(QbittorrentSettings settings, string? password,
                                                       CancellationToken   cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured) throw new InvalidOperationException("qBittorrent 下载器尚未配置完整，请先在设置页填写地址与保存路径。");

        // 设置页保存后会清空密码框：草稿密码留空表示沿用已保存密码，没有已保存密码时才按空密码尝试。
        var effectivePassword = string.IsNullOrWhiteSpace(password)
            ? await settingStore.GetAsync(ManageQbittorrentSettings.PasswordKey, cancellationToken) ?? string.Empty
            : password;

        var proxy = await proxyService.ResolveWebProxyAsync(settings.ProxyId, cancellationToken);
        var options = proxy is null
            ? new QBittorrentClientOptions
              {
                  MaxRetries = 2,
                  Timeout    = TimeSpan.FromSeconds(20)
              }
            : new QBittorrentClientOptions
              {
                  MaxRetries = 2,
                  Timeout    = TimeSpan.FromSeconds(20),
                  // 注入的 HttpClient 由包内部使用；会话释放时 QbittorrentSession 会连带释放 client。
                  HttpClient = new HttpClient(new SocketsHttpHandler { Proxy = proxy })
                               {
                                   Timeout = TimeSpan.FromSeconds(20)
                               }
              };

        var client = await QBittorrentClient.Create(settings.BaseUrl, settings.Username, effectivePassword,
                                                    options, cancellationToken);
        return new QbittorrentSession(client);
    }
}
