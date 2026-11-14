namespace Banori.Core.Torrents;

/// <summary>
///     来源作用域：把 RSS 条目、订阅预选与逐集登记按来源隔离到互不竞争的键域。
///     RSS 入口只按订阅/一次性地址的主机名判定，上传入口只按 tracker 分类判定；两者不互相覆盖。
///     迁移前的旧行统一落在 <see cref="Legacy" />，不参与已知来源竞争；未知 RSS 与未知上传使用不同哨兵。
/// </summary>
public static class SourceScopes
{
    /// <summary>迁移前尚未回填、或无法证明来源的旧行。</summary>
    public const string Legacy = "(legacy)";

    /// <summary>RSS 主机未知或订阅地址无法解析/解密；所有未知 RSS 同域。</summary>
    public const string Unknown = "(unknown)";

    /// <summary>上传种子未命中任何 PT tracker 分类；所有未知上传同域，与未知 RSS 分开。</summary>
    public const string Other = "other";

    /// <summary>
    ///     读取容错归一：trim + 小写；空值归 <see cref="Legacy" />（写入永不产生），
    ///     旧上传兜底 <c>bt</c> 归 <see cref="Other" />；其余原样返回（保留哨兵与已知小写标签）。
    /// </summary>
    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return Legacy;

        return normalized switch
        {
            "bt" => Other,
            _    => normalized
        };
    }

    /// <summary>由 RSS 主机名推导来源作用域；未知主机或空值归 <see cref="Unknown" />，不是 <see cref="Legacy" />。</summary>
    public static string FromFeedHost(string? host) => TorrentSiteSource.FromFeedHost(host) ?? Unknown;

    /// <summary>由 RSS 地址推导来源作用域；地址无法解析或主机未知归 <see cref="Unknown" />。</summary>
    public static string FromFeedUrl(string? feedUrl) => TorrentSiteSource.FromFeedUrl(feedUrl) ?? Unknown;

    /// <summary>由任务的展示来源标签推导来源作用域；null/空白归 <see cref="Unknown" />，旧 <c>bt</c> 归 <see cref="Other" />。</summary>
    public static string FromSiteSource(string? siteSource) =>
        string.IsNullOrWhiteSpace(siteSource) ? Unknown : Normalize(siteSource);

    /// <summary>
    ///     由上传种子的 tracker 集合推导来源作用域：未命中任何分类归 <see cref="Other" />，
    ///     恰好命中一个分类返回该小写标签。多个不同分类不在此抛异常，调用方需自行检查并拒绝；
    ///     此函数此时返回 <see cref="Other" />，不得用于登记多来源任务。
    /// </summary>
    public static string FromUploadTrackers(IEnumerable<string> trackerUrls)
    {
        ArgumentNullException.ThrowIfNull(trackerUrls);

        var labels = TorrentSiteSource.ClassifyTrackers(trackerUrls);
        return labels.Count == 1 ? Normalize(labels[0]) : Other;
    }
}
