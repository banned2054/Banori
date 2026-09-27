namespace MediaManager.Integrations;

/// <summary>通过命名的 HttpClient 下载 .torrent 文件，只负责传输，不做业务判断。</summary>
public sealed class HttpTorrentFileSource(IHttpClientFactory httpClientFactory) : ITorrentFileSource
{
    public async Task<byte[]> DownloadAsync(Uri torrentUri, CancellationToken cancellationToken = default)
    {
        try
        {
            var       client   = httpClientFactory.CreateClient("TorrentDownload");
            using var response = await client.GetAsync(torrentUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HttpIOException)
        {
            throw new InvalidOperationException($"下载种子文件失败：{exception.Message}", exception);
        }
    }
}
