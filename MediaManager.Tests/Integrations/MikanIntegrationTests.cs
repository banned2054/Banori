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
        var reader = new MikanFeedSourceReader(new FakeHttpClientFactory(_ =>
                                                                             new HttpResponseMessage(HttpStatusCode.OK)
                                                                                 { Content = new StringContent(xml) }));

        var item = Assert.Single(await reader.ReadAsync(new Uri("https://mikanani.me/RSS/test")));

        Assert.Equal("guid:episode-7", item.StableId);
        Assert.Equal("episode-7", item.SourceItemId);
        Assert.Equal("https://mikanani.me/Download/episode-7.torrent", item.TorrentUri?.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero), item.PublishedAtUtc);
    }

    [Fact]
    public async Task SubjectResolver_FollowsMikanPagesAndExtractsBangumiId()
    {
        var factory = new FakeHttpClientFactory(request =>
        {
            var html = request.RequestUri!.AbsolutePath.Contains("Episode", StringComparison.Ordinal)
                ? "<p class='bangumi-title'><a href='/Home/Bangumi/abcd'>Example</a></p>"
                : "<p class=\"bangumi-info extra\"><a href=\"https://bgm.tv/subject/123456\">Bangumi</a></p>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        });
        var resolver = new MikanSubjectLinkResolver(factory);

        var subjectId = await resolver.ResolveSubjectIdAsync(new Uri("https://mikanani.me/Home/Episode/episode-7"));

        Assert.Equal(123456, subjectId);
    }

    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubHandler(responseFactory));
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
