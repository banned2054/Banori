using MediaManager.Features.Subscriptions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MediaManager.Integrations.Mikan;

public sealed class MikanFeedSourceReader(IHttpClientFactory httpClientFactory) : IFeedSourceReader
{
    private const long MaximumFeedCharacters = 5 * 1024 * 1024;

    public async Task<IReadOnlyList<FeedSourceItem>> ReadAsync(
        Uri feedUri, Guid? proxyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, feedUri);
        using var client  = httpClientFactory.CreateClient("Mikan");
        using var response =
            await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mikan RSS 返回 HTTP {(int)response.StatusCode}。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async                   = true,
            DtdProcessing           = DtdProcessing.Prohibit,
            XmlResolver             = null,
            MaxCharactersInDocument = MaximumFeedCharacters
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var entries = document.Descendants()
                              .Where(element => element.Name.LocalName is "item" or "entry")
                              .Select(ParseEntry)
                              .Where(item => item is not null)
                              .Cast<FeedSourceItem>().ToList();
        return entries;
    }

    private static FeedSourceItem? ParseEntry(XElement element)
    {
        var title = ChildValue(element, "title")?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var guid = ChildValue(element, "guid")?.Trim() ?? ChildValue(element, "id")?.Trim();
        var linkText = element.Elements().FirstOrDefault(value => value.Name.LocalName == "link") is { } linkElement
            ? linkElement.Attribute("href")?.Value ?? linkElement.Value
            : null;
        Uri.TryCreate(linkText?.Trim(), UriKind.Absolute, out var link);
        var enclosure = element.Elements().FirstOrDefault(value => value.Name.LocalName == "enclosure" &&
                                                                   (value.Attribute("type")?.Value
                                                                         .Equals("application/x-bittorrent",
                                                                              StringComparison
                                                                                 .OrdinalIgnoreCase) ?? false));
        var torrentText = enclosure?.Attribute("url")?.Value ?? enclosure?.Attribute("href")?.Value;
        Uri.TryCreate(torrentText?.Trim(), UriKind.Absolute, out var torrentUri);
        var publishedText = ChildValue(element, "pubDate") ?? ChildValue(element, "published") ??
            ChildValue(element, "updated");
        DateTimeOffset? published = DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture,
                                                            DateTimeStyles.AllowWhiteSpaces |
                                                            DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;
        var stableId = !string.IsNullOrWhiteSpace(guid) ? $"guid:{guid}" :
            link is not null                            ? $"link:{NormalizeUri(link)}" :
                                                          $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{publishedText}\n{torrentText}")))}";

        return new FeedSourceItem(stableId, guid, title, link, torrentUri, published);
    }

    private static string? ChildValue(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(value => value.Name.LocalName == localName)?.Value;
    }

    private static string NormalizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }
}
