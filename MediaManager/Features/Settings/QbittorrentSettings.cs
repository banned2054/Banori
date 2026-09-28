namespace MediaManager.Features.Settings;

/// <summary>qBittorrent 下载器设置。Password 仅在保存时写入，读取路径不携带明文。ProxyId 指向代理设置中的命名代理。</summary>
public sealed record QbittorrentSettings(
    string BaseUrl,
    string Username,
    string SavePath,
    bool   Enabled,
    Guid?  ProxyId = null)
{
    public const string DefaultCategory = "MediaManager";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(SavePath);
}
