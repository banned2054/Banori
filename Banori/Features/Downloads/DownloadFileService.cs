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
using Torrent = MonoTorrent.Torrent;

namespace Banori.Features.Downloads;

/// <summary>
///     目标路径被其他任务、下载器种子或同种子内其他文件占用。占用本身不破坏数据：调用方可以安全地
///     推迟迁移并记录真实冲突原因，但不能据此启动写入。
/// </summary>
public sealed class TargetPathOccupiedException(string message) : InvalidOperationException(message);

/// <summary>
///     文件身份无法确认：下载器清单与持久化身份对不上、日志不完整、原始元数据不一致等。
///     这类失败必须阻塞推进（失败/待核对），绝不进入任何可启动或可迁移的回退——身份不明时
///     既不能证明目标可写，也不能证明当前布局可保留。
/// </summary>
public class DownloadFileIdentityException : InvalidOperationException
{
    public DownloadFileIdentityException(string message) : base(message)
    {
    }

    /// <summary>保留底层失败原因，用于区分「下载器导出失败」与「导出内容无效」等不同恢复障碍。</summary>
    public DownloadFileIdentityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
///     布局请求（改名、移动）在确认窗口内有界补发后仍未生效：下载器可能拒绝或吞掉了请求，
///     继续重发只会形成请求风暴；停机待人工核对，校验布局的调用方转隔离链。
/// </summary>
public sealed class DownloadLayoutConfirmationException(string message) : DownloadFileIdentityException(message);

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
    TimeProvider                            timeProvider,
    DownloadTimingPolicy?                   timingPolicy = null) : IDownloadFileService
{
    private static readonly HashSet<string> SubtitleExtensions =
        [with(StringComparer.OrdinalIgnoreCase), ".ass", ".ssa", ".srt", ".sup"];

    private static readonly Regex SubtitleSuffix =
        new(@"(?<suffix>(?:\.(?:chs|cht|sc|tc|zh|zh-cn|zh-tw|chi|zho|gb|big5|jp|jpn|ja|en|eng|简|繁|简日|繁日|forced|default))+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>时间策略：布局请求确认窗口与有界补发预算；未注册时用 Immediate 保持测试旧行为。</summary>
    private readonly DownloadTimingPolicy timing = timingPolicy ?? DownloadTimingPolicy.Immediate;

    public async Task<DownloadTaskFilesView> ListAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var       lease = await coordinationGate.EnterAsync(cancellationToken);
        await using var db    = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task  = await LoadAsync(db, taskId, cancellationToken);
        if (task.Files.Count != 0) return await ViewAsync(db, task, cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);

        return await ViewAsync(db, task, cancellationToken);
    }

    public async Task<DownloadTaskFilesView> RefreshAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task    = await LoadAsync(db, taskId, cancellationToken);
        if (task.Files.Count == 0)
            throw new InvalidOperationException("文件清单尚未初始化，请先展开文件面板。");
        await WithMissingTorrentCheckAsync(async () =>
        {
            await RefreshPathsUnderGateAsync(db, session, task, cancellationToken);
            return task;
        }, session, task.InfoHash, cancellationToken);
        return await ViewAsync(db, task, cancellationToken);
    }

    public async Task<DownloadFilePreview> PreviewAsync(
        Guid taskId, int index, DownloadFileEditRequest request, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task    = await LoadAsync(db, taskId, cancellationToken);
        EnsureEditable(task);
        // 查询放在清单/快照读取之后：查询时种子还在、读取前被删的竞态下，读取失败才触发按 hash 补查。
        // 只有成功查无才升级为专用提示；查询未知沿用原错误；取消正常传播。补查只读，不写库。
        task = await WithMissingTorrentCheckAsync(() => InitializeUnderGateAsync(db, session, taskId,
                                                      cancellationToken), session, task.InfoHash,
                                                  cancellationToken);
        if (HasPending(task)) throw new InvalidOperationException("文件操作尚未确认，请等待对账后重试。");
        var file    = FindFile(task, index);
        var current = DownloadRoundLayout.CurrentRelative(task, file);
        ApplyEdit(file, request);
        var (plan, fileTemplate) = await WithMissingTorrentCheckAsync(
            () => PlanEditUnderGateAsync(db, session, task, index, cancellationToken), session, task.InfoHash,
            cancellationToken);
        var root = DownloadRoundLayout.Root(task);
        // 模板实时化后，编辑一行的同时可能会一并重命名同种子里的其他已选文件；把差异带回界面明示。
        // ApplyEdit 只改分类与覆盖值，不改当前落盘路径，因此此处读取的当前路径仍是编辑前状态。
        var otherRenames = task.Files
            .Where(other => other.FileIndex != index && other.IsSelectedForDownload)
            .Select(other => new
            {
                other.FileIndex,
                Current = DownloadRoundLayout.CurrentAbsolute(task, other),
                Target  = Join(plan.Root, plan.Paths[other.FileIndex])
            })
            .Where(item => !string.Equals(item.Current, item.Target, StringComparison.Ordinal))
            .Select(item => new OtherFileRename(item.FileIndex, item.Current, item.Target))
            .ToList();
        return new DownloadFilePreview(Join(root, current), Join(plan.Root, plan.Paths[index]), root,
                                       plan.Root,
                                       !SameRoot(root, plan.Root), !SameRoot(root, plan.Root)
                                           ? "保存目录改变会移动整个种子；所有文件路径将一起安全转换，异步移动完成前不能再次编辑。"
                                           : null,
                                       fileTemplate, otherRenames);
    }

    public async Task<DownloadTaskFilesView> ApplyAsync(
        Guid taskId, int index, DownloadFileEditRequest request,
        CancellationToken cancellationToken = default, string? expectedFileTemplate = null)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        EnsureEditable(await LoadAsync(db, taskId, cancellationToken));
        var task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
        if (HasPending(task) && !await ExecutePendingAsync(db, session, task, cancellationToken))
            throw new InvalidOperationException("下载器正在移动文件，请等待对账后再修改。");
        ApplyEdit(FindFile(task, index), request);
        var (plan, _) = await PlanEditUnderGateAsync(db, session, task, index, cancellationToken,
                                                     expectedFileTemplate);
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken); // Overrides and journal must survive before any remote side effect.
        await ExecutePendingAsync(db, session, task, cancellationToken);
        return await ViewAsync(db, task, cancellationToken);
    }

    /// <summary>
    ///     重新核对已完成但归档受阻的任务；重新执行相同的占用检查与迁移链，不授予覆盖占用的许可。
    /// </summary>
    public async Task RetryArchiveAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);

        string infoHash;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var task = await LoadAsync(db, taskId, cancellationToken);
            var review = DownloadArchiveReview.FromJson(task.ArchiveReview) ??
                         throw new InvalidOperationException("该任务没有待核对的归档记录。");
            if (task.Status != DownloadTaskStatus.Completed)
                throw new InvalidOperationException("只有已完成但归档待核对的任务可以确认归档。");
            infoHash = task.InfoHash;

            var snapshot  = await SnapshotAsync(session, infoHash, cancellationToken);
            // 上一轮改名可能已在下载器生效但应用未确认：按持久化日志两端核对身份，不能因此拒绝重核。
            var remote    = MatchRemoteFiles(task, await session.ListFilesAsync(infoHash, cancellationToken), true);
            // 重下轮的本轮下载器根仍在 PendingSubmitSavePath，SavePath 仍是旧成片根；已登记的移动目标
            // 也可能已在下载器生效。按本轮布局核对，不能拿历史成片根否定重新核对入口。
            var roundRoot = DownloadRoundLayout.Root(task);
            var movedRoot = task.PendingSavePath;
            if (snapshot.IsMoving                                                                            ||
                snapshot.Category is QbittorrentTorrentCategory.Checking or QbittorrentTorrentCategory.Error ||
                (!SameRoot(snapshot.SavePath, roundRoot) &&
                 (movedRoot is null || !SameRoot(snapshot.SavePath, movedRoot))) ||
                task.Files.All(file => !file.IsSelectedForDownload)              ||
                task.Files.Where(file => file.IsSelectedForDownload)
                    .Any(file => !remote[file.FileIndex].IsSelectedForDownload ||
                                 (file.Size > 0 && remote[file.FileIndex].Progress < 1)))
                throw new InvalidOperationException("下载器仍在移动或所选内容未确认完成，暂不能确认归档。");
        }

        var finalSnapshot = await SnapshotAsync(session, infoHash, cancellationToken);
        if (finalSnapshot.Category is QbittorrentTorrentCategory.Checking or QbittorrentTorrentCategory.Error ||
            finalSnapshot.IsMoving)
            throw new InvalidOperationException("下载器状态发生变化，请等待稳定后重新核对。");
        await PlanAndExecuteUnderGateAsync(session, taskId, finalSnapshot with { Progress = 1 }, cancellationToken);
        var after = await ReadTaskAsync(taskId, cancellationToken);
        // 占用解除后归档计划可能只发出移动/改名请求，异步确认前仍留着旧核对原因：
        // 这是待确认而非再次受阻，不能把上一次的占用原因当成当前失败抛出。
        var hasPendingMove = after.PendingSavePath is not null ||
                             after.Files.Any(file => file.PendingRenameFrom is not null ||
                                                     file.PendingRenameTo is not null);
        if (after.ArchiveReview is { } remaining                     &&
            DownloadArchiveReview.FromJson(remaining) is { } blocked &&
            !hasPendingMove)
            throw new InvalidOperationException(blocked.Reason);
    }

    /// <summary>
    ///     包裹一次可能读取文件清单/快照的操作：失败后按 hash 补查，只有成功返回的有效空列表
    ///     才升级为专用缺种提示；查询未知沿用原错误，取消照常传播。补查只读。
    /// </summary>
    private static async Task<T> WithMissingTorrentCheckAsync<T>(
        Func<Task<T>> operation, IQbittorrentSession session, string infoHash, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                // 成功返回且为空才算「确认查无」；无效响应在适配层已翻译为异常，落入下方保留原错误。
                if ((await session.FindTorrentsAsync([infoHash], cancellationToken)).Count == 0)
                    throw new TorrentMissingException();
            }
            catch (TorrentMissingException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 查询未知：保留原始读取失败，不误报缺种。
            }

            throw;
        }
    }

    public async Task InitializeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var       lease   = await coordinationGate.EnterAsync(cancellationToken);
        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await using var db      = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
    }

    private static void EnsureEditable(DownloadTask task)
    {
        if (task.VerificationIntent is not null || task.IsolationState != DownloadIsolationState.None ||
            task.Files.Any(file => HasVerifiedReuseEvidence(task, DownloadRoundLayout.CurrentAbsolute(task, file),
                                                            file.Size)))
            throw new InvalidOperationException("任务正在校验、隔离或复用共享数据，不能通过文件编辑移动共享布局。");
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

    /// <summary>
    ///     计算选中文件的最终归档目标（绝对路径）。不触达下载器：登记后、提交前使用，种子尚未添加，
    ///     不能走 Initialize 的远端身份核对。命名要素缺失等规划错误向上抛出，由调用方决定降级。
    /// </summary>
    public async Task<IReadOnlyList<(int FileIndex, string TargetPath)>> ComputeArchivalTargetsAsync(
        Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await LoadAsync(db, taskId, cancellationToken);
        var fileTemplate = await EnsureNamingIngredientsAsync(task, cancellationToken);
        var plan = Plan(task, fileTemplate);
        return task.Files
                   .Where(file => file.IsSelectedForDownload)
                   .Select(file => (file.FileIndex, Join(plan.Root, plan.Paths[file.FileIndex])))
                   .ToList();
    }

    internal async Task<bool> HasReplacementAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await LoadAsync(db, taskId, cancellationToken);
        return (await LoadReplacementAsideFilesAsync(db, task, cancellationToken)).Count > 0;
    }

    /// <summary>
    ///     枚举归档目标上同路径同大小的复用候选，不依赖受管任务之间的替换关系；待确认布局不参与候选。
    ///     父子重叠与大小不符不构成候选。候选只授权停止状态的受控映射与 recheck，不授予替换旧文件的权限。
    /// </summary>
    public async Task<IReadOnlyList<ReuseCandidate>> EnumerateReuseCandidatesAsync(
        IQbittorrentSession session, Guid taskId, IReadOnlyList<(int FileIndex, string TargetPath)> targets,
        CancellationToken   cancellationToken = default)
    {
        await using var db          = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task        = await LoadAsync(db, taskId, cancellationToken);
        var             sizeByIndex = task.Files.ToDictionary(file => file.FileIndex, file => file.Size);
        var             result      = new Dictionary<int, ReuseCandidate>();
        var others = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .Where(value => value.Id != task.Id).ToListAsync(cancellationToken);
        var torrents      = await session.ListTorrentsAsync(null, cancellationToken);
        var managedHashes = others.Select(value => value.InfoHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unstableHashes = others.Where(HasUnsettledReuseLayout).Select(value => value.InfoHash)
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
        unstableHashes.UnionWith(torrents.Where(torrent => torrent.IsMoving ||
                                                           torrent.Category is QbittorrentTorrentCategory.Checking
                                                                            or QbittorrentTorrentCategory.Error
                                                                            or QbittorrentTorrentCategory.Unknown)
                                         .Select(torrent => torrent.Hash));

        foreach (var owner in others)
        {
            if (unstableHashes.Contains(owner.InfoHash)) continue;
            var matchingFiles = owner.Files
                                     .Where(file => targets.Any(target =>
                                                                    sizeByIndex[target.FileIndex] == file.Size &&
                                                                    EqualPath(target.TargetPath,
                                                                              DownloadRoundLayout
                                                                                 .PublishedAbsolute(owner,
                                                                                      file))))
                                     .ToList();
            if (matchingFiles.Count == 0) continue;
            var torrent = torrents.FirstOrDefault(value => string.Equals(value.Hash, owner.InfoHash,
                                                                         StringComparison.OrdinalIgnoreCase));
            if (torrent is not null && !SameRoot(torrent.SavePath, owner.SavePath)) continue;
            var remoteFiles = torrent is null ? null : await session.ListFilesAsync(owner.InfoHash, cancellationToken);
            foreach (var file in matchingFiles)
            {
                var absolute = DownloadRoundLayout.PublishedAbsolute(owner, file);
                if (remoteFiles is not null &&
                    !remoteFiles.Any(remote => EqualPath(Join(owner.SavePath, remote.Path), absolute) &&
                                               remote.Size == file.Size                               &&
                                               (remote.Size <= 0 || remote.Progress >= 1 ||
                                                !remote.IsSelectedForDownload))) continue;
                foreach (var (index, target) in targets)
                    if (EqualPath(target, absolute) && sizeByIndex[index] == file.Size)
                        result[index] = new ReuseCandidate(index, target, file.Size, true);
            }
        }

        foreach (var torrent in torrents)
        {
            if (string.Equals(torrent.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase) ||
                unstableHashes.Contains(torrent.Hash)                                          ||
                managedHashes.Contains(torrent.Hash)) continue;
            try
            {
                var root = Root(torrent.SavePath);
                foreach (var file in await session.ListFilesAsync(torrent.Hash, cancellationToken))
                {
                    var occupied = root + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                    foreach (var (index, target) in targets)
                    {
                        if (!EqualPath(target, occupied) || file.Size != sizeByIndex[index]) continue;
                        result.TryAdd(index, new ReuseCandidate(index, target, file.Size, false));
                    }
                }
            }
            catch (ArgumentException)
            {
                // 无法解析保存根的外部种子跳过。
            }
        }

        return result.Values.ToList();
    }

    /// <summary>
    ///     只从本任务已登记的旧归档证据里建立复用候选：用户删除种子但保留成片时，普通枚举会排除本任务，
    ///     只剩这些文件时也必须走受控映射与真实校验。只接受同路径同大小且未被其他任务承接的文件。
    /// </summary>
    public async Task<IReadOnlyList<ReuseCandidate>> EnumerateOwnReuseCandidatesAsync(
        Guid              taskId, IReadOnlyList<(int FileIndex, string TargetPath)> targets,
        CancellationToken cancellationToken = default)
    {
        await using var db     = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task   = await LoadAsync(db, taskId, cancellationToken);
        var             result = new Dictionary<int, ReuseCandidate>();
        foreach (var file in task.Files)
        {
            if (!file.IsSelectedForDownload || file.SupersededByTaskId is not null || file.Size <= 0) continue;
            // 以已发布的成片布局为准：重下期间 CurrentRelativePath 已切到下载器原始文件名，
            // 不能再与成片根拼接；VerificationPath 也可能残留复用失败的旧校验路径，只作附加线索。
            var confirmed = DownloadRoundLayout.PublishedAbsolute(task, file);
            var stalePath = string.IsNullOrWhiteSpace(file.VerificationPath)
                ? null
                : DownloadPathNamePolicy.NormalizePath(file.VerificationPath);
            foreach (var (index, target) in targets)
            {
                if (index != file.FileIndex) continue;
                var normalizedTarget = DownloadPathNamePolicy.NormalizePath(target);
                if (EqualPath(normalizedTarget, confirmed) ||
                    (stalePath is not null && EqualPath(normalizedTarget, stalePath)))
                    result[index] = new ReuseCandidate(index, target, file.Size, true);
            }
        }

        return [.. result.Values];
    }

    private static bool HasUnsettledReuseLayout(DownloadTask task) =>
        task.PendingSavePath is not null                                                               ||
        DownloadRoundLayout.IsPending(task)                                                            ||
        task.Files.Any(file => file.PendingRenameFrom is not null || file.PendingRenameTo is not null) ||
        task.VerificationIntent is not null                                                            ||
        task.IsolationState != DownloadIsolationState.None;

    /// <summary>
    ///     在协调锁内执行持久化的逐文件校验布局：候选映射许可仅允许改名和校验；普通缺失文件进入正式目标，需要保护旧版的文件保留暂存路径。所有请求共用可恢复日志。
    /// </summary>
    public async Task<bool> ApplyVerificationMappingUnderGateAsync(
        IQbittorrentSession session, Guid taskId, IReadOnlyDictionary<int, string> candidateRelativePaths,
        CancellationToken   cancellationToken = default)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
        var intent = DownloadVerificationIntent.FromJson(task.VerificationIntent) ??
                     throw new DownloadFileIdentityException("受控映射缺少持久化校验意图。");
        if (intent.Step != DownloadVerificationStep.Mapping ||
            !SameRoot(DownloadRoundLayout.Root(task), intent.CandidateRoot))
            throw new DownloadFileIdentityException("受控映射阶段或保存根已改变。");
        var mappingPaths = intent.Files.Where(file => file.IsCandidate).Select(file => file.VerificationPath)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roundRoot = DownloadRoundLayout.Root(task);
        if (!mappingPaths.SetEquals(candidateRelativePaths.Values.Select(path => Join(roundRoot, path))))
            throw new DownloadFileIdentityException("受控映射许可与持久化意图不一致。");

        // 布局只由提交规划生成一次；执行与恢复直接消费同一份持久化计划。
        var rootPrefix = Root(roundRoot) + "/";
        if (intent.Files.Count != task.Files.Count ||
            intent.Files.Any(file => !file.VerificationPath.StartsWith(rootPrefix, StringComparison.Ordinal)))
            throw new DownloadFileIdentityException("校验布局与保存根不一致。");
        var paths = intent.Files.ToDictionary(file => file.FileIndex,
                                              file =>
                                                  RelativeDownloadPathPolicy.Normalize(file.VerificationPath
                                                      [rootPrefix.Length..]));

        if (paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count ||
            paths.Values.Any(left => paths.Values.Any(right =>
                                                          right.StartsWith(left + "/",
                                                                           StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("受控映射的目标路径冲突，拒绝执行。");

        var plan = new FilePlan(roundRoot, paths);
        if (HasPending(task))
        {
            if (!SameRoot(task.PendingSavePath ?? string.Empty, intent.CandidateRoot) ||
                task.Files.Any(file => !paths.TryGetValue(file.FileIndex, out var expected) ||
                                       !EqualPath(file.PendingRenameTo, expected)))
                throw new DownloadFileIdentityException("待恢复的改名日志与校验映射计划不一致。");
            return await ExecutePendingAsync(db, session, task, cancellationToken, mappingPaths);
        }

        await ValidatePlanAsync(db, session, task, plan, cancellationToken, mappingPaths);
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken);
        return await ExecutePendingAsync(db, session, task, cancellationToken, mappingPaths);
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
    ///     任务专属根的任务在同一套最终相对路径下改名（下载只触碰按构造安全的命名空间），
    ///     存量任务沿用原语义把文件改到正式目标；完成时的归档变为同一计划的幂等校验。
    ///     返回 false 表示移动/确认未就绪，交由后续对账重试。
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

    /// <summary>
    ///     存量已提交任务恢复替换暂存布局：任务在补建替换关系前把种子添加到正式根，若重试只补登记
    ///     关系，后续接管仍会在正式根整理并与待替换的已完成旧成片撞占用。这里只登记一个把种子根整体
    ///     迁入 {下载根}/.tasks/{taskId} 的布局计划（相对路径与已下载进度不变），由接管流程在同一
    ///     协调锁内执行；完成期仍按既有迁让与归档链处理旧成片。仅在种子真实布局与登记一致、下载
    ///     未完成、停止且稳定、且确有已完成现任可迁让时登记；否则不做任何修改——外部目录、已完成
    ///     种子和无法确认身份的任务都不在这里移动。
    ///     返回 true 表示任务已位于任务专属根或暂存计划已登记，调用方可继续按替换任务接管。
    /// </summary>
    public async Task<bool> EnsureReplacementStagingUnderGateAsync(
        IQbittorrentSession session, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await LoadAsync(db, taskId, cancellationToken);
        if (task is { SubmittedAtUtc: null }    || string.IsNullOrWhiteSpace(task.DownloadRootPath)) return false;
        if (task.VerificationIntent is not null || task.IsolationState != DownloadIsolationState.None) return false;

        var targetRoot = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath, task.Id);
        // 同目标待执行计划已登记：交给接管流程继续确认，不重复规划。
        if (task.PendingSavePath is not null && SameRoot(task.PendingSavePath, targetRoot)) return true;
        if (HasPending(task)) return false;
        // 只有确有已完成现任需要迁让时才暂存；没有旧成片的新任务继续走普通接管。
        if ((await LoadReplacementAsideFilesAsync(db, task, cancellationToken)).Count == 0) return false;
        if (DownloadTaskPlacement.IsTaskScoped(task)) return true;
        // 只恢复本应用提交到正式根的布局：接管到其他目录的种子不在这里搬迁。
        if (!DownloadTaskPlacement.IsFormalRoot(task)) return false;

        var snapshot = await SnapshotAsync(session, task.InfoHash, cancellationToken);
        if (snapshot.IsMoving || snapshot.Progress >= 1 ||
            !SameRoot(snapshot.SavePath, DownloadRoundLayout.Root(task))) return false;
        if (snapshot.Category != QbittorrentTorrentCategory.Stopped) return false;

        // 路径或大小无法与持久化身份唯一对应时不得移动：身份不明时既不能证明目标可写，也不能猜测布局。
        MatchRemoteFiles(task, await session.ListFilesAsync(task.InfoHash, cancellationToken));

        var plan = new FilePlan(targetRoot,
                                task.Files.ToDictionary(file => file.FileIndex,
                                                        file => DownloadRoundLayout.CurrentRelative(task, file)));
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> PlanAndExecuteUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                          TorrentSnapshot snapshot, CancellationToken cancellationToken,
                                                          bool adoptExisting = false)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken, adoptExisting);
        if (task.VerificationIntent is not null || task.IsolationState != DownloadIsolationState.None)
            throw new DownloadFileIdentityException("校验或隔离中的任务不能进入普通文件整理链。");
        if (!string.Equals(task.InfoHash, snapshot.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("下载器快照不属于当前任务。");
        if (HasPending(task)) return await ExecutePendingAsync(db, session, task, cancellationToken);
        // 外部既有种子的接管只登记真实布局。接管本身不能授权改写正式布局；完成仍走证据驱动归档。
        if (adoptExisting && task.SubmittedAtUtc is null && !DownloadTaskPlacement.IsTaskScoped(task)) return true;
        var fileTemplate = await EnsureNamingIngredientsAsync(task, cancellationToken);
        // 任务专属根且尚未完成归档：同一套最终相对路径挂在任务根下执行（下载期绝不写正式布局），
        // 归档轮（progress >= 1）才用最终根计算迁移计划。
        var       intraTask  = DownloadTaskPlacement.IsTaskScoped(task) && snapshot.Progress < 1;
        // 升级前旧归档日志已确认的任务：完成收尾沿用已落库的旧根与相对路径，绝不再按新版规则
        // 重新切到媒体根。随访仍是同一套完成链（命名、状态、逐集切换），只是计划固定为现状。
        FilePlan? legacyPlan = null;
        var legacyFinal = snapshot.Progress >= 1 && !adoptExisting && !intraTask &&
                          TryLegacyArchiveFinalPlan(task, out legacyPlan);
        var plan = legacyFinal ? legacyPlan! : Plan(task, fileTemplate, intraTask);
        if (!intraTask && !adoptExisting && snapshot.Progress >= 1)
            return await ExecuteCompletionArchivalAsync(db, session, task, plan, snapshot, cancellationToken);

        var asideReady = await MoveConflictingReplacementFilesAsideAsync(db, session, task, plan, snapshot,
                                                                         cancellationToken);
        if (!asideReady) return false;
        await ValidatePlanAsync(db, session, task, plan, cancellationToken);
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken);
        return await ExecutePendingAsync(db, session, task, cancellationToken);
    }

    /// <summary>
    ///     完成后统一归档：先检查旧版的其他引用，必要时迁让旧版，再确认目标无冲突并自动迁入；只有真实冲突才记录受阻原因。
    /// </summary>
    private async Task<bool> ExecuteCompletionArchivalAsync(ApplicationDbContext db, IQbittorrentSession session,
                                                            DownloadTask task, FilePlan plan, TorrentSnapshot snapshot,
                                                            CancellationToken ct)
    {
        // 整体 SetLocation 会先改相对路径再搬根；即使最终绝对路径相同，也不能借此移动共享候选。
        var currentRoot = DownloadRoundLayout.Root(task);
        if (task.Files.Any(file => HasVerifiedReuseEvidence(task,
                                                            DownloadRoundLayout.CurrentAbsolute(task, file),
                                                            file.Size) &&
                                   (!SameRoot(currentRoot, plan.Root) ||
                                    !EqualPath(DownloadRoundLayout.CurrentRelative(task, file),
                                               plan.Paths[file.FileIndex]))))
        {
            await SaveArchiveReviewAsync(db, task, "归档计划会移动正在复用的共享文件，需先解除共享布局。", ct);
            return true;
        }

        try
        {
            var asideReady = await MoveConflictingReplacementFilesAsideAsync(db, session, task, plan,
                                                                             snapshot, ct);
            if (!asideReady) return false;
            await ValidatePlanAsync(db, session, task, plan, ct);
        }
        catch (TargetPathOccupiedException exception)
        {
            // 占用不可自动迁让（现任种子已移除、外部种子占位、迁让路径被交叉引用等）：
            // 保留任务目录并记录待核对，不做任何移动。
            await SaveArchiveReviewAsync(db, task, exception.Message, ct);
            return true;
        }

        Stage(task, plan);
        await db.SaveChangesAsync(ct);
        var executed = await ExecutePendingAsync(db, session, task, ct);
        if (!executed || task.ArchiveReview is null) return executed;
        task.ArchiveReview = null;
        await db.SaveChangesAsync(ct);

        return executed;
    }

    private async Task SaveArchiveReviewAsync(
        ApplicationDbContext db, DownloadTask task, string reason, CancellationToken ct)
    {
        task.ArchiveReview = new DownloadArchiveReview
        {
            Reason = reason, UpdatedAtUtc = timeProvider.GetUtcNow()
        }.ToJson();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    ///     新文件完成后迁让仍占目标的受管旧版及关联字幕。迁让前检查其他引用；仅有效复用证据允许同路径共存。返回 false 表示仍在等待远端操作确认。
    /// </summary>
    private async Task<bool> MoveConflictingReplacementFilesAsideAsync(ApplicationDbContext db,
                                                                       IQbittorrentSession session, DownloadTask task,
                                                                       FilePlan plan, TorrentSnapshot snapshot,
                                                                       CancellationToken ct)
    {
        if (snapshot.Progress < 1) return true;

        var asideCandidates = await LoadReplacementAsideFilesAsync(db, task, ct);
        if (asideCandidates.Count == 0) return true;

        var targets = plan.Paths.Values
                          .SelectMany(path => new[]
                           {
                               Join(plan.Root, path), Join(DownloadRoundLayout.Root(task), path)
                           })
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var infoHashByOwner = asideCandidates.GroupBy(item => item.OwnerId)
                                             .ToDictionary(group => group.Key, group => group.First().InfoHash);
        // 第一遍只确定要迁让的文件，不移动：引用检查必须先于任何一次移动。
        var movesByOwner =
            new List<(DownloadTask Owner,
                List<(ReplacementAsideEntry Entry, DownloadTaskFile File, string AbsolutePath)> Files)>();
        foreach (var ownerTaskId in infoHashByOwner.Keys)
        {
            var ownerSnapshot = (await session.FindTorrentsAsync([infoHashByOwner[ownerTaskId]], ct)).SingleOrDefault();
            if (ownerSnapshot is null) continue;
            if (ownerSnapshot.IsMoving || ownerSnapshot.Category == QbittorrentTorrentCategory.Checking) return false;

            // 迁让对象的文件操作必须经 tracked 实体执行；先完成其未确认操作再规划保留位置。
            var owner = await db.DownloadTasks.Include(value => value.Files)
                                .SingleOrDefaultAsync(value => value.Id == ownerTaskId, ct);
            if (owner is null || owner.Files.Count == 0) continue;
            if (HasPending(owner) && !await ExecutePendingAsync(db, session, owner, ct)) return false;

            var candidateIndices = asideCandidates.Where(item => item.OwnerId == ownerTaskId)
                                                  .Select(item => item.FileIndex)
                                                  .ToHashSet();
            var filesToMoveAside = new List<(ReplacementAsideEntry, DownloadTaskFile, string)>();
            foreach (var file in owner.Files.Where(file => candidateIndices.Contains(file.FileIndex)))
            {
                var occupied = DownloadRoundLayout.PublishedAbsolute(owner, file);
                foreach (var target in targets)
                {
                    if (!PathsOverlap(target, occupied)) continue;
                    var entry = asideCandidates.First(item =>
                                                          item.OwnerId   == ownerTaskId &&
                                                          item.FileIndex == file.FileIndex);
                    // 同路径且占用方与本任务经校验确认复用同一份数据时才跳过迁让；同大小本身不是证据。
                    var verifiedReuse = EqualPath(target, occupied) &&
                                        HasVerifiedReuseEvidence(task, occupied, file.Size);
                    if (!verifiedReuse) filesToMoveAside.Add((entry, file, occupied));
                }
            }

            if (filesToMoveAside.Count > 0)
                movesByOwner.Add((owner, filesToMoveAside));
        }

        foreach (var (owner, files) in movesByOwner)
        {
            var asidePaths = files.Select(item => item.AbsolutePath)
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await EnsureAsidePathsUnreferencedAsync(db, session, task, [owner.Id], asidePaths, ct);
        }

        foreach (var (owner, files) in movesByOwner)
        {
            var replacementPaths = owner.Files.ToDictionary(file => file.FileIndex, file => file.CurrentRelativePath);
            foreach (var (_, file, occupied) in files)
            {
                var extension = Path.GetExtension(file.CurrentRelativePath);
                replacementPaths[file.FileIndex] =
                    RelativeDownloadPathPolicy
                       .Normalize($".replaced/{owner.Id:N}/{task.Id:N}-{file.FileIndex}{extension}");
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
    ///     迁让前置引用检查：被迁让文件的现行绝对路径除所属任务自身与当前任务外，不得再被任何
    ///     数据库任务（含未确认改名/移动日志）或下载器种子引用。发现引用立即抛出占用异常进入
    ///     待核对——受管任务的登记路径不等于物理文件可自由迁让。
    /// </summary>
    private async Task EnsureAsidePathsUnreferencedAsync(ApplicationDbContext db,         IQbittorrentSession session,
                                                         DownloadTask         task,       HashSet<Guid>       ownerIds,
                                                         HashSet<string>      asidePaths, CancellationToken   ct)
    {
        var others = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .Where(value => value.Id != task.Id && !ownerIds.Contains(value.Id))
                             .ToListAsync(ct);
        // 迁让者自身的种子引用是正常状态：只阻塞「其他」依赖方，排除集合含迁让者与当前任务。
        var ownerHashes = (await db.DownloadTasks.AsNoTracking()
                                   .Where(value => ownerIds.Contains(value.Id))
                                   .Select(value => value.InfoHash).ToListAsync(ct))
           .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in others)
        foreach (var file in other.Files)
        {
            var occupied = DownloadRoundLayout.OccupiedPaths(other, file);
            if (occupied.Any(path => asidePaths.Any(aside => PathsOverlap(path, aside))))
                throw new TargetPathOccupiedException($"迁让对象路径仍被任务“{other.NamingSnapshot}”（任务 ID {other.Id}）引用，" +
                                                      "不能移动其共享文件，请先人工处理。");
        }

        foreach (var torrent in await session.ListTorrentsAsync(null, ct))
        {
            if (string.Equals(torrent.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)) continue;
            if (ownerHashes.Contains(torrent.Hash)) continue;
            try
            {
                var root = Root(torrent.SavePath);
                foreach (var file in await session.ListFilesAsync(torrent.Hash, ct))
                {
                    var occupied = root + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                    if (asidePaths.Any(aside => PathsOverlap(aside, occupied)))
                        throw new TargetPathOccupiedException($"迁让对象路径“{occupied}”被下载器中的其他种子（{torrent.Name}）交叉引用，" +
                                                              "不能移动共享文件，请先人工处理。");
                }
            }
            catch (ArgumentException)
            {
                // 无法解析保存根的外部种子跳过：其路径无法与规范化目标比对。
            }
        }
    }

    /// <summary>
    ///     校验证据必须仍绑定当前选中文件的实际路径和大小，才能允许共享已完成数据。
    /// </summary>
    private static bool HasVerifiedReuseEvidence(DownloadTask task, string absolutePath, long occupantSize) =>
        task.Files.Any(file => file is { IsSelectedForDownload: true, VerificationOutcome: not null } &&
                               string.Equals(file.VerificationOutcome,
                                             DownloadFileVerificationOutcomes.ReusedVerified,
                                             StringComparison.Ordinal)        &&
                               EqualPath(file.VerificationPath, absolutePath) &&
                               (EqualPath(DownloadRoundLayout.PublishedAbsolute(task, file), absolutePath) ||
                                EqualPath(DownloadRoundLayout.CurrentAbsolute(task, file), absolutePath)) &&
                               file.Size == occupantSize);

    /// <summary>
    ///     本任务完成替换时的迁让与放行对象：每集登记表中本任务来源下本任务为待完成优胜的集，其
    ///     现任视频文件（现任所属任务须已完成且未被声明替换）；以及被本任务逐集承接取消选择的休眠
    ///     文件（所属任务任意状态、来源不限——部分承接的合集仍在下载其余集）。
    /// </summary>
    private async Task<List<ReplacementAsideEntry>> LoadReplacementAsideFilesAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken ct)
    {
        var result = new List<ReplacementAsideEntry>();
        var myVideoIds = task.Files.Where(file => file is { Kind: DownloadFileKind.Anime, IsSelectedForDownload: true })
                             .Select(file => file.Id)
                             .ToList();

        if (myVideoIds.Count > 0 && task.SubjectId is { } subjectId)
        {
            // 只把本任务来源的 Pending 对应现任列入迁让对象；跨来源的登记行互不顶替。
            var sourceScope = SourceScopes.FromSiteSource(task.SiteSource);
            var incumbentFileIds = await db.EpisodeVersions.AsNoTracking()
                                           .Where(row => row.SubjectId          == subjectId   &&
                                                         row.SourceScope        == sourceScope &&
                                                         row.PendingVideoFileId != null)
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
                                                     owner.ReplacedByTaskId == null                         &&
                                                     owner.Files.Any(file => wanted.Contains(file.Id)))
                                     .ToListAsync(ct);
                foreach (var owner in owners)
                foreach (var video in owner.Files.Where(file => wanted.Contains(file.Id)))
                {
                    result.Add(new ReplacementAsideEntry(owner.Id, owner.InfoHash, owner.SavePath,
                                                         video.FileIndex, video.CurrentRelativePath, video.Size));
                    foreach (var subtitle in UniquelyAssociatedSubtitles(owner, video))
                        result.Add(new ReplacementAsideEntry(owner.Id, owner.InfoHash, owner.SavePath,
                                                             subtitle.FileIndex, subtitle.CurrentRelativePath,
                                                             subtitle.Size));
                }
            }
        }

        var dormantOwners = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                                    .Where(owner => owner.Id != task.Id &&
                                                    owner.Files.Any(file => file.SupersededByTaskId == task.Id))
                                    .ToListAsync(ct);
        foreach (var owner in dormantOwners)
        foreach (var file in owner.Files.Where(file => file.SupersededByTaskId == task.Id &&
                                                       !file.IsSelectedForDownload))
            result.Add(new ReplacementAsideEntry(owner.Id, owner.InfoHash, owner.SavePath,
                                                 file.FileIndex, file.CurrentRelativePath, file.Size));

        return result;
    }

    /// <summary>
    ///     Sidecar subtitles explicitly named after one video: same original directory and video stem, with only
    ///     the recognized subtitle language suffix appended. A stem shared by multiple videos is ambiguous and is
    ///     not associated with either one; episode equality alone is never used here.
    /// </summary>
    private static IEnumerable<DownloadTaskFile> UniquelyAssociatedSubtitles(DownloadTask owner, DownloadTaskFile video)
    {
        var videoIdentity = OriginalContentIdentity(video);
        foreach (var subtitle in owner.Files.Where(file => file.Kind == DownloadFileKind.Subtitle))
        {
            if (!SameContentIdentity(OriginalContentIdentity(subtitle, true),
                                     videoIdentity)) continue;
            if (owner.Files.Count(file => file.Kind == DownloadFileKind.Anime &&
                                          SameContentIdentity(OriginalContentIdentity(file), videoIdentity)) != 1)
                continue;
            yield return subtitle;
        }
    }

    private static (string Directory, string Stem) OriginalContentIdentity(
        DownloadTaskFile file, bool stripSubtitleSuffix = false)
    {
        var path     = file.OriginalRelativePath.Replace('\\', '/');
        var fileName = DownloadEpisodeResolver.FileName(path);
        var stem     = Path.GetFileNameWithoutExtension(fileName);
        if
            (!stripSubtitleSuffix) return (path[..^fileName.Length], stem);
        var suffix = SubtitleSuffix.Match(stem).Groups["suffix"].Value;
        stem = stem[..^suffix.Length];

        return (path[..^fileName.Length], stem);
    }

    private static bool SameContentIdentity(
        (string Directory, string Stem) left, (string Directory, string Stem) right) =>
        string.Equals(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Stem, right.Stem, StringComparison.OrdinalIgnoreCase);

    /// <summary>调用方必须持有同一个 DownloadCoordinationGate；仅恢复已持久化日志，不重新分类或规划。</summary>
    public async Task<bool> RecoverPendingUnderGateAsync(
        IQbittorrentSession session, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task = await LoadAsync(db, taskId, cancellationToken);
        if (task.VerificationIntent is not null || task.IsolationState != DownloadIsolationState.None) return false;
        return await ExecutePendingAsync(db, session, task, cancellationToken);
    }

    /// <summary>
    ///     旧版纯正片布局的一次性迁移：使用调用方逐文件核对过的目标（相对路径只去掉媒体目录前缀，
    ///     绝对路径不变），共用现有的占用检查、journal-first 暂存与异步移动恢复链。返回的决策记录
    ///     随日志一起落库，重启后 ExecutePendingAsync 不再用命名模板重新推导这批相对路径。
    /// </summary>
    internal async Task<bool> ApplyLegacyMigrationUnderGateAsync(IQbittorrentSession session, Guid taskId,
                                                                 string targetRoot,
                                                                 IReadOnlyDictionary<int, string> paths,
                                                                 CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await LoadAsync(db, taskId, cancellationToken);
        if (task.VerificationIntent is not null || task.IsolationState != DownloadIsolationState.None ||
            task.Files.Count == 0 || HasPending(task) || paths.Count != task.Files.Count ||
            task.Files.Any(file => !paths.ContainsKey(file.FileIndex)))
            return false;

        var plan = new FilePlan(Root(targetRoot),
                                paths.ToDictionary(pair => pair.Key,
                                                   pair => RelativeDownloadPathPolicy.Normalize(pair.Value)));
        await ValidatePlanAsync(db, session, task, plan, cancellationToken);
        task.PathDecision = new DownloadPathDecision
        {
            DecidedAtUtc = timeProvider.GetUtcNow(), Root = plan.Root,
            Kind         = DownloadPathDecisionKinds.LegacyLayoutMigration,
            Reason       = "旧版纯正片布局一次性迁移到媒体目录，文件绝对路径保持不变。"
        }.ToJson();
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken); // 决策与改名/移动日志必须先于任何远端副作用。
        return await ExecutePendingAsync(db, session, task, cancellationToken);
    }

    private async Task<DownloadTask> InitializeUnderGateAsync(ApplicationDbContext db,     IQbittorrentSession session,
                                                              Guid                 taskId, CancellationToken   ct,
                                                              bool                 adoptExisting = false)
    {
        var task = await LoadAsync(db, taskId, ct);
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile))
            (torrent, isMultiFile) = await RecoverTorrentFileAsync(session, task, ct);
        // Library enumeration (including zero-length files) can change. Match immutable originals, never its indices.
        var remaining =
            torrent.Files.ToDictionary(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile),
                                       StringComparer.Ordinal);
        var usedIndices = task.Files.Select(file => file.FileIndex).ToHashSet();
        if (usedIndices.Count != task.Files.Count)
            throw new DownloadFileIdentityException("已保存文件内部键重复。");
        foreach (var file in task.Files)
        {
            var original = file.OriginalRelativePath.Replace('\\', '/');
            if (!remaining.Remove(original, out var entry) || file.Size != entry.Length)
                throw new DownloadFileIdentityException("持久化原始文件身份与种子元数据不一致，拒绝改写。");
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
                    throw new DownloadFileIdentityException("下载器文件 index/大小与原始种子不一致，无法接管。");
                file.CurrentRelativePath = observed.Path.Replace('\\', '/');
            }
        }

        MatchRemoteFiles(task, remote, true);

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

    /// <summary>
    ///     旧任务缺失或无效的原始种子元数据经下载器导出补存。仅在元数据解析失败时调用；导出内容
    ///     必须解析成功且 InfoHash 与任务一致才允许赋回任务。核验通过前不触碰数据库，赋值后的后续
    ///     身份失败随未提交上下文丢弃。接口不支持导出与取消原样传播，其余导出异常包装为身份异常。
    /// </summary>
    private async Task<(Torrent Torrent, bool IsMultiFile)> RecoverTorrentFileAsync(
        IQbittorrentSession session, DownloadTask task, CancellationToken ct)
    {
        byte[] exported;
        try
        {
            exported = await session.ExportTorrentAsync(task.InfoHash, ct);
        }
        catch (Exception exception) when (exception is not NotSupportedException &&
                                          exception is not OperationCanceledException)
        {
            throw new DownloadFileIdentityException("下载器导出种子失败，无法恢复原始元数据。", exception);
        }

        if (exported is not { Length: > 0 } ||
            !TorrentDownloadPolicy.TryLoad(exported, out var torrent, out var isMultiFile))
            throw new DownloadFileIdentityException("下载器导出的种子内容无效，无法恢复原始元数据。");
        var exportedHash = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();
        if (!string.Equals(exportedHash, task.InfoHash, StringComparison.OrdinalIgnoreCase))
            throw new DownloadFileIdentityException("下载器导出的种子 InfoHash 与任务不一致，拒绝恢复。");

        task.TorrentFile = exported;
        return (torrent, isMultiFile);
    }

    private async Task RefreshPathsUnderGateAsync(
        ApplicationDbContext db, IQbittorrentSession session, DownloadTask task, CancellationToken ct)
    {
        if (HasPending(task)) throw new InvalidOperationException("文件操作尚未确认，请等待对账后重试。");
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile))
            (torrent, isMultiFile) = await RecoverTorrentFileAsync(session, task, ct);
        var hash = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();
        if (!string.Equals(hash, task.InfoHash, StringComparison.OrdinalIgnoreCase))
            throw new DownloadFileIdentityException("原始种子 InfoHash 与任务不一致，拒绝刷新。");

        var snapshot = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving || !SameRoot(snapshot.SavePath, DownloadRoundLayout.Root(task)))
            throw new DownloadFileIdentityException("下载器保存目录已变化或仍在移动，拒绝刷新。");

        var remaining =
            torrent.Files.ToDictionary(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile),
                                       StringComparer.Ordinal);
        if (remaining.Count != task.Files.Count)
            throw new DownloadFileIdentityException("种子文件数量与持久化清单不一致，拒绝刷新。");
        if (task.Files.Select(file => file.FileIndex).ToHashSet().Count != task.Files.Count)
            throw new DownloadFileIdentityException("已保存文件内部键重复，拒绝刷新。");
        foreach (var file in task.Files)
        {
            var original = file.OriginalRelativePath.Replace('\\', '/');
            if (!remaining.Remove(original, out var entry) || file.Size != entry.Length)
                throw new DownloadFileIdentityException("持久化原始文件身份与种子元数据不一致，拒绝刷新。");
        }

        var indices = TorrentDownloadPolicy.GetQbittorrentFileIndices(task.TorrentFile, torrent, isMultiFile);
        if (indices.Count != task.Files.Count)
            throw new DownloadFileIdentityException("下载器文件 index 映射数量与持久化清单不一致，拒绝刷新。");

        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        if (remote.Count != task.Files.Count)
            throw new DownloadFileIdentityException("下载器文件清单不完整或包含未登记文件，拒绝刷新。");
        var byIndex   = new Dictionary<int, (string Path, long Size)>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observed in remote)
        {
            var path = RefreshRelativePath(observed.Path);
            if (!byIndex.TryAdd(observed.Index, (path, observed.Size)) || !seenPaths.Add(path))
                throw new DownloadFileIdentityException("下载器返回重复的文件 index 或路径，拒绝刷新。");
        }

        var updates = new List<(DownloadTaskFile File, string Relative)>();
        foreach (var file in task.Files)
        {
            var original = file.OriginalRelativePath.Replace('\\', '/');
            if (!indices.TryGetValue(original, out var qbIndex) ||
                !byIndex.TryGetValue(qbIndex, out var observed) ||
                observed.Size != file.Size)
                throw new DownloadFileIdentityException("下载器文件 index/大小与原始种子不一致，拒绝刷新。");
            updates.Add((file, observed.Path));
        }

        var finalSnapshot = await SnapshotAsync(session, task.InfoHash, ct);
        if (finalSnapshot.IsMoving ||
            !SameRoot(finalSnapshot.SavePath, DownloadRoundLayout.Root(task)))
            throw new DownloadFileIdentityException("下载器保存目录在读取清单期间发生变化或仍在移动，拒绝刷新。");

        foreach (var (file, relative) in updates)
        {
            var oldCurrent = DownloadRoundLayout.CurrentAbsolute(task, file);
            var published  = DownloadRoundLayout.PublishedAbsolute(task, file);
            var sameVideo  = EqualPath(oldCurrent, published);
            if (!SameRelativePath(file.CurrentRelativePath, relative))
            {
                file.CurrentRelativePath = relative;
                file.UpdatedAtUtc         = timeProvider.GetUtcNow();
            }
            if (sameVideo)
            {
                file.PublishedRelativePath = relative;
                file.PublishedPath         = Join(DownloadRoundLayout.Root(task), relative);
                if (!EqualPath(DownloadRoundLayout.PublishedAbsolute(task, file),
                               DownloadRoundLayout.CurrentAbsolute(task, file)))
                    throw new DownloadFileIdentityException("刷新后已发布路径与当前路径不一致，拒绝刷新。");
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string RefreshRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new DownloadFileIdentityException("下载器返回空文件路径，拒绝刷新。");
        var normalized = path.Replace('\\', '/');
        var segments   = normalized.Split('/');
        if (normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':') ||
            segments.Any(segment => segment.Length == 0 ||
                                    segment is "." or ".." ||
                                    segment != segment.Trim()))
            throw new DownloadFileIdentityException("下载器返回非法相对路径或文件名含首尾空白，拒绝刷新。");
        return normalized;
    }

    private IReadOnlyDictionary<int, TorrentFileSnapshot> MatchRemoteFiles(
        DownloadTask task, IReadOnlyList<TorrentFileSnapshot> remote, bool allowPendingPaths = false)
    {
        if (task.Files.Count == 0 || remote.Count != task.Files.Count)
            throw new DownloadFileIdentityException("下载器文件清单不完整或包含未登记文件，拒绝猜测身份。");
        var byPath = new Dictionary<string, TorrentFileSnapshot>(StringComparer.Ordinal);
        foreach (var observed in remote)
        {
            var path = observed.Path.Replace('\\', '/');
            if (!byPath.TryAdd(path, observed with { Path = path }))
                throw new DownloadFileIdentityException("下载器文件清单包含重复的规范路径。");
        }

        var matched = new Dictionary<int, TorrentFileSnapshot>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in task.Files)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (file.PendingRenameFrom is not null || file.PendingRenameTo is not null)
            {
                if (file.PendingRenameFrom is null || file.PendingRenameTo is null)
                    throw new DownloadFileIdentityException("文件操作日志不完整。");
                if (allowPendingPaths)
                {
                    paths.Add(file.PendingRenameFrom.Replace('\\', '/'));
                    paths.Add(file.PendingRenameTo.Replace('\\', '/'));
                }
            }

            if (paths.Count == 0) paths.Add(DownloadRoundLayout.CurrentRelative(task, file).Replace('\\', '/'));
            // 未发布的新轮次同时接受已登记路径：映射阶段下载器仍报原始文件名，映射执行后又可能已改名；
            // 按 index 与大小的唯一匹配裁决，不能把「计划位置」当成当前身份。
            if (DownloadRoundLayout.IsPending(task))
            {
                paths.Add(file.OriginalRelativePath.Replace('\\', '/'));
                paths.Add(file.CurrentRelativePath.Replace('\\', '/'));
            }

            var candidates = paths
                            .Where(path => byPath.TryGetValue(path, out var observed) && observed.Size == file.Size)
                            .Select(path => byPath[path]).ToArray();
            if (candidates.Length != 1 || !claimed.Add(candidates[0].Path) ||
                !matched.TryAdd(file.FileIndex, candidates[0]))
                throw new DownloadFileIdentityException("下载器文件路径/大小无法唯一对应持久化身份或改名日志，拒绝猜测。");
        }

        // Count plus unique claims makes this a bijection: extra entries (including unknown padding) are not ignored.
        return claimed.Count != remote.Count ? throw new DownloadFileIdentityException("下载器文件清单包含无法对应的文件。") : matched;
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

    /// <summary>
    ///     补齐缺失的命名要素并返回当前全局文件名模板。模板只存在设置里：已有快照不被回写，实时路径
    ///     把返回的模板显式传给规划层覆盖渲染；种子名模板仍随快照保留。已完成暂存的文件操作恢复不经过
    ///     本方法，只按持久化日志核对目标，不受命名模板与目录设置变化影响。
    /// </summary>
    private async Task<string> EnsureNamingIngredientsAsync(DownloadTask task, CancellationToken ct)
    {
        var fileTemplate = await namingTemplateStore.GetFileTemplateAsync(ct);
        if (TorrentNamingIngredients.FromJson(task.NamingIngredients) is not null) return fileTemplate;
        if (task.Files.All(file => file.Kind == DownloadFileKind.Seed)) return fileTemplate;
        var subject = task.Subject;
        var title = !string.IsNullOrWhiteSpace(subject?.NameCn) ? subject.NameCn :
            !string.IsNullOrWhiteSpace(subject?.Name)           ? subject.Name : task.NamingSnapshot;
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("缺少可用作品名，不能按人工集数生成动画命名。");
        var episode = task.Episode ?? task.Files.Select(file => file.EpisodeNumberOverride)
                                          .FirstOrDefault(value => value is not null)
         ?? task.Files.Select(file => aniParser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath)))
                .Select(parsed => parsed?.Episode ?? parsed?.StartEpisode).FirstOrDefault(value => value is not null);
        if (episode is null) throw new InvalidOperationException("缺少命名要素和集数，请填写集数或补充任务命名快照。");
        task.NamingIngredients = new TorrentNamingIngredients(await namingTemplateStore.GetAsync(ct), title,
                                                              ReleaseNamePreviewPolicy.FormatEpisodeText(episode.Value),
                                                              task.Version, null, task.SiteSource,
                                                              BangumiId : task.SubjectId, BangumiName : subject?.Name,
                                                              BangumiNameCn : subject?.NameCn,
                                                              BangumiPlatform : subject?.Platform,
                                                              BangumiDate : subject?.AirDate).ToJson();
        return fileTemplate;
    }

    /// <summary>
    ///     计算文件目标计划。纯正片包只有在每个保留文件都能按大小写敏感规则表达为媒体根下的相对路径时
    ///     才切到媒体根；其他情况（跨包落选、替换暂存、复用停靠等）继续沿用旧版全局根布局。
    ///     intraTask 为 true 时把旧版同一套目标名挂在任务专属根下，下载期只触碰按构造安全的命名空间。
    ///     fileTemplate 是当前全局文件名模板，仅覆盖本次渲染、不落库；null 表示沿用快照（在飞恢复路径）。
    /// </summary>
    private FilePlan Plan(DownloadTask task, string? fileTemplate = null, bool intraTask = false)
    {
        if (intraTask) return LegacyPlan(task, fileTemplate, true);
        if (TryPlanMediaRoot(task, fileTemplate, out var plan)) return plan;
        return LegacyPlan(task, fileTemplate, false);
    }

    private static TorrentNamingIngredients? RenderIngredients(DownloadTask task, string? fileTemplate)
    {
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        return ingredients is not null && fileTemplate is not null
            ? ingredients with { FileTemplate = fileTemplate }
            : ingredients;
    }

    /// <summary>尝试用媒体目录作根表达全部最终路径；任一保留文件无法精确表达时整体降级到旧布局。</summary>
    private bool TryPlanMediaRoot(DownloadTask task, string? fileTemplate, out FilePlan plan)
    {
        plan = default!;
        if (task.Files.Count == 0 || task.Files.Any(file => file.Kind == DownloadFileKind.Seed)) return false;

        try
        {
            var currentRoot = DownloadRoundLayout.Root(task);
            var basePath    = Root(task.DownloadRootPath ?? currentRoot);
            var media       = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory!);
            var seed        = RelativeDownloadPathPolicy.Normalize(task.SeedDirectory!);
            RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
            var root        = Join(basePath, media);
            var ingredients = RenderIngredients(task, fileTemplate);
            var paths       = new Dictionary<int, string>();
            foreach (var file in task.Files)
            {
                string relative;
                if (!file.IsSelectedForDownload)
                {
                    relative = RelativeUnderRoot(DownloadRoundLayout.CurrentAbsolute(task, file), root);
                }
                else
                {
                    var name = file.AnimeRelativePathOverride ?? ContentName(task, file, ingredients);
                    relative = RelativeDownloadPathPolicy.Normalize(name);
                }

                paths.Add(file.FileIndex, relative);
            }

            if (paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count ||
                paths.Values.Any(left =>
                                     paths.Values.Any(right =>
                                                          right.StartsWith(left + "/",
                                                                           StringComparison.OrdinalIgnoreCase))))
                return false;

            // 复用证据绑定当前根上的绝对路径：能表达时继续保留旧根，不为归档先移动共享数据。
            plan = task.Files.Any(file => HasVerifiedReuseEvidence(task,
                                                                   DownloadRoundLayout.CurrentAbsolute(task, file),
                                                                   file.Size)) &&
                   paths.Values.All(path => IsWithin(Join(root, path), currentRoot))
                ? AdjustPlanToCurrentRoot(task, root, paths)
                : new FilePlan(root, paths);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>升级前的归档布局：全局根 + 媒体/做种目录前缀，保留未选中文件的既有停靠路径。</summary>
    private FilePlan LegacyPlan(DownloadTask task, string? fileTemplate, bool intraTask)
    {
        var currentRoot = DownloadRoundLayout.Root(task);
        var basePath    = Root(task.DownloadRootPath ?? currentRoot);
        var media       = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory!);
        var seed        = RelativeDownloadPathPolicy.Normalize(task.SeedDirectory!);
        RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
        var ingredients        = RenderIngredients(task, fileTemplate);
        var cleanPrefix        = media + "/";
        var currentRootIsMedia = SameRoot(currentRoot, Join(basePath, media));
        var clean              = task.Files.All(file => file.Kind != DownloadFileKind.Seed) && currentRootIsMedia;
        var absolute           = new Dictionary<int, string>();
        foreach (var file in task.Files)
        {
            var keepCurrentPath = !file.IsSelectedForDownload &&
                                  file is not { Kind: DownloadFileKind.Seed, SupersededByTaskId: null };
            var relative = keepCurrentPath
                ? currentRootIsMedia
                    ? cleanPrefix + DownloadRoundLayout.CurrentRelative(task, file)
                    : DownloadRoundLayout.CurrentRelative(task, file)
                : file.Kind == DownloadFileKind.Seed
                    ? seed        + "/" + file.OriginalRelativePath
                    : cleanPrefix + (file.AnimeRelativePathOverride ?? ContentName(task, file, ingredients));
            absolute.Add(file.FileIndex, RelativeDownloadPathPolicy.Normalize(relative));
        }

        var root = clean ? Join(basePath, media) : basePath;
        var paths = absolute.ToDictionary(pair => pair.Key,
                                          pair => clean ? pair.Value[cleanPrefix.Length..] : pair.Value);
        if (paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count ||
            paths.Values.Any(left =>
                                 paths.Values.Any(right =>
                                                      right.StartsWith(left + "/",
                                                                       StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("多个文件的目标路径冲突，请修改集数或手工动画路径。");
        if (intraTask) return new FilePlan(currentRoot, paths);
        if (task.Files.Any(file => HasVerifiedReuseEvidence(task,
                                                            DownloadRoundLayout.CurrentAbsolute(task, file),
                                                            file.Size)) &&
            paths.Values.All(path => IsWithin(Join(root, path), currentRoot)))
            return AdjustPlanToCurrentRoot(task, root, paths);

        return new FilePlan(root, paths);
    }

    /// <summary>复用证据要求保留现有根；仅当所有目标都能精确反解时，才用当前根表达同一组绝对路径。</summary>
    private static FilePlan AdjustPlanToCurrentRoot(
        DownloadTask task, string plannedRoot, IReadOnlyDictionary<int, string> paths)
    {
        var currentRoot = Root(DownloadRoundLayout.Root(task));
        var prefix      = currentRoot + "/";
        return paths.Values.Any(path => !Join(plannedRoot, path).StartsWith(prefix, StringComparison.Ordinal))
            ? new FilePlan(plannedRoot, paths)
            : new FilePlan(currentRoot, paths.ToDictionary(pair => pair.Key,
                                                           pair => Join(plannedRoot, pair.Value)[prefix.Length..]));
    }

    /// <summary>
    ///     人工编辑共用一套规划与校验：预览与保存得到完全相同的目标路径和拒绝结论。
    ///     明确编辑的文件按原有分类、命名和占用规则处理；只有目标根真的改变时，才禁止移动其他
    ///     未选中的保留文件。同根编辑继续沿用既有分类与占用规则，不额外套用换根保护。
    ///     expectedFileTemplate 非空时先核对模板，防止预览后设置被改导致应用落到不同路径。
    /// </summary>
    private async Task<(FilePlan Plan, string FileTemplate)> PlanEditUnderGateAsync(
        ApplicationDbContext db, IQbittorrentSession session, DownloadTask task, int editedIndex,
        CancellationToken    cancellationToken, string? expectedFileTemplate = null)
    {
        var fileTemplate = await EnsureNamingIngredientsAsync(task, cancellationToken);
        if (expectedFileTemplate is not null &&
            !string.Equals(expectedFileTemplate, fileTemplate, StringComparison.Ordinal))
            throw new InvalidOperationException("命名模板已变更，请重新预览后再应用。");
        // 任务专属根的任务执行任务内计划（改名只发生在命名空间内）；正式布局的迁入只经归档链。
        var plan = Plan(task, fileTemplate, DownloadTaskPlacement.IsTaskScoped(task));
        if (!SameRoot(DownloadRoundLayout.Root(task), plan.Root))
            EnsureUnselectedPathsPreserved(task, plan, editedIndex);
        await ValidatePlanAsync(db, session, task, plan, cancellationToken);
        return (plan, fileTemplate);
    }

    /// <summary>人工切根不得移动其他需要保留的未选中文件；被明确编辑的文件由分类、命名和占用规则决定。</summary>
    private static void EnsureUnselectedPathsPreserved(DownloadTask task, FilePlan plan, int editedIndex)
    {
        foreach (var file in task.Files.Where(file => !file.IsSelectedForDownload && file.FileIndex != editedIndex))
        {
            var current = DownloadRoundLayout.CurrentAbsolute(task, file);
            var target  = Join(plan.Root, plan.Paths[file.FileIndex]);
            if (!string.Equals(current, target, StringComparison.Ordinal))
                throw new TargetPathOccupiedException($"布局转换会移动未选中的保留文件“{current}”，拒绝修改。");
        }
    }

    /// <summary>把已确认的绝对路径表达为目标根下的相对路径；无法表达（需要 ../）时拒绝，绝不部分迁移。</summary>
    private static string RelativeUnderRoot(string absolute, string root)
    {
        var prefix = Root(root) + "/";
        if (!absolute.StartsWith(prefix, StringComparison.Ordinal))
            throw new TargetPathOccupiedException($"文件“{absolute}”无法在目标根“{root}”下安全表达，拒绝布局转换。");
        return RelativeDownloadPathPolicy.Normalize(absolute[prefix.Length..]);
    }

    private string ContentName(DownloadTask task, DownloadTaskFile file, TorrentNamingIngredients? ingredients)
    {
        var parsed = aniParser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath));
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

        var originalName = DownloadEpisodeResolver.FileName(file.OriginalRelativePath);
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
                                         HashSet<string>?     verificationMappingPaths = null)
    {
        var snapshot = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving || !SameRoot(snapshot.SavePath, DownloadRoundLayout.Root(task)))
            throw new DownloadFileIdentityException("下载器保存目录不一致或仍在移动，拒绝规划。");
        var remote  = await session.ListFilesAsync(task.InfoHash, ct);
        var matched = MatchRemoteFiles(task, remote);
        foreach (var file in task.Files)
        {
            var target = plan.Paths[file.FileIndex];
            // Conservative: reject swaps/chains rather than rename into another file's current path.
            if (matched.Any(other => other.Key != file.FileIndex && EqualPath(other.Value.Path, target)))
                throw new TargetPathOccupiedException("目标路径占用另一文件当前路径，不能安全执行改名。");
        }

        await ValidateTargetOccupancyAsync(db, session, task, plan, remote, ct, verificationMappingPaths);
    }

    private async Task ValidateTargetOccupancyAsync(ApplicationDbContext               db, IQbittorrentSession session,
                                                    DownloadTask                       task, FilePlan plan,
                                                    IReadOnlyList<TorrentFileSnapshot> remote, CancellationToken ct,
                                                    HashSet<string>?                   verificationMappingPaths = null)
    {
        var matched = MatchRemoteFiles(task, remote, true);
        var entries = plan.Paths.ToList();
        foreach (var (index, path) in entries)
        {
            RelativeDownloadPathPolicy.Normalize(path);
            if (entries.Any(other => other.Key != index && PathsOverlap(path, other.Value)) ||
                matched.Any(other => other.Key != index && PathsOverlap(path, other.Value.Path)))
                throw new TargetPathOccupiedException("目标路径与同种子另一文件目标/当前路径相同或互为父子，不能安全执行。");
        }

        var currentRoot = DownloadRoundLayout.Root(task);
        var targets = plan.Paths.Values.Select(path => Join(plan.Root, path))
                          .Concat(plan.Paths.Values.Select(path => Join(currentRoot, path)))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var sizeByIndex = task.Files.ToDictionary(file => file.FileIndex, file => file.Size);
        foreach (var pair in plan.Paths)
        {
            targetSizes[Join(plan.Root, pair.Value)]   = sizeByIndex[pair.Key];
            targetSizes[Join(currentRoot, pair.Value)] = sizeByIndex[pair.Key];
        }

        // 受管与外部占用采用同一证据要求：休眠或已被替换的任务也可能保留真实文件，不能直接放行。
        var others = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .Where(value => value.Id != task.Id).ToListAsync(ct);
        var managedTasksByHash = others.GroupBy(value => value.InfoHash, StringComparer.OrdinalIgnoreCase)
                                       .ToDictionary(group => group.Key, group => group.First(),
                                                     StringComparer.OrdinalIgnoreCase);
        foreach (var other in others)
        {
            var hasPendingFileOperation = other.PendingSavePath is not null    ||
                                          DownloadRoundLayout.IsPending(other) ||
                                          other.Files.Any(file => file.PendingRenameFrom is not null ||
                                                                  file.PendingRenameTo is not null);
            if (other.Files.Count == 0 && targets.Any(path =>
                                                          IsWithin(path, other.SavePath) ||
                                                          (other.PendingSubmitSavePath is not null &&
                                                           IsWithin(path, other.PendingSubmitSavePath))))
                throw new TargetPathOccupiedException("目标目录可能被其他尚未初始化的任务占用。");
            foreach (var file in other.Files)
            {
                var occupied = DownloadRoundLayout.OccupiedPaths(other, file).ToList();
                foreach (var target in targets)
                foreach (var path in occupied)
                {
                    if (!PathsOverlap(target, path)) continue;
                    // 验证映射许可：仅绑定当前校验意图的候选精确路径放行同路径重叠，供受控映射执行，
                    // 不授权后续任何复用、归档或启动（已验证复用许可须凭校验通过的结果另行取得）。
                    if (verificationMappingPaths is not null    &&
                        verificationMappingPaths.Contains(path) &&
                        !HasUnsettledReuseLayout(other)         &&
                        targetSizes[target] == file.Size        &&
                        EqualPath(target, path)) continue;
                    var matchingReplacementFile = !hasPendingFileOperation         && EqualPath(target, path) &&
                                                  targetSizes[target] == file.Size &&
                                                  HasVerifiedReuseEvidence(task, path, file.Size);
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
        // 同路径同大小不再构成放行：只有绑定该路径且仍有效的 ReusedVerified 证据（复用校验通过）
        // 才允许与外部种子共存于同一路径；独立下载的新文件一律按占用处理。
        foreach (var other in await session.ListTorrentsAsync(null, ct))
        {
            if (string.Equals(other.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)) continue;
            var managedTask = managedTasksByHash.GetValueOrDefault(other.Hash);
            try
            {
                var otherRoot = Root(other.SavePath);
                foreach (var file in await session.ListFilesAsync(other.Hash, ct))
                {
                    var occupied = otherRoot + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                    foreach (var target in targets)
                    {
                        if (!PathsOverlap(target, occupied)) continue;
                        if (verificationMappingPaths is not null        &&
                            verificationMappingPaths.Contains(occupied) &&
                            other is
                            {
                                IsMoving: false, Category: not (QbittorrentTorrentCategory.Checking
                                                             or QbittorrentTorrentCategory.Error
                                                             or QbittorrentTorrentCategory.Unknown)
                            } &&
                            (managedTask is null ||
                             (!HasUnsettledReuseLayout(managedTask) &&
                              (file.Size <= 0 || file.Progress >= 1 || !file.IsSelectedForDownload))) &&
                            EqualPath(target, occupied)                                               &&
                            targetSizes[target] == file.Size) continue;
                        switch (managedTask)
                        {
                            // 复用证据驱动：本任务该文件经校验确认与占用方复用同一份数据时才共存；
                            // 证据绑定路径与大小，路径、选择或大小变化后作废（HasVerifiedReuseEvidence 内核对）。
                            case var _ when EqualPath(target, occupied) && targetSizes[target] == file.Size &&
                                            HasVerifiedReuseEvidence(task, occupied, file.Size) :
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
        task.PendingSavePath             = plan.Root;
        // 新计划重置布局请求台账：确认窗口与补发预算随计划生效重新计算。
        task.PendingLayoutRequestedAtUtc = null;
        task.PendingLayoutRequestCount   = 0;
        foreach (var file in task.Files)
        {
            file.PendingRenameFrom = DownloadRoundLayout.CurrentRelative(task, file);
            file.PendingRenameTo   = plan.Paths[file.FileIndex];
            file.UpdatedAtUtc      = timeProvider.GetUtcNow();
        }
    }

    /// <summary>布局等待的到期调度：持久化下一次观察时间，到期前的对账轮次跳过本任务。</summary>
    private static async Task DeferPendingAsync(
        ApplicationDbContext db, DownloadTask task, DateTimeOffset dueUtc, CancellationToken ct)
    {
        task.NextObservationAtUtc = dueUtc;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    ///     旧归档日志确认后，完成收尾使用的固定计划：目标根与逐文件相对路径就是已发布的现状，
    ///     不再调用新版 Plan() 重新切根。只在持久化的决策记录、根和文件状态自洽时成立。
    ///     ReuseUnverified 只是历史复用失败记录，独立下载的文件仍可沿用旧布局；ReusedVerified
    ///     才是仍需保护的共享引用，不能由固定计划擅自移动。
    /// </summary>
    private static bool TryLegacyArchiveFinalPlan(DownloadTask task, out FilePlan plan)
    {
        plan = default!;
        if (DownloadPathDecision.FromJson(task.PathDecision) is not
                { Kind: DownloadPathDecisionKinds.LegacyArchiveFinal } decision ||
            !SameRoot(decision.Root, task.SavePath)                             ||
            HasPending(task)                                                    ||
            task.Files.Count == 0                                               ||
            task.Files.Any(file => file is { Kind: DownloadFileKind.Seed } or
            {
                VerificationOutcome: not null and
                                     not DownloadFileVerificationOutcomes.ReuseUnverified
            }))
            return false;

        var paths = new Dictionary<int, string>();
        foreach (var file in task.Files)
        {
            if (string.IsNullOrWhiteSpace(file.CurrentRelativePath)) return false;
            try
            {
                paths.Add(file.FileIndex, RelativeDownloadPathPolicy.Normalize(file.CurrentRelativePath));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        plan = new FilePlan(Root(task.SavePath), paths);
        return true;
    }

    /// <summary>
    ///     识别升级前纯正片归档日志：目标是全局下载根，选中文件带媒体目录前缀，未选中文件保持既有
    ///     相对停靠路径。只校验已登记路径的结构，不重新渲染文件名：模板是实时规则，恢复必须沿用日志。
    /// </summary>
    private bool IsLegacyArchiveLog(DownloadTask task, FilePlan pendingPlan)
    {
        if (task.VerificationIntent is not null                        || task.Files.Count == 0 ||
            task.Files.Any(file => file.Kind == DownloadFileKind.Seed) ||
            string.IsNullOrWhiteSpace(task.DownloadRootPath)           || task.MediaDirectory is null ||
            task.SeedDirectory is null)
            return false;

        try
        {
            var baseRoot = Root(task.DownloadRootPath);
            if (!SameRoot(pendingPlan.Root, baseRoot)) return false;
            var media = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory);
            var seed  = RelativeDownloadPathPolicy.Normalize(task.SeedDirectory);
            RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
            var currentIsMedia = SameRoot(task.SavePath, Join(baseRoot, media));
            var mediaPrefix    = media + "/";
            return pendingPlan.Paths.Count == task.Files.Count &&
                   task.Files.All(file =>
                   {
                       if (!pendingPlan.Paths.TryGetValue(file.FileIndex, out var target)) return false;
                       var relative = RelativeDownloadPathPolicy.Normalize(target);
                       if (!file.IsSelectedForDownload)
                           return EqualPath(relative, currentIsMedia
                               ? mediaPrefix + file.CurrentRelativePath
                               : file.CurrentRelativePath);
                       return relative.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase) &&
                              relative.Length > mediaPrefix.Length;
                   });
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     校验已登记的归档目标：根必须是下载根/媒体根/任务专属根之一，相对路径不得越界或互相冲突。
    ///     只做结构校验，不重算文件名——待执行操作按日志收尾，命名模板与目录设置的改动只影响下一份计划。
    /// </summary>
    private static void ValidateRegisteredArchiveTargets(DownloadTask task, FilePlan pendingPlan)
    {
        static DownloadFileIdentityException Reject(string reason) => new(
            $"归档目标计划无效：{reason}，停止恢复旧文件操作。");

        if (task.Files.Count == 0 || string.IsNullOrWhiteSpace(task.DownloadRootPath) ||
            task.MediaDirectory is null || task.SeedDirectory is null)
            throw Reject("任务缺少布局配置");

        var baseRoot    = Root(task.DownloadRootPath!);
        var media       = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory!);
        var seed        = RelativeDownloadPathPolicy.Normalize(task.SeedDirectory!);
        var mediaRoot   = Join(baseRoot, media);
        var taskRoot    = DownloadTaskPlacement.TaskRoot(baseRoot, task.Id);
        var targetRoot  = Root(pendingPlan.Root);
        var allowedRoot = SameRoot(targetRoot, baseRoot) || SameRoot(targetRoot, mediaRoot) ||
                          SameRoot(targetRoot, taskRoot);
        if (!allowedRoot) throw Reject("目标根不是合法归档根");

        try
        {
            RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
            var normalized = pendingPlan.Paths.ToDictionary(pair => pair.Key,
                                                            pair => RelativeDownloadPathPolicy.Normalize(pair.Value));
            if (normalized.Count != task.Files.Count || task.Files.Any(file => !normalized.ContainsKey(file.FileIndex)))
                throw Reject("目标路径条目不完整");
            // 手工动画路径覆盖是任务自身的命名输入：登记后若被改动，旧目标不再代表当前意图，仍拒绝恢复。
            foreach (var file in task.Files.Where(file =>
                         file.IsSelectedForDownload && !string.IsNullOrWhiteSpace(file.AnimeRelativePathOverride)))
            {
                var overrideName = Path.GetFileName(file.AnimeRelativePathOverride!.Replace('\\', '/'));
                var target       = normalized[file.FileIndex];
                if (!EqualPath(target, overrideName) &&
                    !target.EndsWith("/" + overrideName, StringComparison.OrdinalIgnoreCase))
                    throw Reject("手工命名路径已改变");
            }
            if (normalized.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count ||
                normalized.Values.Any(left => normalized.Values.Any(right =>
                    right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase))))
                throw Reject("目标路径重复或互相包含");
        }
        catch (ArgumentException exception)
        {
            throw new DownloadFileIdentityException("归档目标计划无效：目标路径不合法，停止恢复旧文件操作。",
                                                    exception);
        }
    }

    private async Task<bool> ExecutePendingAsync(ApplicationDbContext db,   IQbittorrentSession session,
                                                 DownloadTask         task, CancellationToken   ct,
                                                 HashSet<string>?     verificationMappingPaths = null)
    {
        if (!HasPending(task)) return true;
        if (task.IsolationState != DownloadIsolationState.None ||
            (task.VerificationIntent is not null && verificationMappingPaths is null)) return false;
        var targetRoot = task.PendingSavePath ?? throw new DownloadFileIdentityException("待执行文件操作缺少保存目录。");
        var snapshot   = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving ||
            snapshot.Category is QbittorrentTorrentCategory.Checking or QbittorrentTorrentCategory.Error)
        {
            await DeferPendingAsync(db, task, timeProvider.GetUtcNow() + timing.VerificationSampleInterval, ct);
            return false;
        }

        if (verificationMappingPaths is not null &&
            (snapshot.Category != QbittorrentTorrentCategory.Stopped || task.IsUserPaused)) return false;
        if (task.Files.Any(file => HasVerifiedReuseEvidence(task, DownloadRoundLayout.CurrentAbsolute(task, file),
                                                            file.Size) &&
                                   (!SameRoot(DownloadRoundLayout.Root(task), targetRoot) ||
                                    !EqualPath(DownloadRoundLayout.CurrentRelative(task, file),
                                               file.PendingRenameTo!))))
            throw new DownloadFileIdentityException("文件操作日志会移动正在复用的共享文件，拒绝执行。");
        if (!SameRoot(snapshot.SavePath, DownloadRoundLayout.Root(task)) &&
            !SameRoot(snapshot.SavePath, targetRoot))
            throw new DownloadFileIdentityException("下载器被外部移动到未知目录，不能恢复操作。");
        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        // Validate every identity and reservation before executing even the first remaining side effect.
        if (task.Files.Any(file => file.PendingRenameFrom is null || file.PendingRenameTo is null))
            throw new DownloadFileIdentityException("文件操作日志不完整。");
        var matched = MatchRemoteFiles(task, remote, true);

        var pendingPlan =
            new FilePlan(targetRoot, task.Files.ToDictionary(file => file.FileIndex, file => file.PendingRenameTo!));
        // 旧版布局迁移的日志携带专门的决策记录：其相对路径按已确认的绝对路径逐文件反解，
        // 不能由当前命名模板重新推导，重启后以持久化的决策+日志为准继续恢复。
        var migration = DownloadPathDecision.FromJson(task.PathDecision) is
                            { Kind: DownloadPathDecisionKinds.LegacyLayoutMigration } decision &&
                        SameRoot(decision.Root, targetRoot);
        // 升级前已落库的归档日志目标为“下载根 + 媒体目录前缀”，不能用新版纯正片根重新推导。
        var legacyArchive = !migration && IsLegacyArchiveLog(task, pendingPlan);
        if (!migration && !legacyArchive && task.VerificationIntent is null && snapshot.Progress >= 1 &&
            !SameRoot(DownloadRoundLayout.Root(task), targetRoot))
        {
            // 已登记的文件操作按持久化的目标收尾，不按当前的命名模板或目录设置重算：模板属于实时
            // 规则，改动只影响下一份计划。这里只校验目标根是合法归档根、路径不越界且不互相冲突。
            ValidateRegisteredArchiveTargets(task, pendingPlan);
        }

        var now       = timeProvider.GetUtcNow();
        var needsMove = !SameRoot(snapshot.SavePath, targetRoot);
        var renamePending = task.Files
                                .Where(file => matched[file.FileIndex].Path !=
                                               RelativeDownloadPathPolicy.Normalize(file.PendingRenameTo!))
                                .Select(file => file.FileIndex)
                                .ToList();

        // 布局确认窗口与有界补发：同一计划已发出的请求在窗口内不重发（先查后决定是否补发），
        // 查询轮次与 pulse 不消耗预算；预算按实际发出的请求轮数扣减，耗尽仍未生效即受阻待人工核对。
        if (renamePending.Count > 0 || needsMove)
        {
            if (task is { PendingLayoutRequestCount: > 0, PendingLayoutRequestedAtUtc: { } issuedAt } &&
                now < issuedAt + timing.LayoutConfirmationWindow)
            {
                await DeferPendingAsync(db, task, now + timing.PreparationSampleInterval, ct);
                return false;
            }

            if (task.PendingLayoutRequestCount >= timing.MaxLayoutRequestIssues)
                throw new DownloadLayoutConfirmationException($"布局请求（改名/移动）已发出 {task.PendingLayoutRequestCount} " +
                                                              "轮仍未生效，为避免请求风暴已停止推进，请人工核对下载器。");

            // 副作用前持久化请求台账：重启后确认窗口与补发预算仍然生效。
            task.PendingLayoutRequestedAtUtc =  now;
            task.PendingLayoutRequestCount   += 1;
            await db.SaveChangesAsync(ct);
        }

        await ValidateTargetOccupancyAsync(db, session, task, pendingPlan, remote, ct, verificationMappingPaths);
        if (renamePending.Count > 0)
        {
            foreach (var index in renamePending.OrderBy(value => value))
            {
                var file = task.Files.Single(item => item.FileIndex == index);
                var from = file.PendingRenameFrom!.Replace('\\', '/');
                var to   = RelativeDownloadPathPolicy.Normalize(file.PendingRenameTo!);
                if (matched[file.FileIndex].Path == to) continue; // Remote succeeded, DB acknowledgement was lost.
                await session.RenameTorrentFileAsync(task.InfoHash, from, to, ct);
            }

            remote  = await session.ListFilesAsync(task.InfoHash, ct);
            matched = MatchRemoteFiles(task, remote, true);
            if (task.Files.Any(file => matched[file.FileIndex].Path !=
                                       RelativeDownloadPathPolicy.Normalize(file.PendingRenameTo!)))
            {
                await DeferPendingAsync(db, task, now + timing.PreparationSampleInterval, ct);
                return false;
            }
        }

        await ValidateTargetOccupancyAsync(db, session, task, pendingPlan, remote, ct, verificationMappingPaths);
        if (needsMove) await session.SetLocationAsync(task.InfoHash, targetRoot, ct);
        snapshot = await SnapshotAsync(session, task.InfoHash, ct);
        if (snapshot.IsMoving || !SameRoot(snapshot.SavePath, targetRoot))
        {
            await DeferPendingAsync(db, task, now + timing.PreparationSampleInterval, ct);
            return false;
        }

        // Only publish paths after every remote rename and the asynchronous move have been observed.
        // 新一轮未确认时（校验映射、下载期改名）只清操作日志：成片路径须等校验或归档成功后发布。
        foreach (var file in task.Files)
        {
            file.CurrentRelativePath = file.PendingRenameTo!;
            file.PendingRenameFrom   = null;
            file.PendingRenameTo     = null;
            file.UpdatedAtUtc        = timeProvider.GetUtcNow();
        }

        // 旧版归档日志确认后，把“最终布局固定为旧根 + 相对路径”持久落库：与 pending 清理同一事务，
        // 完成状态落库前重启也能据此继续收尾，不会被新版 Plan() 再次切到媒体根。
        if (legacyArchive)
            task.PathDecision = new DownloadPathDecision
            {
                Kind         = DownloadPathDecisionKinds.LegacyArchiveFinal,
                Root         = targetRoot,
                Reason       = "升级前的归档日志已确认，完成收尾沿用旧版全局下载根与媒体目录前缀。",
                DecidedAtUtc = timeProvider.GetUtcNow()
            }.ToJson();

        // 成片根只在文件已确认迁入正式目标后发布：本轮从未暂存的普通任务在正式根原地改名可直接发布；
        // 重下轮的目标必须不是任务专属暂存根，且下载器快照已位于该目标根（迁入/改名已被观察）。
        // 整种进度 100% 不能单独证明正式路径——下载期暂存日志会随重下种子一起到 100%，而正式归档
        // 仍可能因目标占用受阻（ArchiveReview）；此时逐集现任必须继续沿已发布的旧成片完整路径解析。
        // 校验映射只更新逐文件真实路径，成片路径由 DownloadService 按校验证据逐个发布。
        var stagingRoot = string.IsNullOrWhiteSpace(task.DownloadRootPath)
            ? null
            : DownloadTaskPlacement.TaskRoot(task.DownloadRootPath, task.Id);
        var isStagingTarget = stagingRoot is not null && SameRoot(targetRoot, stagingRoot);
        var publishRound = !DownloadRoundLayout.IsPending(task) ||
                           (verificationMappingPaths is null &&
                            !isStagingTarget                 &&
                            SameRoot(snapshot.SavePath, targetRoot));
        if (publishRound) DownloadRoundLayout.Publish(task, task.Files, targetRoot);
        task.PendingSavePath             = null;
        task.PendingLayoutRequestedAtUtc = null;
        task.PendingLayoutRequestCount   = 0;
        task.NextObservationAtUtc        = null;
        if (task.Status == DownloadTaskStatus.Completed) task.ArchiveReview = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<DownloadTaskFilesView> ViewAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken ct)
    {
        var supersededNames = await SupersededNamesAsync(db, task, ct);
        return new DownloadTaskFilesView(task.Id, DownloadRoundLayout.Root(task),
                                         ProjectFiles(task, supersededNames), HasPending(task));
    }

    private IReadOnlyList<DownloadFileView> ProjectFiles(
        DownloadTask task, IReadOnlyDictionary<Guid, string> supersededNames) =>
        task.Files.OrderBy(file => file.FileIndex)
            .Select(file =>
             {
                 var parsed = aniParser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath));
                 var (episode, end) = Episodes(task, file, parsed);
                 if (file.EpisodeNumberOverride is null &&
                     parsed?.Episode is null            &&
                     parsed?.StartEpisode is null)
                     episode = null; // Display identification honestly; task episode fallback is only for naming.
                 var supersededByNaming = file.SupersededByTaskId is { } supersededId
                     ? supersededNames.GetValueOrDefault(supersededId)
                     : null;
                 return new DownloadFileView(file.FileIndex, file.OriginalRelativePath,
                                             DownloadRoundLayout.CurrentRelative(task, file),
                                             file.Kind, file.ClassificationReason, episode, end,
                                             file.AnimeRelativePathOverride, file.Size,
                                             file.IsSelectedForDownload, file.SelectionReason,
                                             file.SupersededByTaskId, supersededByNaming, file.VerificationOutcome);
             }).ToList();

    /// <summary>承接方命名：按本任务文件的 SupersededByTaskId 集合一次查询；承接任务已删除时对应字段保持 null。</summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> SupersededNamesAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken ct)
    {
        var ids = task.Files.Where(file => file.SupersededByTaskId is not null)
                      .Select(file => file.SupersededByTaskId!.Value)
                      .Distinct()
                      .ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        return await db.DownloadTasks.AsNoTracking()
                       .Where(value => ids.Contains(value.Id))
                       .ToDictionaryAsync(value => value.Id, value => value.NamingSnapshot, ct);
    }

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

    private static bool EqualPath(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameRelativePath(string? left, string right) =>
        string.Equals(left?.Replace('\\', '/'), right, StringComparison.Ordinal);

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

    /// <summary>
    ///     迁让/复用候选的轻量键：携带所属任务的内部文件键与真实大小。
    ///     消费方按路径、大小关联自身文件，不能把不同任务的内部键当作同一身份。
    /// </summary>
    private readonly record struct ReplacementAsideEntry(
        Guid   OwnerId,
        string InfoHash,
        string SavePath,
        int    FileIndex,
        string CurrentRelativePath,
        long   Size);

    private sealed record FilePlan(string Root, IReadOnlyDictionary<int, string> Paths);
}
