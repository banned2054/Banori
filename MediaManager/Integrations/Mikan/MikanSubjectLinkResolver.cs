using System.Net;
using System.Text.RegularExpressions;

namespace MediaManager.Integrations.Mikan;

public sealed partial class MikanSubjectLinkResolver(IProxiedHttpClientSource httpClientSource) : ISubjectLinkResolver
{
    private const int MaximumHtmlLength = 2 * 1024 * 1024;

    public async Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = null,
                                                  CancellationToken cancellationToken = default)
    {
        if (!IsMikanUri(episodeUri)) return null;

        var episodeHtml      = await GetHtmlAsync(episodeUri, proxyId, cancellationToken);
        var bangumiPageMatch = BangumiTitleParagraphRegex().Match(episodeHtml);
        if (!bangumiPageMatch.Success) return null;
        var hrefMatch = HrefRegex().Match(bangumiPageMatch.Value);
        if (!hrefMatch.Success) return null;
        var href = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value);
        if (!Uri.TryCreate(episodeUri, href, out var bangumiPageUri) || !IsMikanUri(bangumiPageUri)) return null;

        var bangumiHtml = await GetHtmlAsync(bangumiPageUri, proxyId, cancellationToken);
        // Bangumi 页面有多个 bangumi-info 段落（放送日期、总集数、官方网站等），
        // bgm.tv 链接在末尾的"Bangumi番组计划链接"段落，必须扫描全部段落。
        foreach (Match infoMatch in BangumiInfoParagraphRegex().Matches(bangumiHtml))
        foreach (Match candidateHrefMatch in HrefRegex().Matches(infoMatch.Value))
        {
            var candidateHref         = WebUtility.HtmlDecode(candidateHrefMatch.Groups[1].Value);
            var candidateSubjectMatch = BangumiSubjectRegex().Match(candidateHref);
            if (candidateSubjectMatch.Success &&
                int.TryParse(candidateSubjectMatch.Groups[1].Value, out var subjectId))
                return subjectId;
        }

        return null;
    }

    private async Task<string> GetHtmlAsync(Uri uri, Guid? proxyId, CancellationToken cancellationToken)
    {
        using var client   = await httpClientSource.CreateClientAsync("FeedSource", proxyId, cancellationToken);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return string.Empty;
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaximumHtmlLength) return string.Empty;
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return html.Length <= MaximumHtmlLength ? html : string.Empty;
    }

    private static bool IsMikanUri(Uri uri)
    {
        return uri is { IsAbsoluteUri: true, Scheme: "https" or "http" } &&
               (uri.Host.Equals("mikanani.me", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".mikanani.me", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("<p\\b[^>]*class\\s*=\\s*[\\\"'][^\\\"']*\\bbangumi-title\\b[^\\\"']*[\\\"'][^>]*>.*?</p>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BangumiTitleParagraphRegex();

    [GeneratedRegex("<p\\b[^>]*class\\s*=\\s*[\\\"'][^\\\"']*\\bbangumi-info\\b[^\\\"']*[\\\"'][^>]*>.*?</p>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BangumiInfoParagraphRegex();

    [GeneratedRegex("<a\\b[^>]*href\\s*=\\s*[\\\"']([^\\\"']+)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("https?://(?:www\\.)?bgm\\.tv/subject/(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BangumiSubjectRegex();
}
