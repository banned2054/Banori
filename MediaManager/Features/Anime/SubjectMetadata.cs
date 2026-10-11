namespace MediaManager.Features.Anime;

/// <summary>Bangumi Subject 的元数据快照：本地缓存行、API 结果与页面展示共用同一形状。</summary>
public sealed record SubjectMetadata(
    int            Id,
    string         Name,
    string         NameCn,
    string         Platform,
    string?        AirDate,
    string?        ImageUrl,
    DateTimeOffset CachedAtUtc)
{
    public string DisplayName => string.IsNullOrWhiteSpace(NameCn) ? Name : NameCn;
}
