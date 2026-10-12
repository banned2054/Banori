using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Banori.Tests.Notifications;

/// <summary>
///     最小 TCP 桩：接收一个 HTTP 请求、记录方法/路径/正文并按配置响应。
///     与 QbittorrentSessionFactoryTests 的 StubQbittorrentServer 同一思路，用于测试真实发送器。
/// </summary>
public sealed class StubHttpServer : IAsyncDisposable
{
    private readonly SemaphoreSlim           _gate     = new(1, 1);
    private readonly TcpListener             _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopped  = new();

    /// <summary>已收到的请求（按连接顺序）。</summary>
    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    /// <summary>响应策略：按收到的请求返回状态码与正文。</summary>
    public Func<(string Method, string Path, string Body), (int Status, string Body)> Responder { get; set; } =
        _ => (200, "{}");

    public string BaseUrl { get; private set; } = string.Empty;

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

    public static StubHttpServer Start()
    {
        var server = new StubHttpServer();
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
            await using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream);
                await _gate.WaitAsync(_stopped.Token);
                try
                {
                    Requests.Add(request);
                }
                finally
                {
                    _gate.Release();
                }

                var (status, body) = Responder(request);
                await RespondAsync(stream, status, body);
            }
        }
        catch (Exception)
        {
            // 客户端提前断开不影响后续连接。
        }
    }

    private async Task<(string Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer   = new byte[4096];
        var received = new List<byte>();
        int headerEnd;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, _stopped.Token);
            if (read == 0) break;
            received.AddRange(buffer.Take(read));
            headerEnd = FindHeaderEnd(received);
            if (headerEnd >= 0) break;
        }

        var head          = Encoding.ASCII.GetString([.. received]);
        var lines         = head.Split("\r\n");
        var requestLine   = lines[0].Split(' ');
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            contentLength = int.Parse(line["Content-Length:".Length..].Trim());
        }

        var body = contentLength == 0
            ? string.Empty
            : await ReadBodyAsync(stream, buffer, received, contentLength);
        return (requestLine[0], requestLine[1], body);
    }

    private async Task<string> ReadBodyAsync(NetworkStream stream, byte[] buffer, List<byte> received,
                                             int           contentLength)
    {
        var headerEnd = FindHeaderEnd(received);
        var body      = received.Skip(headerEnd + 4).ToList();
        while (body.Count < contentLength)
        {
            var read = await stream.ReadAsync(buffer, _stopped.Token);
            if (read == 0) break;
            body.AddRange(buffer.Take(read));
        }

        return Encoding.UTF8.GetString([.. body.Take(contentLength)]);
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (var index = 0; index <= bytes.Count - 4; index++)
        {
            if (bytes[index] != 0x0D || bytes[index + 1] != 0x0A) continue;
            if (bytes[index                         + 2] == 0x0D && bytes[index + 3] == 0x0A) return index;
        }

        return -1;
    }

    private async Task RespondAsync(NetworkStream stream, int status, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
                                           $"HTTP/1.1 {status} Stub\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, _stopped.Token);
        await stream.WriteAsync(bodyBytes, _stopped.Token);
    }
}
