using Banori.Data;
using System.Net;

namespace Banori.Features.Proxies;

/// <summary>把结构化代理配置转换为 .NET 网络栈可用的 WebProxy 或带凭据的代理 URL。</summary>
public static class ProxyEndpoint
{
    public static WebProxy ToWebProxy(ResolvedProxy proxy)
    {
        var webProxy = new WebProxy(new UriBuilder(Scheme(proxy.Protocol), proxy.Host, proxy.Port).Uri);
        if (!string.IsNullOrEmpty(proxy.Username))
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        return webProxy;
    }

    /// <summary>userinfo 按组件转义，与 ProxyUrlParser 的先转义后拆分约定配套。</summary>
    public static string ToUrl(ResolvedProxy proxy)
    {
        var builder = new UriBuilder(Scheme(proxy.Protocol), proxy.Host, proxy.Port);
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            builder.UserName = Uri.EscapeDataString(proxy.Username);
            builder.Password = Uri.EscapeDataString(proxy.Password ?? string.Empty);
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string Scheme(ProxyProtocol protocol)
    {
        return protocol switch
        {
            ProxyProtocol.Socks4 => "socks4",
            ProxyProtocol.Socks5 => "socks5",
            _                    => "http"
        };
    }
}
