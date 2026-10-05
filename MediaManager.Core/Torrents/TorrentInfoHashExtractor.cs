using System.Security.Cryptography;
using System.Text;

namespace MediaManager.Core.Torrents;

/// <summary>
///     从 .torrent 文件字节中提取 info-hash（BEP-3：info 字典的 SHA-1，小写十六进制）。
///     只扫描字段边界，不构建完整对象树；提交下载器前必须先取得规范化 Hash，供响应丢失后对账。
/// </summary>
public static class TorrentInfoHashExtractor
{
    public static bool TryExtractInfoHash(ReadOnlySpan<byte> torrentBytes, out string infoHashHex)
    {
        infoHashHex = string.Empty;
        if (torrentBytes.Length < 2 || torrentBytes[0] != (byte)'d') return false;

        var position = 1;
        while (position < torrentBytes.Length)
        {
            if (torrentBytes[position] == (byte)'e') return false;

            if (!TryReadStringBytes(torrentBytes, ref position, out var key)) return false;

            var valueStart = position;
            if (!SkipValue(torrentBytes, ref position)) return false;

            if (key.SequenceEqual("info"u8))
            {
                infoHashHex = Convert.ToHexString(SHA1.HashData(torrentBytes[valueStart..position]))
                                     .ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    /// <summary>提取 BEP-3 info.name，作为上传种子的默认显示名；不要求种子包含可选的 name.utf-8。</summary>
    public static bool TryExtractDisplayName(ReadOnlySpan<byte> torrentBytes, out string displayName)
    {
        displayName = string.Empty;
        if (torrentBytes.Length < 2 || torrentBytes[0] != (byte)'d') return false;

        var position = 1;
        while (position < torrentBytes.Length)
        {
            if (torrentBytes[position] == (byte)'e') return false;

            if (!TryReadStringBytes(torrentBytes, ref position, out var key)) return false;

            if (key.SequenceEqual("info"u8))
                return TryReadInfoName(torrentBytes, ref position, out displayName);

            if (!SkipValue(torrentBytes, ref position)) return false;
        }

        return false;
    }

    private static bool TryReadInfoName(ReadOnlySpan<byte> data, ref int position, out string displayName)
    {
        displayName = string.Empty;
        if (position >= data.Length || data[position] != (byte)'d') return false;

        position++;
        while (position < data.Length && data[position] != (byte)'e')
        {
            if (!TryReadStringBytes(data, ref position, out var key)) return false;

            if (key.SequenceEqual("name"u8))
            {
                if (!TryReadStringBytes(data, ref position, out var value)) return false;

                displayName = Encoding.UTF8.GetString(value);
                return displayName.Length > 0;
            }

            if (!SkipValue(data, ref position)) return false;
        }

        return false;
    }

    private static bool TryReadStringBytes(ReadOnlySpan<byte> data, ref int position, out ReadOnlySpan<byte> value)
    {
        value = default;
        var colon = data[position..].IndexOf((byte)':');
        if (colon <= 0) return false;

        var lengthSpan = data[position..(position + colon)];
        if (!TryParseDigits(lengthSpan, out var length)) return false;

        var start = position + colon + 1;
        if (length < 0 || start + length > data.Length) return false;

        value    = data[start..(start + length)];
        position = start + length;
        return true;
    }

    /// <summary>跳过一个 bencode 值（整数、字符串、列表或字典），位置停在值结束后的下一字节。</summary>
    private static bool SkipValue(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length) return false;

        switch (data[position])
        {
            case (byte)'i' :
                var end = data[position..].IndexOf((byte)'e');
                if (end < 0) return false;

                position += end + 1;
                return true;
            case (byte)'d' or (byte)'l' :
                var depth = 0;
                while (position < data.Length)
                    switch (data[position])
                    {
                        case (byte)'d' or (byte)'l' :
                            depth++;
                            position++;
                            break;
                        case (byte)'e' :
                            depth--;
                            position++;
                            if (depth == 0) return true;

                            break;
                        case (byte)'i' :
                            var intEnd = data[position..].IndexOf((byte)'e');
                            if (intEnd < 0) return false;

                            position += intEnd + 1;
                            break;
                        default :
                            if (!TryReadStringBytes(data, ref position, out _)) return false;

                            break;
                    }

                return false;
            default :
                return TryReadStringBytes(data, ref position, out _);
        }
    }

    private static bool TryParseDigits(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        foreach (var digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                value = 0;
                return false;
            }

            if (value > int.MaxValue / 10) return false;

            value = value * 10 + (digit - '0');
        }

        return true;
    }
}
