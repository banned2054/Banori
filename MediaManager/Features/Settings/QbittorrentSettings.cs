namespace MediaManager.Features.Settings;

/// <summary>qBittorrent 下载器设置。Password 仅在保存时写入，读取路径不携带明文。</summary>
public sealed record QbittorrentSettings(
    string BaseUrl,
    string Username,
    string SavePath,
    bool   Enabled)
{
    public const string DefaultCategory = "MediaManager";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(SavePath);
}
