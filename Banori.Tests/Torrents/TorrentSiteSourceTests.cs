using Banori.Core.Torrents;

namespace Banori.Tests.Torrents;

public sealed class TorrentSiteSourceTests
{
    [Theory]
    [InlineData("https://mikanani.me/RSS/MyBangumi?token=secret", "mikan")]
    [InlineData("https://mikanani.me/Download/1.torrent", "mikan")]
    [InlineData("https://www.mikanani.me/RSS/latest", "mikan")]
    [InlineData("https://anibt.net/rss/torrents", "anibt")]
    [InlineData("https://api.anibt.net/feed", "anibt")]
    // 不支持的 RSS 主机不取首段域名标签充当来源。
    [InlineData("https://example.com/rss.xml", null)]
    [InlineData("https://acg.rss.example.com/feed", null)]
    // 伪造的相似主机不得命中。
    [InlineData("https://mikanani.me.evil.com/RSS", null)]
    [InlineData("https://anibt.net.example.org/rss", null)]
    public void FromFeedUrl_MapsOnlySupportedHosts(string url, string? expected)
    {
        Assert.Equal(expected, TorrentSiteSource.FromFeedUrl(url));
    }

    [Fact]
    public void FromFeedUrl_ReturnsNullForMissingOrInvalidUrl()
    {
        Assert.Null(TorrentSiteSource.FromFeedUrl(null));
        Assert.Null(TorrentSiteSource.FromFeedUrl(""));
        Assert.Null(TorrentSiteSource.FromFeedUrl("not a url"));
    }

    [Fact]
    public void FromFeedHost_ReturnsNullForUnknownOrSimilarHosts()
    {
        Assert.Equal("mikan", TorrentSiteSource.FromFeedHost("Mikanani.me"));
        Assert.Equal("anibt", TorrentSiteSource.FromFeedHost("anibt.NET"));
        Assert.Null(TorrentSiteSource.FromFeedHost("tracker.byr.pt"));
        Assert.Null(TorrentSiteSource.FromFeedHost("mikanani.me.evil.com"));
        Assert.Null(TorrentSiteSource.FromFeedHost(null));
        Assert.Null(TorrentSiteSource.FromFeedHost("   "));
    }

    /// <summary>上传 .torrent 的 PT tracker 分类表：主机名+路径精确匹配，忽略 query（passkey/secure 等）。</summary>
    [Theory]
    [InlineData("https://daydream.dmhy.best/announce", "u2")]
    [InlineData("https://daydream.dmhy.best/announce?secure=1&passkey=abc", "u2")]
    [InlineData("https://tracker.byr.pt/announce.php", "byrbt")]
    [InlineData("https://tracker.byr.pt:6969/announce.php?passkey=xyz", "byrbt")]
    [InlineData("https://tracker-public.tjupt.org/announce.php?secure=1&passkey=k", "tjupt")]
    [InlineData("https://tracker-campus.tjupt.org/announce.php?passkey=k", "tjupt")]
    public void ClassifyTrackers_MatchesPTCategoriesIgnoringQuery(string tracker, string expected)
    {
        var labels = TorrentSiteSource.ClassifyTrackers([tracker]);
        var label  = Assert.Single(labels);
        Assert.Equal(expected, label);
    }

    [Fact]
    public void ClassifyTrackers_SameCategoryMultipleTrackersCountsOnce()
    {
        // 北洋园两个 tracker 同时存在仍只算 tjupt 一个分类。
        var labels = TorrentSiteSource.ClassifyTrackers(
        [
            "https://tracker-public.tjupt.org/announce.php?passkey=a",
            "https://tracker-campus.tjupt.org/announce.php?passkey=b"
        ]);
        var label = Assert.Single(labels);
        Assert.Equal("tjupt", label);
    }

    [Fact]
    public void ClassifyTrackers_DistinctCategoriesAreAllReported()
    {
        // 不同分类同时命中不做先命中先得，全部返回给调用方按冲突处理。
        var labels = TorrentSiteSource.ClassifyTrackers(
        [
            "http://tracker.example/announce",
            "https://tracker.byr.pt/announce.php?passkey=x",
            "https://tracker-campus.tjupt.org/announce.php?passkey=y"
        ]);
        Assert.Equal(["byrbt", "tjupt"], labels.Order());
    }

    [Fact]
    public void ClassifyTrackers_IgnoresUnknownPublicAndSimilarHosts()
    {
        // 公共 tracker 不再用于推断 mikan/anibt；伪造相似主机与错误路径不得误命中。
        Assert.Empty(TorrentSiteSource.ClassifyTrackers([]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["http://tracker.example/announce"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["https://tr.bangumi.moe:9696/announce"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["udp://nyaa.tracker.wf:457/announce"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["https://tracker.anibt.net/announce"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["https://evil-tracker.byr.pt/announce.php"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["https://tracker.byr.pt.evil.com/announce.php"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["https://tracker.byr.pt/announce"]));
        Assert.Empty(TorrentSiteSource.ClassifyTrackers(["not a url", " "]));
    }
}
