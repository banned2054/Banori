using System.Net;
using System.Text.RegularExpressions;

namespace MediaManager.Integrations.Mikan;

public sealed partial class MikanSubjectLinkResolver(IHttpClientFactory httpClientFactory) : ISubjectLinkResolver
{
    private const int MaximumHtmlLength = 2 * 1024 * 1024;

    public async Task<int?> ResolveSubjectIdAsync(Uri episodeUri, CancellationToken cancellationToken = default)
    {
        if (!IsMikanUri(episodeUri)) return null;

        var episodeHtml      = await GetHtmlAsync(episodeUri, cancellationToken);
        var bangumiPageMatch = BangumiTitleParagraphRegex().Match(episodeHtml);
        if (!bangumiPageMatch.Success) return null;
        var hrefMatch = HrefRegex().Match(bangumiPageMatch.Value);
        if (!hrefMatch.Success) return null;
        var href = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value);
        if (!Uri.TryCreate(episodeUri, href, out var bangumiPageUri) || !IsMikanUri(bangumiPageUri)) return null;

        var bangumiHtml = await GetHtmlAsync(bangumiPageUri, cancellationToken);
        var infoMatch   = BangumiInfoParagraphRegex().Match(bangumiHtml);
        if (!infoMatch.Success) return null;
        var subjectMatch = BangumiSubjectRegex().Match(infoMatch.Value);
        return subjectMatch.Success && int.TryParse(subjectMatch.Groups[1].Value, out var subjectId)
            ? subjectId
            : null;
    }

    private async Task<string> GetHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var client   = httpClientFactory.CreateClient("Mikan");
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
