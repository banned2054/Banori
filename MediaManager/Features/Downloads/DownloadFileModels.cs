namespace MediaManager.Features.Downloads;

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

public interface IDownloadFileService
{
    Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<DownloadFilePreview> PreviewAsync(Guid              taskId, int index, DownloadFileEditRequest request,
                                           CancellationToken cancellationToken = default);

    Task<DownloadTaskFilesView> ApplyAsync(Guid              taskId, int index, DownloadFileEditRequest request,
                                           CancellationToken cancellationToken = default);
}
