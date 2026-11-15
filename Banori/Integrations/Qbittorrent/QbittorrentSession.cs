using Banned.Qbittorrent;
using Banned.Qbittorrent.Exceptions;
using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Requests;
using Banned.Qbittorrent.Models.Torrent;
using Banned.Qbittorrent.Utils;

namespace Banori.Integrations.Qbittorrent;

/// <summary>包装 Banned.Qbittorrent 客户端：临时文件上传、Hash 规范化与快照映射都在这里完成。</summary>
public sealed class QbittorrentSession(QBittorrentClient client) : IQbittorrentSession
{
    public string ServerVersion => client.QbittorrentVersion;

    public async Task<string> AddTorrentAsync(byte[]                torrentBytes, string  savePath,
                                              IReadOnlyList<string> tags,         string? rename,
                                              bool                  startStopped,
                                              CancellationToken     cancellationToken = default)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"Banori-{Guid.NewGuid():N}.torrent");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, torrentBytes, cancellationToken);
            var request = new AddTorrentRequest
            {
                FilePaths      = [temporaryPath],
                SavePath       = savePath,
                ContentLayout  = EnumContentLayout.Original,
                Tags           = string.Join(',', tags),
                Rename         = string.IsNullOrWhiteSpace(rename) ? null : rename,
                // qB 5.x 认 stopped、4.x 认 paused，客户端按服务器版本择一发送，这里同时声明两种开关。
                PausedEnabled  = startStopped,
                StoppedEnabled = startStopped,
                // 复用校验依赖真实 recheck：skip_checking 会让 qB 跳过哈希校验并假定数据完整，
                // 既毁掉「请求前未完成 → 请求后完成」的基线证据，也可能把空目录判成已完成。
                SkipCheckingEnabled = false
            };
            return await client.Torrent.AddTorrent(request, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }

    public async Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken = default)
    {
        if (infoHashes.Count == 0) return [];

        var hashes = infoHashes.Select(NormalizeHash).ToList();
        var infos = await client.Torrent.GetTorrentInfos(hashes,
                                                         cancellationToken : cancellationToken);
        return infos.Select(ToSnapshot).ToList();
    }

    public async Task<IReadOnlyList<TorrentSnapshot>> ListTorrentsAsync(
        string? tag, CancellationToken cancellationToken = default)
    {
        var infos = await client.Torrent.GetTorrentInfos(tag : tag, cancellationToken : cancellationToken);
        return infos.Select(ToSnapshot).ToList();
    }

    public Task AddTagAsync(string infoHash, string tag, CancellationToken cancellationToken = default) =>
        client.Torrent.AddTorrentTag(NormalizeHash(infoHash), tag, cancellationToken);

    public async Task<IReadOnlyList<string>> ListFileNamesAsync(
        string infoHash, CancellationToken cancellationToken = default)
    {
        var files = await client.Torrent.GetTorrentFiles(NormalizeHash(infoHash),
                                                         cancellationToken : cancellationToken);
        return (files ?? []).Select(file => file.Name ?? string.Empty).ToList();
    }

    public async Task<IReadOnlyList<TorrentFileSnapshot>> ListFilesAsync(
        string infoHash, CancellationToken cancellationToken = default)
    {
        var files = await client.Torrent.GetTorrentFiles(NormalizeHash(infoHash),
                                                         cancellationToken : cancellationToken);
        return (files ?? []).Select(file =>
            new TorrentFileSnapshot(file.Index ?? throw new InvalidOperationException("下载器没有返回可靠的文件 index。"),
                                    file.Name ?? throw new InvalidOperationException("下载器没有返回文件路径。"),
                                    file.Size, file.Progress, file.Priority != EnumTorrentFilePriority.DoNotDownload))
                            .ToList();
    }

    public async Task<byte[]> ExportTorrentAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.Torrent.ExportTorrent(NormalizeHash(infoHash), cancellationToken);
        }
        catch (QbittorrentNotSupportedException exception)
        {
            // 库按服务器 WebAPI 版本抛 QbittorrentNotSupportedException（非系统 NotSupportedException）：
            // 在适配层翻译成接口约定的「不支持」，业务层统一按会话能力缺失区分，其余协议错误照常包装。
            throw new NotSupportedException("下载器版本不支持种子导出。", exception);
        }
    }

    public Task SetTorrentFileSelectionAsync(string                   infoHash,
                                             IReadOnlyCollection<int> fileIndices, bool download,
                                             CancellationToken        cancellationToken = default) =>
        client.Torrent.SetFilesPriority(NormalizeHash(infoHash), [.. fileIndices],
                                        download
                                            ? EnumTorrentFilePriority.Normal
                                            : EnumTorrentFilePriority.DoNotDownload,
                                        cancellationToken);

    public Task RecheckTorrentAsync(string infoHash, CancellationToken cancellationToken = default) =>
        client.Torrent.RecheckTorrent(NormalizeHash(infoHash), cancellationToken);

    public async Task<TorrentPropertiesSnapshot?> GetTorrentPropertiesAsync(
        string infoHash, CancellationToken cancellationToken = default)
    {
        var properties = await client.Torrent.GetTorrentGenericProperties(NormalizeHash(infoHash), cancellationToken);
        if (properties is null) return null;
        return new TorrentPropertiesSnapshot(properties.PiecesHave, properties.PiecesNum,
                                             properties.TotalSize, properties.TotalDownloaded,
                                             properties.CompletionDate);
    }

    public Task ReannounceAsync(string infoHash, CancellationToken cancellationToken = default) =>
        client.Torrent.ReannounceTorrent(NormalizeHash(infoHash), cancellationToken);

    public Task SetLocationAsync(string infoHash, string savePath, CancellationToken cancellationToken = default) =>
        client.Torrent.SetTorrentLocation(NormalizeHash(infoHash), savePath, cancellationToken);

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default) =>
        client.Torrent.PauseTorrent(NormalizeHash(infoHash), cancellationToken);

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default) =>
        client.Torrent.ResumeTorrent(NormalizeHash(infoHash), cancellationToken);

    public Task RenameTorrentAsync(string infoHash, string newName, CancellationToken cancellationToken = default) =>
        client.Torrent.RenameTorrent(NormalizeHash(infoHash), newName, cancellationToken);

    public Task RenameTorrentFileAsync(
        string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default) =>
        client.Torrent.RenameTorrentFile(NormalizeHash(infoHash), oldPath, newPath, cancellationToken);

    public Task RenameTorrentFolderAsync(
        string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default) =>
        client.Torrent.RenameTorrentFolder(NormalizeHash(infoHash), oldPath, newPath, cancellationToken);

    public Task DeleteTorrentAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default) =>
        client.Torrent.DeleteTorrent(NormalizeHash(infoHash), deleteFiles, cancellationToken);

    public Task DeleteTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, bool deleteFiles, CancellationToken cancellationToken = default) =>
        client.Torrent.DeleteTorrents(infoHashes.Select(NormalizeHash).ToList(), deleteFiles,
                                      cancellationToken);

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TorrentSnapshot ToSnapshot(TorrentInfo info) =>
        new(NormalizeHash(info.Hash                      ?? string.Empty), info.Name ?? string.Empty,
            QbittorrentTorrentStateMapper.Map(info.State ?? EnumTorrentState.Unknown),
            info.Progress  ?? 0,
            info.TotalSize ?? 0, info.SavePath ?? string.Empty,
            info.TagList   ?? [],
            string.Equals(info.State?.ToString(), "Moving", StringComparison.OrdinalIgnoreCase),
            // 仅 checking 系列（checkingDL/checkingUP/checkingResumeData）在 qB 4.x/5.x 字符串相同；
            // 停止态字符串是 5.x 形态（stoppedUP/stoppedDL），4.x 原始值实为 pausedUP/pausedDL。
            // RawState 只用于展示与 checkingResumeData 判定，不用于停止态判定（停止态由
            // IsStoppedComplete 以 Progress+AmountLeft 兜底），因此统一采用 V5 字符串不失配业务语义。
            info.State is { } state ? StringUtils.TorrentState2StringV5(state) : null,
            info.AmountLeft,
            info.Completed,
            info.CompletionOn,
            info.ContentPath,
            info.AddedOn,
            info.LastActivity,
            info.SeedingTime);

    private static string NormalizeHash(string hash) => hash.Trim().ToLowerInvariant();
}
