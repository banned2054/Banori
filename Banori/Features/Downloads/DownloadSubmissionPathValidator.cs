using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Torrents;
using Banori.Data;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

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
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile))
            throw new InvalidOperationException("提交路径检查失败：种子原始文件清单无效或不包含受支持的 v1 info-hash。");

        var targets = torrent.Files
                             .Select(file => Join(task.SavePath,
                                                  TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile)))
                             .ToArray();
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
                                 value.Id, value.InfoHash, value.Status, value.SavePath, value.PendingSavePath,
                                 value.PendingSubmitSavePath, value.UnconfirmedAddSavePaths,
                                 HasPublishedPath = value.Files.Any(file => file.PublishedPath != null)
                             })
                            .ToListAsync(ct);
        var root = Root(task.SavePath);
        var ids = probe.Where(value => !SameHash(value.InfoHash, task.InfoHash))
                       .Where(value => value.HasPublishedPath ||
                                       RootsOverlap(root, value.SavePath, value.PendingSavePath,
                                                    value.PendingSubmitSavePath, value.UnconfirmedAddSavePaths))
                       .Select(value => value.Id)
                       .ToList();
        if (ids.Count == 0) return;

        var tasks = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                            .Where(value => ids.Contains(value.Id)).ToListAsync(ct);
        foreach (var other in tasks)
        {
            ct.ThrowIfCancellationRequested();
            var source = $"数据库下载任务 {other.Id}（{other.InfoHash}）";
            // 待确认提交根同样可能已被下载器接受：查询未知期间它尚未出现在 SavePath，
            // 但原始文件已可能占用该根下的路径，必须一并比对，不能让第二个不同 hash 任务放行。
            var unconfirmedRoots =
                DownloadRoundLayout.PendingAddRoots(other.PendingSubmitSavePath, other.UnconfirmedAddSavePaths);
            var roots = new[] { other.SavePath, other.PendingSavePath }
                       .Concat(unconfirmedRoots)
                       .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToArray();
            var pendingRoots = new[] { other.PendingSavePath }
                              .Concat(unconfirmedRoots)
                              .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToArray();
            if (other.Files.Count != 0)
            {
                foreach (var file in other.Files)
                {
                    // 已发布成片的完整路径可能在另一个根上；待确认根下 qB 写入的是原始文件名，
                    // 不能等 CurrentRelativePath 对齐后才认为该路径被占用。
                    CheckFile(targets, DownloadRoundLayout.PublishedAbsolute(other, file), source);
                    // Cover current, published and journal paths at both roots. In particular,
                    // rename succeeds BEFORE move, so old-root/new-relative is already occupied.
                    var paths = new[]
                                {
                                    file.CurrentRelativePath, DownloadRoundLayout.PublishedRelative(file),
                                    file.PendingRenameFrom, file.PendingRenameTo
                                }
                               .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
                               .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths) CheckRelative(other, path, roots, targets, source);
                    foreach (var path in new[] { file.OriginalRelativePath }.Distinct(StringComparer.OrdinalIgnoreCase))
                        CheckRelative(other, path, pendingRoots, targets, source);
                }

                continue;
            }

            if (TorrentDownloadPolicy.TryLoad(other.TorrentFile, out var original, out var originalIsMultiFile))
                foreach (var file in original.Files)
                foreach (var candidate in roots)
                    CheckFile(targets, Join(candidate,
                                            TorrentDownloadPolicy.GetRelativePath(original, file, originalIsMultiFile)),
                              source + "（原始清单可能占用）");
            else if (other.Status.IsActive() || other.PendingSavePath is not null)
                foreach (var candidate in roots)
                    CheckScope(targets, candidate, source + "（文件清单未知）");
            // A terminal legacy row without files or a valid manifest is not evidence
            // that every file below its shared base is occupied forever.
        }
    }

    /// <summary>判断其他任务的任一保存根与新任务保存根是否可能 共享路径树；无法规范化的根按相交保守处理。</summary>
    private static bool RootsOverlap(
        string  root, string? savePath, string? pendingSavePath, string? pendingSubmitSavePath,
        string? unconfirmedAddSavePaths)
    {
        var roots = new[] { savePath, pendingSavePath }
           .Concat(DownloadRoundLayout.PendingAddRoots(pendingSubmitSavePath, unconfirmedAddSavePaths));
        foreach (var raw in roots)
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

    private static bool SameHash(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Conflicts(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)   ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static void CheckRelative(
        DownloadTask other, string path, IEnumerable<string> roots, IEnumerable<string> targets, string source)
    {
        // 坏记录隔离只适用于终态且没有挂起转移的任务，并且必须能证明其文件落在
        // 本次目标之外；解析失败或仍可能占用目标时一律 fail-closed。
        if (!IsSafeRelativePath(path) && UnsafePathIsDisjoint(other, path, targets)) return;
        foreach (var candidate in roots) CheckFile(targets, Join(candidate, path), source);
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

    private static bool IsSafeRelativePath(string path)
    {
        try
        {
            RelativeDownloadPathPolicy.Normalize(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     终态旧记录的错误路径只有在能沿保存根解析到目标之外时才可跳过；解析失败或仍与
    ///     任一目标相交时无法排除占用，必须继续挡住提交。
    /// </summary>
    private static bool UnsafePathIsDisjoint(DownloadTask other, string path, IEnumerable<string> targets)
    {
        if (other.Status.IsActive()                                   || other.PendingSavePath is not null ||
            other.PendingSubmitSavePath is not null                   ||
            !string.IsNullOrWhiteSpace(other.UnconfirmedAddSavePaths) ||
            other.Files.Any(file => file.PendingRenameFrom is not null || file.PendingRenameTo is not null))
            return false;

        return TryResolveUnsafeAbsolute(other.SavePath, path, out var absolute) &&
               !targets.Any(target => Conflicts(target, absolute));
    }

    /// <summary>尽力按字符串解析不可信相对路径；含无法判断的字符或越出根时返回 false，不访问下载器或磁盘。</summary>
    private static bool TryResolveUnsafeAbsolute(string root, string relative, out string absolute)
    {
        absolute = string.Empty;
        var raw = relative.Trim().Replace('\\', '/');
        if (raw.Length is 0 or > RelativeDownloadPathPolicy.MaxLength ||
            raw.Any(char.IsControl) || raw.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            return false;

        var combined = raw.StartsWith('/') ? raw : DownloadPathNamePolicy.NormalizePath(root) + "/" + raw;
        // 网络共享根以 // 开头；解析结果必须保留同样的前缀，才能与按 Root 拼出的目标路径正确比较。
        var prefix   = combined.StartsWith("//", StringComparison.Ordinal) ? "//" : "/";
        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
            switch (segment)
            {
                case "" or "." : break;
                case ".." :
                    if (segments.Count == 0) return false;
                    segments.RemoveAt(segments.Count - 1);
                    break;
                default :
                    if (segment != segment.Trim() || segment.Contains(':')) return false;
                    segments.Add(segment);
                    break;
            }

        if (segments.Count == 0) return false;
        absolute = prefix + string.Join('/', segments);
        return true;
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
