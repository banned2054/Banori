using Banned.AniParser;
using Banned.AniParser.Models;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Banori.Features.Downloads;

/// <summary>
///     目标路径被其他任务、下载器种子或同种子内其他文件占用。这类失败可以安全回退——
///     推迟命名、以原始命名继续下载，完成时再替换；与身份校验失败（必须阻塞推进）区分开。
/// </summary>
public sealed class TargetPathOccupiedException(string message) : InvalidOperationException(message);

/// <summary>文件身份、人工覆盖与可恢复归档。所有远端操作通过会话执行，不访问下载器文件系统。</summary>
public sealed class DownloadFileService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    DownloadCoordinationGate                coordinationGate,
    IQbittorrentSessionFactory              sessionFactory,
    DownloadDirectorySettingsStore          directorySettings,
    NamingTemplateStore                     namingTemplateStore,
    DownloadFileClassificationPolicy        classificationPolicy,
    AniParser                               aniParser,
    ReleaseNamePreviewPolicy                namingPolicy,
    SubjectEpisodeCorrectionPolicy          episodeCorrectionPolicy,
    TimeProvider                            timeProvider) : IDownloadFileService
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ass", ".ssa", ".srt", ".sup" };

    private static readonly Regex SubtitleSuffix =
        new(@"(?<suffix>(?:\.(?:chs|cht|sc|tc|zh|zh-cn|zh-tw|chi|zho|gb|big5|jp|jpn|ja|en|eng|简|繁|简日|繁日|forced|default))+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var       lease = await coordinationGate.EnterAsync(cancellationToken);
        await using var db    = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task  = await LoadAsync(db, taskId, cancellationToken);
        if (task.Files.Count != 0) return View(task);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);

        return View(task);
    }

    public async Task<DownloadFilePreview> PreviewAsync(
        Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task    = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
        if (HasPending(task)) throw new InvalidOperationException("文件操作尚未确认，请等待对账后重试。");
        var file    = FindFile(task, index);
        var current = file.CurrentRelativePath;
        ApplyEdit(file, request);
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        var plan = Plan(task);
        await ValidatePlanAsync(db, session, task, plan, cancellationToken);
        return new DownloadFilePreview(Join(task.SavePath, current), Join(plan.Root, plan.Paths[index]), task.SavePath,
                                       plan.Root,
                                       !SameRoot(task.SavePath, plan.Root), !SameRoot(task.SavePath, plan.Root)
                                           ? "保存目录改变会移动整个种子；所有文件路径将一起安全转换，异步移动完成前不能再次编辑。"
                                           : null);
    }

    public async Task<DownloadTaskFilesView> ApplyAsync(
        Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task    = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
        if (HasPending(task) && !await ExecutePendingAsync(db, session, task, cancellationToken))
            throw new InvalidOperationException("下载器正在移动文件，请等待对账后再修改。");
        ApplyEdit(FindFile(task, index), request);
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        var plan = Plan(task);
        await ValidatePlanAsync(db, session, task, plan, cancellationToken);
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken); // Overrides and journal must survive before any remote side effect.
        await ExecutePendingAsync(db, session, task, cancellationToken);
        return View(task);
    }

    public async Task InitializeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
    }

    public async Task<bool> ArchiveCompletedAsync(
        Guid taskId, bool alreadyHoldingGate = false, CancellationToken cancellationToken = default)
    {
        using var       lease   = alreadyHoldingGate ? null : await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        var snapshot = await SnapshotAsync(session, (await ReadTaskAsync(taskId, cancellationToken)).InfoHash,
                                           cancellationToken);
        return await ArchiveCompletedUnderGateAsync(session, taskId, snapshot, cancellationToken);
    }

    /// <summary>调用方必须持有同一个 DownloadCoordinationGate。返回 false 表示尚在等待移动/路径确认，不能标记完成。</summary>
    public async Task<bool> ArchiveCompletedUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                           TorrentSnapshot     snapshot,
                                                           CancellationToken   cancellationToken = default)
    {
        if (snapshot.Progress < 1 || snapshot.IsMoving) return false;
        return await PlanAndExecuteUnderGateAsync(session, taskId, snapshot, cancellationToken);
    }

    /// <summary>
    ///     调用方必须持有同一个 DownloadCoordinationGate。下载中任务的对账首轮即执行命名计划：
    ///     文件在下载开始时就改到目标相对路径（qB 对未完成文件改名后继续下载），不等完成；
    ///     完成时的归档变为同一计划的幂等校验。返回 false 表示移动/确认未就绪，交由后续对账重试。
    /// </summary>
    public async Task<bool> SettleNamingUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                       TorrentSnapshot     snapshot,
                                                       CancellationToken   cancellationToken = default)
    {
        if (snapshot.IsMoving) return false;
        return await PlanAndExecuteUnderGateAsync(session, taskId, snapshot, cancellationToken);
    }

    /// <summary>接管同 hash 种子：用原始元数据身份关联 qB 的当前路径，分类和模板解析继续使用原名。</summary>
    public async Task<bool> AdoptExistingUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                        TorrentSnapshot     snapshot,
                                                        CancellationToken   cancellationToken = default)
    {
        if (snapshot.IsMoving) return false;
        return await PlanAndExecuteUnderGateAsync(session, taskId, snapshot, cancellationToken, true);
    }

    private async Task<bool> PlanAndExecuteUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                          TorrentSnapshot snapshot, CancellationToken cancellationToken,
                                                          bool adoptExisting = false)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken, adoptExisting);
        if (!string.Equals(task.InfoHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("下载器快照不属于当前任务。");
        if (HasPending(task)) return await ExecutePendingAsync(db, session, task, cancellationToken);
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        var plan = Plan(task);
        if (!await MoveConflictingReplacementFilesAsideAsync(db, session, task, plan, snapshot, cancellationToken))
            return false;
        await ValidatePlanAsync(db, session, task, plan, cancellationToken,
                                snapshot.Progress >= 1);
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken);
        return await ExecutePendingAsync(db, session, task, cancellationToken);
    }

    /// <summary>
    ///     新任务已完成且需要腾出目标路径时，先把承接对象文件移入隐藏保留目录：每集登记表中本任务
    ///     为待完成优胜的集，其已完成现任的现行文件；以及被本任务逐集承接取消选择的休眠部分数据。
    ///     这样旧 torrent 在替换完成前仍能做种、休眠数据可恢复；文件操作沿用现有持久化日志，进程
    ///     重启后可恢复。同路径同大小视为同一资源交叉做种，不迁让。下载中只预检路径，不移动旧成片。
    /// </summary>
    private async Task<bool> MoveConflictingReplacementFilesAsideAsync(ApplicationDbContext db,
                                                                       IQbittorrentSession session, DownloadTask task,
                                                                       FilePlan plan, TorrentSnapshot snapshot,
                                                                       CancellationToken ct)
    {
        if (snapshot.Progress < 1) return true;

        var asideCandidates = await LoadReplacementAsideFilesAsync(db, task, ct);
        if (asideCandidates.Count == 0) return true;

        var targets = plan.Paths.Values.SelectMany(path => new[] { Join(plan.Root, path), Join(task.SavePath, path) })
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var sizeByIndex = task.Files.ToDictionary(file => file.FileIndex, file => file.Size);
        foreach (var pair in plan.Paths)
        {
            targetSizes[Join(plan.Root, pair.Value)]     = sizeByIndex[pair.Key];
            targetSizes[Join(task.SavePath, pair.Value)] = sizeByIndex[pair.Key];
        }

        foreach (var ownerTaskId in asideCandidates.Select(item => item.Owner.Id).Distinct())
        {
            // 种子已不在下载器（外部移除或承接清理已执行）：无法借其迁让，占用校验按放行集合处理。
            var ownerSnapshot = (await session.FindTorrentsAsync([asideCandidates.First(item => item.Owner.Id == ownerTaskId)
                                                                      .Owner.InfoHash], ct)).SingleOrDefault();
            if (ownerSnapshot is null) continue;
            if (ownerSnapshot.IsMoving) return false;

            // 迁让对象的文件操作必须经 tracked 实体执行；先完成其未确认操作再规划保留位置。
            var owner = await db.DownloadTasks.Include(value => value.Files)
                                .SingleOrDefaultAsync(value => value.Id == ownerTaskId, ct);
            if (owner is null || owner.Files.Count == 0) continue;
            if (HasPending(owner) && !await ExecutePendingAsync(db, session, owner, ct)) return false;

            var candidateIndices = asideCandidates.Where(item => item.Owner.Id == ownerTaskId)
                                                  .Select(item => item.File.FileIndex)
                                                  .ToHashSet();
            var replacementPaths = owner.Files.ToDictionary(file => file.FileIndex, file => file.CurrentRelativePath);
            var filesToMoveAside = new HashSet<int>();
            foreach (var file in owner.Files.Where(file => candidateIndices.Contains(file.FileIndex)))
            {
                var occupied = Join(owner.SavePath, file.CurrentRelativePath);
                foreach (var target in targets)
                {
                    if (!PathsOverlap(target, occupied)) continue;
                    var sameFile = EqualPath(target, occupied)                           &&
                                   targetSizes.TryGetValue(target, out var expectedSize) && expectedSize == file.Size;
                    if (!sameFile) filesToMoveAside.Add(file.FileIndex);
                }
            }

            if (filesToMoveAside.Count == 0) continue;

            foreach (var file in owner.Files.Where(file => filesToMoveAside.Contains(file.FileIndex)))
            {
                var extension = Path.GetExtension(file.CurrentRelativePath);
                replacementPaths[file.FileIndex] = RelativeDownloadPathPolicy.Normalize(
                 $".replaced/{owner.Id:N}/{task.Id:N}-{file.FileIndex}{extension}");
            }

            var replacementPlan = new FilePlan(owner.SavePath, replacementPaths);
            await ValidatePlanAsync(db, session, owner, replacementPlan, ct);
            Stage(owner, replacementPlan);
            await db.SaveChangesAsync(ct);
            if (!await ExecutePendingAsync(db, session, owner, ct)) return false;
        }

        return true;
    }

    /// <summary>
    ///     本任务完成替换时的迁让与放行对象：每集登记表中本任务为待完成优胜的集，其现任视频文件
    ///     （现任所属任务须已完成且未被声明替换）；以及被本任务逐集承接取消选择的休眠文件
    ///     （所属任务任意状态——部分承接的合集仍在下载其余集）。
    /// </summary>
    private async Task<List<(DownloadTask Owner, DownloadTaskFile File)>> LoadReplacementAsideFilesAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken ct)
    {
        var result = new List<(DownloadTask, DownloadTaskFile)>();
        var myVideoIds = task.Files.Where(file => file.Kind == DownloadFileKind.Anime && file.IsSelectedForDownload)
                             .Select(file => file.Id)
                             .ToList();

        if (myVideoIds.Count > 0 && task.SubjectId is { } subjectId)
        {
            var incumbentFileIds = await db.EpisodeVersions.AsNoTracking()
                                           .Where(row => row.SubjectId == subjectId && row.PendingVideoFileId != null)
                                           .ToListAsync(ct);
            var wanted = incumbentFileIds
                        .Where(row => myVideoIds.Contains(row.PendingVideoFileId!.Value) &&
                                      row.IncumbentVideoFileId != null)
                        .Select(row => row.IncumbentVideoFileId!.Value)
                        .ToList();
            if (wanted.Count > 0)
            {
                var owners = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                                     .Where(owner => owner.Status           == DownloadTaskStatus.Completed &&
                                                     owner.ReplacedByTaskId == null                     &&
                                                     owner.Files.Any(file => wanted.Contains(file.Id)))
                                     .ToListAsync(ct);
                foreach (var owner in owners)
                    foreach (var file in owner.Files.Where(file => wanted.Contains(file.Id)))
                        result.Add((owner, file));
            }
        }

        var dormantOwners = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                                    .Where(owner => owner.Id != task.Id &&
                                                    owner.Files.Any(file => file.SupersededByTaskId == task.Id))
                                    .ToListAsync(ct);
        foreach (var owner in dormantOwners)
            foreach (var file in owner.Files.Where(file => file.SupersededByTaskId == task.Id &&
                                                           !file.IsSelectedForDownload))
                result.Add((owner, file));

        return result;
    }

    /// <summary>逐集承接的等路径放行集合：迁让对象的现行文件绝对路径（迁让由 MoveConflicting 执行，放行覆盖交叉做种与种子已移除的休眠数据）。</summary>
    private async Task<HashSet<string>> LoadReplacementAllowedPathsAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (owner, file) in await LoadReplacementAsideFilesAsync(db, task, ct))
            allowed.Add(Join(owner.SavePath, file.CurrentRelativePath));
        return allowed;
    }

    /// <summary>调用方必须持有同一个 DownloadCoordinationGate；仅恢复已持久化日志，不重新分类或规划。</summary>
    public async Task<bool> RecoverPendingUnderGateAsync(
        IQbittorrentSession session, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await LoadAsync(db, taskId, cancellationToken);
        return await ExecutePendingAsync(db, session, task, cancellationToken);
    }

    private async Task<DownloadTask> InitializeUnderGateAsync(ApplicationDbContext db,     IQbittorrentSession session,
                                                              Guid                 taskId, CancellationToken   ct,
                                                              bool                 adoptExisting = false)
    {
        var task = await LoadAsync(db, taskId, ct);
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile))
            throw new InvalidOperationException("种子原始元数据不存在或文件清单无效，不能伪造原始文件路径。");
        // Library enumeration (including zero-length files) can change. Match immutable originals, never its indices.
        var remaining =
            torrent.Files.ToDictionary(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile),
                                       StringComparer.Ordinal);
        var usedIndices = task.Files.Select(file => file.FileIndex).ToHashSet();
        if (usedIndices.Count != task.Files.Count)
            throw new InvalidOperationException("已保存文件内部键重复。");
        foreach (var file in task.Files)
        {
            var original = file.OriginalRelativePath.Replace('\\', '/');
            if (!remaining.Remove(original, out var entry) || file.Size != entry.Length)
                throw new InvalidOperationException("持久化原始文件身份与种子元数据不一致，拒绝改写。");
        }

        var nextIndex = 0;
        foreach (var (path, entry) in remaining)
        {
            while (usedIndices.Contains(nextIndex)) nextIndex++;
            var classified = classificationPolicy.Classify(path);
            var added = new DownloadTaskFile
            {
                Id                   = Guid.NewGuid(), DownloadTaskId = task.Id, FileIndex = nextIndex,
                OriginalRelativePath = path, CurrentRelativePath = path,
                Size                 = entry.Length, Kind = Kind(classified), ClassificationReason = classified.Reason,
                UpdatedAtUtc         = timeProvider.GetUtcNow()
            };
            usedIndices.Add(nextIndex++);
            task.Files.Add(added);
            db.Set<DownloadTaskFile>()
              .Add(added); // Explicit Added state: an assigned Guid must not be inferred as an existing row.
        }

        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        if (adoptExisting && !HasPending(task))
        {
            var indices = TorrentDownloadPolicy.GetQbittorrentFileIndices(task.TorrentFile, torrent, isMultiFile);
            var byIndex = remote.ToDictionary(file => file.Index);
            foreach (var file in task.Files)
            {
                var index = indices[file.OriginalRelativePath];
                if (!byIndex.TryGetValue(index, out var observed) || observed.Size != file.Size)
                    throw new InvalidOperationException("下载器文件 index/大小与原始种子不一致，无法接管。");
                file.CurrentRelativePath = observed.Path.Replace('\\', '/');
            }
        }

        MatchRemoteFiles(task.Files, remote, true);

        if (task.MediaDirectory is null || task.SeedDirectory is null)
        {
            var directories = await directorySettings.GetAsync(ct);
            task.MediaDirectory ??= directories.MediaDirectory;
            task.SeedDirectory  ??= directories.SeedDirectory;
        }

        // Legacy/adopted tasks have no known global base: retain their real root rather than guess a parent directory.
        task.DownloadRootPath ??= task.SavePath;
        RelativeDownloadPathPolicy.ValidateDirectories(task.MediaDirectory, task.SeedDirectory);
        await db.SaveChangesAsync(ct);
        return task;
    }

    private static IReadOnlyDictionary<int, TorrentFileSnapshot> MatchRemoteFiles(
        ICollection<DownloadTaskFile> files, IReadOnlyList<TorrentFileSnapshot> remote, bool allowPendingPaths = false)
    {
        if (files.Count == 0 || remote.Count != files.Count)
            throw new InvalidOperationException("下载器文件清单不完整或包含未登记文件，拒绝猜测身份。");
        var byPath = new Dictionary<string, TorrentFileSnapshot>(StringComparer.Ordinal);
        foreach (var observed in remote)
        {
            var path = observed.Path.Replace('\\', '/');
            if (!byPath.TryAdd(path, observed with { Path = path }))
                throw new InvalidOperationException("下载器文件清单包含重复的规范路径。");
        }

        var matched = new Dictionary<int, TorrentFileSnapshot>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (file.PendingRenameFrom is not null || file.PendingRenameTo is not null)
            {
                if (file.PendingRenameFrom is null || file.PendingRenameTo is null)
                    throw new InvalidOperationException("文件操作日志不完整。");
                if (allowPendingPaths)
                {
                    paths.Add(file.PendingRenameFrom.Replace('\\', '/'));
                    paths.Add(file.PendingRenameTo.Replace('\\', '/'));
                }
            }

            if (paths.Count == 0) paths.Add(file.CurrentRelativePath.Replace('\\', '/'));
            var candidates = paths
                            .Where(path => byPath.TryGetValue(path, out var observed) && observed.Size == file.Size)
                            .Select(path => byPath[path]).ToArray();
            if (candidates.Length != 1 || !claimed.Add(candidates[0].Path) ||
                !matched.TryAdd(file.FileIndex, candidates[0]))
                throw new InvalidOperationException("下载器文件路径/大小无法唯一对应持久化身份或改名日志，拒绝猜测。");
        }

        // Count plus unique claims makes this a bijection: extra entries (including unknown padding) are not ignored.
        return claimed.Count != remote.Count ? throw new InvalidOperationException("下载器文件清单包含无法对应的文件。") : matched;
    }

    private void ApplyEdit(DownloadTaskFile file, DownloadFileEditRequest request)
    {
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentException("无效文件分类。");
        if (request.Episode < 0 || request.EpisodeEnd < 0 ||
            (request.EpisodeEnd is not null && (request.Episode is null || request.EpisodeEnd < request.Episode)))
            throw new ArgumentException("集数范围无效。");
        if (request.ReidentifyOriginalName)
        {
            var classified = classificationPolicy.Classify(file.OriginalRelativePath);
            file.Kind                      = Kind(classified);
            file.ClassificationReason      = classified.Reason;
            file.IsManualOverride          = false;
            file.EpisodeNumberOverride     = null;
            file.EpisodeEndOverride        = null;
            file.AnimeRelativePathOverride = null;
        }
        else
        {
            file.Kind                  = request.Kind;
            file.IsManualOverride      = true;
            file.ClassificationReason  = "人工分类";
            file.EpisodeNumberOverride = request.Episode;
            file.EpisodeEndOverride    = request.EpisodeEnd;
            file.AnimeRelativePathOverride = string.IsNullOrWhiteSpace(request.AnimeRelativePath)
                ? null
                : RelativeDownloadPathPolicy.Normalize(request.AnimeRelativePath);
            if (file is { Kind: DownloadFileKind.Seed, AnimeRelativePathOverride: not null })
                throw new ArgumentException("做种文件不接受动画路径覆盖，必须使用原始完整路径。");
        }

        // Reidentify clears old overrides, but explicit values supplied with this request still win.
        if (request.ReidentifyOriginalName)
        {
            file.EpisodeNumberOverride = request.Episode;
            file.EpisodeEndOverride    = request.EpisodeEnd;
            file.AnimeRelativePathOverride = string.IsNullOrWhiteSpace(request.AnimeRelativePath)
                ? null
                : RelativeDownloadPathPolicy.Normalize(request.AnimeRelativePath);
            file.IsManualOverride     =  true;
            file.ClassificationReason += "；人工分类/覆盖优先";
        }

        file.Kind = request.Kind == DownloadFileKind.Anime &&
                    SubtitleExtensions.Contains(Path.GetExtension(file.OriginalRelativePath))
            ? DownloadFileKind.Subtitle
            : request.Kind;
        if (file is { Kind: DownloadFileKind.Seed, AnimeRelativePathOverride: not null })
            throw new ArgumentException("做种文件不接受动画路径覆盖。");
        file.UpdatedAtUtc = timeProvider.GetUtcNow();
    }

    private async Task EnsureNamingIngredientsAsync(DownloadTask task, CancellationToken ct)
    {
        if (TorrentNamingIngredients.FromJson(task.NamingIngredients) is not null ||
            task.Files.All(file => file.Kind == DownloadFileKind.Seed)) return;
        var subject = task.Subject;
        var title = !string.IsNullOrWhiteSpace(subject?.NameCn) ? subject.NameCn :
            !string.IsNullOrWhiteSpace(subject?.Name)           ? subject.Name : task.NamingSnapshot;
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("缺少可用作品名，不能按人工集数生成动画命名。");
        var episode = task.Episode ?? task.Files.Select(file => file.EpisodeNumberOverride)
                                          .FirstOrDefault(value => value is not null)
         ?? task.Files.Select(file => aniParser.Parse(FileName(file.OriginalRelativePath)))
                .Select(parsed => parsed?.Episode ?? parsed?.StartEpisode).FirstOrDefault(value => value is not null);
        if (episode is null) throw new InvalidOperationException("缺少命名要素和集数，请填写集数或补充任务命名快照。");
        task.NamingIngredients = new TorrentNamingIngredients(await namingTemplateStore.GetAsync(ct), title,
                                                              ReleaseNamePreviewPolicy.FormatEpisodeText(episode.Value),
                                                              task.Version, null, task.SiteSource,
                                                              FileTemplate : await namingTemplateStore
                                                                 .GetFileTemplateAsync(ct),
                                                              BangumiId : task.SubjectId, BangumiName : subject?.Name,
                                                              BangumiNameCn : subject?.NameCn,
                                                              BangumiPlatform : subject?.Platform,
                                                              BangumiDate : subject?.AirDate).ToJson();
    }

    private FilePlan Plan(DownloadTask task)
    {
        var basePath = Root(task.DownloadRootPath ?? task.SavePath);
        var media    = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory!);
        var seed     = RelativeDownloadPathPolicy.Normalize(task.SeedDirectory!);
        RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        var absolute    = new Dictionary<int, string>();
        foreach (var file in task.Files)
        {
            // 种子名称模板只命名种子，不产生保存目录层；媒体目录内的子目录结构只由文件模板中的 / 决定。
            var relative = file.Kind == DownloadFileKind.Seed
                ? seed  + "/" + file.OriginalRelativePath
                : media + "/" + (file.AnimeRelativePathOverride ?? ContentName(task, file, ingredients));
            absolute.Add(file.FileIndex, RelativeDownloadPathPolicy.Normalize(relative));
        }

        // Only retain an existing clean base/media root; never move an established mixed root back to clean.
        var cleanPrefix = media + "/";
        var clean = task.Files.All(file => file.Kind != DownloadFileKind.Seed) &&
                    SameRoot(task.SavePath, Join(basePath, media));
        var root = clean ? Join(basePath, media) : basePath;
        var paths = absolute.ToDictionary(pair => pair.Key,
                                          pair => clean ? pair.Value[cleanPrefix.Length..] : pair.Value);
        if (paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count ||
            paths.Values.Any(left =>
                                 paths.Values.Any(right =>
                                                      right.StartsWith(left + "/",
                                                                       StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("多个文件的目标路径冲突，请修改集数或手工动画路径。");
        return new FilePlan(root, paths);
    }

    private string ContentName(DownloadTask task, DownloadTaskFile file, TorrentNamingIngredients? ingredients)
    {
        var parsed = aniParser.Parse(FileName(file.OriginalRelativePath));
        var (episode, end) = Episodes(task, file, parsed);
        if (episode is null && file.AnimeRelativePathOverride is null)
            throw new InvalidOperationException("正片/字幕没有可用集数，请填写集数或手工动画相对路径。");
        var name = task.NamingSnapshot;
        if (ingredients is not null)
        {
            var text = ReleaseNamePreviewPolicy.FormatEpisodeText(episode!.Value);

            if (end is not null && end != episode) text += "-E" + ReleaseNamePreviewPolicy.FormatEpisodeText(end.Value);

            var input = ingredients with { EpisodeText = text };
            if (parsed is not null)
                input = input with
                {
                    Version = parsed.Version > 1 ? parsed.Version :
                    task.Version             > 1 ? task.Version : ingredients.Version,
                    AniMediaType = parsed.MediaType.ToString(), AniTitle = parsed.Title, AniGroup = parsed.Group,
                    AniGroupType = parsed.GroupType.ToString(), AniSubtitleType = parsed.SubtitleType.ToString(),
                    AniSource = parsed.Source.ToString(), AniWebSource = parsed.WebSource,
                    AniResolution = parsed.Resolution.ToString(), AniVideoCodec = parsed.VideoCodec,
                    AniAudioCodec = parsed.AudioCodec, AniColorBitDepth = parsed.ColorBitDepth,
                    Language = parsed.Language.ToString(), Resolution = parsed.Resolution.ToString()
                };
            name = namingPolicy.Create(input, true);
        }

        var originalName = FileName(file.OriginalRelativePath);
        var extension    = Path.GetExtension(originalName);
        var suffix = file.Kind == DownloadFileKind.Subtitle
            ? SubtitleSuffix.Match(Path.GetFileNameWithoutExtension(originalName)).Groups["suffix"].Value
            : string.Empty;
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];
        return RelativeDownloadPathPolicy.Normalize(name + suffix + extension);
    }

    private (decimal? Episode, decimal? End) Episodes(DownloadTask task, DownloadTaskFile file, ParseResult? parsed)
    {
        var raw     = parsed?.Episode ?? parsed?.StartEpisode;
        var episode = raw is null ? task.Episode : episodeCorrectionPolicy.Apply(task.SubjectId, raw.Value).Episode;
        decimal? end = parsed?.Episode is null && parsed?.EndEpisode is { } last
            ? episodeCorrectionPolicy.Apply(task.SubjectId, last).Episode
            : null;
        return file.EpisodeNumberOverride is not null
            ? (file.EpisodeNumberOverride, file.EpisodeEndOverride)
            : (episode, file.EpisodeEndOverride ?? end);
    }

    private async Task ValidatePlanAsync(ApplicationDbContext db,   IQbittorrentSession session, DownloadTask task,
                                         FilePlan             plan, CancellationToken   ct,
                                         bool                 allowCompletedReplacementCollision = false)
    {
        var snapshot = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving || !SameRoot(snapshot.SavePath, task.SavePath))
            throw new InvalidOperationException("下载器保存目录不一致或仍在移动，拒绝规划。");
        var remote  = await session.ListFilesAsync(task.InfoHash, ct);
        var matched = MatchRemoteFiles(task.Files, remote);
        foreach (var file in task.Files)
        {
            var target = plan.Paths[file.FileIndex];
            // Conservative: reject swaps/chains rather than rename into another file's current path.
            if (matched.Any(other => other.Key != file.FileIndex && EqualPath(other.Value.Path, target)))
                throw new TargetPathOccupiedException("目标路径占用另一文件当前路径，不能安全执行改名。");
        }

        await ValidateTargetOccupancyAsync(db, session, task, plan, remote, ct, allowCompletedReplacementCollision);
    }

    private async Task ValidateTargetOccupancyAsync(ApplicationDbContext db, IQbittorrentSession session,
                                                    DownloadTask task, FilePlan plan,
                                                    IReadOnlyList<TorrentFileSnapshot> remote, CancellationToken ct,
                                                    bool allowCompletedReplacementCollision = false)
    {
        var matched = MatchRemoteFiles(task.Files, remote, true);
        var entries = plan.Paths.ToList();
        foreach (var (index, path) in entries)
        {
            RelativeDownloadPathPolicy.Normalize(path);
            if (entries.Any(other => other.Key != index && PathsOverlap(path, other.Value)) ||
                matched.Any(other => other.Key != index && PathsOverlap(path, other.Value.Path)))
                throw new TargetPathOccupiedException("目标路径与同种子另一文件目标/当前路径相同或互为父子，不能安全执行。");
        }

        var targets = plan.Paths.Values.Select(path => Join(plan.Root, path))
                          .Concat(plan.Paths.Values.Select(path => Join(task.SavePath, path)))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var sizeByIndex = task.Files.ToDictionary(file => file.FileIndex, file => file.Size);
        foreach (var pair in plan.Paths)
        {
            targetSizes[Join(plan.Root, pair.Value)]     = sizeByIndex[pair.Key];
            targetSizes[Join(task.SavePath, pair.Value)] = sizeByIndex[pair.Key];
        }

        // 逐集承接的等路径放行：本任务为登记表待完成优胜的集，其已完成现任的现行文件路径
        // （同大小视为同一资源交叉做种，不同大小先经 MoveConflicting 迁让），以及被本任务承接
        // 取消选择的休眠文件路径（迁让同样由 MoveConflicting 负责，种子已移除时直接放行覆盖）。
        var replacementAllowedPaths = allowCompletedReplacementCollision
            ? await LoadReplacementAllowedPathsAsync(db, task, ct)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var others = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .Where(value => value.Id != task.Id).ToListAsync(ct);
        var managedTasksByHash = others.GroupBy(value => value.InfoHash, StringComparer.OrdinalIgnoreCase)
                                       .ToDictionary(group => group.Key, group => group.First(),
                                                     StringComparer.OrdinalIgnoreCase);
        foreach (var other in others)
        {
            var hasPendingFileOperation = other.PendingSavePath is not null ||
                                          other.Files.Any(file => file.PendingRenameFrom is not null ||
                                                                  file.PendingRenameTo is not null);
            if (other is { Status: DownloadTaskStatus.Replaced, CompletedAtUtc: null } &&
                !hasPendingFileOperation)
                continue;

            if (other.Files.Count == 0 && targets.Any(path => IsWithin(path, other.SavePath)))
                throw new TargetPathOccupiedException("目标目录可能被其他尚未初始化的任务占用。");
            foreach (var file in other.Files)
            {
                var occupied = new List<string> { Join(other.SavePath, file.CurrentRelativePath) };
                if (other.PendingSavePath is not null)
                    occupied.Add(Join(other.PendingSavePath, file.CurrentRelativePath));
                foreach (var pending in
                         new[] { file.PendingRenameFrom, file.PendingRenameTo }.Where(value => value is not null))
                {
                    occupied.Add(Join(other.SavePath, pending!));
                    if (other.PendingSavePath is not null) occupied.Add(Join(other.PendingSavePath, pending!));
                }

                foreach (var target in targets)
                foreach (var path in occupied)
                {
                    if (!PathsOverlap(target, path)) continue;
                    var matchingReplacementFile = allowCompletedReplacementCollision &&
                                                  replacementAllowedPaths.Contains(path) &&
                                                  !hasPendingFileOperation             &&
                                                  EqualPath(target, path);
                    if (matchingReplacementFile) continue;
                    var owner = string.IsNullOrWhiteSpace(other.NamingSnapshot)
                        ? other.Id.ToString()
                        : other.NamingSnapshot;
                    throw new
                        TargetPathOccupiedException($"目标文件路径“{target}”被下载任务“{owner}”（任务 ID {other.Id}，状态 {other.Status}）占用；该任务记录的文件路径为“{path}”。");
                }
            }
        }

        // Include unmanaged qB torrents and all tags, not just the task's source tag.
        // 外部（非本应用登记）种子路径不经过 RelativeDownloadPathPolicy.Normalize：文件名可能含
        // ':' 等本应用相对路径校验禁止的字符（校验器只约束本应用写入的路径），占用判定本身是
        // 纯字符串比较，按原样拼接参与重叠比对即可。个别根目录连 Root 都无法解析时跳过该种子。
        // 旧库种子沿用同一命名规则：与目标同路径且大小一致视为同一资源的交叉做种放行，
        // 大小不同仍可能是不同压制的真实覆盖，拒绝。
        foreach (var other in await session.ListTorrentsAsync(null, ct))
        {
            if (string.Equals(other.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)) continue;
            var managedTask = managedTasksByHash.GetValueOrDefault(other.Hash ?? string.Empty);
            try
            {
                var otherRoot = Root(other.SavePath);
                foreach (var file in await session.ListFilesAsync(other.Hash, ct))
                {
                    var occupied = otherRoot + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                foreach (var target in targets)
                {
                    if (!PathsOverlap(target, occupied)) continue;
                    // 逐集承接放行覆盖等路径的迁让对象（交叉做种或休眠数据），与数据库侧同一集合。
                    if (allowCompletedReplacementCollision && replacementAllowedPaths.Contains(occupied)) continue;
                    switch (managedTask)
                    {
                            case null when
                                targetSizes.TryGetValue(target, out var expected) &&
                                expected == file.Size :
                                continue;
                            case null :
                                throw new
                                    TargetPathOccupiedException($"目标文件路径“{target}”被下载器中的其他种子占用，种子文件路径为“{occupied}”。");
                            default :
                            {
                                var owner = string.IsNullOrWhiteSpace(managedTask.NamingSnapshot)
                                    ? managedTask.Id.ToString()
                                    : managedTask.NamingSnapshot;
                                throw new
                                    TargetPathOccupiedException($"目标文件路径“{target}”被下载任务“{owner}”（任务 ID {managedTask.Id}，状态 {managedTask.Status}）占用；该种子的文件路径为“{occupied}”。");
                            }
                        }
                    }
                }
            }
            catch (ArgumentException)
            {
                // 无法解析保存根的外部种子（不信用路径）跳过：其路径无法与规范化目标相等或互为前缀。
            }
        }
    }

    private void Stage(DownloadTask task, FilePlan plan)
    {
        task.PendingSavePath = plan.Root;
        foreach (var file in task.Files)
        {
            file.PendingRenameFrom = file.CurrentRelativePath;
            file.PendingRenameTo   = plan.Paths[file.FileIndex];
            file.UpdatedAtUtc      = timeProvider.GetUtcNow();
        }
    }

    private async Task<bool> ExecutePendingAsync(
        ApplicationDbContext db, IQbittorrentSession session, DownloadTask task, CancellationToken ct)
    {
        if (!HasPending(task)) return true;
        var targetRoot = task.PendingSavePath ?? throw new InvalidOperationException("待执行文件操作缺少保存目录。");
        var snapshot   = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving) return false;
        if (!SameRoot(snapshot.SavePath, task.SavePath) && !SameRoot(snapshot.SavePath, targetRoot))
            throw new InvalidOperationException("下载器被外部移动到未知目录，不能恢复操作。");
        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        // Validate every identity and reservation before executing even the first remaining side effect.
        if (task.Files.Any(file => file.PendingRenameFrom is null || file.PendingRenameTo is null))
            throw new InvalidOperationException("文件操作日志不完整。");
        var matched = MatchRemoteFiles(task.Files, remote, true);

        var pendingPlan =
            new FilePlan(targetRoot, task.Files.ToDictionary(file => file.FileIndex, file => file.PendingRenameTo!));
        await ValidateTargetOccupancyAsync(db, session, task, pendingPlan, remote, ct,
                                           snapshot.Progress >= 1);
        foreach (var file in task.Files.OrderBy(value => value.FileIndex))
        {
            var observed = matched[file.FileIndex];
            var from     = file.PendingRenameFrom!.Replace('\\', '/');
            var to       = RelativeDownloadPathPolicy.Normalize(file.PendingRenameTo!);
            if (observed.Path == to) continue; // Remote succeeded, DB acknowledgement was lost.
            await session.RenameTorrentFileAsync(task.InfoHash, from, to, ct);
        }

        remote  = await session.ListFilesAsync(task.InfoHash, ct);
        matched = MatchRemoteFiles(task.Files, remote, true);
        if (task.Files.Any(file => matched[file.FileIndex].Path !=
                                   RelativeDownloadPathPolicy.Normalize(file.PendingRenameTo!))) return false;
        await ValidateTargetOccupancyAsync(db, session, task, pendingPlan, remote, ct,
                                           snapshot.Progress >= 1);
        if (!SameRoot(snapshot.SavePath, targetRoot)) await session.SetLocationAsync(task.InfoHash, targetRoot, ct);
        snapshot = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving || !SameRoot(snapshot.SavePath, targetRoot)) return false;
        // Only publish paths after every remote rename and the asynchronous move have been observed.
        foreach (var file in task.Files)
        {
            file.CurrentRelativePath = file.PendingRenameTo!;
            file.PendingRenameFrom   = null;
            file.PendingRenameTo     = null;
            file.UpdatedAtUtc        = timeProvider.GetUtcNow();
        }

        task.SavePath        = targetRoot;
        task.PendingSavePath = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private DownloadTaskFilesView View(DownloadTask task) =>
        new(task.Id, task.SavePath,
            task.Files.OrderBy(file => file.FileIndex)
                .Select(file =>
                 {
                     var parsed = aniParser.Parse(FileName(file.OriginalRelativePath));
                     var (episode, end) = Episodes(task, file, parsed);
                     if (file.EpisodeNumberOverride is null &&
                         parsed?.Episode is null            &&
                         parsed?.StartEpisode is null)
                         episode = null; // Display identification honestly; task episode fallback is only for naming.
                     return new DownloadFileView(file.FileIndex, file.OriginalRelativePath, file.CurrentRelativePath,
                                                 file.Kind, file.ClassificationReason, episode, end,
                                                 file.AnimeRelativePathOverride);
                 }).ToList());

    private static DownloadFileKind Kind(DownloadFileClassification classification) =>
        classification.Kind == Core.Downloads.DownloadFileKind.Seed
            ? DownloadFileKind.Seed
            : SubtitleExtensions.Contains(Path.GetExtension(classification.Path))
                ? DownloadFileKind.Subtitle
                : DownloadFileKind.Anime;

    private static DownloadTaskFile FindFile(DownloadTask task, int index) =>
        task.Files.SingleOrDefault(file => file.FileIndex == index) ??
        throw new KeyNotFoundException("找不到指定文件 index。");

    private static bool HasPending(DownloadTask task) => task.PendingSavePath is not null ||
                                                         task.Files.Any(file => file.PendingRenameTo is not null);

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Root(string path)
    {
        var root = DownloadPathNamePolicy.NormalizePath(path);
        if (root.Length == 0 || root.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("下载根目录无效。");
        return root;
    }

    private static string Join(string root, string relative) =>
        Root(root) + "/" + relative.Replace('\\', '/');

    private static bool SameRoot(string left, string right) =>
        string.Equals(Root(left), Root(right), StringComparison.Ordinal);

    private static bool EqualPath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool PathsOverlap(string left, string right) =>
        EqualPath(left, right)                                           ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(Root(root) + "/", StringComparison.OrdinalIgnoreCase);

    private static async Task<TorrentSnapshot> SnapshotAsync(
        IQbittorrentSession session, string hash, CancellationToken ct) =>
        (await session.FindTorrentsAsync([hash], ct)).SingleOrDefault() ??
        throw new InvalidOperationException("下载器中不存在该种子。");

    private static async Task<DownloadTask> LoadAsync(ApplicationDbContext db, Guid id, CancellationToken ct) =>
        await db.DownloadTasks.Include(task => task.Files).Include(task => task.Subject)
                .SingleOrDefaultAsync(task => task.Id == id, ct) ?? throw new KeyNotFoundException("找不到指定下载任务。");

    private async Task<DownloadTask> ReadTaskAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await LoadAsync(db, id, ct);
    }

    private sealed record FilePlan(string Root, IReadOnlyDictionary<int, string> Paths);
}
