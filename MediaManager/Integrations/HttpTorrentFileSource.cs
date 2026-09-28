namespace MediaManager.Integrations;

/// <summary>通过代理感知的 HttpClient 源下载 .torrent 文件，只负责传输，不做业务判断。</summary>
public sealed class HttpTorrentFileSource(IProxiedHttpClientSource httpClientSource) : ITorrentFileSource
{
    public async Task<byte[]> DownloadAsync(Uri torrentUri, Guid? proxyId = null,
                                            CancellationToken   cancellationToken = default)
    {
        try
        {
            using var client   = await httpClientSource.CreateClientAsync("TorrentDownload", proxyId, cancellationToken);
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
