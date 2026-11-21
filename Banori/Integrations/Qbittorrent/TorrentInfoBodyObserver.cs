namespace Banori.Integrations.Qbittorrent;

/// <summary>
///     记录单次 <c>torrents/info</c> 查询的原始 HTTP 响应正文。Banned.Qbittorrent 1.8.0 在
///     <c>GetTorrentInfos</c> 内用 <c>?? new List&lt;TorrentInfo&gt;()</c> 把 JSON <c>null</c> 归并为空列表，
///     业务层无法从返回类型区分「成功查无」与「无效响应」。捕获 handler 把原始正文写入这里，
///     只有成功的 JSON 数组才能证明查无。用 AsyncLocal 隔离同一会话内的并发查询。
/// </summary>
public sealed class TorrentInfoBodyObserver
{
    private readonly AsyncLocal<Scope?> _current = new();

    internal Scope BeginScope() => _current.Value = new Scope(this);

    public void Observe(string body) => _current.Value?.Observe(body);

    internal sealed class Scope(TorrentInfoBodyObserver owner) : IDisposable
    {
        private string? _rawBody;

        public void Observe(string body) => _rawBody = body;

        /// <summary>正文是 JSON <c>null</c>（不是数组）：本次结果未知，不能当作成功查无。</summary>
        public bool IsNullResponse =>
            _rawBody is not null &&
            string.Equals(_rawBody.Trim(), "null", StringComparison.OrdinalIgnoreCase);

        public void Dispose() => owner._current.Value = null;
    }
}
