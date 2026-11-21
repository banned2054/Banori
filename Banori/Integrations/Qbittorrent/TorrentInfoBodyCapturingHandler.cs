using System.Text;

namespace Banori.Integrations.Qbittorrent;

/// <summary>
///     在 HTTP 层读取 <c>/api/v2/torrents/info</c> 的原始响应正文交给 <see cref="TorrentInfoBodyObserver" />，
///     再用等值内容回填响应供客户端正常反序列化；其余端点完全透传。这样生产传输与注入 fetcher 的
///     测试传输都能把 JSON <c>null</c> 翻译成「查询未知」，而 <c>[]</c> 仍是成功查无。
/// </summary>
internal sealed class TorrentInfoBodyCapturingHandler(TorrentInfoBodyObserver observer) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content is null   ||
            request.RequestUri is null ||
            !request.RequestUri.AbsolutePath.EndsWith("/api/v2/torrents/info", StringComparison.Ordinal))
            return response;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        var body      = await response.Content.ReadAsStringAsync(cancellationToken);
        observer.Observe(body);
        response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        return response;
    }
}
