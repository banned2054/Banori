using Banned.AniParser;
using Banned.AniParser.Models;
using Banned.AniParser.Models.Enums;
using MediaManager.Core.Downloads;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Torrents;
using MediaManager.Data;
using MediaManager.Features.Anime;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace MediaManager.Features.Downloads;

/// <summary>
///     下载登记与资源导入编排：上传种子、一次性 RSS、订阅优胜条目三个入口共用的解析、
///     Bangumi 绑定、查重、同集裁决与意图落库。登记事务内只写入任务与替换状态，不登录
///     下载器、不做远端删除；提交、进度对账与远端清理由 DownloadService 的对账流程承接。
/// </summary>
public sealed class DownloadIntakeService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    DownloadCoordinationGate                coordinationGate,
    ITorrentFileSource                      torrentFileSource,
    IFeedSourceReader                       feedSourceReader,
    ManageQbittorrentSettings               qbittorrentSettings,
    AniParser                               aniParser,
    NamingTemplateStore                     namingTemplateStore,
    ReleaseNamePreviewPolicy                namingPolicy,
    CandidateEligibilityPolicy              eligibilityPolicy,
    CandidateRankingPolicy                  rankingPolicy,
    ISelectionRuleProvider                  ruleProvider,
    SubjectMetadataCache                    subjectCache,
    ISubjectLinkResolver                    subjectLinkResolver,
    TimeProvider                            timeProvider,
    ILogger<DownloadIntakeService>          logger,
    DownloadDirectorySettingsStore?         directorySettingsStore   = null,
    DownloadFileClassificationPolicy?       fileClassificationPolicy = null)
{
    /// <summary>所有下载登记的前置门禁：解析 Bangumi 绑定，未绑定返回 null（调用方拒绝下载并要求补 ID）。</summary>
    private const string SubjectUnboundMessage = "未能绑定 Bangumi Subject，请在表单补充 Bangumi ID 后重试。";

    /// <summary>上传种子的大小上限：.torrent 通常远小于该值，超限基本说明选错了文件。服务端入口始终校验。</summary>
    public const long MaxTorrentUploadBytes = 10 * 1024 * 1024;

    public async Task<Guid> CreateForFeedItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            throw new InvalidOperationException("请先在设置页启用并配置 qBittorrent 下载器。");

        FeedItem item;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            item = await db.FeedItems.AsNoTracking()
                           .Include(value => value.Subject)
                           .Include(value => value.Subscription)
                           .SingleOrDefaultAsync(value => value.Id == itemId, cancellationToken)
                ?? throw new KeyNotFoundException("找不到指定条目。");

            // P4 起手动创建与自动登记共用同一门槛：条目必须是所在同集组的当前优胜候选，
            // 历史 Accepted 条目需经人工重评触发组内重选后才能创建任务。
            var isWinner = await db.CandidateSelections.AsNoTracking()
                                   .AnyAsync(selection => selection.WinnerFeedItemId == itemId, cancellationToken);
            if (!isWinner)
                throw new InvalidOperationException("该条目不是所在集的当前优胜候选，请先重新评估或调整选择规则。");
        }

        AssertDownloadable(item);

        // 种子下载跟随所属订阅的代理配置，与 RSS 抓取保持同一出口。
        var (torrentBytes, infoHash) =
            await DownloadTorrentAsync(item.TorrentUrl!, item.Subscription.ProxyId, cancellationToken);

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            return await CreateTaskWithinContextAsync(db, item, torrentBytes, infoHash, cancellationToken);
        }
    }

    /// <summary>
    ///     一次性上传种子：创建即等待提交，不关联订阅与条目；必须绑定 Bangumi Subject（表单显式 ID 或
    ///     标题映射命中），未绑定拒绝登记并要求补 ID，绑定后命名用 Bangumi 作品名并回填 Subject 参与
    ///     跨路径同集互认。
    ///     上传时沿用 ByrTorrent 的做法先解析种子内部文件路径批量提取标题，标题映射优先检查这些文件名，
    ///     再回退到 info.name；显式 ID 会学习首个可解析的文件标题映射。
    ///     命名优先按模板渲染（标题能被解析出集数时），并落命名要素供完成改名；
    ///     解析失败回退种子内 info.name（缺失时按 hash 派生）；来源站点优先按种子内 tracker 自动识别，
    ///     siteSource 仅在 tracker 无法判定时作为兜底。
    ///     解析出单集/剧场版标识时先过统一同集裁决：既有同源同集任务按规则更优则拒绝本次上传，
    ///     新上传更优则自动顶替（ReplacesTaskId 指向旧任务）。
    /// </summary>
    public async Task<Guid> CreateFromUploadAsync(byte[] torrentBytes, string? siteSource = null, int? subjectId = null,
                                                  CancellationToken cancellationToken = default)
    {
        if (torrentBytes.Length == 0) throw new InvalidOperationException("所选文件为空，请重新选择。");

        if (torrentBytes.Length > MaxTorrentUploadBytes)
            throw new InvalidOperationException("种子文件超过 10 MB，已拒绝。");

        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            throw new InvalidOperationException("请先在设置页启用并配置 qBittorrent 下载器。");

        if (!TorrentInfoHashExtractor.TryExtractInfoHash(torrentBytes, out var infoHash))
            throw new InvalidOperationException("无法从种子文件中提取 info-hash，文件可能无效或已损坏。");

        var displayName = TorrentInfoHashExtractor.TryExtractDisplayName(torrentBytes, out var info)
            ? info
            : null;
        var fileTitleCandidates = ParseTorrentFileTitles(torrentBytes);
        var resolvedSource      = ResolveSiteSource(torrentBytes, null, null, siteSource);
        // 门禁：必须绑定 Bangumi Subject；标题映射未命中且未填 ID 时拒绝登记。
        var uploadedParsed = displayName is null ? null : aniParser.Parse(displayName);
        var binding =
            await ResolveUploadedSubjectBindingAsync(fileTitleCandidates, uploadedParsed, subjectId,
                                                     cancellationToken) ??
            throw new InvalidOperationException(SubjectUnboundMessage);
        var fileTitleFallback = fileTitleCandidates.FirstOrDefault()?.OriginTitle;
        var (namingSnapshot, namingIngredients, namingParsed) =
            await BuildNamingAsync(displayName, fileTitleFallback, resolvedSource, $"上传种子：{infoHash[..8]}",
                                   uploadedParsed, binding, cancellationToken);
        var directorySettings = await GetDirectoriesAsync(cancellationToken);

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await DownloadTaskLookups.FindActiveDuplicateIdAsync(db, infoHash, null, cancellationToken) is not null)
            throw new InvalidOperationException("同一资源已有下载任务（可能因更低优先级被替换终止）。");

        // 与条目路径一致：已完成的同资源同样不重复登记。
        if (await DownloadTaskLookups.FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
            throw new InvalidOperationException("同一资源已有已完成的下载任务。");

        // 解析出单集/剧场版标识才参与统一裁决并落集数快照；解析失败保持原行为。
        var identity = namingParsed is { } parsedIdentity && IsEpisodeScoped(parsedIdentity)
            ? parsedIdentity
            : null;
        Guid? replacesTaskId = null;
        if (identity is not null)
        {
            var config = await ruleProvider.GetAsync(cancellationToken);
            var arbitration = await ArbitrateEpisodeAsync(db, config,
                                                          BuildAdhocCandidate(infoHash, null, identity,
                                                                              binding.SubjectId),
                                                          binding.SubjectId,
                                                          TitleNormalizer.Normalize(identity.Title),
                                                          identity.Episode, resolvedSource, infoHash, null,
                                                          cancellationToken);
            if (!arbitration.Register)
            {
                // 与 RSS 路径同口径：落选留终止记录承担同 hash 查重，顶替者被删时回退重新排队。
                var superseded = new DownloadTask
                {
                    Id                = Guid.NewGuid(),
                    InfoHash          = infoHash,
                    TorrentFile       = torrentBytes,
                    TorrentUrl        = $"upload://{infoHash}",
                    SavePath          = SelectSavePath(settings.SavePath, torrentBytes, directorySettings),
                    DownloadRootPath  = settings.SavePath.Trim().TrimEnd('/', '\\'),
                    MediaDirectory    = directorySettings.MediaDirectory,
                    SeedDirectory     = directorySettings.SeedDirectory,
                    SiteSource        = resolvedSource,
                    NamingSnapshot    = namingSnapshot,
                    NamingIngredients = namingIngredients,
                    SubjectId         = binding.SubjectId,
                    Episode           = identity.Episode,
                    Version           = identity.Version,
                    ReplacedByTaskId  = arbitration.SupersededByTaskId,
                    FailedStep        = "择优",
                    Error             = "同集候选落选，保留终止记录供查重。",
                    Status            = DownloadTaskStatus.Replaced,
                    CreatedAtUtc      = timeProvider.GetUtcNow()
                };
                AddOriginalFiles(superseded);
                db.DownloadTasks.Add(superseded);
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("上传种子同集落选，已登记终止记录 {TaskId}（hash {InfoHash}）。",
                                      superseded.Id, infoHash);
                throw new InvalidOperationException($"本次上传未登记：{arbitration.SkipReason}。（已保留终止记录供查重）");
            }

            replacesTaskId = arbitration.ReplacesTaskId;
        }

        var task = new DownloadTask
        {
            Id                = Guid.NewGuid(),
            InfoHash          = infoHash,
            TorrentFile       = torrentBytes,
            TorrentUrl        = $"upload://{infoHash}",
            SavePath          = SelectSavePath(settings.SavePath, torrentBytes, directorySettings),
            DownloadRootPath  = settings.SavePath.Trim().TrimEnd('/', '\\'),
            MediaDirectory    = directorySettings.MediaDirectory,
            SeedDirectory     = directorySettings.SeedDirectory,
            SiteSource        = resolvedSource,
            NamingSnapshot    = namingSnapshot,
            NamingIngredients = namingIngredients,
            SubjectId         = binding.SubjectId,
            Episode           = identity?.Episode,
            Version           = identity?.Version ?? 1,
            ReplacesTaskId    = replacesTaskId,
            Status            = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc      = timeProvider.GetUtcNow()
        };
        AddOriginalFiles(task);
        db.DownloadTasks.Add(task);
        // 登记即替换：同事务只翻转现任状态；下载器远端清理留给协调门内对账。
        if (replacesTaskId is { } incumbentId)
            await SupersedeIncumbentAsync(db, incumbentId, task.Id, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发下绕过前置查重时由数据库活动唯一索引兜底。
            throw new InvalidOperationException("同一资源已有进行中的下载任务，重复提交已被数据库约束阻止。");
        }

        logger.LogInformation("已从上传种子创建下载任务 {TaskId}（hash {InfoHash}）。", task.Id, infoHash);
        return task.Id;
    }

    /// <summary>
    ///     RSS 链接下载：抓取 feed 后逐条下载种子并直接登记为下载任务，与上传种子一样不关联订阅与条目。
    ///     每条都必须绑定 Bangumi Subject（表单显式 ID &gt; 标题映射 &gt; feed 自带 bgmId &gt; 条目链接解析），
    ///     未绑定的条目拒绝登记并要求补 ID，绑定后命名用 Bangumi 作品名并回填 Subject 参与跨路径同集互认。
    ///     批次内先按解析标题与集数分组做同集择优（与订阅链同一套规则），组内落选者跳过；
    ///     优胜者与不成组条目登记前再过统一同集裁决（见 ArbitrateEpisodeAsync）。逐条尽力而为：
    ///     单条失败或重复不影响其他条目，结果按明细返回。feed 与种子下载共用同一代理出口。
    /// </summary>
    public async Task<RssDownloadResult> CreateFromRssFeedAsync(
        string feedUrl, Guid? proxyId = null, int? subjectId = null, CancellationToken cancellationToken = default)
    {
        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            throw new InvalidOperationException("请先在设置页启用并配置 qBittorrent 下载器。");

        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var feedUri) ||
            (feedUri.Scheme != Uri.UriSchemeHttps && !(feedUri.Scheme == Uri.UriSchemeHttp && feedUri.IsLoopback)))
            throw new ArgumentException("RSS 地址必须是 HTTPS 绝对地址（本机测试可使用 HTTP）。", nameof(feedUrl));

        var feedItems = await feedSourceReader.ReadAsync(feedUri, proxyId, cancellationToken);
        if (feedItems.Count == 0) throw new InvalidOperationException("RSS 中没有条目。");

        // 来源站点首选 .torrent 内 tracker 判定；feed 地址域名仅作种子无法判定时的回退。
        var feedHost = feedUri.Host;

        // 解析一次全批标题：择优分组与命名共用同一解析结果。
        var entries = new List<(FeedSourceItem Item, ParseResult? Parsed)>(feedItems.Count);
        foreach (var item in feedItems)
            entries.Add((item, item.TorrentUri is null ? null : aniParser.Parse(item.Title)));

        // 批内同集择优与登记期裁决共用同一份规则快照，避免批次中途规则变更造成口径不一。
        var config      = await ruleProvider.GetAsync(cancellationToken);
        var batchLosers = PlanBatchSelection(entries, config);
        var results     = new List<RssDownloadItemResult>(feedItems.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var (item, parsed) = entries[index];
            if (item.TorrentUri is null)
            {
                results.Add(new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Failed, null,
                                                      "条目没有种子地址。"));
                continue;
            }

            if (batchLosers.TryGetValue(index, out var loserReason))
            {
                results.Add(new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Superseded, null,
                                                      loserReason));
                continue;
            }

            // 登记门禁：必须绑定 Bangumi Subject，未绑定的条目拒绝下载并要求补 ID。
            SubjectBinding? binding;
            try
            {
                binding = await ResolveSubjectBindingAsync(parsed, item.Link, subjectId, item.SourceSubjectId,
                                                           proxyId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "RSS 条目 {Title} 解析 Bangumi Subject 失败。", item.Title);
                results.Add(new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Failed, null,
                                                      "读取 Bangumi Subject 失败，可稍后重试，或在表单补充 Bangumi ID 显式绑定。"));
                continue;
            }

            if (binding is null)
            {
                results.Add(new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Failed, null,
                                                      SubjectUnboundMessage));
                continue;
            }

            try
            {
                var (taskId, supersededReason) =
                    await RegisterRssItemAsync(item, parsed, settings.SavePath, proxyId, feedHost, config, binding,
                                               cancellationToken);
                results.Add(taskId is not null
                                ? new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Created, taskId, null)
                                : supersededReason is not null
                                    ? new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Superseded, null,
                                                                supersededReason)
                                    : new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Duplicate, null,
                                                                "同一资源已有下载任务。"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException)
            {
                // 并发下绕过前置查重时由数据库活动唯一索引兜底，按重复处理。
                results.Add(new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Duplicate, null,
                                                      "同一资源已有下载任务。"));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "RSS 条目 {Title} 登记下载任务失败。", item.Title);
                results.Add(new RssDownloadItemResult(item.Title, RssDownloadItemOutcome.Failed, null,
                                                      exception.Message));
            }
        }

        var result = new RssDownloadResult(results.Count(item => item.Outcome == RssDownloadItemOutcome.Created),
                                           results.Count(item => item.Outcome == RssDownloadItemOutcome.Duplicate),
                                           results.Count(item => item.Outcome == RssDownloadItemOutcome.Superseded),
                                           results.Count(item => item.Outcome == RssDownloadItemOutcome.Failed),
                                           results);
        logger.LogInformation("RSS 链接下载完成：{FeedUrl} 新建 {Created} 个、跳过重复 {Duplicate} 个、择优跳过 {Superseded} 个、失败 {Failed} 个。",
                              feedUri, result.CreatedCount, result.DuplicateCount, result.SupersededCount,
                              result.FailedCount);
        return result;
    }

    /// <summary>
    ///     批内同集择优：按（解析标题归一化, 类型, 集数）分组，多成员组用当前择优规则选一个优胜，
    ///     返回落选条目的下标与原因。无任何候选通过硬性过滤时整组兜底参与排名（一次性下载
    ///     内容不因规则消失）。解析失败、无集数、多集合集与单成员组不参与分组。
    /// </summary>
    private Dictionary<int, string> PlanBatchSelection(
        List<(FeedSourceItem Item, ParseResult? Parsed)> entries, SelectionRuleConfig config)
    {
        var losers = new Dictionary<int, string>();

        var groupable = new List<(int Index, ParseResult Parsed, EpisodeCandidate Candidate)>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Parsed is not { } parsed || !IsEpisodeScoped(parsed)) continue;
            // 批内择优只比较规则要素，Subject 在登记门禁解析后由裁决单独携带。
            groupable.Add((index, parsed, BuildAdhocCandidate(entries[index].Item.StableId,
                                                              entries[index].Item.PublishedAtUtc, parsed, 0)));
        }

        foreach (var group in groupable.GroupBy(entry => (
                                                    SeriesKey : TitleNormalizer.Normalize(entry.Parsed.Title),
                                                    MediaType : entry.Parsed.MediaType.ToString(),
                                                    Episode : entry.Parsed.Episode ?? 0)))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;

            var eligible = members
                          .Where(member => eligibilityPolicy.Evaluate(member.Candidate, config).Status ==
                                           EligibilityStatus.Eligible)
                          .Select(member => member.Candidate)
                          .ToList();
            if (eligible.Count == 0) eligible = members.Select(member => member.Candidate).ToList();

            var winner = rankingPolicy.Select(eligible, config).Winner ?? eligible[0];
            var winnerTitle = entries[members.First(member => member.Candidate.FeedItemId == winner.FeedItemId).Index]
                             .Item.Title;
            foreach (var member in members.Where(member => member.Candidate.FeedItemId != winner.FeedItemId))
                losers[member.Index] = $"同集择优落选，优胜：{winnerTitle}";
        }

        return losers;
    }

    /// <summary>
    ///     下载单条 RSS 条目的种子并在查重、同集裁决后登记任务；binding 是调用方门禁解析出的
    ///     Bangumi 绑定，命名与任务回填共用。返回 (null, null) 表示同 hash 任务已存在；
    ///     (null, 原因) 表示统一裁决落选。
    /// </summary>
    private async Task<(Guid? TaskId, string? SupersededReason)> RegisterRssItemAsync(
        FeedSourceItem      item,   ParseResult?   parsed,  string            basePath, Guid? proxyId, string feedHost,
        SelectionRuleConfig config, SubjectBinding binding, CancellationToken cancellationToken)
    {
        var (torrentBytes, infoHash) = await DownloadTorrentAsync(item.TorrentUri!.AbsoluteUri, proxyId,
                                                                  cancellationToken);

        // 命名优先按模板渲染（条目标题能被解析出集数时），其次种子内 info.name，均失败回退原始标题。
        var resolvedSource = ResolveSiteSource(torrentBytes, item.TorrentUri.AbsoluteUri, feedHost, null);
        var displayName = TorrentInfoHashExtractor.TryExtractDisplayName(torrentBytes, out var info)
            ? info
            : null;
        var (namingSnapshot, namingIngredients, namingParsed) =
            await BuildNamingAsync(item.Title, displayName, resolvedSource, $"RSS 下载：{infoHash[..8]}", parsed,
                                   binding, cancellationToken);
        var directorySettings = await GetDirectoriesAsync(cancellationToken);

        using var       coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using var db                = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await DownloadTaskLookups.FindActiveDuplicateIdAsync(db, infoHash, null, cancellationToken) is not null ||
            await DownloadTaskLookups.FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
            return (null, null);

        // 解析出单集/剧场版标识才参与统一裁决并落集数快照；解析失败保持原行为。
        var identity = namingParsed is { } parsedIdentity && IsEpisodeScoped(parsedIdentity)
            ? parsedIdentity
            : null;
        Guid? replacesTaskId = null;
        if (identity is not null)
        {
            var arbitration = await ArbitrateEpisodeAsync(db, config,
                                                          BuildAdhocCandidate(item.StableId, item.PublishedAtUtc,
                                                                              identity, binding.SubjectId),
                                                          binding.SubjectId,
                                                          TitleNormalizer.Normalize(identity.Title),
                                                          identity.Episode, resolvedSource, infoHash, null,
                                                          cancellationToken);
            if (!arbitration.Register)
            {
                // 落选仍留一条终止记录：同 hash 查重命中即知该资源已输给更高优先级任务，重复添加直接终止；
                // 顶替者被删时该记录回退重新排队。字段与正常任务一致，回退后可直接提交。
                var superseded = BuildTaskRecord(DownloadTaskStatus.Replaced, null,
                                                 arbitration.SupersededByTaskId);
                superseded.FailedStep = "择优";
                superseded.Error      = "同集候选落选，保留终止记录供查重。";
                AddOriginalFiles(superseded);
                db.DownloadTasks.Add(superseded);
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("RSS 条目同集落选，已登记终止记录 {TaskId}（hash {InfoHash}）。",
                                      superseded.Id, infoHash);
                return (null, arbitration.SkipReason);
            }

            replacesTaskId = arbitration.ReplacesTaskId;
        }

        DownloadTask BuildTaskRecord(DownloadTaskStatus status, Guid? replaces, Guid? replacedBy) =>
            new()
            {
                Id                = Guid.NewGuid(),
                InfoHash          = infoHash,
                TorrentFile       = torrentBytes,
                TorrentUrl        = item.TorrentUri!.AbsoluteUri,
                SavePath          = SelectSavePath(basePath, torrentBytes, directorySettings),
                DownloadRootPath  = basePath.Trim().TrimEnd('/', '\\'),
                MediaDirectory    = directorySettings.MediaDirectory,
                SeedDirectory     = directorySettings.SeedDirectory,
                SiteSource        = resolvedSource,
                NamingSnapshot    = namingSnapshot,
                NamingIngredients = namingIngredients,
                SubjectId         = binding.SubjectId,
                Episode           = identity?.Episode,
                Version           = identity?.Version ?? 1,
                ReplacesTaskId    = replaces,
                ReplacedByTaskId  = replacedBy,
                Status            = status,
                CreatedAtUtc      = timeProvider.GetUtcNow()
            };

        var task = BuildTaskRecord(DownloadTaskStatus.PendingSubmit, replacesTaskId, null);
        AddOriginalFiles(task);
        db.DownloadTasks.Add(task);
        // 登记即替换：同事务只翻转现任状态，低优先任务不再进入提交或继续下载；远端清理交由对账。
        if (replacesTaskId is { } incumbentId)
            await SupersedeIncumbentAsync(db, incumbentId, task.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("已从 RSS 条目创建下载任务 {TaskId}（hash {InfoHash}）。", task.Id, infoHash);
        return (task.Id, null);
    }

    /// <summary>命名快照列上限 200 字符，超长截断；标题为空白时返回 null 交给回退来源。</summary>
    private static string? TruncateSnapshot(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    /// <summary>
    ///     命名快照与命名要素：标题能被解析出集数或合集区间时按模板渲染（与订阅评估同规则：
    ///     后缀优先字幕组名、缺失退回语言），要素一并落库供完成改名按文件集数重渲染；
    ///     {title} 用已绑定的 Bangumi 作品名（中文名非空用之，否则原名），subjectName 由调用方
    ///     在登记门禁（必须绑定 Subject）中解析后传入。均无法解析时回退原始标题并返回 null 要素，
    ///     完成时按快照兜底。成功渲染的那次解析随要素一并返回，供同集裁决复用同一解析口径。
    /// </summary>
    private async Task<(string Snapshot, string? IngredientsJson, ParseResult? Parsed)> BuildNamingAsync(
        string?      primaryTitle,  string?        fallbackTitle, string?           siteSource, string lastResort,
        ParseResult? primaryParsed, SubjectBinding subject,       CancellationToken cancellationToken)
    {
        var template     = await namingTemplateStore.GetAsync(cancellationToken);
        var fileTemplate = await namingTemplateStore.GetFileTemplateAsync(cancellationToken);
        var metadata     = await subjectCache.GetCachedAsync(subject.SubjectId, cancellationToken);
        var bgmName      = metadata?.Name   ?? subject.DisplayName;
        var bgmNameCn    = metadata?.NameCn ?? string.Empty;

        var attempts = new[]
        {
            (Title : primaryTitle, Parsed : primaryParsed),
            (Title : fallbackTitle, Parsed : null)
        };
        foreach (var (title, parsedAttempt) in attempts)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            var parsed      = parsedAttempt ?? aniParser.Parse(title);
            var episodeText = parsed is null ? null : ReleaseNamePreviewPolicy.FormatEpisodeText(parsed);
            if (parsed is null || episodeText is null || string.IsNullOrWhiteSpace(parsed.Title)) continue;

            var group = string.IsNullOrWhiteSpace(parsed.Group) ? parsed.Language.ToString() : parsed.Group;
            var ingredients = NamingIngredientsComposer.Compose(template, fileTemplate, parsed, episodeText, group,
                                                                siteSource, subject.SubjectId, bgmName, bgmNameCn,
                                                                metadata?.Platform ?? string.Empty, metadata?.AirDate);
            return (namingPolicy.Create(ingredients), ingredients.ToJson(), parsed);
        }

        var fallbackIngredients = new TorrentNamingIngredients(template, subject.DisplayName, string.Empty, 1,
                                                               null, siteSource, FileTemplate : fileTemplate,
                                                               BangumiId : subject.SubjectId, BangumiName : bgmName,
                                                               BangumiNameCn : bgmNameCn,
                                                               BangumiPlatform : metadata?.Platform,
                                                               BangumiDate : metadata?.AirDate);
        return (TruncateSnapshot(primaryTitle) ?? TruncateSnapshot(fallbackTitle) ?? lastResort,
                fallbackIngredients.ToJson(), null);
    }

    /// <summary>
    ///     登记即替换未完成现任；已完成资源在新版本实际完成前仍是当前版本，必须保留 Completed，
    ///     否则后续候选的版本守卫无法将其作为替换链目标。登记不登录下载器、不做远端删除：
    ///     本地回滚无法撤销远端操作，被顶替未完成资源的清理由对账流程在提交后恢复执行。
    /// </summary>
    private async Task SupersedeIncumbentAsync(
        ApplicationDbContext db, Guid incumbentId, Guid newTaskId, CancellationToken cancellationToken)
    {
        var incumbent = await db.DownloadTasks.SingleOrDefaultAsync(value => value.Id == incumbentId,
                                                                    cancellationToken);
        if (incumbent is null                                                               ||
            incumbent.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Replaced ||
            incumbent.ReplacedByTaskId != null)
            return;

        incumbent.Status           = DownloadTaskStatus.Replaced;
        incumbent.ReplacedByTaskId = newTaskId;
        logger.LogInformation("任务 {TaskId} 因更高优先级任务 {NewTaskId} 登记被替换，远端清理交由对账流程。",
                              incumbentId, newTaskId);
    }

    /// <summary>
    ///     Subject 绑定解析链（与订阅评估同序）：表单显式 ID &gt; 标题映射 &gt; feed 自带 bgmId &gt;
    ///     条目链接解析（Mikan 页面自带 bgmId）。除标题映射外均需确保 MediaSubject 落库（本地缓存行
    ///     优先，否则调 Bangumi API），用显式 ID 或链接解析成功且能解析出系列标题时回写标题映射，
    ///     后续同系列条目直接命中缓存。
    /// </summary>
    private async Task<SubjectBinding?> ResolveSubjectBindingAsync(
        ParseResult?      parsed, Uri? itemLink, int? requestedSubjectId, int? sourceSubjectId, Guid? proxyId,
        CancellationToken cancellationToken)
    {
        var seriesKey = parsed is null ? string.Empty : TitleNormalizer.Normalize(parsed.Title);
        if (requestedSubjectId is { } explicitId)
        {
            var binding = await EnsureSubjectAsync(explicitId, cancellationToken);
            await subjectCache.SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
            return binding;
        }

        if (seriesKey.Length > 0)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var mapped = await (from mapping in db.SubjectTitleMappings.AsNoTracking()
                                join media in db.MediaSubjects.AsNoTracking() on mapping.SubjectId equals media.Id
                                where mapping.NormalizedTitle == seriesKey
                                select new { media.Id, media.Name, media.NameCn })
               .FirstOrDefaultAsync(cancellationToken);
            if (mapped is not null)
                return new SubjectBinding(mapped.Id,
                                          string.IsNullOrWhiteSpace(mapped.NameCn) ? mapped.Name : mapped.NameCn);
        }

        if (sourceSubjectId is { } feedId)
        {
            var binding = await EnsureSubjectAsync(feedId, cancellationToken);
            await subjectCache.SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
            return binding;
        }

        if (itemLink is not null)
        {
            var linkedSubjectId = await subjectLinkResolver.ResolveSubjectIdAsync(itemLink, proxyId,
                cancellationToken);
            if (linkedSubjectId is null) return null;
            var binding = await EnsureSubjectAsync(linkedSubjectId.Value, cancellationToken);
            await subjectCache.SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
            return binding;
        }

        return null;
    }

    /// <summary>
    ///     上传种子的 Subject 绑定：先按 ByrTorrent 的流程解析 torrent.Files 路径，再依次检查文件标题映射，
    ///     最后回退 info.name。这样 info.name 只是常见命名来源，不是文件名检查的前置条件。
    /// </summary>
    private async Task<SubjectBinding?> ResolveUploadedSubjectBindingAsync(
        IReadOnlyList<ParseResult> fileTitleCandidates, ParseResult? infoNameParsed, int? requestedSubjectId,
        CancellationToken          cancellationToken)
    {
        List<ParseResult> candidates = [..fileTitleCandidates];
        if (infoNameParsed is not null) candidates.Add(infoNameParsed);
        candidates = candidates.Where(parsed => !string.IsNullOrWhiteSpace(parsed.Title)).ToList();
        var preferred = candidates.FirstOrDefault();

        if (requestedSubjectId is { } explicitId)
        {
            var binding = await EnsureSubjectAsync(explicitId, cancellationToken);
            if (preferred is null) return binding;
            var normalizedTitle = TitleNormalizer.Normalize(preferred.Title);
            if (normalizedTitle.Length > 0)
                await subjectCache.SaveTitleMappingAsync(normalizedTitle, binding.SubjectId, cancellationToken);

            return binding;
        }

        var normalizedTitles = candidates.Select(parsed => TitleNormalizer.Normalize(parsed.Title))
                                         .Where(title => title.Length > 0)
                                         .Distinct(StringComparer.Ordinal)
                                         .ToArray();
        if (normalizedTitles.Length == 0) return null;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mappedTitles = await (from mapping in db.SubjectTitleMappings.AsNoTracking()
                                  join media in db.MediaSubjects.AsNoTracking() on mapping.SubjectId equals media.Id
                                  where normalizedTitles.Contains(mapping.NormalizedTitle)
                                  select new
                                  {
                                      mapping.NormalizedTitle,
                                      media.Id,
                                      media.Name,
                                      media.NameCn
                                  }).ToListAsync(cancellationToken);

        foreach (var parsed in candidates)
        {
            var normalizedTitle = TitleNormalizer.Normalize(parsed.Title);
            var mapped          = mappedTitles.FirstOrDefault(value => value.NormalizedTitle == normalizedTitle);
            if (mapped is not null)
                return new SubjectBinding(mapped.Id,
                                          string.IsNullOrWhiteSpace(mapped.NameCn) ? mapped.Name : mapped.NameCn);
        }

        return null;
    }

    /// <summary>按 ByrTorrent 所用的 ParseBatch 路径解析方式，从种子内部文件名提取作品标题候选。</summary>
    private IReadOnlyList<ParseResult> ParseTorrentFileTitles(byte[] torrentBytes)
    {
        if (!TorrentFileManifestParser.TryParse(torrentBytes, out var manifest)) return [];
        var filePaths = manifest.Files.Select(file => file.Path).ToArray();
        return aniParser.ParseBatch(filePaths)
                        .Where(parsed => !string.IsNullOrWhiteSpace(parsed.Title))
                        .ToArray();
    }

    /// <summary>确保 Subject 元数据在本地可用：缓存行直接用，否则调 Bangumi API 拉取并落库（失败抛出，由调用方拒绝登记）。</summary>
    private async Task<SubjectBinding> EnsureSubjectAsync(int subjectId, CancellationToken cancellationToken)
    {
        var metadata = await subjectCache.EnsureAsync(subjectId, cancellationToken);
        return new SubjectBinding(metadata.Id, metadata.DisplayName);
    }

    /// <summary>下载种子文件并提取 info-hash；网络请求不置于数据库事务内。</summary>
    public async Task<(byte[] TorrentBytes, string InfoHash)> DownloadTorrentAsync(
        string torrentUrl, Guid? proxyId, CancellationToken cancellationToken)
    {
        var torrentBytes = await torrentFileSource.DownloadAsync(new Uri(torrentUrl), proxyId, cancellationToken);
        return !TorrentInfoHashExtractor.TryExtractInfoHash(torrentBytes, out var infoHash)
            ? throw new InvalidOperationException("无法从种子文件中提取 info-hash，文件可能无效或已损坏。")
            : (torrentBytes, infoHash);
    }

    /// <summary>
    ///     在给定上下文内核验重复、决定替换对象并落库任务，由调用方控制事务提交。
    ///     自动登记与手动创建共用该核心，保证查重与替换判定一致。
    /// </summary>
    public async Task<Guid> CreateTaskWithinContextAsync(ApplicationDbContext db, FeedItem item, byte[] torrentBytes,
                                                         string infoHash, CancellationToken cancellationToken)
    {
        if (await DownloadTaskLookups.FindActiveDuplicateIdAsync(db, infoHash, item.Id, cancellationToken) is not null)
            throw new InvalidOperationException("该条目或同一资源已有进行中的下载任务。");

        // 有效完成的同资源不自动重新下载；完成记录不因不属于活动唯一索引而被重复登记绕过。
        if (await DownloadTaskLookups.FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
            throw new InvalidOperationException("同一资源已有已完成的下载任务。");

        // 同源同集统一裁决：跨路径（订阅/RSS/上传）互认后按规则比较，现任保留、更优层才顶替；
        // 落选抛出带说明的异常，提交链会把它记入 IntentFailedReason 等待人工处理。
        var resolvedSource = ResolveSiteSource(torrentBytes, item.TorrentUrl, item.Subscription.FeedUrlProtected, null);
        Guid? arbitrationReplaces = null;
        if (item.MediaType is not null                                                  &&
            !string.Equals(item.MediaType, "MultipleEpisode", StringComparison.Ordinal) &&
            item.Episode is not null)
        {
            var config = await ruleProvider.GetAsync(cancellationToken);
            var language = Enum.TryParse(item.Language, true, out EnumLanguage parsedLanguage)
                ? parsedLanguage
                : (EnumLanguage?)null;
            var resolution = Enum.TryParse(item.Resolution, true, out EnumResolution parsedResolution)
                ? parsedResolution
                : EnumResolution.Unknown;
            var incoming = new EpisodeCandidate
            {
                FeedItemId = item.Id,
                SubjectId = item.SubjectId ?? 0,
                MediaType = item.MediaType,
                Episode = item.Episode.Value,
                Group = string.IsNullOrWhiteSpace(item.Preference) ? item.Group ?? string.Empty : item.Preference,
                Language = language,
                Resolution = resolution,
                Version = item.Version,
                PublishedAtUtc = item.PublishedAtUtc,
                StableId = item.StableId
            };
            var arbitration = await ArbitrateEpisodeAsync(db, config, incoming, item.SubjectId,
                                                          TitleNormalizer.Normalize(item.ParsedTitle),
                                                          item.Episode, resolvedSource, infoHash, item.SubjectId,
                                                          cancellationToken);
            if (!arbitration.Register)
                throw new InvalidOperationException(arbitration.SkipReason!);
            arbitrationReplaces = arbitration.ReplacesTaskId;
        }

        // 替换对象在创建与完成两处共用同一规则，见 FindReplacementTargetAsync；
        // 同源占用者（规则裁决）优先，其余情况沿用跨源 Completed 的版本守卫目标。
        var replacesTaskId = arbitrationReplaces ??
                             await DownloadTaskLookups.FindReplacementTargetAsync(db, item.SubjectId, item.Episode,
                                 infoHash, item.Version,
                                 cancellationToken);

        var settings          = await qbittorrentSettings.GetAsync(cancellationToken);
        var directorySettings = await GetDirectoriesAsync(cancellationToken);
        var task = new DownloadTask
        {
            Id                = Guid.NewGuid(),
            FeedItemId        = item.Id,
            SubjectId         = item.SubjectId,
            InfoHash          = infoHash,
            TorrentFile       = torrentBytes,
            TorrentUrl        = item.TorrentUrl!,
            SavePath          = SelectSavePath(settings.SavePath, torrentBytes, directorySettings),
            DownloadRootPath  = settings.SavePath.Trim().TrimEnd('/', '\\'),
            MediaDirectory    = directorySettings.MediaDirectory,
            SeedDirectory     = directorySettings.SeedDirectory,
            SiteSource        = resolvedSource,
            NamingSnapshot    = item.NamingPreview!,
            NamingIngredients = item.NamingIngredients,
            Episode           = item.Episode,
            Version           = item.Version,
            Status            = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc      = timeProvider.GetUtcNow(),
            ReplacesTaskId    = replacesTaskId
        };
        AddOriginalFiles(task);
        db.DownloadTasks.Add(task);
        // 订阅事务由调用方提交；此处仅在同一 DbContext 中翻转现任状态，保持原子落库，远端清理由对账承接。
        if (replacesTaskId is { } incumbentId)
            await SupersedeIncumbentAsync(db, incumbentId, task.Id, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发下绕过前置查重时由数据库活动唯一索引兜底。
            throw new InvalidOperationException("同一资源已有进行中的下载任务，重复提交已被数据库约束阻止。");
        }

        logger.LogInformation("已为条目 {ItemId} 创建下载任务 {TaskId}（hash {InfoHash}）。", item.Id, task.Id,
                              infoHash);
        return task.Id;
    }

    /// <summary>
    ///     同集统一裁决：新候选与同源（SiteSource 相同，未知也算同源）同集的既有未替换任务按当前
    ///     择优规则比较，复用现任保留语义——同规则层级不换（防抖），仅更高层级或同字幕组更高
    ///     修订版本才顶替；顶替时 ReplacesTaskId 指向占用者，沿用完成期替换链。
    ///     跨路径互认：占用者 SubjectId 相同，或（RSS/上传任务）解析标题归一化相同；
    ///     无 Subject 的新候选先经标题映射（SubjectTitleMappings）换算 Subject 与订阅任务互认。
    ///     exemptSubjectId 指定的同 Subject 订阅条目任务不参与（订阅路径内部由评估、择优与版本
    ///     守卫替换链治理，裁决只负责跨路径去重）；RSS/上传任务无订阅治理，同 Subject 也照常
    ///     参与。占用者含未替换的进行中与已完成任务；
    ///     解析要素缺失的存量任务按未命中规则参与比较。字幕组比较用 preference 口径
    ///     （字幕组名，缺失回退语言名），与命名要素的 {group} 同源。
    /// </summary>
    private async Task<EpisodeArbitration> ArbitrateEpisodeAsync(
        ApplicationDbContext db,              SelectionRuleConfig config, EpisodeCandidate incoming,
        int?                 subjectId,       string seriesKey, decimal? episode, string? siteSource, string infoHash,
        int?                 exemptSubjectId, CancellationToken cancellationToken)
    {
        if (subjectId is null && seriesKey.Length > 0)
            subjectId = await db.SubjectTitleMappings.AsNoTracking()
                                .Where(mapping => mapping.NormalizedTitle == seriesKey)
                                .Select(mapping => (int?)mapping.SubjectId)
                                .FirstOrDefaultAsync(cancellationToken);

        var occupants = await db.DownloadTasks.AsNoTracking()
                                .Where(task => task.Episode          == episode                   &&
                                               task.ReplacedByTaskId == null                      &&
                                               task.Status           != DownloadTaskStatus.Failed &&
                                               task.Status           != DownloadTaskStatus.Lost   &&
                                               task.Status           != DownloadTaskStatus.Replaced)
                                .Select(task => new
                                 {
                                     task.Id, task.SubjectId, task.FeedItemId, task.Version,
                                     task.NamingIngredients, task.NamingSnapshot,
                                     task.SiteSource, task.InfoHash, task.CreatedAtUtc
                                 })
                                .ToListAsync(cancellationToken);

        var candidates = new List<(Guid TaskId, string Naming, EpisodeCandidate Candidate)>();
        foreach (var task in occupants)
        {
            if (task.InfoHash == infoHash) continue;
            // 同 Subject 豁免只覆盖订阅条目任务（其替换由评估/择优/版本守卫链治理）；
            // RSS/上传任务（无 FeedItem）即使同 Subject 也参与跨路径裁决。
            if (exemptSubjectId is not null && task.FeedItemId != null && task.SubjectId == exemptSubjectId) continue;
            if (!string.Equals(task.SiteSource, siteSource, StringComparison.Ordinal)) continue;

            var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
            var taskSeriesKey = ingredients is null
                ? string.Empty
                : TitleNormalizer.Normalize(ingredients.Title);
            var matched = subjectId is not null && task.SubjectId == subjectId;
            if (!matched && task.SubjectId is null && taskSeriesKey.Length > 0)
                matched = taskSeriesKey == seriesKey;
            if (!matched) continue;

            var language = Enum.TryParse(ingredients?.Language, true, out EnumLanguage parsedLanguage)
                ? parsedLanguage
                : (EnumLanguage?)null;
            var resolution = Enum.TryParse(ingredients?.Resolution, true, out EnumResolution parsedResolution)
                ? parsedResolution
                : EnumResolution.Unknown;

            candidates.Add((task.Id, task.NamingSnapshot, new EpisodeCandidate
            {
                FeedItemId     = task.Id,
                SubjectId      = task.SubjectId ?? 0,
                MediaType      = incoming.MediaType,
                Episode        = episode            ?? 0,
                Group          = ingredients?.Group ?? string.Empty,
                Language       = language,
                Resolution     = resolution,
                Version        = task.Version,
                PublishedAtUtc = task.CreatedAtUtc,
                StableId       = task.InfoHash
            }));
        }

        if (candidates.Count == 0)
            return new EpisodeArbitration(true, null, null);

        // 多个占用者（历史并存）先按规则内部选出最强现任，再与新候选一对一对决。
        var incumbentCandidate = candidates.Count == 1
            ? candidates[0].Candidate
            : rankingPolicy.Select(candidates.Select(value => value.Candidate).ToList(), config).Winner
           ?? candidates[0].Candidate;
        var incumbent = candidates.First(value => value.TaskId == incumbentCandidate.FeedItemId);

        var verdict = rankingPolicy.Select([incumbent.Candidate, incoming], config, incumbent.TaskId);
        return verdict.Winner?.FeedItemId == incoming.FeedItemId
            ? new EpisodeArbitration(true, incumbent.TaskId, null)
            : new EpisodeArbitration(false, null, $"同集已有更优任务：{incumbent.Naming}", incumbent.TaskId);
    }

    /// <summary>解析结果是否能构成同集裁决标识：单集（有集数）或剧场版；合集与解析失败不参与。</summary>
    private static bool IsEpisodeScoped(ParseResult parsed)
    {
        var mediaType = parsed.MediaType.ToString();
        return mediaType == "Movie" || (mediaType == "SingleEpisode" && parsed.Episode is not null);
    }

    /// <summary>
    ///     由 RSS 条目或上传种子构建合成候选：FeedItemId 由稳定标识确定性派生，仅用于
    ///     现任匹配与决胜稳定，不落库。字幕组用 preference 口径（缺失回退语言名），
    ///     与命名要素 {group} 及订阅条目 Preference 同域，保证与占用者的比较口径一致。
    /// </summary>
    private static EpisodeCandidate BuildAdhocCandidate(
        string stableId, DateTimeOffset? publishedAtUtc, ParseResult parsed, int subjectId) => new()
    {
        FeedItemId     = StableIdGuid(stableId),
        SubjectId      = subjectId,
        MediaType      = parsed.MediaType.ToString(),
        Episode        = parsed.Episode ?? 0,
        Group          = string.IsNullOrWhiteSpace(parsed.Group) ? parsed.Language.ToString() : parsed.Group,
        Language       = parsed.Language,
        Resolution     = parsed.Resolution,
        Version        = parsed.Version,
        PublishedAtUtc = publishedAtUtc,
        StableId       = stableId
    };

    /// <summary>由稳定标识派生确定性 Guid：合成候选的决胜输入在同批次与重试之间保持稳定。</summary>
    private static Guid StableIdGuid(string stableId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(stableId))[..16]);

    /// <summary>
    ///     来源站点判定：优先读 .torrent 内 tracker（ByrTorrent 同源映射，上传种子也能自动识别），
    ///     依次回退下载地址域名、订阅 feed 主机名、用户手填标注。
    /// </summary>
    private static string? ResolveSiteSource(byte[]  torrentBytes, string? torrentUrl, string? fallbackHost,
                                             string? manualSource)
    {
        List<string> trackers = [];
        TorrentInfoHashExtractor.TryExtractTrackerUrls(torrentBytes, trackers);
        return TorrentSiteSource.FromTrackers(trackers) ??
               TorrentSiteSource.FromUrl(torrentUrl) ??
               TorrentSiteSource.FromHost(fallbackHost) ??
               TorrentSiteSource.Sanitize(manualSource);
    }

    private Task<DownloadDirectorySettings> GetDirectoriesAsync(CancellationToken cancellationToken) =>
        directorySettingsStore is null
            ? Task.FromResult(DownloadDirectorySettingsStore.Normalize(null, null))
            : directorySettingsStore.GetAsync(cancellationToken);

    private string SelectSavePath(string basePath, byte[] torrentBytes, DownloadDirectorySettings directories)
    {
        var root = basePath.Trim().Replace('\\', '/').TrimEnd('/');
        if (!TorrentFileManifestParser.TryParse(torrentBytes, out var manifest))
            throw new InvalidOperationException("无法读取种子文件清单，不能安全决定下载目录。");
        var policy = fileClassificationPolicy ?? new DownloadFileClassificationPolicy(aniParser);
        var hasSeed =
            manifest.Files.Any(file => policy.Classify(file.Path).Kind == Core.Downloads.DownloadFileKind.Seed);
        return hasSeed ? root : DownloadPathNamePolicy.JoinSavePath(root, directories.MediaDirectory);
    }

    private void AddOriginalFiles(DownloadTask task)
    {
        if (!TorrentFileManifestParser.TryParse(task.TorrentFile, out var manifest))
            throw new InvalidOperationException("无法读取种子文件清单，不能记录原始路径。");
        var policy = fileClassificationPolicy ?? new DownloadFileClassificationPolicy(aniParser);
        foreach (var file in manifest.Files)
        {
            var classification = policy.Classify(file.Path);
            var extension      = Path.GetExtension(file.Path);
            var subtitle = extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".sup", StringComparison.OrdinalIgnoreCase);
            task.Files.Add(new DownloadTaskFile
            {
                Id = Guid.NewGuid(), DownloadTaskId = task.Id, FileIndex = file.Index,
                OriginalRelativePath = file.Path, CurrentRelativePath = file.Path, Size = file.Length,
                Kind = classification.Kind == Core.Downloads.DownloadFileKind.Seed ? DownloadFileKind.Seed :
                    subtitle ? DownloadFileKind.Subtitle : DownloadFileKind.Anime,
                ClassificationReason = classification.Reason, UpdatedAtUtc = timeProvider.GetUtcNow()
            });
        }
    }

    internal static void AssertDownloadable(FeedItem item)
    {
        if (item.Status != FeedItemStatus.Accepted || item.DecisionStatus != ReleaseDecisionStatus.Accepted)
            throw new InvalidOperationException("只有筛选通过（已选择）的条目才能创建下载任务。");

        if (string.IsNullOrWhiteSpace(item.TorrentUrl) ||
            !Uri.TryCreate(item.TorrentUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("该条目没有可用的种子地址。");

        if (item.Episode is null || item.SubjectId is null)
            throw new InvalidOperationException("该条目缺少 Subject 或集数，无法创建下载任务。");

        if (string.IsNullOrWhiteSpace(item.NamingPreview)) throw new InvalidOperationException("该条目缺少命名预览，无法创建下载任务。");
    }

    /// <summary>已绑定的 Bangumi Subject：Id 与命名用展示名（中文名非空用之，否则原名）。</summary>
    private sealed record SubjectBinding(int SubjectId, string DisplayName);

    /// <summary>
    ///     统一同集裁决的结论：登记（可带被顶替的占用者）或跳过（附原因，含占用者命名）；
    ///     SupersededByTaskId 为落选时胜出的现任任务，供调用方落查重用的终止记录。
    /// </summary>
    private sealed record EpisodeArbitration(
        bool    Register,
        Guid?   ReplacesTaskId,
        string? SkipReason,
        Guid?   SupersededByTaskId = null);
}
