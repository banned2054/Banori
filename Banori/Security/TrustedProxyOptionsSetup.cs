using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Net;

namespace Banori.Security;

/// <summary>仅接受显式配置的单层反向代理所提供的客户端 IP 和 HTTPS 协议。</summary>
internal sealed class TrustedProxyOptionsSetup(IConfiguration configuration)
    : IConfigureOptions<ForwardedHeadersOptions>
{
    public void Configure(ForwardedHeadersOptions options)
    {
        var proxies = configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];

        // 不沿用默认的回环地址信任；空白名单时完全禁用转发头处理。
        options.ForwardedHeaders = ForwardedHeaders.None;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.ForwardLimit = 1;

        foreach (var proxy in proxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
                throw new InvalidOperationException($"ReverseProxy:KnownProxies 包含无效 IP 地址“{proxy}”。");

            options.KnownProxies.Add(address);
        }

        if (options.KnownProxies.Count > 0)
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }
}
