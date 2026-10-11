using MediaManager.Core.Downloads;
using MediaManager.Core.Rules;
using MediaManager.Core.Torrents;
using MediaManager.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Downloads;

/// <summary>
///     新布局提交前的只读路径门禁。调用方须持有 DownloadCoordinationGate，并先处理同 hash 接管。
///     占用比对只覆盖数据库登记的其他任务；下载器中未经本应用登记的种子不参与比对，也绝不因此访问下载器。
/// </summary>
public sealed class DownloadSubmissionPathValidator(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    public async Task ValidateAsync(DownloadTask task, CancellationToken ct = default)
    {
        // Unknown legacy layouts retain their existing submission/adoption flow.
        if (string.IsNullOrWhiteSpace(task.DownloadRootPath)) return;
        ct.ThrowIfCancellationRequested();
        if (!TorrentFileManifestParser.TryParse(task.TorrentFile, out var manifest))
            throw new InvalidOperationException("提交路径检查失败：种子原始文件清单无效或包含重复/不安全路径。");

        var targets = manifest.Files.Select(file => Join(task.SavePath, file.Path)).ToArray();
        for (var index = 0; index < targets.Length; index++)
            foreach (var other in targets.Take(index))
                if (Conflicts(targets[index], other))
                    throw new InvalidOperationException($"提交路径冲突：同一种子的文件“{targets[index]}”与“{other}”重复或存在文件/目录父子冲突。");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        // 先按保存根是否相交预筛，只加载相交任务的完整记录；不相交任务的路径永远碰不到本次目标，无需加载或比较。
        var probe = await db.DownloadTasks.AsNoTracking()
                            .Where(value => value.Id != task.Id)
                            .Select(value => new
                            {
                                value.Id, value.InfoHash, value.Status, value.SavePath, value.PendingSavePath
                            })
                            .ToListAsync(ct);
        var root = Root(task.SavePath);
        var ids  = probe.Where(value => !SameHash(value.InfoHash, task.InfoHash))
                        .Where(value => RootsOverlap(root, value.SavePath, value.PendingSavePath))
                        .Select(value => value.Id)
                        .ToList();
        if (ids.Count == 0) return;

        var tasks = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                            .Where(value => ids.Contains(value.Id)).ToListAsync(ct);
        foreach (var other in tasks)
        {
            ct.ThrowIfCancellationRequested();
            var source = $"数据库下载任务 {other.Id}（{other.InfoHash}）";
            var roots  = new[] { other.SavePath, other.PendingSavePath }
                        .Where(value => value is not null).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
            if (other.Files.Count != 0)
            {
                foreach (var file in other.Files)
                {
                    // Cover current, journal source and destination at both roots. In particular,
                    // rename succeeds BEFORE move, so old-root/new-relative is already occupied.
                    var paths = new[] { file.CurrentRelativePath, file.PendingRenameFrom, file.PendingRenameTo }
                               .Where(path => path is not null).Select(path => path!)
                               .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths)
                    foreach (var candidate in roots)
                        CheckFile(targets, Join(candidate, path), source);
                }

                continue;
            }

            if (TorrentFileManifestParser.TryParse(other.TorrentFile, out var original))
                foreach (var file in original.Files)
                foreach (var candidate in roots)
                    CheckFile(targets, Join(candidate, file.Path), source + "（原始清单可能占用）");
            else if (other.Status.IsActive() || other.PendingSavePath is not null)
                foreach (var candidate in roots)
                    CheckScope(targets, candidate, source + "（文件清单未知）");
            // A terminal legacy row without files or a valid manifest is not evidence
            // that every file below its shared base is occupied forever.
        }
    }

    /// <summary>判断其他任务的任一保存根与新任务保存根是否可能 共享路径树；无法规范化的根按相交保守处理。</summary>
    private static bool RootsOverlap(string root, string? savePath, string? pendingSavePath)
    {
        foreach (var raw in new[] { savePath, pendingSavePath })
        {
            if (raw is null) continue;
            if (!TryRoot(raw, out var otherRoot)) return true;
            if (root == "/" || otherRoot == "/" || Conflicts(root, otherRoot)) return true;
        }

        return false;
    }

    private static bool TryRoot(string path, out string root)
    {
        try
        {
            root = Root(path);
            return true;
        }
        catch (InvalidOperationException)
        {
            root = string.Empty;
            return false;
        }
    }

    private static bool SameHash(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Conflicts(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)   ||
               left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
               right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckFile(IEnumerable<string> targets, string occupied, string source)
    {
        foreach (var target in targets)
            if (Conflicts(target, occupied))
                throw new InvalidOperationException($"提交路径冲突：原始文件“{target}”与 {source} 的路径“{occupied}”相同或存在文件/目录父子冲突。");
    }

    private static void CheckScope(IEnumerable<string> targets, string root, string source)
    {
        var normalized = Root(root);
        foreach (var target in targets)
            if (normalized == "/" || Conflicts(target, normalized))
                throw new
                    InvalidOperationException($"提交路径无法确认安全：原始文件“{target}”可能与 {source} 的目录范围“{normalized}”冲突，请先核对文件清单。");
    }

    private static string Join(string root, string relative)
    {
        try
        {
            return DownloadPathNamePolicy.JoinSavePath(Root(root), RelativeDownloadPathPolicy.Normalize(relative));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"提交路径检查失败：不安全的相对文件路径“{relative}”。", exception);
        }
    }

    private static string Root(string path)
    {
        var root = DownloadPathNamePolicy.NormalizePath(path);
        if (root.Length == 0 && path.Trim().Replace('\\', '/') == "/") return "/";
        if (root.Length == 0         || root.Split('/').Any(segment => segment is "." or "..") ||
            root.Any(char.IsControl) || root.TrimStart('/').Contains("//", StringComparison.Ordinal))
            throw new InvalidOperationException($"提交路径检查失败：下载保存目录“{path}”无效。");
        return root;
    }
}
