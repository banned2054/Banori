using MediaManager.Core.Torrents;
using System.Security.Cryptography;
using System.Text;

namespace MediaManager.Tests.Downloads;

/// <summary>info-hash 提取边界回归：期望值用独立字面量计算，不与实现共享解析逻辑。</summary>
public sealed class TorrentInfoHashExtractorTests
{
    [Fact]
    public void Extract_ReturnsSha1OfInfoDictSpan()
    {
        // 整个种子与 info 字典都是独立字面量；期望值直接对字面量子串做 SHA-1。
        const string infoDict = "d6:lengthi1024e4:name12:Test Torrent12:piece lengthi16384e" + "e";
        var torrent = Encoding.UTF8.GetBytes(
                                             "d8:announce31:http://tracker.example/announce4:info" + infoDict + "e");

        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentInfoHashExtractor.TryExtractInfoHash(torrent, out var hash));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void Extract_HandlesNestedListsAndSpecialStringContent()
    {
        // info 字典包含嵌套列表与含 'e'、数字冒号等内容的字符串；字符串长度前缀必须正确跳过。
        const string infoDict = "d5:filesl3:a.ee4:name8:a:3bcd:e12:piece lengthi16384e" + "e";
        var          torrent  = Encoding.UTF8.GetBytes("d9:publisher6:person4:info" + infoDict + "e");

        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentInfoHashExtractor.TryExtractInfoHash(torrent, out var hash));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void Extract_InfoDictNotLastKeyStillMatches()
    {
        const string infoDict = "d4:name3:abc" + "e";
        var          torrent  = Encoding.UTF8.GetBytes("d4:info" + infoDict + "7:comment3:hi!" + "e");

        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(infoDict))).ToLowerInvariant();

        Assert.True(TorrentInfoHashExtractor.TryExtractInfoHash(torrent, out var hash));
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("d4:name3:abce")]
    [InlineData("i42ee")]
    [InlineData("d4:infod4:name3:abc")]
    public void Extract_RejectsMalformedPayloads(string payload)
    {
        Assert.False(TorrentInfoHashExtractor.TryExtractInfoHash(Encoding.UTF8.GetBytes(payload), out var hash));
        Assert.Equal(string.Empty, hash);
    }

    [Fact]
    public void ExtractTrackers_CollectsAnnounceAndNestedAnnounceList()
    {
        var torrent = Encoding.UTF8.GetBytes(
                                             "d" + Str("announce", "udp://tracker.byr.pt:6969/announce") +
                                             "13:announce-list" + "ll" +
                                             Str(null, "https://tr.bangumi.moe:6969/announce") +
                                             Str(null, "udp://tracker-public.tjupt.org/announce") + "ee" +
                                             "4:infod4:name3:abce" + "e");

        List<string> trackers = [];
        Assert.True(TorrentInfoHashExtractor.TryExtractTrackerUrls(torrent, trackers));
        Assert.Equal(
        [
            "udp://tracker.byr.pt:6969/announce",
            "https://tr.bangumi.moe:6969/announce",
            "udp://tracker-public.tjupt.org/announce"
        ], trackers);
    }

    [Fact]
    public void ExtractTrackers_HandlesFlatAnnounceListAndReportsAbsence()
    {
        var flat = Encoding.UTF8.GetBytes(
                                          "d13:announce-list" + "l" + Str(null, "udp://daydream.dmhy.best:6969") + "e" +
                                          "4:infod4:name3:abce" + "e");
        List<string> flatTrackers = [];
        Assert.True(TorrentInfoHashExtractor.TryExtractTrackerUrls(flat, flatTrackers));
        Assert.Equal(["udp://daydream.dmhy.best:6969"], flatTrackers);

        var          noTracker = Encoding.UTF8.GetBytes("d4:infod4:name3:abce" + "e");
        List<string> empty     = [];
        Assert.False(TorrentInfoHashExtractor.TryExtractTrackerUrls(noTracker, empty));
        Assert.Empty(empty);
    }

    private static string Str(string? key, string value)
    {
        var prefix = key is null ? string.Empty : $"{key.Length}:{key}";
        return $"{prefix}{Encoding.UTF8.GetByteCount(value)}:{value}";
    }
}
