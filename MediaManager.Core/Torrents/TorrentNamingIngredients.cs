using System.Text.Json;

namespace MediaManager.Core.Torrents;

/// <summary>命名快照的全部渲染输入，包含独立的种子名/文件名模板及 AniParser、Bangumi 字段。</summary>
public sealed record TorrentNamingIngredients(
    string? Template,
    string  Title,
    string  EpisodeText,
    int     Version,
    string? Group,
    string? Source,
    string? Language         = null,
    string? Resolution       = null,
    string? FileTemplate     = null,
    string? AniMediaType     = null,
    string? AniTitle         = null,
    string? AniGroup         = null,
    string? AniGroupType     = null,
    string? AniSubtitleType  = null,
    string? AniSource        = null,
    string? AniWebSource     = null,
    string? AniResolution    = null,
    string? AniVideoCodec    = null,
    string? AniAudioCodec    = null,
    int?    AniColorBitDepth = null,
    int?    BangumiId        = null,
    string? BangumiName      = null,
    string? BangumiNameCn    = null,
    string? BangumiPlatform  = null,
    string? BangumiDate      = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>旧行或脏数据没有合法要素 JSON 时返回 null，调用方回退按命名快照改名。</summary>
    public static TorrentNamingIngredients? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TorrentNamingIngredients>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
