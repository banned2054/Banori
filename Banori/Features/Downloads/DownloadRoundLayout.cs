using Banori.Core.Rules;
using Banori.Data;
using System.Text.Json;

namespace Banori.Features.Downloads;

/// <summary>
///     两套分别持久化的布局：已发布成片（<see cref="DownloadTask.SavePath" /> + 逐文件已发布路径）与本轮
///     下载器布局（<see cref="DownloadTask.PendingSubmitSavePath" /> + <see cref="DownloadTaskFile.CurrentRelativePath" />）。
///     Add 确认、受控映射、同 hash 接管、隔离重加和下载期改名只推进后者；只有新位置确认后才发布完整路径。
/// </summary>
internal static class DownloadRoundLayout
{
    private static readonly JsonSerializerOptions AddRootsJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsPending(DownloadTask task) =>
        !string.IsNullOrWhiteSpace(task.PendingSubmitSavePath);

    /// <summary>
    ///     所有可能仍被下载器写入的保存根：当前布局根加上每次 Add 前登记的实际写入根。
    ///     兼容尚未写入台账的存量行，始终并入 PendingSubmitSavePath。
    /// </summary>
    public static IReadOnlyList<string> PendingAddRoots(DownloadTask task) =>
        PendingAddRoots(task.PendingSubmitSavePath, task.UnconfirmedAddSavePaths);

    public static IReadOnlyList<string> PendingAddRoots(
        string? pendingSubmitSavePath, string? unconfirmedAddSavePaths)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(pendingSubmitSavePath))
            roots.Add(Normalize(pendingSubmitSavePath!));
        roots.AddRange(TryRecordedRoots(unconfirmedAddSavePaths) ?? []);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>该根在本次请求之前是否已有未决占用：同根补发被拒时不能否定更早的那次请求。</summary>
    public static bool HasPendingAddRoot(DownloadTask task, string root)
    {
        var target = Normalize(root);
        return PendingAddRoots(task)
           .Any(value => string.Equals(value, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     撤销一次被明确拒绝的请求所写的根。调用方须先确认该根在本次请求前没有未决占用：
    ///     否则更早的请求可能仍在该根生效，占用必须保留。
    /// </summary>
    public static void RevokePendingAddRoot(DownloadTask task, string root)
    {
        // 台账损坏时无法证明该根是本次新增，保守保留原占用。
        var recorded = TryRecordedRoots(task.UnconfirmedAddSavePaths);
        if (recorded is null) return;
        var target = Normalize(root);
        if (IsPending(task) && string.Equals(Normalize(task.PendingSubmitSavePath!), target,
                                             StringComparison.OrdinalIgnoreCase))
            task.PendingSubmitSavePath = null;
        var remaining = recorded.Where(value => !string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        task.UnconfirmedAddSavePaths = remaining.Length == 0
            ? null
            : JsonSerializer.Serialize(remaining, AddRootsJsonOptions);
    }

    /// <summary>Add 发出前登记实际写入根；响应丢失与跨进程恢复都从这份台账恢复占用。</summary>
    public static void RegisterPendingAdd(DownloadTask task, string root)
    {
        var roots = PendingAddRoots(task).ToList();
        roots.Add(Normalize(root));
        task.UnconfirmedAddSavePaths =
            JsonSerializer.Serialize(roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), AddRootsJsonOptions);
    }

    /// <summary>只有确认接管或明确拒绝后才清除台账；当前布局根仍按 PendingSubmitSavePath 解释。</summary>
    public static void ClearPendingAdds(DownloadTask task) => task.UnconfirmedAddSavePaths = null;

    /// <summary>台账中显式登记的根；台账损坏返回 null，由调用方按无法判断处理。</summary>
    private static IReadOnlyList<string>? TryRecordedRoots(string? unconfirmedAddSavePaths)
    {
        if (string.IsNullOrWhiteSpace(unconfirmedAddSavePaths)) return [];
        try
        {
            var recorded = JsonSerializer.Deserialize<string[]>(unconfirmedAddSavePaths!, AddRootsJsonOptions);
            return recorded?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalize).ToArray() ?? [];
        }
        catch (JsonException)
        {
            // 损坏台账按当前布局根保守处理，不让解析失败悄悄释放占用。
            return null;
        }
    }

    /// <summary>下载器本轮的保存根：未发布时取待确认提交根，已发布后取成片根。</summary>
    public static string Root(DownloadTask task) =>
        IsPending(task) ? Normalize(task.PendingSubmitSavePath!) : task.SavePath;

    /// <summary>本轮文件在下载器中的当前完整路径。</summary>
    public static string CurrentAbsolute(DownloadTask task, DownloadTaskFile file) =>
        Join(Root(task), file.CurrentRelativePath);

    /// <summary>本轮文件相对当前根的路径。</summary>
    public static string CurrentRelative(DownloadTask task, DownloadTaskFile file) => file.CurrentRelativePath;

    /// <summary>已发布成片的相对路径；空值回退当前路径，兼容尚无两份布局的存量行。</summary>
    public static string PublishedRelative(DownloadTaskFile file) =>
        string.IsNullOrWhiteSpace(file.PublishedRelativePath)
            ? file.CurrentRelativePath
            : file.PublishedRelativePath!;

    /// <summary>
    ///     已发布成片的完整路径：部分复用期间新文件与旧成片可能不在同一根，逐文件独立保留旧位置。
    /// </summary>
    public static string PublishedAbsolute(DownloadTask task, DownloadTaskFile file) =>
        string.IsNullOrWhiteSpace(file.PublishedPath)
            ? Join(task.SavePath, PublishedRelative(file))
            : Normalize(file.PublishedPath!);

    /// <summary>新一轮开始前固化已发布布局；已有快照不得被下一轮下载器路径覆盖。</summary>
    public static void CapturePublished(DownloadTask task, IEnumerable<DownloadTaskFile> files)
    {
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.PublishedPath))
                file.PublishedPath = Join(task.SavePath, PublishedRelative(file));
            file.PublishedRelativePath ??= file.CurrentRelativePath;
        }
    }

    /// <summary>新一轮在下载器中的初始布局：种子原始文件名落在待确认根下。</summary>
    public static void StartRound(DownloadTask task, IEnumerable<DownloadTaskFile> files)
    {
        CapturePublished(task, files);
        foreach (var file in files)
        {
            file.CurrentRelativePath = file.OriginalRelativePath;
            file.PendingRenameFrom   = null;
            file.PendingRenameTo     = null;
        }
    }

    /// <summary>单个文件的新位置已确认：只发布该文件的完整路径。</summary>
    public static void PublishFile(DownloadTask task, DownloadTaskFile file)
    {
        file.PublishedPath         = CurrentAbsolute(task, file);
        file.PublishedRelativePath = file.CurrentRelativePath;
    }

    /// <summary>整轮已确认：先发布逐文件路径，再把本轮根落为成片根。</summary>
    public static void Publish(DownloadTask task, IEnumerable<DownloadTaskFile> files, string? publishedRoot = null)
    {
        var root = publishedRoot ?? Root(task);
        foreach (var file in files)
        {
            file.PublishedPath         = Join(root, file.CurrentRelativePath);
            file.PublishedRelativePath = file.CurrentRelativePath;
        }

        PublishRoot(task, root);
    }

    /// <summary>只发布本轮根：尚未确认的文件继续沿已发布完整路径指向旧成片。</summary>
    public static void PublishRoot(DownloadTask task, string? publishedRoot = null)
    {
        task.SavePath              = publishedRoot ?? Root(task);
        task.PendingSubmitSavePath = null;
        ClearPendingAdds(task);
    }

    /// <summary>
    ///     一个文件所有可能实际占用的完整路径：已发布成片、已登记的下载器位置，以及待确认根下
    ///     种子原始文件名和改名日志两端。提交门禁、复用、归档与迁让共用这份依据。
    /// </summary>
    public static IEnumerable<string> OccupiedPaths(DownloadTask task, DownloadTaskFile file)
    {
        var paths = new List<string>
        {
            PublishedAbsolute(task, file),
            Join(task.SavePath, CurrentRelative(task, file))
        };
        var pending = new[] { file.PendingRenameFrom, file.PendingRenameTo }
                     .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
        foreach (var root in new[] { task.PendingSavePath }
                            .Concat(PendingAddRoots(task))
                            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(Join(root, PublishedRelative(file)));
            paths.Add(Join(root, CurrentRelative(task, file)));
            paths.Add(Join(root, file.OriginalRelativePath));
            foreach (var relative in pending) paths.Add(Join(root, relative));
        }

        foreach (var relative in pending) paths.Add(Join(task.SavePath, relative));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => DownloadPathNamePolicy.NormalizePath(path);

    private static string Join(string root, string relative) =>
        Normalize(root.TrimEnd('/') + "/" + relative.Replace('\\', '/').TrimStart('/'));
}
