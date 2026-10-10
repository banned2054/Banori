using MediaManager.Features.Downloads;

namespace MediaManager.Data;

/// <summary>
///     持久化的下载意图：保存执行所需的决策快照（命名、集数、版本、种子字节）与恢复所需的状态。
///     多个条目可能指向同一资源，Subject + Episode 不构成唯一键；同集多版本共存由隔离路径保证。
/// </summary>
public sealed class DownloadTask
{
    public Guid Id { get; set; }

    /// <summary>来源 RSS 条目；本地上传种子没有 RSS 来源，创建任务后保持为空。</summary>
    public Guid? FeedItemId { get; set; }

    public FeedItem? FeedItem { get; set; }

    /// <summary>决策时的 Subject 快照，仅用于查询与展示；Subject 被删除后置空，快照字段不受影响。</summary>
    public int? SubjectId { get; set; }

    public MediaSubject? Subject { get; set; }

    /// <summary>规范化 info-hash（40 位小写十六进制），提交前提取，是响应丢失后对账的依据。</summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>.torrent 原始字节；重试沿用原决策，不依赖源地址仍可访问。</summary>
    public byte[] TorrentFile { get; set; } = [];

    /// <summary>远程种子地址；本地上传种子没有下载来源，保存为 upload://hash。</summary>
    public string? TorrentUrl { get; set; }

    /// <summary>提交时使用的下载器隔离保存路径（含 hash 短码），创建后不变；接管外部种子时对齐为真实路径。</summary>
    public string SavePath { get; set; } = string.Empty;

    /// <summary>来源站点标签（mikan/anibt/u2/byrbt/tjupt 等）：提交时作为下载器 tag 与命名 {source} 令牌；未知来源为空。</summary>
    public string? SiteSource { get; set; }

    /// <summary>命名快照：完成后按此改名，重试沿用原值。</summary>
    public string NamingSnapshot { get; set; } = string.Empty;

    public decimal?           Episode { get; set; }
    public int                Version { get; set; } = 1;
    public DownloadTaskStatus Status  { get; set; }

    public bool    IsUserPaused { get; set; }
    public int     AttemptCount { get; set; }
    public double? Progress     { get; set; }
    public long?   TotalSize    { get; set; }

    /// <summary>本任务完成后应标记为被替换的旧任务；无外键，仅记录替换关系。</summary>
    public Guid? ReplacesTaskId { get; set; }

    public Guid? ReplacedByTaskId { get; set; }

    public DateTimeOffset  CreatedAtUtc    { get; set; }
    public DateTimeOffset? SubmittedAtUtc  { get; set; }
    public DateTimeOffset? CompletedAtUtc  { get; set; }
    public DateTimeOffset? LastSyncedAtUtc { get; set; }

    /// <summary>失败步骤：取源 / 提交 / 认证 / 对账 / 改名 / 下载器。</summary>
    public string? FailedStep { get; set; }

    public string? Error { get; set; }
}
