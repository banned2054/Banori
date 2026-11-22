using Banori.Features.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banori.Features.Anime;

/// <summary>
///     动画总览页排序的持久化：刷新或重新进入页面后恢复用户的排序字段与方向。
///     未保存过时回退为名称升序；保存内容无法解析时同样回退。
/// </summary>
public sealed class AnimeSortStore(IApplicationSettingStore settingStore)
{
    public const string SortKey = "Anime.Sort";

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>读取已保存的排序；未保存或内容无法解析（含已移除的枚举名）时回退为名称升序。</summary>
    public async Task<AnimeSortPreference> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await settingStore.GetAsync(SortKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return AnimeSortPreference.Default;
        try
        {
            var stored = JsonSerializer.Deserialize<AnimeSortPreference>(json, SerializerOptions);
            // 未来枚举值增删后，数字形式仍能落到有效范围外，统一回退默认。
            return stored is null || !Enum.IsDefined(stored.Field) ? AnimeSortPreference.Default : stored;
        }
        catch (JsonException)
        {
            return AnimeSortPreference.Default;
        }
    }

    /// <summary>保存当前排序字段与方向，供重新进入页面时恢复。</summary>
    public Task SaveAsync(
        AnimeSortField field, bool descending, CancellationToken cancellationToken = default) =>
        settingStore.SetAsync(SortKey,
                              JsonSerializer.Serialize(new AnimeSortPreference(field, descending), SerializerOptions),
                              cancellationToken);
}

/// <summary>持久化的排序选择：字段与方向成对保存。</summary>
public sealed record AnimeSortPreference(AnimeSortField Field, bool Descending)
{
    public static AnimeSortPreference Default => new(AnimeSortField.Name, false);
}
