namespace MediaManager.Core.Torrents;

/// <summary>
///     来源站点标签：既作为下载器 tag，也作为命名模板的 {source} 令牌。
///     权威判定按 .torrent 内的 tracker 地址（FromTrackers，映射对齐 ByrTorrent 的既有规则）；
///     无法判定时回退 URL 主机名（mikanani.me → mikan、anibt.net → anibt 显式映射，其余取首个域名标签）。
/// </summary>
public static class TorrentSiteSource
{
    public const int MaxLength = 50;

    /// <summary>tracker 地址（或其主机）与站点的映射；按顺序做子串匹配，先命中先得。</summary>
    private static readonly (string Pattern, string Label)[] KnownTrackers =
    [
        ("tracker-public.tjupt.org", "tjupt"),
        ("tracker-campus.tjupt.org", "tjupt"),
        ("tracker.byr.pt",           "byrbt"),
        ("tr.bangumi.moe",           "mikan"),
        ("nyaa.tracker.wf",          "mikan"),
        ("t.nyaatracker.com",        "mikan"),
        ("open.acgtracker.com",      "mikan"),
        ("t.acg.rip",                "mikan"),
        ("bangumi.moe",              "mikan"),
        ("daydream.dmhy.best",       "U2"),
        ("anibt",                    "anibt")
    ];

    private static readonly Dictionary<string, string> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mikanani.me"] = "mikan",
        ["anibt.net"]   = "anibt",
        ["u2.dmhy.org"] = "U2"
    };

    /// <summary>由 .torrent 内提取的 tracker 地址列表判定站点；全部未知时返回 null。</summary>
    public static string? FromTrackers(IEnumerable<string> trackerUrls)
    {
        foreach (var tracker in trackerUrls)
        {
            if (string.IsNullOrWhiteSpace(tracker)) continue;

            foreach (var (pattern, label) in KnownTrackers)
                if (tracker.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return label;
        }

        return null;
    }

    /// <summary>由 URL 推导来源站点标签；无法解析时返回 null。</summary>
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? FromHost(uri.Host) : null;
    }

    /// <summary>由主机名推导来源站点标签；无法解析时返回 null。</summary>
    public static string? FromHost(string? host)
    {
        var normalized = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;

        foreach (var (knownHost, label) in KnownHosts)
            if (normalized.Equals(knownHost, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith($".{knownHost}", StringComparison.OrdinalIgnoreCase))
                return label;

        var firstLabel = normalized.Split(':').First().Split('.').FirstOrDefault();
        return Sanitize(firstLabel);
    }

    /// <summary>净化用户输入的来源标签：非法字符替换、收缩空白、限长；空结果返回 null。</summary>
    public static string? Sanitize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        var cleaned = new string(trimmed.Select(character => char.IsWhiteSpace(character) ? ' ' : character)
                                        .ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength];
        return cleaned.Length == 0 ? null : cleaned;
    }
}
