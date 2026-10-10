using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MediaManager.Integrations.Notifications;

/// <summary>
///     Gotify 消息发送器：POST {baseUrl}/message?token={appToken}，正文为 title/message。
///     Gotify 通常自托管在局域网：proxyUrl 为空时直连不走代理，配置了代理才走（http/https/socks）。
///     响应非 2xx 视为失败。
/// </summary>
public sealed class GotifySender : IGotifySender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 中文正文按 UTF-8 原样发送，不做 \uXXXX 转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task SendAsync(string baseUrl, string appToken, string? proxyUrl, string title, string message,
                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(appToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var normalized = baseUrl.Trim().TrimEnd('/');
        // Proxy = null 仍会回落系统默认代理（UseProxy 默认 true），必须显式关闭才是自托管直连。
        var handler = string.IsNullOrWhiteSpace(proxyUrl)
            ? new SocketsHttpHandler { UseProxy = false }
            : new SocketsHttpHandler { Proxy    = ProxyUrlParser.Parse(proxyUrl) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var payload = new Dictionary<string, string> { ["message"] = message };
        if (!string.IsNullOrWhiteSpace(title)) payload["title"] = title;
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions),
                                              Encoding.UTF8, "application/json");
        using var response = await client
           .PostAsync($"{normalized}/message?token={Uri.EscapeDataString(appToken.Trim())}",
                      content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NotificationSendException(
                                                $"Gotify 返回 {(int)response.StatusCode}：{Truncate(body)}");
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
