namespace Banori.Features.Downloads;

public enum DownloadFileKind
{
    Anime,
    Subtitle,
    Seed
}

/// <summary>HasPendingFileOperation 为真表示改名/移动尚未被下载器确认；此时路径刷新与再次编辑都不安全。</summary>
public sealed record DownloadTaskFilesView(
    Guid TaskId, string CurrentRootPath, IReadOnlyList<DownloadFileView> Files, bool HasPendingFileOperation = false);

public sealed record DownloadFileView(
    int              Index,
    string           OriginalPath,
    string           CurrentPath,
    DownloadFileKind Kind,
    string           ClassificationReason,
    decimal?         Episode,
    decimal?         EpisodeEnd,
    string?          AnimeRelativePath,
    long             Size,
    bool             IsSelectedForDownload,
    string?          SelectionReason,
    Guid?            SupersededByTaskId,
    string?          SupersededByNaming,
    string?          VerificationOutcome);

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
    string? Warning,
    string  FileTemplate                         = "",
    IReadOnlyList<OtherFileRename>? OtherRenames = null);

/// <summary>
///     本次编辑除当前行外，因命名模板或布局变化会一并改名的已选文件。仅供预览提示，不代替占用校验。
/// </summary>
public sealed record OtherFileRename(int Index, string CurrentPath, string TargetPath);

/// <summary>
///     复用候选：某归档目标路径上已有同路径同大小的受管或外部种子文件，不要求存在替换关系。
///     仅是线索：能否复用必须经停止状态的受控映射与 recheck 校验证实，不授予替换权限。
/// </summary>
public readonly record struct ReuseCandidate(int FileIndex, string AbsolutePath, long Size, bool Managed);

public interface IDownloadFileService
{
    Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     显式刷新：实时查询下载器并按原始种子身份核对后同步当前路径。
    /// </summary>
    Task<DownloadTaskFilesView> RefreshAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<DownloadFilePreview> PreviewAsync(
        Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     expectedFileTemplate 为预览时记录的模板；应用前若当前模板已变化则拒绝，避免预览与实际改名不一致。
    ///     null 表示调用方不要求一致性校验。
    /// </summary>
    Task<DownloadTaskFilesView> ApplyAsync(
        Guid taskId, int index, DownloadFileEditRequest request,
        CancellationToken cancellationToken = default, string? expectedFileTemplate = null);

    /// <summary>
    ///     观察尚未确认的改名/移动操作：在协调门内核对下载器状态，确认生效即落库并返回最新布局，
    ///     仍在确认窗口内返回待确认视图，耗尽补发预算时抛出确认超时异常并保留操作日志。
    /// </summary>
    Task<DownloadTaskFilesView> ObservePendingAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     重新核对受阻归档，按同一检查和迁移链重试，不跳过外部占用或文件身份校验。
    /// </summary>
    Task RetryArchiveAsync(Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>预览按 hash 即时核对发现种子已不在下载器；消息固定，供界面直接展示。</summary>
public sealed class TorrentMissingException() : InvalidOperationException("qBittorrent 中已找不到该种子");
