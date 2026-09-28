using MediaManager.Data;
using MediaManager.Features.Proxies;
using System.Net;

namespace MediaManager.Tests.Proxies;

/// <summary>结构化代理配置到 WebProxy / 代理 URL 的转换回归。</summary>
public sealed class ProxyEndpointTests
{
    public static TheoryData<ProxyProtocol, string> SchemeCases => new()
    {
        { ProxyProtocol.Http, "http" },
        { ProxyProtocol.Socks4, "socks4" },
        { ProxyProtocol.Socks5, "socks5" }
    };

    [Theory]
    [MemberData(nameof(SchemeCases))]
    public void ToUrl_WithoutCredentials_ProducesSchemeHostPort(ProxyProtocol protocol, string scheme)
    {
        var url = ProxyEndpoint.ToUrl(new ResolvedProxy("代理", protocol, "10.0.0.5", 7890, null, null));

        Assert.Equal($"{scheme}://10.0.0.5:7890/", url);
    }

    [Fact]
    public void ToUrl_WithCredentials_EscapesSpecialCharacters()
    {
        var url = ProxyEndpoint.ToUrl(new ResolvedProxy("代理", ProxyProtocol.Socks5, "10.0.0.5", 1080,
                                                        "user:1", "p@ss:word"));

        // userinfo 按组件转义：凭据里的冒号等字符不会破坏 user:pass 的拆分。
        Assert.Equal("socks5://user%3A1:p%40ss%3Aword@10.0.0.5:1080/", url);
    }

    [Theory]
    [MemberData(nameof(SchemeCases))]
    public void ToWebProxy_AcceptsAllSupportedSchemes(ProxyProtocol protocol, string scheme)
    {
        // .NET 的 SOCKS 实现覆盖 socks4/socks5 scheme：构造即验证 scheme 被接受。
        var proxy = ProxyEndpoint.ToWebProxy(new ResolvedProxy("代理", protocol, "10.0.0.5", 7890, null, null));

        Assert.Equal($"{scheme}://10.0.0.5:7890/", proxy.Address!.ToString());
    }

    [Fact]
    public void ToWebProxy_WithCredentials_WrapsNetworkCredential()
    {
        var proxy = ProxyEndpoint.ToWebProxy(new ResolvedProxy("代理", ProxyProtocol.Http, "10.0.0.5", 8080,
                                                        "alice", "secret"));

        var credentials = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("alice", credentials.UserName);
        Assert.Equal("secret", credentials.Password);
    }

    [Fact]
    public void ToWebProxy_WithoutUsername_SetsNoCredentials()
    {
        var proxy = ProxyEndpoint.ToWebProxy(new ResolvedProxy("代理", ProxyProtocol.Socks5, "10.0.0.5", 1080,
                                                        null, null));

        Assert.Null(proxy.Credentials);
    }
}
