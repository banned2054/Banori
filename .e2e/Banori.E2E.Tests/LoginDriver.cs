using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Banori.E2E.Tests;

/// <summary>
///     HTTP 登录驱动：静态 SSR 表单 POST /Account/Login。
///     Login.razor 字段：Input.Username / Input.Password / Input.RememberMe +
///     __RequestVerificationToken（需先 GET 取 token 与 antiforgery cookie）+
///     _handler=login（Blazor SSR 表单路由隐藏字段，缺失时 POST 直接 400）。
///     限流 5 次/分/IP（BaselineRateLimiting）：登录失败重试要留足间隔，本驱动不做自动重试。
/// </summary>
public sealed class LoginDriver(AcceptanceHost host)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>完整登录一次：GET 取防伪令牌 → POST 表单 → 返回是否重定向（成功）。</summary>
    public async Task<bool> LoginAsync(CancellationToken cancellationToken = default)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress       = new Uri("http://localhost/")
        });

        // GET /Account/Login：拿 __RequestVerificationToken 与 antiforgery cookie。
        var page = await client.GetAsync("/Account/Login", cancellationToken);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync(cancellationToken);
        var token = ExtractHiddenField(html, "__RequestVerificationToken")
                    ?? throw new InvalidOperationException("登录页没有找到 __RequestVerificationToken。");
        var handler = ExtractHiddenField(html, "_handler") ?? "login";

        var form = new Dictionary<string, string>
        {
            ["_handler"]                  = handler,
            ["Input.Username"]            = host.Username,
            ["Input.Password"]            = host.Password,
            ["Input.RememberMe"]          = "false",
            ["__RequestVerificationToken"] = token
        };
        var response = await client.PostAsync("/Account/Login",
                                              new FormUrlEncodedContent(form), cancellationToken);
        // 成功：302 重定向（RedirectManager）；失败：200 回显错误。429 = 限流。
        LoginLastStatus = (int)response.StatusCode;
        return response.StatusCode is System.Net.HttpStatusCode.Redirect ||
               response.StatusCode is System.Net.HttpStatusCode.RedirectMethod;
    }

    public int? LoginLastStatus { get; private set; }

    private static string? ExtractHiddenField(string html, string name)
    {
        var marker = $"name=\"{name}\"";
        var index  = html.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var valueIndex = html.IndexOf("value=\"", index, StringComparison.Ordinal);
        if (valueIndex < 0) return null;
        var start = valueIndex + "value=\"".Length;
        var end   = html.IndexOf('"', start);
        return end > start ? html[start..end] : null;
    }

    /// <summary>把环境准备/冒烟摘要写成 JSON 证据文件。</summary>
    public static async Task WriteJsonAsync(string path, object payload)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
    }
}
