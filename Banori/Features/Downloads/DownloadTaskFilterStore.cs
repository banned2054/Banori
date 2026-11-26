using Banori.Features.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banori.Features.Downloads;

/// <summary>下载任务列表的持久化排序偏好：主排序列与升降序方向。</summary>
public sealed record DownloadTaskSortPreference(DownloadTaskSortColumn Column, bool Descending)
{
    public static DownloadTaskSortPreference Default { get; } = new(DownloadTaskSortColumn.AnimeName, false);
}

/// <summary>
///     下载任务列表状态筛选与排序偏好的持久化：刷新或重新进入页面后恢复用户的勾选与排序选择。
///     状态筛选未保存过时按全部勾选（等于不限制状态）；保存空集合表示一个状态都不显示，两者必须能区分。
/// </summary>
public sealed class DownloadTaskFilterStore(IApplicationSettingStore settingStore)
{
    public const string StatusGroupsKey = "Downloads.StatusFilter";
    public const string SortKey         = "Downloads.Sort";

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>读取已保存的勾选分组；未保存或内容无法解析（含已移除的枚举名）时回退为全部勾选。</summary>
    public async Task<IReadOnlySet<DownloadStatusFilterGroup>> GetStatusGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await settingStore.GetAsync(StatusGroupsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return DownloadStatusFilter.CreateAllGroups();
        try
        {
            var groups = JsonSerializer.Deserialize<DownloadStatusFilterGroup[]>(json, SerializerOptions);
            return groups is null
                ? DownloadStatusFilter.CreateAllGroups()
                : new HashSet<DownloadStatusFilterGroup>(groups);
        }
        catch (JsonException)
        {
            return DownloadStatusFilter.CreateAllGroups();
        }
    }

    /// <summary>保存当前勾选：空集合同样写入，作为“全不勾选”的明确记录。</summary>
    public Task SaveStatusGroupsAsync(
        IReadOnlySet<DownloadStatusFilterGroup> groups, CancellationToken cancellationToken = default) =>
        settingStore.SetAsync(StatusGroupsKey,
                              JsonSerializer.Serialize(groups.ToArray(), SerializerOptions), cancellationToken);

    /// <summary>读取已保存的排序偏好；未保存或内容无法解析（含无效或已移除的枚举值）时回退为默认排序。</summary>
    public async Task<DownloadTaskSortPreference> GetSortAsync(CancellationToken cancellationToken = default)
    {
        var json = await settingStore.GetAsync(SortKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return DownloadTaskSortPreference.Default;
        try
        {
            var preference = JsonSerializer.Deserialize<DownloadTaskSortPreference>(json, SerializerOptions);
            return preference is not null && Enum.IsDefined(preference.Column)
                ? preference
                : DownloadTaskSortPreference.Default;
        }
        catch (JsonException)
        {
            return DownloadTaskSortPreference.Default;
        }
    }

    /// <summary>保存当前主排序列与方向。</summary>
    public Task SaveSortAsync(DownloadTaskSortPreference sort, CancellationToken cancellationToken = default) =>
        settingStore.SetAsync(SortKey, JsonSerializer.Serialize(sort, SerializerOptions), cancellationToken);
}
