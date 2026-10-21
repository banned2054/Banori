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
///     目标路径被其他任务、下载器种子或同种子内其他文件占用。占用本身不破坏数据：调用方可以安全地
///     推迟迁移并记录真实冲突原因，但不能据此启动写入。
/// </summary>
public sealed class TargetPathOccupiedException(string message) : InvalidOperationException(message);

/// <summary>
///     文件身份无法确认：下载器清单与持久化身份对不上、日志不完整、原始元数据不一致等。
///     这类失败必须阻塞推进（失败/待核对），绝不进入任何可启动或可迁移的回退——身份不明时
///     既不能证明目标可写，也不能证明当前布局可保留。
/// </summary>
public class DownloadFileIdentityException(string message) : InvalidOperationException(message);

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
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ass", ".ssa", ".srt", ".sup" };

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
        EnsureEditable(await LoadAsync(db, taskId, cancellationToken));
        var task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
        if (HasPending(task)) throw new InvalidOperationException("文件操作尚未确认，请等待对账后重试。");
        var file    = FindFile(task, index);
        var current = file.CurrentRelativePath;
        ApplyEdit(file, request);
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        var plan = Plan(task, DownloadTaskPlacement.IsTaskScoped(task));
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
        EnsureEditable(await LoadAsync(db, taskId, cancellationToken));
        var task = await InitializeUnderGateAsync(db, session, taskId, cancellationToken);
        if (HasPending(task) && !await ExecutePendingAsync(db, session, task, cancellationToken))
            throw new InvalidOperationException("下载器正在移动文件，请等待对账后再修改。");
        ApplyEdit(FindFile(task, index), request);
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        // 任务专属根的任务执行任务内计划（改名只发生在命名空间内）；正式布局的迁入只经归档链。
        var plan = Plan(task, DownloadTaskPlacement.IsTaskScoped(task));
        await ValidatePlanAsync(db, session, task, plan, cancellationToken);
        Stage(task, plan);
        await db.SaveChangesAsync(cancellationToken); // Overrides and journal must survive before any remote side effect.
        await ExecutePendingAsync(db, session, task, cancellationToken);
        return View(task);
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

            var snapshot = await SnapshotAsync(session, infoHash, cancellationToken);
            var remote   = MatchRemoteFiles(task.Files, await session.ListFilesAsync(infoHash, cancellationToken));
            if (snapshot.IsMoving                                                                            ||
                snapshot.Category is QbittorrentTorrentCategory.Checking or QbittorrentTorrentCategory.Error ||
                !SameRoot(snapshot.SavePath, task.SavePath)                                                  ||
                task.Files.All(file => !file.IsSelectedForDownload)                                          ||
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
        if ((await ReadTaskAsync(taskId, cancellationToken)).ArchiveReview is { } remaining &&
            DownloadArchiveReview.FromJson(remaining) is { } blocked)
            throw new InvalidOperationException(blocked.Reason);
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
            task.Files.Any(file => HasVerifiedReuseEvidence(task, Join(task.SavePath, file.CurrentRelativePath),
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
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        var plan = Plan(task);
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
    ///     枚举归档目标的复用候选：目标路径上精确相等且大小一致的数据——外部种子文件（未登记）或
    ///     受管承接对象（现任成片/休眠数据）。父子重叠与大小不符不构成候选（前者无法映射，后者是
    ///     不同压制的真实覆盖）。候选只是线索，能否复用必须经停止状态的受控映射与 recheck 校验证实。
    /// </summary>
    public async Task<IReadOnlyList<ReuseCandidate>> EnumerateReuseCandidatesAsync(
        IQbittorrentSession session, Guid taskId, IReadOnlyList<(int FileIndex, string TargetPath)> targets,
        CancellationToken   cancellationToken = default)
    {
        await using var db          = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             task        = await LoadAsync(db, taskId, cancellationToken);
        var             sizeByIndex = task.Files.ToDictionary(file => file.FileIndex, file => file.Size);
        var             result      = new List<ReuseCandidate>();

        foreach (var entry in await LoadReplacementAsideFilesAsync(db, task, cancellationToken))
        {
            var absolute = Join(entry.SavePath, entry.CurrentRelativePath);
            foreach (var (index, target) in targets)
                if (EqualPath(target, absolute) && sizeByIndex[index] == entry.Size)
                    result.Add(new ReuseCandidate(index, absolute, entry.Size, true));
        }

        var others = await db.DownloadTasks.AsNoTracking().Where(value => value.Id != task.Id)
                             .Select(value => new { value.Id, value.InfoHash }).ToListAsync(cancellationToken);
        var managedHashes = others.Select(value => value.InfoHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var torrent in await session.ListTorrentsAsync(null, cancellationToken))
        {
            if (string.Equals(torrent.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)) continue;
            // 受管种子只经承接对象（现任/休眠）参与候选；其余受管占用不是候选而是真实冲突，整体降级。
            if (managedHashes.Contains(torrent.Hash)) continue;
            try
            {
                var root = Root(torrent.SavePath);
                foreach (var file in await session.ListFilesAsync(torrent.Hash, cancellationToken))
                {
                    var occupied = root + "/" + (file.Path ?? string.Empty).Trim().TrimStart('/');
                    foreach (var (index, target) in targets)
                    {
                        if (!EqualPath(target, occupied) || file.Size != sizeByIndex[index]) continue;
                        result.Add(new ReuseCandidate(index, occupied, file.Size, false));
                        break;
                    }
                }
            }
            catch (ArgumentException)
            {
                // 无法解析保存根的外部种子跳过。
            }
        }

        return result;
    }

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
        if (intent.Step != DownloadVerificationStep.Mapping || !SameRoot(task.SavePath, intent.CandidateRoot))
            throw new DownloadFileIdentityException("受控映射阶段或保存根已改变。");
        var mappingPaths = intent.Files.Where(file => file.IsCandidate).Select(file => file.VerificationPath)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!mappingPaths.SetEquals(candidateRelativePaths.Values.Select(path => Join(task.SavePath, path))))
            throw new DownloadFileIdentityException("受控映射许可与持久化意图不一致。");

        // 布局只由提交规划生成一次；执行与恢复直接消费同一份持久化计划。
        var rootPrefix = Root(task.SavePath) + "/";
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

        var plan = new FilePlan(task.SavePath, paths);
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
        await EnsureNamingIngredientsAsync(task, cancellationToken);
        // 任务专属根且尚未完成归档：同一套最终相对路径挂在任务根下执行（下载期绝不写正式布局），
        // 归档轮（progress >= 1）才用最终根计算迁移计划。
        var intraTask = DownloadTaskPlacement.IsTaskScoped(task) && snapshot.Progress < 1;
        var plan      = Plan(task, intraTask);
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
        if (task.Files.Any(file => HasVerifiedReuseEvidence(task, Join(task.SavePath, file.CurrentRelativePath),
                                                            file.Size) &&
                                   (!SameRoot(task.SavePath, plan.Root) ||
                                    !EqualPath(file.CurrentRelativePath, plan.Paths[file.FileIndex]))))
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
    private async Task<bool> MoveConflictingReplacementFilesAsideAsync(
        ApplicationDbContext db,   IQbittorrentSession session,  DownloadTask      task,
        FilePlan             plan, TorrentSnapshot     snapshot, CancellationToken ct)
    {
        if (snapshot.Progress < 1) return true;

        var asideCandidates = await LoadReplacementAsideFilesAsync(db, task, ct);
        if (asideCandidates.Count == 0) return true;

        var targets = plan.Paths.Values.SelectMany(path => new[] { Join(plan.Root, path), Join(task.SavePath, path) })
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
                var occupied = Join(owner.SavePath, file.CurrentRelativePath);
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
            var occupied = new List<string> { Join(other.SavePath, file.CurrentRelativePath) };
            if (other.PendingSavePath is not null) occupied.Add(Join(other.PendingSavePath, file.CurrentRelativePath));
            foreach (var pending in
                     new[] { file.PendingRenameFrom, file.PendingRenameTo }.Where(value => value is not null))
            {
                occupied.Add(Join(other.SavePath, pending!));
                if (other.PendingSavePath is not null) occupied.Add(Join(other.PendingSavePath, pending!));
            }

            if (occupied.Any(path => asidePaths.Any(aside => PathsOverlap(path, aside))))
                throw new TargetPathOccupiedException(
                                                      $"迁让对象路径仍被任务“{other.NamingSnapshot}”（任务 ID {other.Id}）引用，不能移动其共享文件，请先人工处理。");
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
                        throw new
                            TargetPathOccupiedException($"迁让对象路径“{occupied}”被下载器中的其他种子（{torrent.Name}）交叉引用，不能移动共享文件，请先人工处理。");
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
                                             StringComparison.Ordinal)                                &&
                               EqualPath(file.VerificationPath, absolutePath)                         &&
                               EqualPath(Join(task.SavePath, file.CurrentRelativePath), absolutePath) &&
                               file.Size == occupantSize);

    /// <summary>
    ///     本任务完成替换时的迁让与放行对象：每集登记表中本任务为待完成优胜的集，其现任视频文件
    ///     （现任所属任务须已完成且未被声明替换）；以及被本任务逐集承接取消选择的休眠文件
    ///     （所属任务任意状态——部分承接的合集仍在下载其余集）。
    /// </summary>
    private async Task<List<ReplacementAsideEntry>> LoadReplacementAsideFilesAsync(
        ApplicationDbContext db, DownloadTask task, CancellationToken ct)
    {
        var result = new List<ReplacementAsideEntry>();
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

    private async Task<DownloadTask> InitializeUnderGateAsync(ApplicationDbContext db,     IQbittorrentSession session,
                                                              Guid                 taskId, CancellationToken   ct,
                                                              bool                 adoptExisting = false)
    {
        var task = await LoadAsync(db, taskId, ct);
        if (!TorrentDownloadPolicy.TryLoad(task.TorrentFile, out var torrent, out var isMultiFile))
            throw new DownloadFileIdentityException("种子原始元数据不存在或文件清单无效，不能伪造原始文件路径。");
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
        foreach (var file in files)
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

            if (paths.Count == 0) paths.Add(file.CurrentRelativePath.Replace('\\', '/'));
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
         ?? task.Files.Select(file => aniParser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath)))
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

    /// <summary>
    ///     计算文件目标计划。默认给出最终布局（根 + 正式相对路径）；intraTask 为 true 时把同一套最终
    ///     相对路径挂在任务专属根下——下载期只触碰按构造安全的命名空间，归档时同一相对路径经根迁移
    ///     进入正式布局，两次计划因此共用一套目标名。
    /// </summary>
    private FilePlan Plan(DownloadTask task, bool intraTask = false)
    {
        var basePath = Root(task.DownloadRootPath ?? task.SavePath);
        var media    = RelativeDownloadPathPolicy.Normalize(task.MediaDirectory!);
        var seed     = RelativeDownloadPathPolicy.Normalize(task.SeedDirectory!);
        RelativeDownloadPathPolicy.ValidateDirectories(media, seed);
        var ingredients        = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        var cleanPrefix        = media + "/";
        var currentRootIsMedia = SameRoot(task.SavePath, Join(basePath, media));
        var clean              = task.Files.All(file => file.Kind != DownloadFileKind.Seed) && currentRootIsMedia;
        var absolute           = new Dictionary<int, string>();
        foreach (var file in task.Files)
        {
            // Unselected content keeps its confirmed qB path: cross-package losers must not claim a media
            // incumbent path, while dormant data may already live under .replaced. In-pack losers demoted
            // to Seed retain the established seed-directory layout.
            var keepCurrentPath = !file.IsSelectedForDownload &&
                                  file is not { Kind: DownloadFileKind.Seed, SupersededByTaskId: null };
            var relative = keepCurrentPath
                ? currentRootIsMedia ? cleanPrefix + file.CurrentRelativePath : file.CurrentRelativePath
                : file.Kind == DownloadFileKind.Seed
                    ? seed  + "/" + file.OriginalRelativePath
                    : media + "/" + (file.AnimeRelativePathOverride ?? ContentName(task, file, ingredients));
            absolute.Add(file.FileIndex, RelativeDownloadPathPolicy.Normalize(relative));
        }

        // Only retain an existing clean base/media root; never move an established mixed root back to clean.
        var root = clean ? Join(basePath, media) : basePath;
        var paths = absolute.ToDictionary(pair => pair.Key,
                                          pair => clean ? pair.Value[cleanPrefix.Length..] : pair.Value);
        if (paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count ||
            paths.Values.Any(left =>
                                 paths.Values.Any(right =>
                                                      right.StartsWith(left + "/",
                                                                       StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("多个文件的目标路径冲突，请修改集数或手工动画路径。");
        if (intraTask) return new FilePlan(task.SavePath, paths);
        // 能在现有根下表达全部最终目标时保留根，避免为归档非候选文件先迁移共享候选。
        if (task.Files.Any(file => HasVerifiedReuseEvidence(task, Join(task.SavePath, file.CurrentRelativePath),
                                                            file.Size)) &&
            paths.Values.All(path => IsWithin(Join(root, path), task.SavePath)))
            return new FilePlan(task.SavePath, paths.ToDictionary(pair => pair.Key,
                                                                  pair =>
                                                                      Join(root, pair.Value)
                                                                          [(Root(task.SavePath).Length + 1)..]));
        return new FilePlan(root, paths);
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
        if (snapshot.IsMoving || !SameRoot(snapshot.SavePath, task.SavePath))
            throw new DownloadFileIdentityException("下载器保存目录不一致或仍在移动，拒绝规划。");
        var remote  = await session.ListFilesAsync(task.InfoHash, ct);
        var matched = MatchRemoteFiles(task.Files, remote);
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

        // 受管与外部占用采用同一证据要求：休眠或已被替换的任务也可能保留真实文件，不能直接放行。
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
                    // 验证映射许可：仅绑定当前校验意图的候选精确路径放行同路径重叠，供受控映射执行，
                    // 不授权后续任何复用、归档或启动（已验证复用许可须凭校验通过的结果另行取得）。
                    if (verificationMappingPaths is not null    &&
                        verificationMappingPaths.Contains(path) &&
                        !hasPendingFileOperation                && targetSizes[target] == file.Size &&
                        EqualPath(target, path))
                        continue;
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
                        // 逐集承接放行覆盖等路径的迁让对象（交叉做种或休眠数据），与数据库侧同一集合。
                        if (verificationMappingPaths is not null && verificationMappingPaths.Contains(occupied) &&
                            EqualPath(target, occupied)          && targetSizes[target] == file.Size)
                            continue;
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
            file.PendingRenameFrom = file.CurrentRelativePath;
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
        if (task.Files.Any(file => HasVerifiedReuseEvidence(task, Join(task.SavePath, file.CurrentRelativePath),
                                                            file.Size) &&
                                   (!SameRoot(task.SavePath, targetRoot) ||
                                    !EqualPath(file.CurrentRelativePath, file.PendingRenameTo!))))
            throw new DownloadFileIdentityException("文件操作日志会移动正在复用的共享文件，拒绝执行。");
        if (!SameRoot(snapshot.SavePath, task.SavePath) && !SameRoot(snapshot.SavePath, targetRoot))
            throw new DownloadFileIdentityException("下载器被外部移动到未知目录，不能恢复操作。");
        var remote = await session.ListFilesAsync(task.InfoHash, ct);
        // Validate every identity and reservation before executing even the first remaining side effect.
        if (task.Files.Any(file => file.PendingRenameFrom is null || file.PendingRenameTo is null))
            throw new DownloadFileIdentityException("文件操作日志不完整。");
        var matched = MatchRemoteFiles(task.Files, remote, true);

        var pendingPlan =
            new FilePlan(targetRoot, task.Files.ToDictionary(file => file.FileIndex, file => file.PendingRenameTo!));
        if (task.VerificationIntent is null && snapshot.Progress >= 1 && !SameRoot(task.SavePath, targetRoot))
        {
            var expected = Plan(task);
            if (!SameRoot(expected.Root, pendingPlan.Root) ||
                expected.Paths.Any(pair => !EqualPath(pair.Value, pendingPlan.Paths[pair.Key])))
                throw new DownloadFileIdentityException("归档目标计划已改变，停止恢复旧文件操作。");
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
            if (task.PendingLayoutRequestCount > 0               &&
                task.PendingLayoutRequestedAtUtc is { } issuedAt &&
                now < issuedAt + timing.LayoutConfirmationWindow)
            {
                await DeferPendingAsync(db, task, now + timing.PreparationSampleInterval, ct);
                return false;
            }

            if (task.PendingLayoutRequestCount >= timing.MaxLayoutRequestIssues)
                throw new DownloadLayoutConfirmationException(
                                                              $"布局请求（改名/移动）已发出 {task.PendingLayoutRequestCount} 轮仍未生效，为避免请求风暴已停止推进，请人工核对下载器。");

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
            matched = MatchRemoteFiles(task.Files, remote, true);
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
        foreach (var file in task.Files)
        {
            file.CurrentRelativePath = file.PendingRenameTo!;
            file.PendingRenameFrom   = null;
            file.PendingRenameTo     = null;
            file.UpdatedAtUtc        = timeProvider.GetUtcNow();
        }

        task.SavePath                    = targetRoot;
        task.PendingSavePath             = null;
        task.PendingLayoutRequestedAtUtc = null;
        task.PendingLayoutRequestCount   = 0;
        task.NextObservationAtUtc        = null;
        if (task.Status == DownloadTaskStatus.Completed) task.ArchiveReview = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private DownloadTaskFilesView View(DownloadTask task) =>
        new(task.Id, task.SavePath,
            task.Files.OrderBy(file => file.FileIndex)
                .Select(file =>
                 {
                     var parsed = aniParser.Parse(DownloadEpisodeResolver.FileName(file.OriginalRelativePath));
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
