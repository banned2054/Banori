using System.Net;

namespace Banori.Integrations;

/// <summary>
///     解析代理 URL；空白返回 null 表示沿用调用方的默认行为。
///     支持 http/https/socks4/socks4a/socks5，userinfo 按"先转义后拆分"约定：组件内的冒号等
///     特殊字符必须经 URL 转义（与 ProxyEndpoint.ToUrl 配套）。
/// </summary>
public static class ProxyUrlParser
{
    public static WebProxy? Parse(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return null;

        if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
            throw new FormatException($"代理地址无效：{proxyUrl}（支持 http/https/socks4/socks4a/socks5）。");

        var proxy = new WebProxy(new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var userInfo = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(userInfo[0]),
                                                      userInfo.Length > 1
                                                          ? Uri.UnescapeDataString(userInfo[1])
                                                          : string.Empty);
        }

        return proxy;
    }
}
