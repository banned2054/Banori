using MonoTorrent;
using MonoTorrent.BEncoding;

namespace Banori.Core.Torrents;

public static class TorrentDownloadPolicy
{
    public static bool TryLoad(ReadOnlySpan<byte> torrentBytes, out Torrent torrent, out bool isMultiFile)
    {
        torrent     = null!;
        isMultiFile = false;
        if (!Torrent.TryLoad(torrentBytes, out var loaded) || loaded.InfoHashes.V1 is null) return false;

        // MonoTorrent does not expose the layout; even a multi-file torrent can contain just one file.
        var (root, _) = BEncodedDictionary.DecodeTorrent(torrentBytes);
        isMultiFile   = ((BEncodedDictionary)root["info"]).ContainsKey("files");
        torrent       = loaded;
        return true;
    }

    public static string GetRelativePath(Torrent torrent, ITorrentFile file, bool isMultiFile)
    {
        // qBittorrent paths include the root folder and use / on every host platform.
        var path = isMultiFile ? $"{torrent.Name}/{file.Path}" : file.Path;
        return path.Replace('\\', '/');
    }

    /// <summary>qB 4.4+ 按元数据顺序过滤 padding 后连续编号；保留空文件的位置，改名不改变编号。</summary>
    public static IReadOnlyDictionary<string, int> GetQbittorrentFileIndices(
        ReadOnlySpan<byte> torrentBytes, Torrent torrent, bool isMultiFile)
    {
        if (!isMultiFile)
            return new Dictionary<string, int> { [GetRelativePath(torrent, torrent.Files[0], false)] = 0 };

        var (root, _) = BEncodedDictionary.DecodeTorrent(torrentBytes);
        var files   = (BEncodedList)((BEncodedDictionary)root["info"])["files"];
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            var file = (BEncodedDictionary)files[index];
            if (file.TryGetValue("attr", out var attr) && ((BEncodedString)attr).Text.Contains('p')) continue;
            var path     = (BEncodedList)(file.TryGetValue("path.utf-8", out var utf8) ? utf8 : file["path"]);
            var relative = string.Join('/', path.Cast<BEncodedString>().Select(segment => segment.Text));
            indices.Add($"{torrent.Name}/{relative}".Replace('\\', '/'), indices.Count);
        }

        return indices;
    }
}
