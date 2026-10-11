using MediaManager.Core.Downloads;
using MediaManager.Features.Settings;
using System.Text.Json;

namespace MediaManager.Features.Downloads;

/// <summary>任务创建时应将两个目录同时保存为快照，历史任务不得改读当前设置。</summary>
public sealed record DownloadDirectorySettings(string MediaDirectory, string SeedDirectory)
{
    public const string DefaultMediaDirectory = "Media/Anime";
    public const string DefaultSeedDirectory  = "Seed";
}

/// <summary>相对于下载根目录的两个独立目录设置；一次写入同时保存，避免读取混合布局。</summary>
public sealed class DownloadDirectorySettingsStore(IApplicationSettingStore settingStore)
{
    public const string Key = "Downloads.Directories";

    public async Task<DownloadDirectorySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await settingStore.GetAsync(Key, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return Normalize(null, null);
        var settings = JsonSerializer.Deserialize<DownloadDirectorySettings>(json)
                    ?? throw new ArgumentException("下载目录设置格式无效。");
        return Normalize(settings.MediaDirectory, settings.SeedDirectory);
    }

    public async Task SaveAsync(string?           mediaDirectory, string? seedDirectory,
                                CancellationToken cancellationToken = default)
    {
        var settings = Normalize(mediaDirectory, seedDirectory);
        await settingStore.SetAsync(Key, JsonSerializer.Serialize(settings), cancellationToken);
    }

    public static DownloadDirectorySettings Normalize(string? mediaDirectory, string? seedDirectory)
    {
        var media = RelativeDownloadPathPolicy.Normalize(string.IsNullOrWhiteSpace(mediaDirectory)
                                                             ? DownloadDirectorySettings.DefaultMediaDirectory
                                                             : mediaDirectory);
        var seed = RelativeDownloadPathPolicy.Normalize(string.IsNullOrWhiteSpace(seedDirectory)
                                                            ? DownloadDirectorySettings.DefaultSeedDirectory
                                                            : seedDirectory);
        RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
        return new DownloadDirectorySettings(media, seed);
    }
}
