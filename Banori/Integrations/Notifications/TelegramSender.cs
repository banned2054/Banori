using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Banori.Integrations.Notifications;

/// <summary>
///     Telegram Bot API sendMessage 发送器。通知频率低，按次创建 HttpClient 以承载每次可能不同的代理配置；
///     未指定代理时沿用系统代理设置，指定后覆盖（http/https/socks5）。响应非成功状态码或业务 ok=false 都视为失败。
/// </summary>
public sealed class TelegramSender : ITelegramSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 中文正文按 UTF-8 原样发送，不做 \uXXXX 转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>apiBaseUrl 供测试桩注入；生产使用官方 API 地址。</summary>
    public TelegramSender(string? apiBaseUrl = null) =>
        ApiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "https://api.telegram.org"
            : apiBaseUrl.Trim().TrimEnd('/');

    private string ApiBaseUrl { get; }

    public async Task SendAsync(string            botToken, string chatId, string? proxyUrl, string text,
                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        using var handler = new SocketsHttpHandler
        {
            Proxy          = ParseProxy(proxyUrl),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        using var content = new StringContent(
                                              JsonSerializer.Serialize(new { chat_id = chatId.Trim(), text },
                                                                       JsonOptions),
                                              Encoding.UTF8, "application/json");
        using var response =
            await client.PostAsync($"{ApiBaseUrl}/bot{botToken.Trim()}/sendMessage", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NotificationSendException($"Telegram 返回 {(int)response.StatusCode}：{Truncate(body)}");

        // 200 响应也可能业务失败（ok:false），以 description 为准。
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("ok", out var ok)  &&
                ok.ValueKind == JsonValueKind.False)
            {
                var description = document.RootElement.TryGetProperty("description", out var value)
                    ? value.GetString()
                    : null;
                throw new NotificationSendException($"Telegram 发送失败：{description ?? Truncate(body)}");
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应按状态码判定，已在上方处理。
        }
    }

    /// <summary>解析代理配置；空白返回 null（沿用系统代理）。解析规则见 ProxyUrlParser，无效时转换为发送异常。</summary>
    public static WebProxy? ParseProxy(string? proxyUrl)
    {
        try
        {
            return ProxyUrlParser.Parse(proxyUrl);
        }
        catch (FormatException exception)
        {
            throw new NotificationSendException(exception.Message);
        }
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
