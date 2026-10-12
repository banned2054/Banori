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

    private async Task<bool> PlanAndExecuteUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                          TorrentSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
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
    ///     新任务已完成且需要覆盖已完成替换任务的不同大小文件时，先把旧文件移入隐藏备份目录。
    ///     这样旧 torrent 在替换完成前仍能做种；文件操作沿用现有持久化日志，进程重启后可恢复。
    ///     下载中只预检路径，不移动旧成片。
    /// </summary>
    private async Task<bool> MoveConflictingReplacementFilesAsideAsync(ApplicationDbContext db,
                                                                       IQbittorrentSession session, DownloadTask task,
                                                                       FilePlan plan, TorrentSnapshot snapshot,
                                                                       CancellationToken ct)
    {
        if (snapshot.Progress < 1) return true;

        var replacementId = await DownloadTaskLookups.FindReplacementTargetAsync(
         db, task.SubjectId, task.Episode, task.InfoHash, task.Version, ct);
        if (replacementId is null) return true;

        var incumbent = await db.DownloadTasks.Include(value => value.Files)
                                .SingleOrDefaultAsync(value => value.Id == replacementId, ct);
        if (incumbent is null                      || incumbent.Status      != DownloadTaskStatus.Completed ||
            incumbent.ReplacedByTaskId is not null || incumbent.Files.Count == 0) return true;

        // Finish an interrupted move before deciding which old paths still collide.
        if (HasPending(incumbent) && !await ExecutePendingAsync(db, session, incumbent, ct)) return false;

        var targets = plan.Paths.Values.SelectMany(path => new[] { Join(plan.Root, path), Join(task.SavePath, path) })
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var sizeByIndex = task.Files.ToDictionary(file => file.FileIndex, file => file.Size);
        foreach (var pair in plan.Paths)
        {
            targetSizes[Join(plan.Root, pair.Value)]     = sizeByIndex[pair.Key];
            targetSizes[Join(task.SavePath, pair.Value)] = sizeByIndex[pair.Key];
        }

        var replacementPaths = incumbent.Files.ToDictionary(file => file.FileIndex, file => file.CurrentRelativePath);
        var filesToMoveAside = new HashSet<int>();
        foreach (var file in incumbent.Files)
        {
            var occupied = Join(incumbent.SavePath, file.CurrentRelativePath);
            foreach (var target in targets)
            {
                if (!PathsOverlap(target, occupied)) continue;
                var sameFile = EqualPath(target, occupied)                           &&
                               targetSizes.TryGetValue(target, out var expectedSize) && expectedSize == file.Size;
                if (!sameFile) filesToMoveAside.Add(file.FileIndex);
            }
        }

        if (filesToMoveAside.Count == 0) return true;

        // A completed incumbent may have been removed from qBittorrent outside this app.
        // In that case there is no torrent through which we can move its old file aside;
        // the completed replacement may still claim the exact target path below.
        var incumbentSnapshot = (await session.FindTorrentsAsync([incumbent.InfoHash], ct)).SingleOrDefault();
        if (incumbentSnapshot is null) return true;
        if (incumbentSnapshot.IsMoving) return false;

        foreach (var file in incumbent.Files.Where(file => filesToMoveAside.Contains(file.FileIndex)))
        {
            var extension = Path.GetExtension(file.CurrentRelativePath);
            replacementPaths[file.FileIndex] = RelativeDownloadPathPolicy.Normalize(
             $".replaced/{incumbent.Id:N}/{task.Id:N}-{file.FileIndex}{extension}");
        }

        var replacementPlan = new FilePlan(incumbent.SavePath, replacementPaths);
        await ValidatePlanAsync(db, session, incumbent, replacementPlan, ct);
        Stage(incumbent, replacementPlan);
        await db.SaveChangesAsync(ct);
        return await ExecutePendingAsync(db, session, incumbent, ct);
    }

    /// <summary>调用方必须持有同一个 DownloadCoordinationGate；仅恢复已持久化日志，不重新分类或规划。</summary>
    public async Task<bool> RecoverPendingUnderGateAsync(
        IQbittorrentSession session, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await LoadAsync(db, taskId, cancellationToken);
        return await ExecutePendingAsync(db, session, task, cancellationToken);
    }

    private async Task<DownloadTask> InitializeUnderGateAsync(
        ApplicationDbContext db, IQbittorrentSession session, Guid taskId, CancellationToken ct)
    {
        var task = await LoadAsync(db, taskId, ct);
        if (!TorrentFileManifestParser.TryParse(task.TorrentFile, out var manifest))
            throw new InvalidOperationException("种子原始元数据不存在或文件清单无效，不能伪造原始文件路径。");
        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        ValidateIdentities(manifest, remote);
        var existing = task.Files.ToDictionary(file => file.FileIndex);
        if (existing.Keys.Any(index => !manifest.Files.Any(entry => entry.Index == index)))
            throw new InvalidOperationException("已保存文件身份与种子元数据不一致。");
        foreach (var entry in manifest.Files)
        {
            var observed = remote.Single(file => file.Index == entry.Index);
            if (existing.TryGetValue(entry.Index, out var file))
            {
                if (file.OriginalRelativePath != entry.Path || file.Size != entry.Length)
                    throw new InvalidOperationException("持久化原始文件身份与种子元数据不一致，拒绝改写。");
                if (file.PendingRenameTo is null && file.CurrentRelativePath != observed.Path)
                    throw new InvalidOperationException("下载器中的文件被外部改名，请人工核对后重试。");
                continue;
            }

            var classified = classificationPolicy.Classify(entry.Path);
            var added = new DownloadTaskFile
            {
                Id                   = Guid.NewGuid(), DownloadTaskId = task.Id, FileIndex = entry.Index,
                OriginalRelativePath = entry.Path,
                CurrentRelativePath  = RelativeDownloadPathPolicy.Normalize(observed.Path),
                Size                 = entry.Length, Kind = Kind(classified), ClassificationReason = classified.Reason,
                UpdatedAtUtc         = timeProvider.GetUtcNow()
            };
            task.Files.Add(added);
            db.Set<DownloadTaskFile>()
              .Add(added); // Explicit Added state: an assigned Guid must not be inferred as an existing row.
        }

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

    private static void ValidateIdentities(TorrentFileManifest manifest, IReadOnlyList<TorrentFileSnapshot> remote)
    {
        if (remote.Count                                         != manifest.Files.Count ||
            remote.Select(file => file.Index).Distinct().Count() != remote.Count)
            throw new InvalidOperationException("下载器文件 index 列表不完整或重复，拒绝按列表顺序猜测身份。");
        foreach (var entry in manifest.Files)
        {
            var observed = remote.SingleOrDefault(file => file.Index == entry.Index);
            if (observed is null || observed.Size != entry.Length)
                throw new InvalidOperationException("下载器文件 index/大小无法与原始种子可靠映射。");
            RelativeDownloadPathPolicy.Normalize(observed.Path);
        }
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
        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        foreach (var file in task.Files)
        {
            var observed = remote.SingleOrDefault(value => value.Index == file.FileIndex);
            if (observed is null || observed.Size != file.Size || observed.Path != file.CurrentRelativePath)
                throw new InvalidOperationException("下载器文件身份或路径已改变，请重新核对。");
            var target = plan.Paths[file.FileIndex];
            // Conservative: reject swaps/chains rather than rename into another file's current path.
            if (remote.Any(other => other.Index != file.FileIndex && EqualPath(other.Path, target)))
                throw new InvalidOperationException("目标路径占用另一文件当前路径，不能安全执行改名。");
        }

        await ValidateTargetOccupancyAsync(db, session, task, plan, remote, ct, allowCompletedReplacementCollision);
    }

    private async Task ValidateTargetOccupancyAsync(ApplicationDbContext db, IQbittorrentSession session,
                                                    DownloadTask task, FilePlan plan,
                                                    IReadOnlyList<TorrentFileSnapshot> remote, CancellationToken ct,
                                                    bool allowCompletedReplacementCollision = false)
    {
        if (remote.Count != task.Files.Count || remote.Select(file => file.Index).Distinct().Count() != remote.Count)
            throw new InvalidOperationException("下载器文件身份列表不完整或重复。");
        var entries = plan.Paths.ToList();
        foreach (var (index, path) in entries)
        {
            RelativeDownloadPathPolicy.Normalize(path);
            if (entries.Any(other => other.Key  != index && PathsOverlap(path, other.Value)) ||
                remote.Any(other => other.Index != index && PathsOverlap(path, other.Path)))
                throw new InvalidOperationException("目标路径与同种子另一文件目标/当前路径相同或互为父子，不能安全执行。");
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

        var replacementTargetId =
            await DownloadTaskLookups.FindReplacementTargetAsync(db, task.SubjectId, task.Episode, task.InfoHash,
                                                                 task.Version, ct);
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
                throw new InvalidOperationException("目标目录可能被其他尚未初始化的任务占用。");
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
                                                  other.Id == replacementTargetId    &&
                                                  other is
                                                  {
                                                      Status: DownloadTaskStatus.Completed, ReplacedByTaskId: null
                                                  }                        &&
                                                  !hasPendingFileOperation &&
                                                  EqualPath(target, path);
                    if (matchingReplacementFile) continue;
                    var owner = string.IsNullOrWhiteSpace(other.NamingSnapshot)
                        ? other.Id.ToString()
                        : other.NamingSnapshot;
                    throw new
                        InvalidOperationException($"目标文件路径“{target}”被下载任务“{owner}”（任务 ID {other.Id}，状态 {other.Status}）占用；该任务记录的文件路径为“{path}”。");
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
                        switch (managedTask)
                        {
                            case null when
                                targetSizes.TryGetValue(target, out var expected) &&
                                expected == file.Size :
                                continue;
                            case null :
                                throw new
                                    InvalidOperationException($"目标文件路径“{target}”被下载器中的其他种子占用，种子文件路径为“{occupied}”。");
                            default :
                            {
                                var owner = string.IsNullOrWhiteSpace(managedTask.NamingSnapshot)
                                    ? managedTask.Id.ToString()
                                    : managedTask.NamingSnapshot;
                                throw new
                                    InvalidOperationException($"目标文件路径“{target}”被下载任务“{owner}”（任务 ID {managedTask.Id}，状态 {managedTask.Status}）占用；该种子的文件路径为“{occupied}”。");
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
        foreach (var file in task.Files)
        {
            if (file.PendingRenameFrom is null || file.PendingRenameTo is null)
                throw new InvalidOperationException("文件操作日志不完整。");
            var observed = remote.SingleOrDefault(value => value.Index == file.FileIndex);
            if (observed is null || observed.Size != file.Size ||
                (observed.Path != file.PendingRenameFrom && observed.Path != file.PendingRenameTo))
                throw new InvalidOperationException("恢复时文件身份不匹配或路径既不是原路径也不是目标路径。");
        }

        var pendingPlan =
            new FilePlan(targetRoot, task.Files.ToDictionary(file => file.FileIndex, file => file.PendingRenameTo!));
        await ValidateTargetOccupancyAsync(db, session, task, pendingPlan, remote, ct,
                                           snapshot.Progress >= 1);
        foreach (var file in task.Files.OrderBy(value => value.FileIndex))
        {
            var observed = remote.SingleOrDefault(value => value.Index == file.FileIndex);
            if (observed is null || observed.Size != file.Size) throw new InvalidOperationException("恢复时文件身份不匹配。");
            if (file.PendingRenameTo is null || file.PendingRenameFrom is null)
                throw new InvalidOperationException("文件操作日志不完整。");
            if (observed.Path == file.PendingRenameTo) continue; // Remote succeeded, DB acknowledgement was lost.
            if (observed.Path != file.PendingRenameFrom)
                throw new InvalidOperationException("恢复时文件路径既不是原路径也不是目标路径，拒绝猜测。");
            await session.RenameTorrentFileAsync(task.InfoHash, file.PendingRenameFrom, file.PendingRenameTo, ct);
        }

        remote = await session.ListFilesAsync(task.InfoHash, ct);
        if (task.Files.Any(file => !remote.Any(observed => observed.Index == file.FileIndex &&
                                                           observed.Size  == file.Size      &&
                                                           observed.Path  == file.PendingRenameTo))) return false;
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
        Root(root) + "/" + RelativeDownloadPathPolicy.Normalize(relative);

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
