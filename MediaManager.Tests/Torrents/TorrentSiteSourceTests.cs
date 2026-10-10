using MediaManager.Core.Torrents;

namespace MediaManager.Tests.Torrents;

public sealed class TorrentSiteSourceTests
{
    [Theory]
    [InlineData("https://mikanani.me/Download/1.torrent", "mikan")]
    [InlineData("https://mikanani.me/RSS/MyBangumi", "mikan")]
    [InlineData("https://anibt.net/rss/torrents", "anibt")]
    [InlineData("https://u2.dmhy.org/download.php?id=1", "U2")]
    [InlineData("https://tjupt.org/download.php?id=1", "tjupt")]
    [InlineData("https://byrbt.cn/download?id=1", "byrbt")]
    public void FromUrl_MapsKnownAndFirstLabelHosts(string url, string expected)
    {
        Assert.Equal(expected, TorrentSiteSource.FromUrl(url));
    }

    /// <summary>tracker 映射与 ByrTorrent 的 GetTagAndRatioLimitFromTrackers 对齐。</summary>
    [Theory]
    [InlineData("udp://tracker-public.tjupt.org:6969/announce", "tjupt")]
    [InlineData("https://tracker-campus.tjupt.org/announce", "tjupt")]
    [InlineData("https://tracker.byr.pt:6969/announce", "byrbt")]
    [InlineData("https://tr.bangumi.moe:6969/announce", "mikan")]
    [InlineData("udp://nyaa.tracker.wf:6969/announce", "mikan")]
    [InlineData("udp://t.nyaatracker.com:6969/announce", "mikan")]
    [InlineData("https://open.acgtracker.com:1096/announce", "mikan")]
    [InlineData("http://t.acg.rip:6699/announce", "mikan")]
    [InlineData("udp://daydream.dmhy.best:6969/announce", "U2")]
    [InlineData("https://tracker.anibt.net/announce", "anibt")]
    public void FromTrackers_MapsKnownTrackers(string tracker, string expected)
    {
        Assert.Equal(expected, TorrentSiteSource.FromTrackers([tracker]));
    }

    [Fact]
    public void FromTrackers_ReturnsNullWhenAllUnknown()
    {
        Assert.Null(TorrentSiteSource.FromTrackers(["http://tracker.example/announce"]));
        Assert.Null(TorrentSiteSource.FromTrackers([]));
    }

    [Fact]
    public void FromUrl_ReturnsNullForMissingOrInvalidUrl()
    {
        Assert.Null(TorrentSiteSource.FromUrl(null));
        Assert.Null(TorrentSiteSource.FromUrl(""));
        Assert.Null(TorrentSiteSource.FromUrl("not a url"));
    }

    [Fact]
    public void Sanitize_TrimsCollapsesWhitespaceAndEnforcesLimit()
    {
        Assert.Equal("u2", TorrentSiteSource.Sanitize("  u2  "));
        Assert.Null(TorrentSiteSource.Sanitize("   "));
        Assert.Equal(50, TorrentSiteSource.Sanitize(new string('a', 80))!.Length);
    }
}
