using MediaManager.Integrations;
using MediaManager.Integrations.Mikan;
using System.Net;

namespace MediaManager.Tests.Integrations;

public sealed class MikanIntegrationTests
{
    [Fact]
    public async Task FeedReader_ParsesRssItemAndTorrentEnclosure()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel><item>
              <guid>episode-7</guid>
              <title>[ANi] Example - 07 [1080P][Baha][WEB-DL][AAC AVC][CHT][MP4]</title>
              <link>https://mikanani.me/Home/Episode/episode-7</link>
              <pubDate>Sat, 12 Sep 2026 03:00:00 GMT</pubDate>
              <enclosure url="https://mikanani.me/Download/episode-7.torrent" type="application/x-bittorrent" />
            </item></channel></rss>
            """;
        var reader = new RssFeedSourceReader(new FakeHttpClientSource(_ =>
                                                                          new HttpResponseMessage(HttpStatusCode.OK)
                                                                              { Content = new StringContent(xml) }));

        var item = Assert.Single(await reader.ReadAsync(new Uri("https://mikanani.me/RSS/test")));

        Assert.Equal("guid:episode-7", item.StableId);
        Assert.Equal("episode-7", item.SourceItemId);
        Assert.Equal("https://mikanani.me/Download/episode-7.torrent", item.TorrentUri?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero), item.PublishedAtUtc);
        Assert.Null(item.SourceSubjectId);
    }

    [Fact]
    public async Task FeedReader_ReadsPubDateInsideTorrentExtensionAsChinaLocalTime()
    {
        // 真实 Mikan RSS 的 <item> 顶层没有 pubDate：发布时间在 torrent 扩展元素内，
        // 且为无时区后缀的东八区裸时间。
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel><item>
              <guid>episode-1</guid>
              <title>[ANi] Example - 01 [1080P][Baha][WEB-DL][AAC AVC][CHT][MP4]</title>
              <link>https://mikanani.me/Home/Episode/episode-1</link>
              <torrent xmlns="https://mikanani.me/0.1/">
                <link>https://mikanani.me/Home/Episode/episode-1</link>
                <contentLength>341730912</contentLength>
                <pubDate>2026-10-05T22:07:41.210</pubDate>
              </torrent>
              <enclosure url="https://mikanani.me/Download/20261005/episode-1.torrent" type="application/x-bittorrent" />
            </item></channel></rss>
            """;
        var reader = new RssFeedSourceReader(new FakeHttpClientSource(_ =>
                                                                          new HttpResponseMessage(HttpStatusCode.OK)
                                                                              { Content = new StringContent(xml) }));

        var item = Assert.Single(await reader.ReadAsync(new Uri("https://mikanani.me/RSS/test")));

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 14, 7, 41, 210, TimeSpan.Zero), item.PublishedAtUtc);
    }

    [Fact]
    public async Task SubjectResolver_FollowsMikanPagesAndExtractsBangumiId()
    {
        // Bangumi 页夹具按真实页面结构排列多个 bangumi-info 段落：
        // bgm.tv 链接在最后一个段落，且前面存在带非 bgm.tv 链接的段落（官方网站）。
        var factory = new FakeHttpClientSource(request =>
        {
            var html = request.RequestUri!.AbsolutePath.Contains("Episode", StringComparison.Ordinal)
                ? "<p class='bangumi-title'><a href='/Home/Bangumi/abcd'>Example</a></p>"
                : """
                <p class="bangumi-info">放送日期：星期日</p>
                <p class="bangumi-info">总集数：12</p>
                <p class="bangumi-info">官方网站：<a href="https://example-anime.com/">https://example-anime.com/</a></p>
                <p class="bangumi-info">Bangumi番组计划链接：<a href="https://bgm.tv/subject/123456">Bangumi</a></p>
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        var resolver = new MikanSubjectLinkResolver(factory);

        var subjectId = await resolver.ResolveSubjectIdAsync(new Uri("https://mikanani.me/Home/Episode/episode-7"));

        Assert.Equal(123456, subjectId);
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
