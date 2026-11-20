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

    /// <summary>
    ///     任务是否位于本应用提交的正式根：普通全局根，或纯正片任务直接落地的媒体目录根。
    ///     两者都可能被后续替换关系要求迁入任务专属根；接管到任意其他目录的种子不算。
    /// </summary>
    public static bool IsFormalRoot(DownloadTask task)
    {
        if (string.IsNullOrWhiteSpace(task.DownloadRootPath)) return false;
        var baseRoot = DownloadPathNamePolicy.NormalizePath(task.DownloadRootPath);
        var savePath = DownloadPathNamePolicy.NormalizePath(task.SavePath);
        return string.Equals(savePath, baseRoot, StringComparison.Ordinal) ||
               (!string.IsNullOrWhiteSpace(task.MediaDirectory) &&
                string.Equals(savePath,
                              DownloadPathNamePolicy.JoinSavePath(baseRoot, task.MediaDirectory),
                              StringComparison.Ordinal));
    }

    /// <summary>任务当前是否仍处于任务专属根（未归档迁出）；DownloadRootPath 缺失的存量任务恒为 false。</summary>
    public static bool IsTaskScoped(DownloadTask task)
    {
        if (string.IsNullOrWhiteSpace(task.DownloadRootPath)) return false;
        return string.Equals(DownloadPathNamePolicy.NormalizePath(task.SavePath),
                             DownloadPathNamePolicy.NormalizePath(TaskRoot(task.DownloadRootPath, task.Id)),
                             StringComparison.Ordinal);
    }
}
