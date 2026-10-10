using Banned.Qbittorrent;
using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Requests;
using Banned.Qbittorrent.Models.Torrent;

namespace MediaManager.Integrations.Qbittorrent;

/// <summary>包装 Banned.Qbittorrent 客户端：临时文件上传、Hash 规范化与快照映射都在这里完成。</summary>
public sealed class QbittorrentSession(QBittorrentClient client) : IQbittorrentSession
{
    public string ServerVersion => client.QbittorrentVersion;

    public async Task<string> AddTorrentAsync(byte[]                torrentBytes, string savePath,
                                              IReadOnlyList<string> tags,         string? rename,
                                              CancellationToken     cancellationToken = default)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"MediaManager-{Guid.NewGuid():N}.torrent");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, torrentBytes, cancellationToken);
            var request = new AddTorrentRequest
            {
                FilePaths      = [temporaryPath],
                SavePath       = savePath,
                Tags           = string.Join(',', tags),
                Rename         = string.IsNullOrWhiteSpace(rename) ? null : rename,
                PausedEnabled  = false,
                StoppedEnabled = false
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

    public async Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(IReadOnlyCollection<string> infoHashes,
                                                                        CancellationToken cancellationToken = default)
    {
        if (infoHashes.Count == 0) return [];

        var hashes = infoHashes.Select(NormalizeHash).ToList();
        var infos = await client.Torrent.GetTorrentInfos(hashes,
                                                         cancellationToken : cancellationToken);
        return infos.Select(ToSnapshot).ToList();
    }

    public async Task<IReadOnlyList<TorrentSnapshot>> ListTorrentsAsync(string?           tag,
                                                                        CancellationToken cancellationToken = default)
    {
        var infos = await client.Torrent.GetTorrentInfos(tag : tag, cancellationToken : cancellationToken);
        return infos.Select(ToSnapshot).ToList();
    }

    public Task AddTagAsync(string infoHash, string tag, CancellationToken cancellationToken = default)
    {
        return client.Torrent.AddTorrentTag(NormalizeHash(infoHash), tag, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListFileNamesAsync(string            infoHash,
                                                                CancellationToken cancellationToken = default)
    {
        var files = await client.Torrent.GetTorrentFiles(NormalizeHash(infoHash),
                                                         cancellationToken : cancellationToken);
        return (files ?? []).Select(file => file.Name ?? string.Empty).ToList();
    }

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        return client.Torrent.PauseTorrent(NormalizeHash(infoHash), cancellationToken);
    }

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        return client.Torrent.ResumeTorrent(NormalizeHash(infoHash), cancellationToken);
    }

    public Task RenameTorrentAsync(string            infoHash, string newName,
                                   CancellationToken cancellationToken = default)
    {
        return client.Torrent.RenameTorrent(NormalizeHash(infoHash), newName, cancellationToken);
    }

    public Task RenameTorrentFileAsync(string            infoHash, string oldPath, string newPath,
                                       CancellationToken cancellationToken = default)
    {
        return client.Torrent.RenameTorrentFile(NormalizeHash(infoHash), oldPath, newPath, cancellationToken);
    }

    public Task RenameTorrentFolderAsync(string            infoHash, string oldPath, string newPath,
                                         CancellationToken cancellationToken = default)
    {
        return client.Torrent.RenameTorrentFolder(NormalizeHash(infoHash), oldPath, newPath, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TorrentSnapshot ToSnapshot(TorrentInfo info)
    {
        return new TorrentSnapshot(NormalizeHash(info.Hash ?? string.Empty), info.Name ?? string.Empty,
                                   QbittorrentTorrentStateMapper.Map(info.State ?? EnumTorrentState.Unknown),
                                   info.Progress  ?? 0,
                                   info.TotalSize ?? 0, info.SavePath ?? string.Empty,
                                   info.TagList ?? []);
    }

    private static string NormalizeHash(string hash)
    {
        return hash.Trim().ToLowerInvariant();
    }
}
