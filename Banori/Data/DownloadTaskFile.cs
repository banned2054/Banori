using Banori.Features.Downloads;

namespace Banori.Data;

/// <summary>任务内稳定的文件身份；原始路径与大小关联 torrent 元数据，当前路径/改名日志关联下载器。</summary>
public sealed class DownloadTaskFile
{
    public Guid          Id             { get; set; }
    public Guid          DownloadTaskId { get; set; }
    public DownloadTask? DownloadTask   { get; set; }

    /// <summary>任务内分配后不变的键，供 UI 与计划寻址；不是元数据列表位置，也不是 qB 文件 index。</summary>
    public int FileIndex { get; set; }

    public string OriginalRelativePath { get; set; } = string.Empty;
    public string CurrentRelativePath  { get; set; } = string.Empty;
    public long   Size                 { get; set; }

    public DownloadFileKind Kind                      { get; set; }
    public decimal?         EpisodeNumberOverride     { get; set; }
    public decimal?         EpisodeEndOverride        { get; set; }
    public string?          AnimeRelativePathOverride { get; set; }
    public bool             IsManualOverride          { get; set; }

    public string ClassificationReason { get; set; } = string.Empty;

    // Journal is committed before the remote request. Recovery matches normalized from/to paths plus Size, never indices.
    public string?        PendingRenameFrom { get; set; }
    public string?        PendingRenameTo   { get; set; }
    public DateTimeOffset UpdatedAtUtc      { get; set; }
}
