namespace MediaManager.Integrations;

/// <summary>按条目提供的地址获取 .torrent 原始字节，用于提取 info-hash 与提交下载器。</summary>
public interface ITorrentFileSource
{
    /// <summary>下载失败时抛出 InvalidOperationException，消息可直接展示。</summary>
    Task<byte[]> DownloadAsync(Uri torrentUri, CancellationToken cancellationToken = default);
}
