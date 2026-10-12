using Banori.Core.Downloads;
using System.Globalization;
using System.Text;

namespace Banori.Core.Torrents;

public sealed record TorrentFileEntry(int Index, string Path, long Length);

public sealed record TorrentFileManifest(string Name, bool IsMultiFile, IReadOnlyList<TorrentFileEntry> Files);

/// <summary>读取 BEP-3 单/多文件清单；路径与 qBittorrent 文件清单一致，多文件包含 info.name 根目录。</summary>
public static class TorrentFileManifestParser
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static bool TryParse(ReadOnlySpan<byte> torrentBytes, out TorrentFileManifest manifest)
    {
        manifest = null!;
        try
        {
            if (!TryFields(torrentBytes, out var root) || !root.TryGetValue("info", out var infoRange)) return false;
            var info = torrentBytes[infoRange];
            if (!TryFields(info, out var fields) || !TryName(info, fields, out var name)) return false;
            List<TorrentFileEntry> files     = [];
            var                    multiFile = fields.TryGetValue("files", out var filesRange);
            if (multiFile)
            {
                if (fields.ContainsKey("length")) return false;
                var list = info[filesRange];
                if (list.Length < 2 || list[0] != (byte)'l') return false;
                var position = 1;
                while (position < list.Length && list[position] != (byte)'e')
                {
                    var start = position;
                    if (!TorrentInfoHashExtractor.SkipValue(list, ref position)) return false;
                    var file = list[start..position];
                    if (!TryFields(file, out var fileFields) || !TryLength(file, fileFields, out var length) ||
                        !TryPath(file, fileFields, out var path)) return false;
                    files.Add(new TorrentFileEntry(files.Count, RelativeDownloadPathPolicy.Normalize(name + "/" + path),
                                                   length));
                }

                if (files.Count == 0 || position != list.Length - 1 || list[position] != (byte)'e') return false;
            }
            else
            {
                if (!TryLength(info, fields, out var length)) return false;
                files.Add(new TorrentFileEntry(0, name, length));
            }

            if (files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                return false;
            manifest = new TorrentFileManifest(name, multiFile, files.AsReadOnly());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryFields(ReadOnlySpan<byte> data, out Dictionary<string, Range> fields)
    {
        fields = new Dictionary<string, Range>(StringComparer.Ordinal);
        if (data.Length < 2 || data[0] != (byte)'d') return false;
        var position = 1;
        while (position < data.Length && data[position] != (byte)'e')
        {
            if (!TorrentInfoHashExtractor.TryReadStringBytes(data, ref position, out var key)) return false;
            var start = position;
            if (!TorrentInfoHashExtractor.SkipValue(data, ref position) ||
                !fields.TryAdd(StrictUtf8.GetString(key), start..position)) return false;
        }

        return position == data.Length - 1 && data[position] == (byte)'e';
    }

    private static bool TryName(ReadOnlySpan<byte> data, Dictionary<string, Range> fields, out string name)
    {
        name = string.Empty;
        if (!fields.TryGetValue("name.utf-8", out var range) && !fields.TryGetValue("name", out range)) return false;
        return TrySegment(data[range], out name);
    }

    private static bool TrySegment(ReadOnlySpan<byte> data, out string segment)
    {
        segment = string.Empty;
        var position = 0;
        if (!TorrentInfoHashExtractor.TryReadStringBytes(data, ref position, out var bytes) || position != data.Length)
            return false;
        var text = StrictUtf8.GetString(bytes);
        if (text.Contains('/') || text.Contains('\\') || text != text.Trim()) return false;
        segment = RelativeDownloadPathPolicy.Normalize(text);
        return true;
    }

    private static bool TryPath(ReadOnlySpan<byte> data, Dictionary<string, Range> fields, out string path)
    {
        path = string.Empty;
        if (!fields.TryGetValue("path.utf-8", out var range) && !fields.TryGetValue("path", out range)) return false;
        var list = data[range];
        if (list.Length < 2 || list[0] != (byte)'l') return false;
        List<string> segments = [];
        var          position = 1;
        while (position < list.Length && list[position] != (byte)'e')
        {
            var start = position;
            if (!TorrentInfoHashExtractor.TryReadStringBytes(list, ref position, out _) ||
                !TrySegment(list[start..position], out var segment)) return false;
            segments.Add(segment);
        }

        if (segments.Count == 0 || position != list.Length - 1 || list[position] != (byte)'e') return false;
        path = RelativeDownloadPathPolicy.Normalize(string.Join('/', segments));
        return true;
    }

    private static bool TryLength(ReadOnlySpan<byte> data, Dictionary<string, Range> fields, out long length)
    {
        length = 0;
        if (!fields.TryGetValue("length", out var range)) return false;
        var value = data[range];
        if (value.Length < 3 || value[0] != (byte)'i' || value[^1] != (byte)'e') return false;
        var digits = value[1..^1];
        if (digits.Length > 1 && digits[0] == (byte)'0') return false;
        foreach (var digit in digits)
            if (digit is < (byte)'0' or > (byte)'9')
                return false;
        return long.TryParse(Encoding.ASCII.GetString(digits), NumberStyles.None, CultureInfo.InvariantCulture,
                             out length);
    }
}
