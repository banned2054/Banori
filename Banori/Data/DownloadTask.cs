using Banori.Features.Downloads;

namespace Banori.Data;

/// <summary>
///     持久化的下载意图：保存执行所需的决策快照（命名、集数、版本、种子字节）与恢复所需的状态。
///     多个条目可能指向同一资源，Subject + Episode 不构成唯一键；实际文件归档需检查目标路径冲突。
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

    /// <summary>下载器当前保存根目录；接管与已确认的归档移动会更新该路径。</summary>
    public string SavePath { get; set; } = string.Empty;

    public string? DownloadRootPath { get; set; }
    public string? MediaDirectory   { get; set; }
    public string? SeedDirectory    { get; set; }

    /// <summary>已发起但尚未确认完成的下载器保存位置变更。</summary>
    public string? PendingSavePath { get; set; }

    public ICollection<DownloadTaskFile> Files { get; set; } = [];

    /// <summary>来源站点标签（mikan/anibt/u2/byrbt/tjupt 等）：提交时作为下载器 tag 与命名 {source} 令牌；未知来源为空。</summary>
    public string? SiteSource { get; set; }

    /// <summary>命名快照：完成后按此改名，重试沿用原值。</summary>
    public string NamingSnapshot { get; set; } = string.Empty;

    /// <summary>命名要素 JSON：快照的模板与令牌值；完成改名时按每个内容文件的集数重渲染，缺失时回退快照。</summary>
    public string? NamingIngredients { get; set; }

    public decimal?           Episode { get; set; }
    public int                Version { get; set; } = 1;
    public DownloadTaskStatus Status  { get; set; }

    /// <summary>人工暂停（页面或下载器内暂停），对账绝不自动恢复；与系统准备暂停分开保存。</summary>
    public bool IsUserPaused { get; set; }

    /// <summary>系统以停止状态添加且准备流程（文件选择、命名）未完成；只有系统能在准备完成后清除并恢复下载。</summary>
    public bool IsSystemPaused { get; set; }

    /// <summary>停止添加后的准备阶段；用于解释当前推进位置，实际推进以下载器真实状态比对为准。</summary>
    public DownloadPreparationStage PreparationStage { get; set; }

    public int     AttemptCount { get; set; }
    public double? Progress     { get; set; }
    public long?   TotalSize    { get; set; }

    /// <summary>文件选择计划中选中文件的总大小；存在跳过文件时 Progress 记录的是所选内容进度。</summary>
    public long? SelectedTotalBytes { get; set; }

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
