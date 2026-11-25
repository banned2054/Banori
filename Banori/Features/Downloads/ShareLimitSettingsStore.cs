using Banori.Core.Torrents;
using Banori.Features.Settings;
using System.Text.Json;

namespace Banori.Features.Downloads;

/// <summary>与 qBittorrent 原生「分享限制」弹窗对齐的三种模式。</summary>
public enum TorrentShareLimitMode
{
    /// <summary>使用全局分享限制（qB 参数 -2）。</summary>
    UseGlobal,

    /// <summary>设置为无分享限制（qB 参数 -1）。</summary>
    Unlimited,

    /// <summary>设置自定义分享限制（勾选的项按数值生效，未勾选的项为 -1 不限制）。</summary>
    Custom
}

/// <summary>下发给 qBittorrent API 的解析后分享限制值。</summary>
public sealed record ResolvedShareLimit(
    double RatioLimit,
    int    SeedingTimeLimitMinutes,
    int    InactiveSeedingTimeLimitMinutes)
{
    public const double UseGlobalRatio   = -2d;
    public const double UnlimitedRatio   = -1d;
    public const int    UseGlobalMinutes = -2;
    public const int    UnlimitedMinutes = -1;

    public static readonly ResolvedShareLimit Global    = new(UseGlobalRatio, UseGlobalMinutes, UseGlobalMinutes);
    public static readonly ResolvedShareLimit Unlimited = new(UnlimitedRatio, UnlimitedMinutes, UnlimitedMinutes);

    public bool IsGlobal =>
        Math.Abs(RatioLimit - UseGlobalRatio) < 1e-9  &&
        SeedingTimeLimitMinutes  == UseGlobalMinutes  &&
        InactiveSeedingTimeLimitMinutes == UseGlobalMinutes;
}

/// <summary>单个分享率配置方案：包含限制规则与独占绑定的站点来源分组列表。</summary>
public sealed record ShareLimitProfile(
    Guid                  Id,
    string                Name,
    TorrentShareLimitMode Mode,
    bool                  RatioLimitEnabled,
    double                RatioLimit,
    bool                  SeedingTimeLimitEnabled,
    int                   SeedingTimeLimitMinutes,
    bool                  InactiveSeedingTimeLimitEnabled,
    int                   InactiveSeedingTimeLimitMinutes,
    IReadOnlyList<string> SiteSources)
{
    public ResolvedShareLimit ToResolved() => Mode switch
    {
        TorrentShareLimitMode.Unlimited => ResolvedShareLimit.Unlimited,
        TorrentShareLimitMode.Custom    => new ResolvedShareLimit(
            RatioLimitEnabled ? Math.Round(RatioLimit, 2, MidpointRounding.AwayFromZero) : ResolvedShareLimit.UnlimitedRatio,
            SeedingTimeLimitEnabled ? SeedingTimeLimitMinutes : ResolvedShareLimit.UnlimitedMinutes,
            InactiveSeedingTimeLimitEnabled ? InactiveSeedingTimeLimitMinutes : ResolvedShareLimit.UnlimitedMinutes),
        _ => ResolvedShareLimit.Global
    };
}

/// <summary>全局分享率配置集合：未分配到任何方案的站点来源一律沿用 qBittorrent 全局分享限制。</summary>
public sealed record ShareLimitSettings(IReadOnlyList<ShareLimitProfile> Profiles)
{
    public static readonly ShareLimitSettings Empty = new([]);

    public ResolvedShareLimit ResolveForSiteSource(string? siteSource)
    {
        var normalized = NormalizeSiteSource(siteSource);
        if (normalized is null) return ResolvedShareLimit.Global;

        foreach (var profile in Profiles)
            if (profile.SiteSources.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                return profile.ToResolved();

        return ResolvedShareLimit.Global;
    }

    public static string? NormalizeSiteSource(string? siteSource)
    {
        var trimmed = siteSource?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>分享率配置持久化存储；校验方案名称唯一性与站点来源独占约束。</summary>
public sealed class ShareLimitSettingsStore(IApplicationSettingStore settingStore)
{
    public const string Key = "Downloads.ShareLimits";

    private static readonly HashSet<string> AllowedSiteSources =
        new(TorrentSiteSource.AllLabels, StringComparer.OrdinalIgnoreCase);

    public async Task<ShareLimitSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await settingStore.GetAsync(Key, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return ShareLimitSettings.Empty;

        var settings = JsonSerializer.Deserialize<ShareLimitSettings>(json);
        return settings is null ? ShareLimitSettings.Empty : Normalize(settings);
    }

    public async Task<ShareLimitSettings> SaveAsync(
        ShareLimitSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        await settingStore.SetAsync(Key, JsonSerializer.Serialize(normalized), cancellationToken);
        return normalized;
    }

    public static ShareLimitSettings Normalize(ShareLimitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var profiles       = new List<ShareLimitProfile>();
        var usedIds        = new HashSet<Guid>();
        var usedNames      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in settings.Profiles ?? [])
        {
            var id   = raw.Id == Guid.Empty ? Guid.NewGuid() : raw.Id;
            if (!usedIds.Add(id)) throw new ArgumentException("存在重复的分享率配置 ID。");

            var name = raw.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("分享率配置名称不能为空。");
            if (name.Length > 60) throw new ArgumentException("分享率配置名称不能超过 60 个字符。");
            if (!usedNames.Add(name)) throw new ArgumentException($"分享率配置名称「{name}」已存在。");

            var ratio    = raw.RatioLimit;
            var totalMin = raw.SeedingTimeLimitMinutes;
            var idleMin  = raw.InactiveSeedingTimeLimitMinutes;

            if (raw.Mode == TorrentShareLimitMode.Custom)
            {
                if (!raw.RatioLimitEnabled && !raw.SeedingTimeLimitEnabled && !raw.InactiveSeedingTimeLimitEnabled)
                    throw new ArgumentException($"配置「{name}」选择了自定义分享限制，请至少勾选一项限制条件。");

                if (raw.RatioLimitEnabled && (!double.IsFinite(ratio) || ratio < 0))
                    throw new ArgumentException($"配置「{name}」的比率必须为大于等于 0 的数字。");
                if (raw.SeedingTimeLimitEnabled && totalMin < 0)
                    throw new ArgumentException($"配置「{name}」的总分钟必须为大于等于 0 的整数。");
                if (raw.InactiveSeedingTimeLimitEnabled && idleMin < 0)
                    throw new ArgumentException($"配置「{name}」的不活跃分钟必须为大于等于 0 的整数。");
            }

            var normalizedSources = new List<string>();
            var profileSourceSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawSource in raw.SiteSources ?? [])
            {
                var source = ShareLimitSettings.NormalizeSiteSource(rawSource);
                if (source is null) continue;
                if (!AllowedSiteSources.Contains(source))
                    throw new ArgumentException($"不支持的站点来源分组「{rawSource}」。");
                if (!profileSourceSet.Add(source)) continue;
                if (claimedSources.TryGetValue(source, out var owner))
                    throw new ArgumentException($"分组「{source}」已分配给配置「{owner}」，不能重复分配。");

                claimedSources[source] = name;
                normalizedSources.Add(source);
            }

            // 按规范分组顺序排列，避免序列化顺序无意义抖动。
            normalizedSources.Sort((left, right) =>
                IndexOfCanonicalSource(left).CompareTo(IndexOfCanonicalSource(right)));

            profiles.Add(new ShareLimitProfile(
                id,
                name,
                raw.Mode,
                raw.Mode == TorrentShareLimitMode.Custom && raw.RatioLimitEnabled,
                raw.Mode == TorrentShareLimitMode.Custom && raw.RatioLimitEnabled
                    ? Math.Round(ratio, 2, MidpointRounding.AwayFromZero)
                    : 0d,
                raw.Mode == TorrentShareLimitMode.Custom && raw.SeedingTimeLimitEnabled,
                raw.Mode == TorrentShareLimitMode.Custom && raw.SeedingTimeLimitEnabled ? totalMin : 0,
                raw.Mode == TorrentShareLimitMode.Custom && raw.InactiveSeedingTimeLimitEnabled,
                raw.Mode == TorrentShareLimitMode.Custom && raw.InactiveSeedingTimeLimitEnabled ? idleMin : 0,
                normalizedSources));
        }

        return new ShareLimitSettings(profiles);
    }

    private static int IndexOfCanonicalSource(string source)
    {
        for (var i = 0; i < TorrentSiteSource.AllLabels.Count; i++)
            if (TorrentSiteSource.AllLabels[i].Equals(source, StringComparison.OrdinalIgnoreCase))
                return i;

        return int.MaxValue;
    }
}
