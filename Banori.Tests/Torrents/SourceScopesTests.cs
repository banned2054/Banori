using Banori.Core.Torrents;

namespace Banori.Tests.Torrents;

public sealed class SourceScopesTests
{
    [Theory]
    [InlineData(null, SourceScopes.Legacy)]
    [InlineData("", SourceScopes.Legacy)]
    [InlineData("   ", SourceScopes.Legacy)]
    [InlineData(" MIKAN ", "mikan")]
    [InlineData("U2", "u2")]
    [InlineData("bt", SourceScopes.Other)]
    [InlineData(" BT ", SourceScopes.Other)]
    [InlineData("(legacy)", SourceScopes.Legacy)]
    [InlineData("(UNKNOWN)", SourceScopes.Unknown)]
    [InlineData("custom", "custom")]
    public void Normalize_TrimsLowercasesAndMapsLegacyValues(string? value, string expected)
    {
        Assert.Equal(expected, SourceScopes.Normalize(value));
    }

    [Theory]
    [InlineData("mikanani.me", "mikan")]
    [InlineData("Mikanime.TV", "mikan")]
    [InlineData("rss.mikanani.me", "mikan")]
    [InlineData("anibt.net", "anibt")]
    [InlineData("api.anibt.net", "anibt")]
    // 未知 RSS 主机归 (unknown)，绝不落入 legacy 隔离域。
    [InlineData("example.com", SourceScopes.Unknown)]
    [InlineData("mikanani.me.evil.com", SourceScopes.Unknown)]
    [InlineData(null, SourceScopes.Unknown)]
    [InlineData("   ", SourceScopes.Unknown)]
    public void FromFeedHost_UsesSharedFeedHostTable(string? host, string expected)
    {
        Assert.Equal(expected, SourceScopes.FromFeedHost(host));
    }

    [Theory]
    [InlineData("https://mikanani.me/RSS/MyBangumi?token=secret", "mikan")]
    [InlineData("https://mikanime.tv/RSS/latest", "mikan")]
    [InlineData("https://anibt.net/rss/torrents", "anibt")]
    [InlineData("https://example.com/rss.xml", SourceScopes.Unknown)]
    [InlineData("not a url", SourceScopes.Unknown)]
    [InlineData(null, SourceScopes.Unknown)]
    public void FromFeedUrl_UnknownOrInvalidIsUnknown(string? feedUrl, string expected)
    {
        Assert.Equal(expected, SourceScopes.FromFeedUrl(feedUrl));
    }

    [Theory]
    [InlineData(null, SourceScopes.Unknown)]
    [InlineData("", SourceScopes.Unknown)]
    [InlineData("   ", SourceScopes.Unknown)]
    [InlineData("bt", SourceScopes.Other)]
    [InlineData("U2", "u2")]
    [InlineData(" Mikan ", "mikan")]
    public void FromSiteSource_NullIsUnknownAndLegacyBtMapsToOther(string? siteSource, string expected)
    {
        Assert.Equal(expected, SourceScopes.FromSiteSource(siteSource));
    }

    [Fact]
    public void FromUploadTrackers_UsesTrackerClassification()
    {
        Assert.Equal(SourceScopes.Other, SourceScopes.FromUploadTrackers([]));
        Assert.Equal(SourceScopes.Other, SourceScopes.FromUploadTrackers(["https://tracker.example/announce"]));
        Assert.Equal("u2", SourceScopes.FromUploadTrackers(["https://daydream.dmhy.best/announce?passkey=abc"]));

        // 同一分类的多个 tracker 只算一个来源；不同分类不在本函数抛异常，交由调用方拒绝。
        Assert.Equal("tjupt", SourceScopes.FromUploadTrackers(
        [
            "https://tracker-public.tjupt.org/announce.php?passkey=a",
            "https://tracker-campus.tjupt.org/announce.php?passkey=b"
        ]));
        Assert.Equal(SourceScopes.Other, SourceScopes.FromUploadTrackers(
        [
            "https://tracker.byr.pt/announce.php?passkey=x",
            "https://tracker-campus.tjupt.org/announce.php?passkey=y"
        ]));
    }

    [Fact]
    public void FromUploadTrackers_RejectsNullCollection()
    {
        Assert.Throws<ArgumentNullException>(() => SourceScopes.FromUploadTrackers(null!));
    }
}
