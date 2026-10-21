using Banori.Core.Torrents;
using MonoTorrent;
using System.Security.Cryptography;
using System.Text;

namespace Banori.Tests.Downloads;

/// <summary>TorrentDownloadPolicy.TryLoad 边界回归：期望值用独立原始 info 字节计算，不与实现共享解析逻辑。</summary>
public sealed class TorrentDownloadPolicyTests
{
    private const string TrackerInfo =
        "d6:lengthi1e4:name3:abc12:piece lengthi16384e6:pieces20:00000000000000000000e";

    [Fact]
    public void TryLoad_ReturnsSha1OfOriginalInfoDictBytes()
    {
        // info 字典包含高层 Torrent 加载必需的完整 v1 字段；期望不经过生产解析器或重编码。
        const string infoDict =
            "d6:lengthi1024e4:name12:Test Torrent12:piece lengthi16384e6:pieces20:00000000000000000000e";
        var bytes    = Encoding.UTF8.GetBytes("d8:announce31:http://tracker.example/announce4:info" + infoDict + "e");
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal(expected, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
        Assert.Equal("Test Torrent", torrent.Name);
    }

    [Fact]
    public void TryLoad_HandlesValidNestedFilesAndSpecialStringContent()
    {
        // files 必须是文件字典列表；路径字符串包含 'e'、数字与冒号，不能误当作 bencode 结构。
        // 未知字段也可包含特殊字符串，不影响标准文件字段与 hash 的读取。
        const string infoDict = "d5:filesl" + "d6:lengthi1e4:pathl3:a.eee" + "d6:lengthi2e4:pathl6:folder5:b.mkvee" +
                                "e4:name6:bundle12:piece lengthi16384e6:pieces20:00000000000000000000" +
                                "6:x-datall8:a:3bcd:eeee";
        var bytes    = Encoding.UTF8.GetBytes("d4:info" + infoDict + "9:publisher6:persone");
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal(expected, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
    }

    [Fact]
    public void TryLoad_InfoDictNotLastKeyStillMatches()
    {
        const string infoDict =
            "d6:lengthi1e4:name3:abc12:piece lengthi16384e6:pieces20:00000000000000000000e";
        var bytes    = Encoding.UTF8.GetBytes("d7:comment3:hi!4:info" + infoDict + "9:publisher6:persone");
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal(expected, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
    }

    [Fact]
    public void TryLoad_Utf8DisplayNameWinsButHashIncludesOriginalFallbackBytes()
    {
        const string infoDict = "d6:lengthi1e4:name12:fallback.mkv10:name.utf-810:作品.mkv" +
                                "12:piece lengthi16384e6:pieces20:00000000000000000000e";
        var bytes    = Encoding.UTF8.GetBytes("d4:info" + infoDict + "e");
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal(expected, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
        Assert.Equal("作品.mkv", torrent.Name);
    }

    [Fact]
    public void TryLoad_BinaryPieceHashIsHashedAsOriginalBytes()
    {
        byte[] pieceHash =
        [
            0x00, 0xff, 0xfe, 0x80, 0xc0, 0x65, 0x3a, 0x64, 0x69, 0x6c,
            0x31, 0x32, 0x33, 0x0a, 0x0d, 0x7f, 0x01, 0x02, 0x03, 0x04
        ];
        byte[] infoBytes =
        [
            .. "d6:lengthi1e4:name3:abc12:piece lengthi16384e6:pieces20:"u8.ToArray(),
            .. pieceHash, (byte)'e'
        ];
        byte[] bytes    = [.. "d4:info"u8.ToArray(), .. infoBytes, (byte)'e'];
        var    expected = Convert.ToHexString(SHA1.HashData(infoBytes)).ToLowerInvariant();

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal(expected, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("d4:name3:abce")]
    [InlineData("i42ee")]
    [InlineData("d4:infod4:name3:abc")]
    public void TryLoad_RejectsMalformedPayloads(string payload)
    {
        Assert.False(TorrentDownloadPolicy.TryLoad(Encoding.UTF8.GetBytes(payload), out var torrent, out _));
        Assert.Null(torrent);
    }

    [Theory]
    [InlineData("d4:name3:abce")]
    [InlineData("d6:lengthi1e4:name3:abce")]
    [InlineData("d6:lengthi1e4:name3:abc12:piece lengthi16384ee")]
    [InlineData("d6:lengthi1e4:name3:abc6:pieces20:00000000000000000000e")]
    [InlineData("d5:filesl3:a.ee4:name3:abc12:piece lengthi16384e6:pieces20:00000000000000000000e")]
    public void TryLoad_RejectsIncompleteOrInvalidTorrentMetadata(string infoDict)
    {
        var bytes = Encoding.UTF8.GetBytes("d4:info" + infoDict + "e");

        Assert.False(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Null(torrent);
    }

    [Fact]
    public void TryLoad_RootAnnounceIsUsedWithoutAnnounceList()
    {
        const string tracker = "udp://tracker.byr.pt:6969/announce";
        var          bytes   = Encoding.UTF8.GetBytes("d" + Str("announce", tracker) + "4:info" + TrackerInfo + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal([tracker], torrent.AnnounceUrls.SelectMany(tier => tier));
    }

    [Fact]
    public void TryLoad_StandardTiersOverrideRootAnnounce()
    {
        var bytes = Encoding.UTF8.GetBytes("d" + Str("announce", "udp://tracker.byr.pt:6969/announce") +
                                           "13:announce-listll" +
                                           Str(null, "https://tr.bangumi.moe:6969/announce") +
                                           Str(null, "udp://tracker-public.tjupt.org/announce") + "el" +
                                           Str(null, "udp://daydream.dmhy.best:6969/announce") + "ee" +
                                           "4:info" + TrackerInfo + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal([
            "https://tr.bangumi.moe:6969/announce",
            "udp://tracker-public.tjupt.org/announce",
            "udp://daydream.dmhy.best:6969/announce"
        ], torrent.AnnounceUrls.SelectMany(tier => tier));
    }

    [Fact]
    public void TryLoad_FlatAnnounceListIsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes("d13:announce-listl" + Str(null, "udp://daydream.dmhy.best:6969/announce") +
                                           "e"                  + "4:info" + TrackerInfo + "e");

        Assert.False(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Null(torrent);
    }

    [Fact]
    public void TryLoad_WithoutAnnounceStillReadsHashAndNameButReportsNoTrackers()
    {
        var bytes    = Encoding.UTF8.GetBytes("d4:info" + TrackerInfo + "e");
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(TrackerInfo))).ToLowerInvariant();

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Equal(expected, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
        Assert.Equal("abc", torrent.Name);
        Assert.Empty(torrent.AnnounceUrls.SelectMany(tier => tier));
    }

    [Fact]
    public void V2OnlyTorrent_IsUnsupportedWithoutPartialResults()
    {
        // 有效的单文件 v2 布局：1 字节文件、32 字节 pieces root；不含任何 v1 pieces/length 字段。
        var infoDict = "d9:file treed8:file.mkvd0:d6:lengthi1e11:pieces root32:" +
                       new string('0', 32) + "eee12:meta versioni2e4:name8:file.mkv12:piece lengthi16384ee";
        var bytes = Encoding.UTF8.GetBytes("d4:info" + infoDict + "e");

        Assert.True(Torrent.TryLoad(bytes, out var loaded));
        Assert.NotNull(loaded.InfoHashes.V2);
        Assert.Null(loaded.InfoHashes.V1);

        Assert.False(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
        Assert.Null(torrent);
    }

    private static string Str(string? key, string value)
    {
        var prefix = key is null ? string.Empty : $"{Encoding.UTF8.GetByteCount(key)}:{key}";
        return $"{prefix}{Encoding.UTF8.GetByteCount(value)}:{value}";
    }
}
