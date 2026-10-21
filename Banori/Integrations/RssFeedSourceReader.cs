using Banori.Features.Subscriptions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Banori.Integrations;

/// <summary>通用 RSS/Atom 源读取器：抓取 feed 并解析条目；来源特有的扩展元素（如 AniBT 的 bgmId）按 LocalName 提取。</summary>
public sealed class RssFeedSourceReader(IProxiedHttpClientSource httpClientSource) : IFeedSourceReader
{
    private const long MaximumFeedCharacters = 5 * 1024 * 1024;

    /// <summary>Mikan torrent 扩展元素的裸时间为源站东八区本地时间。</summary>
    private static readonly TimeSpan SourceLocalZone = new(8, 0, 0);

    public async Task<IReadOnlyList<FeedSourceItem>> ReadAsync(
        Uri feedUri, Guid? proxyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, feedUri);
        using var client  = await httpClientSource.CreateClientAsync("FeedSource", proxyId, cancellationToken);
        using var response =
            await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RSS 源返回 HTTP {(int)response.StatusCode}。");

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
        // 扩展命名空间也可能定义 link 元素（如 AniBT 的 torrent:link），优先取无命名空间的主链接。
        var linkElement = element.Elements().FirstOrDefault(value => value.Name.LocalName            == "link" &&
                                                                     value.Name.NamespaceName.Length == 0) ??
                          element.Elements().FirstOrDefault(value => value.Name.LocalName == "link");
        var linkText = linkElement?.Attribute("href")?.Value ?? linkElement?.Value;
        Uri.TryCreate(linkText?.Trim(), UriKind.Absolute, out var link);
        var enclosure = element.Elements().FirstOrDefault(value =>
            value.Name.LocalName == "enclosure" &&
            (value.Attribute("type")?.Value.Equals("application/x-bittorrent", StringComparison.OrdinalIgnoreCase) ?? false));
        var torrentText = enclosure?.Attribute("url")?.Value ?? enclosure?.Attribute("href")?.Value;
        Uri.TryCreate(torrentText?.Trim(), UriKind.Absolute, out var torrentUri);
        var publishedText = ChildValue(element, "pubDate") ?? ChildValue(element, "published") ??
            ChildValue(element, "updated") ??
            // Mikan 顶层没有 pubDate，发布时间位于 torrent 扩展元素内，顶层缺失时向下探一层。
            element.Elements().SelectMany(child => child.Elements())
                   .FirstOrDefault(value => value.Name.LocalName is "pubDate" or "published" or "updated")?.Value;
        var published = ParseTimestamp(publishedText);
        var bgmIdText = ChildValue(element, "bgmId")?.Trim();
        int? sourceSubjectId = int.TryParse(bgmIdText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                            out var bgmId) && bgmId > 0
            ? bgmId
            : null;
        var stableId = !string.IsNullOrWhiteSpace(guid) ? $"guid:{guid}" :
            link is not null                            ? $"link:{NormalizeUri(link)}" :
                                                          $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{publishedText}\n{torrentText}")))}";

        return new FeedSourceItem(stableId, guid, title, link, torrentUri, published, sourceSubjectId);
    }

    private static string? ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(value => value.Name.LocalName == localName)?.Value;

    private static DateTimeOffset? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        // 带时区标记（Z/GMT/UTC 或末尾 ±hh:mm、±hhmm）按原偏移解析；裸时间按东八区解释。
        var hasZone = trimmed.EndsWith('Z')                                       ||
                      trimmed.EndsWith("GMT", StringComparison.OrdinalIgnoreCase) ||
                      trimmed.EndsWith("UTC", StringComparison.OrdinalIgnoreCase) ||
                      trimmed.EndsWith("UT", StringComparison.OrdinalIgnoreCase)  ||
                      (trimmed.Length >= 5 && (trimmed[^5] is '+' or '-' ||
                                               (trimmed.Length >= 6 && trimmed[^6] is '+' or '-')));
        if (hasZone)
            return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                                           DateTimeStyles.AllowWhiteSpaces, out var zoned)
                ? zoned.ToUniversalTime()
                : null;
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var bare)
            ? new DateTimeOffset(DateTime.SpecifyKind(bare, DateTimeKind.Unspecified), SourceLocalZone)
               .ToUniversalTime()
            : null;
    }

    private static string NormalizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }
}
