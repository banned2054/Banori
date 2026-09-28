using MediaManager.Integrations.Notifications;

namespace MediaManager.Tests.Notifications;

/// <summary>真实 GotifySender 的请求格式与错误处理回归：用本地 TCP 桩冒充 Gotify 服务。</summary>
public sealed class GotifySenderTests
{
    [Fact]
    public async Task Send_PostsMessageToTokenEndpoint()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new GotifySender();

        await sender.SendAsync(stub.BaseUrl, "app-token", null, "标题", "正文内容");

        var request = Assert.Single(stub.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/message?token=app-token", request.Path);
        Assert.Contains("标题", request.Body);
        Assert.Contains("正文内容", request.Body);
    }

    [Fact]
    public async Task Send_BlankTitle_OmitsTitleField()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new GotifySender();

        await sender.SendAsync(stub.BaseUrl, "app-token", null, string.Empty, "只有正文");

        var request = Assert.Single(stub.Requests);
        Assert.Contains("只有正文", request.Body);
        Assert.DoesNotContain("title", request.Body);
    }

    [Fact]
    public async Task Send_TrailingSlashBaseUrl_NormalizedOnce()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new GotifySender();

        await sender.SendAsync(stub.BaseUrl + "/", "app-token", null, "标题", "正文");

        var request = Assert.Single(stub.Requests);
        Assert.StartsWith("/message", request.Path);
    }

    [Fact]
    public async Task Send_ErrorStatusCode_ThrowsWithStatusAndBody()
    {
        await using var stub = StubHttpServer.Start();
        stub.Responder = _ => (403, "{\"errorDescription\":\"token invalid\"}");
        var sender = new GotifySender();

        var exception =
            await Assert.ThrowsAsync<NotificationSendException>(() => sender.SendAsync(stub.BaseUrl, "bad-token",
                                                                         null, "标题", "正文"));
        Assert.Contains("403", exception.Message);
        Assert.Contains("token invalid", exception.Message);
    }

    [Fact]
    public async Task Send_WithProxy_RoutesRequestThroughProxy()
    {
        await using var stub   = StubHttpServer.Start();
        var             sender = new GotifySender();

        // 目标是纯 http 的假域名，代理收到的是绝对 URI 形式的请求行。
        await sender.SendAsync("http://gotify.invalid", "app-token", stub.BaseUrl, "标题", "正文内容");

        var request = Assert.Single(stub.Requests);
        Assert.StartsWith("POST http://gotify.invalid/message?token=app-token",
                          $"{request.Method} {request.Path}");
        Assert.Contains("正文内容", request.Body);
    }
}
