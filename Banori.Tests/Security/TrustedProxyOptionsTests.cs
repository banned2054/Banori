using Banori.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace Banori.Tests.Security;

public sealed class TrustedProxyOptionsTests
{
    [Theory]
    [InlineData(null, "127.0.0.1", "127.0.0.1", "http")]
    [InlineData("192.0.2.1", "192.0.2.1", "203.0.113.7", "https")]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1", "203.0.113.7", "https")]
    [InlineData("192.0.2.1", "192.0.2.2", "192.0.2.2", "http")]
    [InlineData("192.0.2.1", "127.0.0.1", "127.0.0.1", "http")]
    public async Task ForwardedHeaders_OnlyAcceptExplicitlyTrustedProxies(
        string? trustedProxy, string remoteIp, string expectedIp, string expectedScheme)
    {
        var options = CreateOptions(trustedProxy);
        var context = new DefaultHttpContext
        {
            Connection =
            {
                RemoteIpAddress = IPAddress.Parse(remoteIp)
            },
            Request =
            {
                Scheme = "http",
                Headers =
                {
                    ["X-Forwarded-For"]   = "203.0.113.7",
                    ["X-Forwarded-Proto"] = "https"
                }
            }
        };

        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
                                                        NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);

        Assert.Equal(IPAddress.Parse(expectedIp), context.Connection.RemoteIpAddress);
        Assert.Equal(expectedScheme, context.Request.Scheme);
    }

    [Fact]
    public async Task ForwardedHeaders_OnlyConsumeNearestProxyHop()
    {
        var context = new DefaultHttpContext
        {
            Connection =
            {
                RemoteIpAddress = IPAddress.Parse("192.0.2.1")
            },
            Request =
            {
                Scheme = "http",
                Headers =
                {
                    ["X-Forwarded-For"]   = "203.0.113.7, 192.0.2.3",
                    ["X-Forwarded-Proto"] = "https, http"
                }
            }
        };

        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
                                                        NullLoggerFactory.Instance,
                                                        Options.Create(CreateOptions("192.0.2.1")));
        await middleware.Invoke(context);

        Assert.Equal(IPAddress.Parse("192.0.2.3"), context.Connection.RemoteIpAddress);
        Assert.Equal("203.0.113.7", context.Request.Headers["X-Forwarded-For"].ToString());
    }

    [Fact]
    public void InvalidProxyAddress_IsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CreateOptions("not-an-ip"));
        Assert.Contains("ReverseProxy:KnownProxies", error.Message);
    }

    private static ForwardedHeadersOptions CreateOptions(string? trustedProxy)
    {
        var values = new Dictionary<string, string?>();
        if (trustedProxy is not null) values["ReverseProxy:KnownProxies:0"] = trustedProxy;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new ForwardedHeadersOptions();
        new TrustedProxyOptionsSetup(configuration).Configure(options);
        return options;
    }
}
