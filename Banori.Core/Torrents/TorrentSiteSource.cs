namespace Banori.Core.Torrents;

/// <summary>
///     来源站点标签：既作为下载器 tag，也作为命名模板的 {source} 令牌。
///     RSS 入口（订阅与一次性）只按实际 RSS 地址的主机名判定（mikanani.me → mikan、anibt.net →
///     anibt），不支持的主机不推导来源，也绝不允许种子内 tracker 覆盖该结论；
///     单独上传 .torrent 才按 PT tracker 分类表判定（u2/byrbt/tjupt），未命中任何分类统一归 bt。
/// </summary>
public static class TorrentSiteSource
{
    /// <summary>上传种子 tracker 未命中任何分类（含仅公共 tracker）时的统一来源。</summary>
    public const string FallbackLabel = "bt";

    /// <summary>RSS 站点主机映射；未列入的主机（含子域）返回 null，不取首段域名标签充当来源。</summary>
    private static readonly Dictionary<string, string> FeedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mikanani.me"] = "mikan",
        ["mikanime.tv"] = "mikan",
        ["anibt.net"]   = "anibt"
    };

    /// <summary>上传 .torrent 的 PT tracker 分类表：主机名与路径精确匹配，忽略 query（passkey、secure 等）。</summary>
    private static readonly (string Host, string Path, string Label)[] TrackerCategories =
    [
        ("daydream.dmhy.best", "/announce", "u2"),
        ("tracker.byr.pt", "/announce.php", "byrbt"),
        ("tracker-public.tjupt.org", "/announce.php", "tjupt"),
        ("tracker-campus.tjupt.org", "/announce.php", "tjupt")
    ];

    /// <summary>由 RSS 地址推导来源站点；地址无法解析或主机不受支持时返回 null。</summary>
    public static string? FromFeedUrl(string? feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return null;
        return Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri) ? FromFeedHost(uri.Host) : null;
    }

    /// <summary>由 RSS 地址主机名推导来源站点；支持站点的子域命中，未知主机返回 null。</summary>
    public static string? FromFeedHost(string? host)
    {
        var normalized = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;

        foreach (var (knownHost, label) in FeedHosts)
            if (normalized.Equals(knownHost, StringComparison.Ordinal) ||
                normalized.EndsWith($".{knownHost}", StringComparison.Ordinal))
                return label;

        return null;
    }

    /// <summary>
    ///     上传 .torrent 的 tracker 分类：扫描全部 tracker，解析 URL 后按主机名与路径精确匹配
    ///     （忽略 query）；主机名不做子串匹配，相似域名不会误命中。返回去重后的命中分类，
    ///     同分类多个地址只算一个；空集合表示未命中任何分类（调用方归 bt），
    ///     多个不同分类由调用方按冲突处理。
    /// </summary>
    public static IReadOnlyList<string> ClassifyTrackers(IEnumerable<string> trackerUrls)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tracker in trackerUrls)
        {
            if (string.IsNullOrWhiteSpace(tracker)) continue;
            if (!Uri.TryCreate(tracker.Trim(), UriKind.Absolute, out var uri)) continue;

            foreach (var (host, path, label) in TrackerCategories)
                if (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                    uri.AbsolutePath.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    labels.Add(label);
                    break;
                }
        }

        return [.. labels];
    }
}
