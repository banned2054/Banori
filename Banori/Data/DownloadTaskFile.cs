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

    /// <summary>该文件是否承担下载：包内择优落选的文件不下载；与内容角色分类分开记录，Seed 附件默认仍下载。</summary>
    public bool IsSelectedForDownload { get; set; } = true;

    /// <summary>不承担下载的原因（如“同集包内择优落选”）；null 表示默认选中，人工覆盖的选择另行标记。</summary>
    public string? SelectionReason { get; set; }

    // Journal is committed before the remote request. Recovery matches normalized from/to paths plus Size, never indices.
    public string?        PendingRenameFrom { get; set; }
    public string?        PendingRenameTo   { get; set; }
    public DateTimeOffset UpdatedAtUtc      { get; set; }
}
