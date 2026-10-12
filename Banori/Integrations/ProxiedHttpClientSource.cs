using Banori.Features.Proxies;

namespace Banori.Integrations;

/// <summary>按可选代理创建 HttpClient：无代理或代理已失效时回落到命名的池化客户端，有代理时构建独立实例。</summary>
public interface IProxiedHttpClientSource
{
    Task<HttpClient> CreateClientAsync(string            clientName, Guid? proxyId,
                                       CancellationToken cancellationToken = default);
}

/// <summary>代理客户端按次创建、由调用方 using 释放；调用节奏（订阅刷新、种子下载）与通知发送器一致。</summary>
public sealed class ProxiedHttpClientSource(IHttpClientFactory httpClientFactory, ProxyService proxyService)
    : IProxiedHttpClientSource
{
    public async Task<HttpClient> CreateClientAsync(string            clientName, Guid? proxyId,
                                                    CancellationToken cancellationToken = default)
    {
        if (proxyId is null) return httpClientFactory.CreateClient(clientName);

        var proxy = await proxyService.ResolveWebProxyAsync(proxyId, cancellationToken);
        if (proxy is null) return httpClientFactory.CreateClient(clientName);

        return new HttpClient(new SocketsHttpHandler
        {
            Proxy          = proxy,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}
