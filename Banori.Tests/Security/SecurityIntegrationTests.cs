using Banori.Initialization;
using Banori.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Banori.Tests.Security;

/// <summary>使用真实应用管线与隔离 SQLite，验证路由、代理信息和限流的组合行为。</summary>
public sealed class SecurityIntegrationTests : IAsyncLifetime
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("Banori.Security.Tests.");

    private WebApplication? _app;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _directory.Delete(true);
    }

    [Theory]
    [InlineData("/Account/Login", "/Account/Login/")]
    [InlineData("/Account/Login/", "/Account/Login")]
    [InlineData("/account/login/", "/Account/Login")]
    public async Task LoginPathVariants_ShareRateLimit(string initialPath, string nextPath)
    {
        var       app    = CreateApplication();
        using var client = await StartAsync(app);
        var       token  = await GetAntiforgeryTokenAsync(client);

        for (var i = 0; i < BaselineRateLimiting.AuthPermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, await PostLoginAsync(client, initialPath, token));

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostLoginAsync(client, nextPath, token));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostLoginAsync(client, initialPath, token));
    }

    [Fact]
    public async Task TrustedProxy_GivesDifferentClientsSeparateRateLimits()
    {
        var       app    = CreateApplication("127.0.0.1");
        using var client = await StartAsync(app);
        var       token  = await GetAntiforgeryTokenAsync(client);

        for (var i = 0; i < BaselineRateLimiting.AuthPermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, await PostLoginAsync(client, "/Account/Login/", token, "203.0.113.7"));

        Assert.Equal(HttpStatusCode.TooManyRequests,
                     await PostLoginAsync(client, "/Account/Login", token, "203.0.113.7"));
        Assert.Equal(HttpStatusCode.OK, await PostLoginAsync(client, "/Account/Login/", token, "203.0.113.8"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("192.0.2.1")]
    public async Task UntrustedForwardedFor_CannotResetRateLimit(string? trustedProxy)
    {
        var       app    = CreateApplication(trustedProxy);
        using var client = await StartAsync(app);
        var       token  = await GetAntiforgeryTokenAsync(client);

        for (var i = 0; i < BaselineRateLimiting.AuthPermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK,
                         await PostLoginAsync(client, "/Account/Login", token, $"203.0.113.{i + 1}"));

        Assert.Equal(HttpStatusCode.TooManyRequests,
                     await PostLoginAsync(client, "/Account/Login/", token, "203.0.113.99"));
    }

    [Theory]
    [InlineData("127.0.0.1", "https")]
    [InlineData(null, "http")]
    [InlineData("192.0.2.1", "http")]
    public async Task LoginRedirect_OnlyUsesHttpsFromTrustedProxy(string? trustedProxy, string expectedScheme)
    {
        var       app     = CreateApplication(trustedProxy);
        using var client  = await StartAsync(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "banori.example.test:11451";
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        Assert.True(location.IsAbsoluteUri);
        Assert.Equal(expectedScheme, location.Scheme);
        Assert.Equal("banori.example.test", location.Host);
        Assert.Equal(11451, location.Port);
        Assert.Equal("/Account/Login", location.AbsolutePath);
        Assert.Equal("?ReturnUrl=%2F", location.Query);
    }

    [Fact]
    public async Task CookieAndCircuit_UseSameSecurityStampInterval()
    {
        var app = CreateApplication();
        using var scope = app.Services.CreateScope();
        var cookieOptions = scope.ServiceProvider.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var interval = provider.GetType()
                               .GetProperty("RevalidationInterval", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(interval);
        Assert.Equal(TimeSpan.FromMinutes(10), cookieOptions.ValidationInterval);
        Assert.Equal(cookieOptions.ValidationInterval, interval.GetValue(provider));
    }

    private WebApplication CreateApplication(string? trustedProxy = null)
    {
        // 不加载现有数据库、用户密钥或环境凭据；所有运行数据只写入本测试的临时目录。
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApplicationInitializer).Assembly.GetName().Name,
            ContentRootPath = _directory.FullName,
            EnvironmentName = Environments.Production,
            Args            = []
        });
        builder.WebHost.UseKestrel();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"]                   = "Sqlite",
            ["ConnectionStrings:DefaultConnection"] = "Data Source=config/security.db;Pooling=False",
            ["BANORI_USERNAME"]                     = "reviewadmin",
            ["BANORI_PASSWORD"]                     = $"{Guid.NewGuid():N}-A9!"
        });
        if (trustedProxy is not null) builder.Configuration["ReverseProxy:KnownProxies:0"]    = trustedProxy;
        // 隔离测试中的宿主机代理允许两种回环地址，兼容实际连接来源为 IPv6 的运行环境。
        if (trustedProxy == "127.0.0.1") builder.Configuration["ReverseProxy:KnownProxies:1"] = "::1";
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var program = typeof(ApplicationInitializer).Assembly.GetType("Banori.Program", true)!;
        program.GetMethod("InitBuilder", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder]);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        _app = app;
        program.GetMethod("InitApplication", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [app]);
        return app;
    }

    private static async Task<HttpClient> StartAsync(WebApplication app)
    {
        await ApplicationInitializer.InitializeAsync(app);
        await app.StartAsync();
        var server    = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()!;
        var address   = Assert.Single(addresses.Addresses);
        Assert.StartsWith("http://127.0.0.1:", address);
        // localhost 验证必须直连，避免系统 HTTP 代理改写转发头或连接来源。
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new Uri(address),
            Timeout     = TimeSpan.FromSeconds(10)
        };
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html  = await client.GetStringAsync("/Account/Login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "登录页应包含防伪令牌。");
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    private static async Task<HttpStatusCode> PostLoginAsync(
        HttpClient client, string path, string token, string? forwardedIp = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["_handler"]                   = "login",
                ["Input.Username"]             = "missingreviewuser",
                ["Input.Password"]             = "uncommon-review-password"
            })
        };
        if (forwardedIp is not null) request.Headers.Add("X-Forwarded-For", forwardedIp);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
