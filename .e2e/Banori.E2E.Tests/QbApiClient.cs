using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Banori.E2E.Tests;

/// <summary>
///     隔离 qBittorrent WebAPI 最小客户端（数据准备与快照采样用，不经 Banori 的
///     QbittorrentSession——那是被测代码的一部分）。
/// </summary>
public sealed class QbApiClient(string baseUrl, string username, string password) : IDisposable
{
    // Cookie 全部手工管理（SID 手动取出、手动回填）：默认 HttpClient 的 CookieContainer 会把首次
    // 登录的 SID 自动附带在后续登录请求上，qB 对已认证会话返回 Ok. 而不再下发 Set-Cookie，
    // 导致重复 LoginAsync 永远取不到新 SID（冒烟只登录一次未暴露，场景链会二次登录）。
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseCookies = false })
    {
        BaseAddress = new Uri(baseUrl)
    };
    private string? _cookie;

    public string Username { get; } = username;

    /// <summary>登录并缓存 SID Cookie；qB 成功登录返回 204 + SID。Referer 头是 CSRF 校验必需。</summary>
    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var lastError = "未知错误";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/auth/login")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["username"] = username,
                        ["password"] = password
                    })
                };
                request.Headers.Referrer = new Uri(_client.BaseAddress!, "/");
                using var response = await _client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var sid = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                        ? cookies.FirstOrDefault(value => value.StartsWith("SID=", StringComparison.Ordinal))
                        : null;
                    _cookie = sid?.Split(';')[0];
                    if (_cookie is not null)
                        return;
                    lastError = "登录成功但没有返回 SID Cookie。";
                }
                else
                {
                    lastError = $"登录 HTTP {(int)response.StatusCode}。";
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception.Message;
            }

            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException($"qB WebUI 登录超时：{lastError}");
            await Task.Delay(500, cancellationToken);
        }
    }

    /// <summary>qB 应用版本（冒烟证据用，如 v4.6.4）。</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(Authorized(HttpMethod.Get, "/api/v2/app/version"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, HttpContent? content = null)
    {
        if (_cookie is null)
            throw new InvalidOperationException("尚未登录，先调用 LoginAsync。");
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("Cookie", _cookie);
        request.Headers.Referrer = new Uri(_client.BaseAddress!, "/");
        return request;
    }

    /// <summary>
    ///     以停止态添加种子文件（数据准备或被测提交的旁路验证共用）。
    ///     WebAPI 2.9.x（qB 4.6.4）的参数名是 paused=true；5.x（WebAPI 2.11）改名为 stopped，
    ///     4.6.4 对 stopped=true 静默忽略、种子直接以运行态加入（实测确认），故两个参数都发。
    /// </summary>
    public async Task AddStoppedAsync(byte[] torrentBytes, string savePath,
                                      CancellationToken cancellationToken = default)
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(torrentBytes), "torrents", "upload.torrent" },
            { new StringContent(savePath), "savepath" },
            { new StringContent("false"), "autoTMM" },
            { new StringContent("true"), "paused" },
            { new StringContent("true"), "stopped" },
            { new StringContent("Original"), "contentLayout" }
        };
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/v2/torrents/add", content),
                                               cancellationToken);
        response.EnsureSuccessStatusCode();
        if (await response.Content.ReadAsStringAsync(cancellationToken) == "Fails.")
            throw new InvalidOperationException("qB 拒绝添加种子（Fails.）。");
    }

    public Task RecheckAsync(string infoHash, CancellationToken cancellationToken = default) =>
        SendActionAsync("recheck", infoHash, cancellationToken);

    /// <summary>resume：普通恢复（完成态种子进 pausedUP→stalledUP；force_start 不置位）。</summary>
    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default) =>
        SendActionAsync("resume", infoHash, cancellationToken);

    /// <summary>
    ///     force-resume：强制开始（setForceStart value=true），qB 4.6.4 实测 state=forcedUP、
    ///     force_start=true；torrents/resume?force=true 不等于强制开始（实测仍 stalledUP）。
    /// </summary>
    public Task ForceResumeAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = infoHash,
            ["value"]  = "true"
        });
        return SendActionRawAsync("/api/v2/torrents/setForceStart", content, cancellationToken);
    }

    private async Task SendActionRawAsync(string url, HttpContent content,
                                          CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, url, content), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendActionAsync(string action, string infoHash, CancellationToken cancellationToken,
                                       bool force = false)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = infoHash });
        var url = force ? $"/api/v2/torrents/{action}?force=true" : $"/api/v2/torrents/{action}";
        await SendActionRawAsync(url, content, cancellationToken);
    }

    /// <summary>取种子列表（含 state/progress/downloaded 字段，供快照与完成态判定）。</summary>
    public async Task<IReadOnlyList<QbTorrentInfo>> ListTorrentsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(Authorized(HttpMethod.Get, "/api/v2/torrents/info"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<QbTorrentInfo>>(cancellationToken) ?? [];
    }

    /// <summary>种子文件清单（/api/v2/torrents/files，供场景核对文件级布局/内容路径）。</summary>
    public async Task<IReadOnlyList<QbTorrentFile>> GetTorrentFilesAsync(string infoHash,
                                                                         CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["hash"] = infoHash });
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/v2/torrents/files", content),
                                               cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<QbTorrentFile>>(cancellationToken) ?? [];
    }

    /// <summary>改种子保存位置（/api/v2/torrents/setLocation；路径为 qB 容器内视角）。</summary>
    public async Task SetLocationAsync(string infoHash, string location,
                                       CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"]   = infoHash,
            ["location"] = location
        });
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/v2/torrents/setLocation", content),
                                               cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<QbTorrentInfo?> FindTorrentAsync(string infoHash,
                                                       CancellationToken cancellationToken = default)
    {
        var list = await ListTorrentsAsync(cancellationToken);
        return list.FirstOrDefault(torrent =>
                                       string.Equals(torrent.Hash, infoHash, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DeleteAsync(string infoHash, bool deleteFiles,
                                  CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"]      = infoHash,
            ["deleteFiles"] = deleteFiles ? "true" : "false"
        });
        var response = await _client.SendAsync(Authorized(HttpMethod.Post, "/api/v2/torrents/delete", content),
                                               cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>qB /api/v2/torrents/info 条目（只留验收需要的字段；JSON snake_case）。</summary>
public sealed record QbTorrentInfo(
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("progress")] double? Progress,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("downloaded")] long? Downloaded,
    [property: JsonPropertyName("amount_left")] long? AmountLeft,
    [property: JsonPropertyName("save_path")] string? SavePath,
    [property: JsonPropertyName("tags")] string? Tags);

/// <summary>qB /api/v2/torrents/files 条目（index/name/size；JSON snake_case）。</summary>
public sealed record QbTorrentFile(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long Size);
