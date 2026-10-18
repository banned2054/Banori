using Banori.Core.Rules;
using Banori.Data;

namespace Banori.Features.Downloads;

/// <summary>
///     替换任务的暂存路径：普通新下载直接使用正式根；保护旧版或隔离校验失败的共享数据时，
///     才在下载根下的 .tasks/{taskId} 下载，完成后由同一归档链迁入正式位置。
/// </summary>
public static class DownloadTaskPlacement
{
    /// <summary>任务专属目录名；隐藏命名空间前缀，库扫描不应将其视为正式布局。</summary>
    public const string TaskScopeDirectoryName = ".tasks";

    /// <summary>任务专属保存根：{下载根}/.tasks/{taskId:N}。taskId 唯一保证初始布局不可能与他人冲突。</summary>
    public static string TaskRoot(string downloadRootPath, Guid taskId) =>
        DownloadPathNamePolicy.JoinSavePath(downloadRootPath.Trim().TrimEnd('/', '\\'),
                                            $"{TaskScopeDirectoryName}/{taskId:N}");

    /// <summary>任务当前是否仍处于任务专属根（未归档迁出）；DownloadRootPath 缺失的存量任务恒为 false。</summary>
    public static bool IsTaskScoped(DownloadTask task)
    {
        if (string.IsNullOrWhiteSpace(task.DownloadRootPath)) return false;
        return string.Equals(DownloadPathNamePolicy.NormalizePath(task.SavePath),
                             DownloadPathNamePolicy.NormalizePath(TaskRoot(task.DownloadRootPath, task.Id)),
                             StringComparison.Ordinal);
    }
}
