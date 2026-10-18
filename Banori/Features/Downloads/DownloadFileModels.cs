namespace Banori.Features.Downloads;

public enum DownloadFileKind
{
    Anime,
    Subtitle,
    Seed
}

public sealed record DownloadTaskFilesView(Guid TaskId, string CurrentRootPath, IReadOnlyList<DownloadFileView> Files);

public sealed record DownloadFileView(
    int              Index,
    string           OriginalPath,
    string           CurrentPath,
    DownloadFileKind Kind,
    string           ClassificationReason,
    decimal?         Episode,
    decimal?         EpisodeEnd,
    string?          AnimeRelativePath);

public sealed record DownloadFileEditRequest(
    DownloadFileKind Kind,
    bool             ReidentifyOriginalName = false,
    decimal?         Episode                = null,
    decimal?         EpisodeEnd             = null,
    string?          AnimeRelativePath      = null);

public sealed record DownloadFilePreview(
    string  CurrentPath,
    string  TargetPath,
    string  CurrentRootPath,
    string  TargetRootPath,
    bool    RequiresRootRename,
    string? Warning);

/// <summary>
///     复用候选：某归档目标路径上已存在同路径同大小的数据——外部种子文件（未登记）或受管承接对象
///     （现任/休眠）。仅是线索：能否复用必须经停止状态的受控映射与 recheck 校验证实。
/// </summary>
public readonly record struct ReuseCandidate(int FileIndex, string AbsolutePath, long Size, bool Managed);

public interface IDownloadFileService
{
    Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<DownloadFilePreview> PreviewAsync(
        Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default);

    Task<DownloadTaskFilesView> ApplyAsync(
        Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     重新核对受阻归档，按同一检查和迁移链重试，不跳过外部占用或文件身份校验。
    /// </summary>
    Task RetryArchiveAsync(Guid taskId, CancellationToken cancellationToken = default);
}
