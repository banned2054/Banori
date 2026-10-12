namespace Banori.Integrations;

/// <summary>按条目提供的地址获取 .torrent 原始字节，用于提取 info-hash 与提交下载器。proxyId 为 null 时直连。</summary>
public interface ITorrentFileSource
{
    /// <summary>下载失败时抛出 InvalidOperationException，消息可直接展示。</summary>
    Task<byte[]> DownloadAsync(Uri torrentUri, Guid? proxyId = null, CancellationToken cancellationToken = default);
}
