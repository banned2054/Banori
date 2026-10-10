using MediaManager.Integrations;
using System.Net;

namespace MediaManager.Tests.Integrations;

/// <summary>AniBT feed（标准 RSS 2.0 + anibt:/torrent: 命名空间扩展）的解析行为。</summary>
public sealed class AnibtIntegrationTests
{
    [Fact]
    public async Task FeedReader_ParsesAnibtItemAndExtractsBgmId()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"
                 xmlns:atom="http://www.w3.org/2005/Atom"
                 xmlns:anibt="https://anibt.net/xmlns/rss/1.0/"
                 xmlns:torrent="https://anibt.moe/xmlns/0.1/">
              <channel>
                <title>AniBT Anime Releases</title>
                <item>
                  <guid isPermaLink="false">rel_AZc1TOy0NyuJ</guid>
                  <title>[Nix-Raws] 航海王 One Piece - 1122 [CR WEB-DL 1080p AVC AAC][CHS CTT][MKV]</title>
                  <link>https://anibt.net/release/rel_AZc1TOy0NyuJ</link>
                  <pubDate>Mon, 28 Sep 2026 00:02:41 +0800</pubDate>
                  <enclosure url="https://anibt.net/api/torrent/rel_AZc1TOy0NyuJ.torrent"
                             length="1229264846" type="application/x-bittorrent" />
                  <anibt:releaseId>rel_AZc1TOy0NyuJ</anibt:releaseId>
                  <anibt:bgmId>975</anibt:bgmId>
                  <anibt:fileSize>1229264846</anibt:fileSize>
                  <torrent:link>https://anibt.net/api/torrent/rel_AZc1TOy0NyuJ.torrent</torrent:link>
                  <torrent:contentLength>1229264846</torrent:contentLength>
                  <torrent:infohash>eb7d7d0cfdc65f4d8dfcb1723f640cac05d060e5</torrent:infohash>
                </item>
              </channel>
            </rss>
            """;
        var reader = new RssFeedSourceReader(new FakeHttpClientSource(_ =>
                                                                          new HttpResponseMessage(HttpStatusCode.OK)
                                                                              { Content = new StringContent(xml) }));

        var item = Assert.Single(await reader.ReadAsync(new Uri("https://anibt.net/rss/magnets.xml?limit=100")));

        Assert.Equal("guid:rel_AZc1TOy0NyuJ", item.StableId);
        Assert.Equal("rel_AZc1TOy0NyuJ", item.SourceItemId);
        Assert.Equal("https://anibt.net/release/rel_AZc1TOy0NyuJ", item.Link?.AbsoluteUri);
        Assert.Equal("https://anibt.net/api/torrent/rel_AZc1TOy0NyuJ.torrent", item.TorrentUri?.AbsoluteUri);
        Assert.Equal(975, item.SourceSubjectId);
        // +0800 发布时间换算为 UTC。
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 16, 2, 41, TimeSpan.Zero), item.PublishedAtUtc);
    }

    [Fact]
    public async Task FeedReader_PrefersNonNamespacedLinkOverTorrentExtensionLink()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:torrent="https://anibt.moe/xmlns/0.1/">
              <channel><item>
                <guid>rel_order</guid>
                <title>[Group] Example - 01 [1080p]</title>
                <torrent:link>https://anibt.net/api/torrent/rel_order.torrent</torrent:link>
                <link>https://anibt.net/release/rel_order</link>
                <enclosure url="https://anibt.net/api/torrent/rel_order.torrent" type="application/x-bittorrent" />
              </item></channel>
            </rss>
            """;
        var reader = new RssFeedSourceReader(new FakeHttpClientSource(_ =>
                                                                          new HttpResponseMessage(HttpStatusCode.OK)
                                                                              { Content = new StringContent(xml) }));

        var item = Assert.Single(await reader.ReadAsync(new Uri("https://anibt.net/rss/magnets.xml")));

        Assert.Equal("https://anibt.net/release/rel_order", item.Link?.AbsoluteUri);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task FeedReader_IgnoresInvalidBgmId(string bgmId)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:anibt="https://anibt.net/xmlns/rss/1.0/">
              <channel><item>
                <guid>rel_invalid</guid>
                <title>[Group] Example - 02 [1080p]</title>
                <link>https://anibt.net/release/rel_invalid</link>
                <enclosure url="https://anibt.net/api/torrent/rel_invalid.torrent" type="application/x-bittorrent" />
                <anibt:bgmId>{bgmId}</anibt:bgmId>
              </item></channel>
            </rss>
            """;
        var reader = new RssFeedSourceReader(new FakeHttpClientSource(_ =>
                                                                          new HttpResponseMessage(HttpStatusCode.OK)
                                                                              { Content = new StringContent(xml) }));

        var item = Assert.Single(await reader.ReadAsync(new Uri("https://anibt.net/rss/magnets.xml")));

        Assert.Null(item.SourceSubjectId);
    }

    private sealed class FakeHttpClientSource(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : IProxiedHttpClientSource
    {
        public Task<HttpClient> CreateClientAsync(string            clientName, Guid? proxyId,
                                                  CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HttpClient(new StubHandler(responseFactory)));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken  cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
