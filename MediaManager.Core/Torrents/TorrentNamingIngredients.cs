using System.Text.Json;

namespace MediaManager.Core.Torrents;

/// <summary>
///     命名要素：生成命名快照时的模板与各令牌取值。快照是渲染结果，要素是可再渲染的原料；
///     随任务落库后，完成改名阶段按每个内容文件的实际集数替换 EpisodeText 重新渲染，
///     保证种子名与内容文件名同源，且不受模板后续变更影响。
/// </summary>
public sealed record TorrentNamingIngredients(
    string? Template,
    string  Title,
    string  EpisodeText,
    int     Version,
    string? Group,
    string? Source,
    string? Language   = null,
    string? Resolution = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>存量行或脏数据没有合法要素 JSON 时返回 null，调用方回退按命名快照改名。</summary>
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
