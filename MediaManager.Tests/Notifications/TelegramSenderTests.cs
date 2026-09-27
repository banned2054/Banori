using MediaManager.Integrations.Notifications;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MediaManager.Tests.Notifications;

/// <summary>
///     真实 TelegramSender 的请求格式与错误处理回归：用本地 TCP 桩冒充 Bot API，
///     校验 sendMessage 的路径与正文、ok=false 与非成功状态码的失败语义、代理与代理地址校验。
/// </summary>
public sealed class TelegramSenderTests
{
    [Fact]
    public async Task Send_PostsToBotApiWithChatIdAndText()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new TelegramSender(stub.BaseUrl);

        await sender.SendAsync("token-123", "42", null, "你好，MediaManager");

        var request = Assert.Single(stub.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/bottoken-123/sendMessage", request.Path);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal("42", document.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("你好，MediaManager", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Send_OkFalse_ThrowsWithDescription()
    {
        await using var stub = StubHttpServer.Start();
        stub.Responder = _ => (200, """{"ok":false,"description":"chat not found"}""");
        var sender = new TelegramSender(stub.BaseUrl);

        var exception =
            await Assert.ThrowsAsync<NotificationSendException>(() => sender.SendAsync("token-123", "42", null,
                                                                         "text"));
        Assert.Contains("chat not found", exception.Message);
    }

    [Fact]
    public async Task Send_ErrorStatusCode_ThrowsWithStatusAndBody()
    {
        await using var stub = StubHttpServer.Start();
        stub.Responder = _ => (401, """{"ok":false,"description":"Unauthorized"}""");
        var sender = new TelegramSender(stub.BaseUrl);

        var exception =
            await Assert.ThrowsAsync<NotificationSendException>(() => sender.SendAsync("bad-token", "42", null,
                                                                         "text"));
        Assert.Contains("401", exception.Message);
        Assert.Contains("Unauthorized", exception.Message);
    }

    [Fact]
    public async Task Send_WithHttpProxy_RoutesRequestThroughProxy()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new TelegramSender("http://telegram.invalid");

        // 目标是纯 http 的假域名，代理收到的是绝对 URI 形式的请求行。
        await sender.SendAsync("token-123", "42", stub.BaseUrl, "text");

        var request = Assert.Single(stub.Requests);
        Assert.Equal("POST http://telegram.invalid/bottoken-123/sendMessage HTTP/1.1",
                     $"{request.Method} {request.Path} HTTP/1.1");
    }

    [Fact]
    public async Task Send_WithUnsupportedProxyScheme_ThrowsInsteadOfSending()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new TelegramSender(stub.BaseUrl);

        var exception =
            await Assert.ThrowsAsync<NotificationSendException>(() => sender.SendAsync("token-123", "42",
                                                                         "ftp://127.0.0.1:21", "text"));
        Assert.Contains("代理地址无效", exception.Message);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void ParseProxy_HttpsWithCredentials_StripsUserInfoIntoCredentials()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var proxy = TelegramSender.ParseProxy($"socks5://user:p%40ss@127.0.0.1:{port}");
        Assert.NotNull(proxy);
        // socks5 scheme 原样保留（SocketsHttpHandler 识别 socks5 代理地址），userinfo 剥离进凭据。
        Assert.Equal($"socks5://127.0.0.1:{port}/", proxy.Address!.ToString());
        var credentials = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("user", credentials.UserName);
        Assert.Equal("p@ss", credentials.Password);
    }
}
