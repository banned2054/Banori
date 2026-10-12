using Banori.Features.Downloads;

namespace Banori.Data;

/// <summary>由 torrent 元数据和 qB 文件 index 关联的稳定身份；原始路径在初始化后不可改写。</summary>
public sealed class DownloadTaskFile
{
    public Guid             Id                        { get; set; }
    public Guid             DownloadTaskId            { get; set; }
    public DownloadTask?    DownloadTask              { get; set; }
    public int              FileIndex                 { get; set; }
    public string           OriginalRelativePath      { get; set; } = string.Empty;
    public string           CurrentRelativePath       { get; set; } = string.Empty;
    public long             Size                      { get; set; }
    public DownloadFileKind Kind                      { get; set; }
    public decimal?         EpisodeNumberOverride     { get; set; }
    public decimal?         EpisodeEndOverride        { get; set; }
    public string?          AnimeRelativePathOverride { get; set; }
    public bool             IsManualOverride          { get; set; }

    public string ClassificationReason { get; set; } = string.Empty;

    // Journal is committed before the remote request. Recovery observes qB's index, never infers original names.
    public string?        PendingRenameFrom { get; set; }
    public string?        PendingRenameTo   { get; set; }
    public DateTimeOffset UpdatedAtUtc      { get; set; }
}
