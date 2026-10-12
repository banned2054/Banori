using Banned.Qbittorrent.Exceptions;
using MediaManager.Features.Settings;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Tests.Downloads;
using MediaManager.Tests.Proxies;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MediaManager.Tests.Integrations;

/// <summary>
///     真实 QbittorrentSessionFactory 的凭据规则回归：用本地 TCP 桩冒充 qBittorrent 的登录与版本端点，
///     校验草稿密码留空时必须改用已保存密码、填写草稿时使用草稿值。
/// </summary>
public sealed class QbittorrentSessionFactoryTests
{
    [Fact]
    public async Task Create_WithBlankDraftPassword_FallsBackToSavedPassword()
    {
        var store = new MemoryApplicationSettingStore();
        await store.SetProtectedAsync(ManageQbittorrentSettings.PasswordKey, "saved-secret");
        await using var stub    = StubQbittorrentServer.Start("saved-secret");
        var             factory = new QbittorrentSessionFactory(store, ProxyServiceTestFactory.CreateEphemeral());

        await using var session = await factory.CreateAsync(
                                                            new QbittorrentSettings(stub.BaseUrl, "admin", "/downloads",
                                                                true), null);

        Assert.Equal("saved-secret", stub.ReceivedPassword);
        Assert.Equal("v5.0.0", session.ServerVersion);
    }

    [Fact]
    public async Task Create_WithDraftPassword_SendsDraftInsteadOfSaved()
    {
        var store = new MemoryApplicationSettingStore();
        await store.SetProtectedAsync(ManageQbittorrentSettings.PasswordKey, "saved-secret");
        await using var stub    = StubQbittorrentServer.Start("saved-secret");
        var             factory = new QbittorrentSessionFactory(store, ProxyServiceTestFactory.CreateEphemeral());

        await Assert.ThrowsAsync<QbittorrentLoginFailedException>(() => factory.CreateAsync(
                                                                   new QbittorrentSettings(stub.BaseUrl, "admin",
                                                                       "/downloads", true), "draft-secret"));

        Assert.Equal("draft-secret", stub.ReceivedPassword);
    }

    private sealed record StubRequest(string Method, string Path, string Body);

    /// <summary>最小 TCP 桩：按 WebUI 协议响应登录与版本探测，记录收到的凭据；凭据不符按协议返回 Fails。</summary>
    private sealed class StubQbittorrentServer(string expectedPassword) : IAsyncDisposable
    {
        private readonly TcpListener             _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopped  = new();

        public string  BaseUrl          { get; private set; } = string.Empty;
        public string? ReceivedUsername { get; private set; }
        public string? ReceivedPassword { get; private set; }

        public ValueTask DisposeAsync()
        {
            _stopped.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }

            return ValueTask.CompletedTask;
        }

        public static StubQbittorrentServer Start(string expectedPassword)
        {
            var server = new StubQbittorrentServer(expectedPassword);
            server._listener.Start();
            server.BaseUrl = $"http://127.0.0.1:{((IPEndPoint)server._listener.LocalEndpoint).Port}";
            _              = Task.Run(server.ServeAsync);
            return server;
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stopped.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (Exception)
            {
                // 桩服务器停止或客户端异常断开都直接退出。
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var request = await ReadRequestAsync(stream);
                    if (request.Method == "POST" &&
                        request.Path.EndsWith("auth/login", StringComparison.Ordinal))
                    {
                        (ReceivedUsername, ReceivedPassword) = ParseCredentials(request.Body);
                        await RespondAsync(stream,
                                           ReceivedUsername == "admin" &&
                                           ReceivedPassword == expectedPassword
                                               ? "Ok."
                                               : "Fails.");
                        return;
                    }

                    // 其余端点（webapiVersion、app/version 等版本探测）一律返回固定版本文本。
                    await RespondAsync(stream,
                                       request.Path.EndsWith("webapiVersion", StringComparison.Ordinal)
                                           ? "2.11.2"
                                           : "v5.0.0");
                }
            }
            catch (IOException)
            {
                // 客户端提前断开属于正常情况。
            }
        }

        private static async Task<StubRequest> ReadRequestAsync(NetworkStream stream)
        {
            var headerBytes = new List<byte>();
            var buffer      = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(headerBytes)) < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;

                headerBytes.AddRange(buffer.Take(read));
            }

            var headerText = Encoding.ASCII.GetString(
                                                      headerBytes.Take(headerEnd < 0 ? headerBytes.Count : headerEnd)
                                                                 .ToArray());
            var lines         = headerText.Split("\r\n");
            var firstLine     = lines[0].Split(' ');
            var method        = firstLine[0];
            var path          = firstLine.Length > 1 ? firstLine[1] : string.Empty;
            var contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 && line[..separator].Trim()
                                                      .Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[(separator + 1)..].Trim(), out contentLength);
            }

            var body           = new byte[contentLength];
            var bodyFromHeader = Math.Min(contentLength, Math.Max(0, headerBytes.Count - (headerEnd + 4)));
            headerBytes.Skip(headerEnd + 4).Take(bodyFromHeader).ToArray().CopyTo(body, 0);
            var offset = bodyFromHeader;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset));
                if (read == 0) break;

                offset += read;
            }

            return new StubRequest(method, path, Encoding.UTF8.GetString(body, 0, offset));
        }

        private static int IndexOfHeaderEnd(List<byte> bytes) => Encoding.ASCII.GetString(bytes.ToArray())
                                                                         .IndexOf("\r\n\r\n", StringComparison.Ordinal);

        private static (string? Username, string? Password) ParseCredentials(string body)
        {
            var fields = new Dictionary<string, string>();
            if (body.Contains('=') && !body.TrimStart().StartsWith('{'))
                foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = pair.IndexOf('=');
                    if (separator > 0)
                        fields[Uri.UnescapeDataString(pair[..separator])] =
                            Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
                }
            else
                try
                {
                    using var document = JsonDocument.Parse(body);
                    foreach (var property in document.RootElement.EnumerateObject())
                        fields[property.Name] = property.Value.ToString();
                }
                catch (JsonException)
                {
                }

            return (fields.GetValueOrDefault("username"), fields.GetValueOrDefault("password"));
        }

        private static async Task RespondAsync(NetworkStream stream, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                                                 $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                                                 $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }
}
