using Banned.AniParser;
using Banned.AniParser.Models;
using Banned.AniParser.Models.Enums;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Torrent = MonoTorrent.Torrent;

namespace Banori.Features.Downloads;

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
    SubjectEpisodeCorrectionPolicy          episodeCorrectionPolicy,
    TimeProvider                            timeProvider,
    ILogger<DownloadIntakeService>          logger,
    DownloadDirectorySettingsStore?         directorySettingsStore   = null,
    DownloadFileClassificationPolicy?       fileClassificationPolicy = null,
    DownloadEpisodeResolver?                episodeResolver          = null)
{
    /// <summary>所有下载登记的前置门禁：解析 Bangumi 绑定，未绑定返回 null（调用方拒绝下载并要求补 ID）。</summary>
    private const string SubjectUnboundMessage = "未能绑定 Bangumi Subject，请在表单补充 Bangumi ID 后重试。";

    /// <summary>上传种子的大小上限：.torrent 通常远小于该值，超限基本说明选错了文件。服务端入口始终校验。</summary>
    public const long MaxTorrentUploadBytes = 10 * 1024 * 1024;

    private readonly DownloadEpisodeResolver episodeResolver =
        episodeResolver ?? new DownloadEpisodeResolver(aniParser, episodeCorrectionPolicy);

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
    ///     解析失败时回退包内文件集胜者（缺失时按 hash 派生）；来源站点优先按种子内 tracker 自动识别，
    ///     siteSource 仅在 tracker 无法判定时作为兜底。
    ///     解析出集数的正片逐集过统一裁决（合集同样参与）：既有同源同集任务按规则更优则该集不下载，
    ///     全部内容集落选则拒绝登记；新上传更优则逐集顶替（未完成占用者的承接集取消选择，
    ///     ReplacesTaskId 仅在整任务被承接时作为展示摘要指向旧任务）。
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

        if (!TorrentDownloadPolicy.TryLoad(torrentBytes, out var torrent, out var isMultiFile))
            throw new InvalidOperationException("种子元数据无效或不包含受支持的 v1 info-hash。");

        var infoHash            = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();
        var displayName         = torrent.Name;
        var fileTitleCandidates = ParseTorrentFileTitles(torrent, isMultiFile);
        var resolvedSource      = ResolveSiteSource(torrent, null, null, siteSource);
        // 门禁：必须绑定 Bangumi Subject；标题映射未命中且未填 ID 时拒绝登记。
        var uploadedParsed = displayName is null ? null : aniParser.Parse(displayName);
        var binding =
            await ResolveUploadedSubjectBindingAsync(fileTitleCandidates, uploadedParsed, subjectId,
                                                     cancellationToken) ??
            throw new InvalidOperationException(SubjectUnboundMessage);
        var config       = await ruleProvider.GetAsync(cancellationToken);
        var fileAnalysis = AnalyzeTorrentFiles(infoHash, torrent, isMultiFile, config);
        var (namingSnapshot, namingIngredients, namingParsed) =
            await BuildNamingAsync(displayName, fileAnalysis.IdentityParse, null, resolvedSource,
                                   $"上传种子：{infoHash[..8]}",
                                   uploadedParsed, binding, cancellationToken);
        var directorySettings = await GetDirectoriesAsync(cancellationToken);

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await DownloadTaskLookups.FindActiveDuplicateIdAsync(db, infoHash, null, cancellationToken) is not null)
            throw new InvalidOperationException("同一资源已有下载任务（可能因更低优先级被替换终止）。");

        // 与条目路径一致：已完成的同资源同样不重复登记。
        if (await DownloadTaskLookups.FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
            throw new InvalidOperationException("同一资源已有已完成的下载任务。");

        // 逐集裁决：文件级集数（含合集任务）逐集与同源占用者对决；标题解析出的单集/剧场版身份
        // 落任务集数快照并与文件集并入同一口径。所有集落选时不登记，保留终止记录承担同 hash 查重。
        var identity = namingParsed is { } parsedIdentity && IsEpisodeScoped(parsedIdentity)
            ? parsedIdentity
            : null;
        var task = new DownloadTask
        {
            Id          = Guid.NewGuid(),
            InfoHash    = infoHash,
            TorrentFile = torrentBytes,
            TorrentUrl  = $"upload://{infoHash}",
            SavePath = SelectSavePath(settings.SavePath, torrent, isMultiFile, directorySettings,
                                      fileAnalysis.DemotedSeedPaths),
            DownloadRootPath  = settings.SavePath.Trim().TrimEnd('/', '\\'),
            MediaDirectory    = directorySettings.MediaDirectory,
            SeedDirectory     = directorySettings.SeedDirectory,
            SiteSource        = resolvedSource,
            NamingSnapshot    = namingSnapshot,
            NamingIngredients = namingIngredients,
            SubjectId         = binding.SubjectId,
            Episode           = identity?.Episode,
            Version           = identity?.Version ?? 1,
            Status            = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc      = timeProvider.GetUtcNow()
        };
        AddOriginalFiles(task, torrent, isMultiFile, fileAnalysis.DemotedSeedPaths);
        var scopes = BuildEpisodeScopes(task, fileAnalysis, identity?.Episode,
                                        identity is null
                                            ? null
                                            : BuildAdhocCandidate(infoHash, null, identity, binding.SubjectId));
        var verdicts = await ArbitrateEpisodesAsync(db, config, scopes, binding.SubjectId,
                                                    TitleNormalizer.Normalize(identity?.Title ?? string.Empty),
                                                    resolvedSource, infoHash, null, cancellationToken);
        ApplyLosingSelections(task, verdicts);
        if (AllContentLost(task, verdicts))
        {
            // 与 RSS 路径同口径：落选留终止记录承担同 hash 查重，顶替者被删时回退重新排队。
            task.Status           = DownloadTaskStatus.Replaced;
            task.ReplacedByTaskId = DominantIncumbentId(verdicts);
            task.FailedStep       = "择优";
            task.Error            = "同集候选落选，保留终止记录供查重。";
            db.DownloadTasks.Add(task);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("上传种子同集落选，已登记终止记录 {TaskId}（hash {InfoHash}）。",
                                  task.Id, infoHash);
            throw new InvalidOperationException(
                                                $"本次上传未登记：{ComposeLossReason(verdicts)}。（已保留终止记录供查重）");
        }

        await ApplyWinningEpisodesAsync(db, task, scopes, verdicts, cancellationToken);
        db.DownloadTasks.Add(task);
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
    ///     优胜者与不成组条目登记前再过统一逐集裁决（见 ArbitrateEpisodesAsync）。逐条尽力而为：
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
        var (torrentBytes, torrent, isMultiFile) = await DownloadTorrentFileAsync(item.TorrentUri!.AbsoluteUri, proxyId,
            cancellationToken);
        var infoHash = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();

        // 命名优先按模板渲染（条目标题能被解析出集数时），其次包内文件集胜者，再次种子内 info.name，
        // 均失败回退原始标题。
        var resolvedSource = ResolveSiteSource(torrent, item.TorrentUri.AbsoluteUri, feedHost, null);
        var displayName    = torrent.Name;
        var fileAnalysis   = AnalyzeTorrentFiles(infoHash, torrent, isMultiFile, config);
        var (namingSnapshot, namingIngredients, namingParsed) =
            await BuildNamingAsync(item.Title, fileAnalysis.IdentityParse, displayName, resolvedSource,
                                   $"RSS 下载：{infoHash[..8]}", parsed,
                                   binding, cancellationToken);
        var directorySettings = await GetDirectoriesAsync(cancellationToken);

        using var       coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using var db                = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await DownloadTaskLookups.FindActiveDuplicateIdAsync(db, infoHash, null, cancellationToken) is not null ||
            await DownloadTaskLookups.FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
            return (null, null);

        // 逐集裁决：文件级集数（含合集任务）逐集与同源占用者对决；标题身份与文件集并入同一口径。
        var identity = namingParsed is { } parsedIdentity && IsEpisodeScoped(parsedIdentity)
            ? parsedIdentity
            : null;
        var task = new DownloadTask
        {
            Id          = Guid.NewGuid(),
            InfoHash    = infoHash,
            TorrentFile = torrentBytes,
            TorrentUrl  = item.TorrentUri!.AbsoluteUri,
            SavePath =
                SelectSavePath(basePath, torrent, isMultiFile, directorySettings, fileAnalysis.DemotedSeedPaths),
            DownloadRootPath  = basePath.Trim().TrimEnd('/', '\\'),
            MediaDirectory    = directorySettings.MediaDirectory,
            SeedDirectory     = directorySettings.SeedDirectory,
            SiteSource        = resolvedSource,
            NamingSnapshot    = namingSnapshot,
            NamingIngredients = namingIngredients,
            SubjectId         = binding.SubjectId,
            Episode           = identity?.Episode,
            Version           = identity?.Version ?? 1,
            Status            = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc      = timeProvider.GetUtcNow()
        };
        AddOriginalFiles(task, torrent, isMultiFile, fileAnalysis.DemotedSeedPaths);
        var scopes = BuildEpisodeScopes(task, fileAnalysis, identity?.Episode,
                                        identity is null
                                            ? null
                                            : BuildAdhocCandidate(item.StableId, item.PublishedAtUtc, identity,
                                                                  binding.SubjectId));
        var verdicts = await ArbitrateEpisodesAsync(db, config, scopes, binding.SubjectId,
                                                    TitleNormalizer.Normalize(identity?.Title ?? string.Empty),
                                                    resolvedSource, infoHash, null, cancellationToken);
        ApplyLosingSelections(task, verdicts);
        if (AllContentLost(task, verdicts))
        {
            // 落选仍留一条终止记录：同 hash 查重命中即知该资源已输给更高优先级任务，重复添加直接终止；
            // 顶替者被删时该记录回退重新排队。字段与正常任务一致，回退后可直接提交。
            task.Status           = DownloadTaskStatus.Replaced;
            task.ReplacedByTaskId = DominantIncumbentId(verdicts);
            task.FailedStep       = "择优";
            task.Error            = "同集候选落选，保留终止记录供查重。";
            db.DownloadTasks.Add(task);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("RSS 条目同集落选，已登记终止记录 {TaskId}（hash {InfoHash}）。",
                                  task.Id, infoHash);
            return (null, ComposeLossReason(verdicts));
        }

        await ApplyWinningEpisodesAsync(db, task, scopes, verdicts, cancellationToken);
        db.DownloadTasks.Add(task);
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
    ///     在登记门禁（必须绑定 Subject）中解析后传入。标题解析失败时依次尝试包内文件集胜者、
    ///     种子内 info.name；均无法解析时回退原始标题并返回 null 要素，完成时按快照兜底。
    ///     成功渲染的那次解析随要素一并返回，供同集裁决复用同一解析口径。
    /// </summary>
    private async Task<(string Snapshot, string? IngredientsJson, ParseResult? Parsed)> BuildNamingAsync(
        string? primaryTitle, ParseResult? fallbackParse, string? fallbackTitle, string? siteSource, string lastResort,
        ParseResult? primaryParsed, SubjectBinding subject, CancellationToken cancellationToken)
    {
        var template     = await namingTemplateStore.GetAsync(cancellationToken);
        var fileTemplate = await namingTemplateStore.GetFileTemplateAsync(cancellationToken);
        var metadata     = await subjectCache.GetCachedAsync(subject.SubjectId, cancellationToken);
        var bgmName      = metadata?.Name   ?? subject.DisplayName;
        var bgmNameCn    = metadata?.NameCn ?? string.Empty;

        var attempts = new (string? Title, ParseResult? Parsed)[]
        {
            (primaryTitle, primaryParsed),
            (fallbackParse?.OriginTitle, fallbackParse),
            (fallbackTitle, null)
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
    ///     构造逐集入擂范围：正片视频按文件级单集身份（人工覆盖 &gt; 修正后解析；合集区间不拆分）
    ///     分组，集代表候选取组内首个正片的解析。条目/标题身份（单集/剧场版）命中同一集时沿用
    ///     身份候选，保持与既有任务级裁决相同的比较口径；文件归不进该集时以全部正片承载，使
    ///     文件不可解析的单集任务仍保有旧口径的裁决覆盖。
    /// </summary>
    private List<EpisodeScope> BuildEpisodeScopes(
        DownloadTask task, TorrentFileAnalysis analysis, decimal? identityEpisode, EpisodeCandidate? identityCandidate)
    {
        var byEpisode = new Dictionary<decimal, List<DownloadTaskFile>>();
        foreach (var file in task.Files.Where(file => file.Kind == DownloadFileKind.Anime)
                                 .OrderBy(file => file.FileIndex))
        {
            if (episodeResolver.EpisodeOf(task, file) is not { } episode) continue;
            if (!byEpisode.TryGetValue(episode, out var files)) byEpisode[episode] = files = [];
            files.Add(file);
        }

        var scopes = new List<EpisodeScope>();
        foreach (var (episode, files) in byEpisode)
        {
            if (identityCandidate is not null && identityEpisode == episode)
            {
                var version = DownloadTaskLookups.EpisodeFileVersion(aniParser, task, files[0], episode);
                scopes.Add(new EpisodeScope(episode, identityCandidate with { Version = version }, files));
                continue;
            }

            var parse = aniParser.Parse(DownloadEpisodeResolver.FileName(files[0].OriginalRelativePath));
            if (parse is null) continue;
            scopes.Add(new EpisodeScope(episode,
                                        BuildAdhocCandidate($"{task.InfoHash}:{episode:0.###}:{files[0].FileIndex}",
                                                            task.CreatedAtUtc, parse, task.SubjectId ?? 0), files));
        }

        if (identityCandidate is null             ||
            identityEpisode is not { } episodeKey ||
            scopes.Any(scope => scope.Episode == episodeKey))
            return scopes;

        // 文件归不进该集时以内容文件承载；文件全部不可解析的单文件种子退回唯一文件——
        // 任务级身份已断言该文件属于此集，分类缺失不阻止承接（与旧任务级裁决同覆盖）。
        var fallbackFiles = task.Files.Where(file => file.Kind == DownloadFileKind.Anime)
                                .OrderBy(file => file.FileIndex).ToList();
        if (fallbackFiles.Count == 0)
            fallbackFiles = task.Files.Where(file => file.Kind != DownloadFileKind.Seed)
                                .OrderBy(file => file.FileIndex).ToList();
        if (fallbackFiles.Count == 0 && task.Files.Count == 1)
            fallbackFiles = task.Files.ToList();
        fallbackFiles = fallbackFiles.Where(file => DownloadTaskLookups.CanUseTaskEpisodeFallback(aniParser, file))
                                     .ToList();
        if (fallbackFiles.Count > 0) scopes.Add(new EpisodeScope(episodeKey, identityCandidate, fallbackFiles));


        return scopes;
    }

    /// <summary>
    ///     跨包逐集统一裁决：新任务每个集数的入擂候选与同源（SiteSource 相同，未知也算同源）同集的
    ///     既有未替换任务对决，复用现任保留语义——同规则层级不换（防抖），仅更高层级或同字幕组更高
    ///     修订版本才顶替。占用者以任务级集数（单集任务既有身份）或文件级集数（合集任务的选中正片）
    ///     覆盖该集；跨路径互认与订阅豁免口径沿用既有任务级裁决。纯决策无副作用，结论由调用方应用。
    /// </summary>
    private async Task<List<EpisodeVerdict>> ArbitrateEpisodesAsync(
        ApplicationDbContext db,        SelectionRuleConfig config, IReadOnlyList<EpisodeScope> scopes,
        int?                 subjectId, string seriesKey, string? siteSource, string infoHash, int? exemptSubjectId,
        CancellationToken    cancellationToken)
    {
        if (scopes.Count == 0) return [];

        if (subjectId is null && seriesKey.Length > 0)
            subjectId = await db.SubjectTitleMappings.AsNoTracking()
                                .Where(mapping => mapping.NormalizedTitle == seriesKey)
                                .Select(mapping => (int?)mapping.SubjectId)
                                .FirstOrDefaultAsync(cancellationToken);
        if (subjectId is null)
            // 无法互认 Subject 时不再按标题兜底对决：宁可重复登记，不凭猜测淘汰候选。
            return scopes.Select(scope => new EpisodeVerdict(scope.Episode, true, null, null, false, null)).ToList();

        var occupants = await db.DownloadTasks.AsNoTracking()
                                .Include(task => task.Files)
                                .Where(task => task.SubjectId        == subjectId                 &&
                                               task.ReplacedByTaskId == null                      &&
                                               task.Status           != DownloadTaskStatus.Failed &&
                                               task.Status           != DownloadTaskStatus.Lost   &&
                                               task.Status           != DownloadTaskStatus.Replaced)
                                .ToListAsync(cancellationToken);

        var verdicts = new List<EpisodeVerdict>(scopes.Count);
        foreach (var scope in scopes)
        {
            var candidates = new List<(Guid TaskId, string Naming, EpisodeCandidate Candidate, bool Completed,
                Guid? VideoFileId)>();
            // 登记期版本守卫候选：同 Subject 已完成且覆盖该集的占用者（不区分来源与豁免——
            // 它们不参与规则对决，但沿用条目链完成期替换的语义记入每集登记表，供完成期迁让
            // 与现任切换；旧任务本身不动）。
            var registryIncumbents = new List<(Guid TaskId, string Naming, int Version, DateTimeOffset CreatedAtUtc,
                Guid VideoFileId)>();
            foreach (var occupant in occupants)
            {
                if (occupant.InfoHash == infoHash) continue;

                var covered = CoveringFiles(occupant, scope.Episode);
                var carrier = covered.FirstOrDefault(file => file.Kind == DownloadFileKind.Anime) ??
                              covered.FirstOrDefault();
                var incumbentVersion = carrier is null
                    ? occupant.Version
                    : DownloadTaskLookups.EpisodeFileVersion(aniParser, occupant, carrier, scope.Episode);
                if (DownloadTaskLookups.IsTakeoverEligibleIncumbent(occupant.Status, occupant.ReplacedByTaskId,
                                                                    incumbentVersion, scope.Incoming.Version))
                    if (carrier is not null)
                        registryIncumbents.Add((occupant.Id, occupant.NamingSnapshot, incumbentVersion,
                                                occupant.CreatedAtUtc, carrier.Id));

                // 同 Subject 豁免只覆盖订阅条目任务（其替换由评估/择优/版本守卫链治理）；
                // RSS/上传任务（无 FeedItem）即使同 Subject 也参与跨路径裁决。
                if (exemptSubjectId is not null && occupant.FeedItemId != null &&
                    occupant.SubjectId                                 == exemptSubjectId) continue;
                if (!string.Equals(occupant.SiteSource, siteSource, StringComparison.Ordinal)) continue;
                if (occupant.SubjectId is null)
                {
                    var taskSeriesKey = TorrentNamingIngredients.FromJson(occupant.NamingIngredients) is
                        { } occupantIngredients
                        ? TitleNormalizer.Normalize(occupantIngredients.Title)
                        : string.Empty;
                    if (taskSeriesKey.Length == 0 || taskSeriesKey != seriesKey) continue;
                }

                var covered2 = CoveringFiles(occupant, scope.Episode);
                if (covered2.Count == 0) continue;

                var video = covered2.FirstOrDefault(file => file.Kind == DownloadFileKind.Anime);
                var parse = video is null
                    ? null
                    : aniParser.Parse(DownloadEpisodeResolver.FileName(video.OriginalRelativePath));
                candidates.Add((occupant.Id, occupant.NamingSnapshot,
                                OccupantCandidate(occupant, parse, scope, video?.Id),
                                occupant.Status == DownloadTaskStatus.Completed,
                                video?.Id ?? covered2.FirstOrDefault()?.Id));
            }

            if (candidates.Count == 0)
            {
                // 无同源占用者：缺集胜出不写登记表；登记期版本守卫命中的已完成现任除外。
                var guardIncumbent = registryIncumbents.OrderByDescending(item => item.Version)
                                                       .ThenByDescending(item => item.CreatedAtUtc)
                                                       .FirstOrDefault();
                verdicts.Add(guardIncumbent.TaskId != Guid.Empty
                                 ? new EpisodeVerdict(scope.Episode, true, guardIncumbent.TaskId,
                                                      guardIncumbent.Naming, true, guardIncumbent.VideoFileId)
                                 : new EpisodeVerdict(scope.Episode, true, null, null, false, null));
                continue;
            }

            // 多个占用者（历史并存）先按规则内部选出最强现任，再与新候选一对一对决。
            var incumbent = candidates.Count == 1
                ? candidates[0]
                : candidates.First(value => value.TaskId == (rankingPolicy
                                                            .Select(candidates.Select(item => item.Candidate).ToList(),
                                                                    config).Winner ?? candidates[0].Candidate)
                                      .FeedItemId);
            var verdict = rankingPolicy.Select([incumbent.Candidate, scope.Incoming], config, incumbent.TaskId);
            verdicts.Add(verdict.Winner?.FeedItemId == scope.Incoming.FeedItemId
                             ? new EpisodeVerdict(scope.Episode, true, incumbent.TaskId, incumbent.Naming,
                                                  incumbent.Completed, incumbent.VideoFileId)
                             : new EpisodeVerdict(scope.Episode, false, incumbent.TaskId, incumbent.Naming,
                                                  incumbent.Completed, incumbent.VideoFileId));
        }

        return verdicts;
    }

    /// <summary>
    ///     占用者在该集上仍承担的选中内容文件：任务级身份命中时全部选中内容文件归属该集
    ///     （没有内容文件的单文件种子退回唯一文件，任务级身份即该文件），否则按文件级单集身份
    ///     归属；Seed 附件不承担集数职责。
    /// </summary>
    private List<DownloadTaskFile> CoveringFiles(DownloadTask task, decimal episode)
    {
        if (task.Episode != episode)
            return task.Files.Where(file => file.IsSelectedForDownload && file.Kind != DownloadFileKind.Seed &&
                                            episodeResolver.EpisodeOf(task, file)   == episode).ToList();

        var covered = task.Files.Where(file => file.IsSelectedForDownload && file.Kind != DownloadFileKind.Seed &&
                                               (episodeResolver.EpisodeOf(task, file) == episode ||
                                                (episodeResolver.EpisodeOf(task, file) is null &&
                                                 DownloadTaskLookups.IsSingleEpisodeCarrier(aniParser, task, file,
                                                     episode)))).ToList();
        if (covered.Count == 0 && task.Files.Count(file => file.IsSelectedForDownload) == 1 &&
            task.Files.Single(file => file.IsSelectedForDownload) is { } onlySelected &&
            episodeResolver.EpisodeOf(task, onlySelected) is null &&
            DownloadTaskLookups.IsSingleEpisodeCarrier(aniParser, task, onlySelected, episode))
            covered = [onlySelected];
        return covered;
    }

    /// <summary>
    ///     占用者候选：任务级身份（单集任务）沿用命名要素口径构造，与历史行为一致；文件级承载
    ///     （合集任务的正片）用该文件的解析构造，字幕组沿用 preference 口径（缺失回退语言名）。
    /// </summary>
    private EpisodeCandidate OccupantCandidate(
        DownloadTask task, ParseResult? parse, EpisodeScope scope, Guid? videoFileId)
    {
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        var language = Enum.TryParse(ingredients?.Language, true, out EnumLanguage parsedLanguage)
            ? parsedLanguage
            : (EnumLanguage?)null;
        var resolution = Enum.TryParse(ingredients?.Resolution, true, out EnumResolution parsedResolution)
            ? parsedResolution
            : EnumResolution.Unknown;
        return new EpisodeCandidate
        {
            FeedItemId = task.Id,
            SubjectId  = task.SubjectId              ?? 0,
            MediaType  = parse?.MediaType.ToString() ?? scope.Incoming.MediaType,
            Episode    = scope.Episode,
            Group = parse is null
                ? ingredients?.Group ?? string.Empty
                : string.IsNullOrWhiteSpace(parse.Group)
                    ? parse.Language.ToString()
                    : parse.Group,
            Language       = parse?.Language   ?? language,
            Resolution     = parse?.Resolution ?? resolution,
            Version        = parse?.Version    ?? task.Version,
            PublishedAtUtc = task.CreatedAtUtc,
            StableId       = videoFileId is { } fileId ? $"{task.InfoHash}:{fileId}" : task.InfoHash
        };
    }

    /// <summary>
    ///     落选集取消选择：新任务该集承载文件（与占用者同一覆盖口径：任务级身份命中或文件级单集
    ///     归属）取消下载，含随行字幕。包内择优落选已在分析阶段归 Seed；跨包落选保留内容角色
    ///     分类，仅记录不承担下载及原因。
    /// </summary>
    private void ApplyLosingSelections(DownloadTask task, IReadOnlyList<EpisodeVerdict> verdicts)
    {
        foreach (var verdict in verdicts.Where(verdict => !verdict.Winner))
        foreach (var file in CoveringFiles(task, verdict.Episode))
        {
            file.IsSelectedForDownload = false;
            file.SelectionReason = file.Kind == DownloadFileKind.Subtitle
                ? $"随本集视频落选：同集已有更优任务「{verdict.IncumbentNaming}」"
                : $"同集已有更优任务：{verdict.IncumbentNaming}";
        }

        DownloadTaskLookups.RefreshSelectedTotals(task);
    }

    /// <summary>
    ///     胜出集登记：每集登记表写入待完成优胜（指向本任务该集胜者正片；已完成现任的正片一并入表，
    ///     供完成期迁让与逐集切换）；未完成占用者执行承接——该集文件取消选择等待对账下推到下载器，
    ///     其余集职责保留继续下载，全部内容职责被承接后整任务翻转 Replaced（任务级替换字段自此只作
    ///     展示摘要）。已完成占用者不动：新版本完成前旧成片仍是现任，保留做种与展示。
    /// </summary>
    private async Task ApplyWinningEpisodesAsync(
        ApplicationDbContext          db,       DownloadTask      task, IReadOnlyList<EpisodeScope> scopes,
        IReadOnlyList<EpisodeVerdict> verdicts, CancellationToken cancellationToken)
    {
        if (task.SubjectId is not { } subjectId) return;
        foreach (var verdict in verdicts.Where(verdict => verdict.Winner))
        {
            // 缺集胜出（无占用者）不写登记表：没有承接关系，现任在完成时直接落表；
            // 跨来源占用者不参与对决，其现任不能被本任务的登记悄悄顶掉。
            if (verdict.IncumbentTaskId is null) continue;

            var scope = scopes.FirstOrDefault(item => item.Episode == verdict.Episode);
            var winnerFile = scope?.VideoFiles.FirstOrDefault(file => file.Kind == DownloadFileKind.Anime) ??
                             scope?.VideoFiles.FirstOrDefault();
            if (winnerFile is null) continue;

            var row = await db.EpisodeVersions
                              .SingleOrDefaultAsync(value => value.SubjectId == subjectId &&
                                                             value.Episode   == verdict.Episode, cancellationToken);
            if (row is null)
            {
                row = new EpisodeVersion { SubjectId = subjectId, Episode = verdict.Episode };
                db.EpisodeVersions.Add(row);
            }

            if (verdict is { IncumbentCompleted: true, IncumbentVideoFileId: { } incumbentVideoFileId })
                row.IncumbentVideoFileId = incumbentVideoFileId;
            row.PendingVideoFileId = winnerFile.Id;
            row.MediaType          = scope!.Incoming.MediaType;
            row.UpdatedAtUtc       = timeProvider.GetUtcNow();

            if (verdict.IncumbentTaskId is not { } incumbentTaskId || verdict.IncumbentCompleted) continue;

            var incumbent = await db.DownloadTasks.Include(value => value.Files)
                                    .SingleOrDefaultAsync(value => value.Id == incumbentTaskId, cancellationToken);
            if (incumbent is null ||
                incumbent.Status is DownloadTaskStatus.Replaced or DownloadTaskStatus.Failed
                                                                or DownloadTaskStatus.Lost) continue;

            foreach (var file in CoveringFiles(incumbent, verdict.Episode))
            {
                file.IsSelectedForDownload = false;
                file.SelectionReason       = $"已被更优任务替代：{task.NamingSnapshot}";
                file.SupersededByTaskId    = task.Id;
                file.UpdatedAtUtc          = timeProvider.GetUtcNow();
            }

            DownloadTaskLookups.RefreshSelectedTotals(incumbent);
            incumbent.SelectionSyncPending = true;
            if (incumbent.Files.Any(file => file.IsSelectedForDownload && file.Kind != DownloadFileKind.Seed))
                continue;

            incumbent.Status           =   DownloadTaskStatus.Replaced;
            incumbent.ReplacedByTaskId =   task.Id;
            task.ReplacesTaskId        ??= incumbent.Id;
            logger.LogInformation("任务 {TaskId} 的全部内容职责已被更高优先级任务 {NewTaskId} 承接，整任务翻转被替换。",
                                  incumbent.Id, task.Id);
        }
    }

    /// <summary>
    ///     整包落选判定：存在内容文件时以内容文件全部不承担为准；文件全部不可解析的单文件种子
    ///     以唯一文件被取消选择为准（任务级身份承担了该文件的集数语义）。
    /// </summary>
    private static bool AllContentLost(DownloadTask task, IReadOnlyList<EpisodeVerdict> verdicts)
    {
        if (verdicts.Count == 0) return false;
        var carriers = task.Files.Where(file => file.Kind != DownloadFileKind.Seed).ToList();
        if (carriers.Count    == 0 && task.Files.Count == 1) carriers = task.Files.ToList();
        return carriers.Count > 0  && carriers.All(file => !file.IsSelectedForDownload);
    }

    /// <summary>整包落选时的优势占用者：承担最多落选集的任务，用于墓碑回退与展示摘要。</summary>
    private static Guid? DominantIncumbentId(IReadOnlyList<EpisodeVerdict> verdicts) =>
        verdicts.Where(verdict => verdict is { Winner: false, IncumbentTaskId: not null })
                .GroupBy(verdict => verdict.IncumbentTaskId)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();

    /// <summary>落选原因摘要：按集列出胜出占用者命名，超长截断以适配错误字段上限。</summary>
    private static string ComposeLossReason(IReadOnlyList<EpisodeVerdict> verdicts)
    {
        var detail = string.Join("；", verdicts.Where(verdict => !verdict.Winner)
                                              .Select(verdict =>
                                                          $"第{verdict.Episode:0.###}集：{verdict.IncumbentNaming}"));
        if (detail.Length > 900) detail = detail[..900];
        return $"同集已有更优任务（{detail}）";
    }

    /// <summary>
    ///     Subject 绑定解析链（与订阅评估同序）：表单显式 ID &gt; 标题映射 &gt; feed 自带 bgmId &gt;
    ///     条目链接解析（Mikan 页面自带 bgmId）。除标题映射外均需确保 MediaSubject 落库（本地缓存行
    ///     优先，否则调 Bangumi API），用显式 ID 或链接解析成功且能解析出系列标题时回写标题映射，
    ///     后续同系列条目直接命中缓存。
    /// </summary>
    private async Task<SubjectBinding?> ResolveSubjectBindingAsync(ParseResult? parsed, Uri? itemLink,
                                                                   int? requestedSubjectId, int? sourceSubjectId,
                                                                   Guid? proxyId, CancellationToken cancellationToken)
    {
        var seriesKey = parsed is null ? string.Empty : TitleNormalizer.Normalize(parsed.Title);

        SubjectBinding? binding;
        if (requestedSubjectId is { } explicitId)
        {
            binding = await EnsureSubjectAsync(explicitId, cancellationToken);
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
            binding = await EnsureSubjectAsync(feedId, cancellationToken);
            await subjectCache.SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
            return binding;
        }

        if (itemLink is null) return null;

        var linkedSubjectId = await subjectLinkResolver.ResolveSubjectIdAsync(itemLink, proxyId, cancellationToken);
        if (linkedSubjectId is null) return null;
        binding = await EnsureSubjectAsync(linkedSubjectId.Value, cancellationToken);
        await subjectCache.SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
        return binding;
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
                                  where ((IEnumerable<string>)normalizedTitles).Contains(mapping.NormalizedTitle)
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
    private IReadOnlyList<ParseResult> ParseTorrentFileTitles(Torrent torrent, bool isMultiFile)
    {
        var filePaths = torrent.Files
                               .Select(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile))
                               .ToArray();
        return aniParser.ParseBatch(filePaths)
                        .Where(parsed => !string.IsNullOrWhiteSpace(parsed.Title))
                        .ToArray();
    }

    /// <summary>
    ///     包内文件集分析：与跨 torrent 同一粒度地执行同集择优——正片视频逐个解析后按「动画 + 集」分组，
    ///     同集存在多个版本（字幕组/语言/分辨率/版本不同）时用同一套择优规则在包内决胜，落选版本的文件
    ///     归 Seed。标题解析失败时若正片恰好全部落在同一集，由该集胜者的解析充当任务身份；跨集或含
    ///     合集区间命名视为合集，不落身份。每集包内胜者的解析随结果返回，供跨包逐集裁决构造入擂候选。
    ///     字幕文件是随行资源，不参与版本竞争。
    /// </summary>
    private TorrentFileAnalysis AnalyzeTorrentFiles(
        string infoHash, Torrent torrent, bool isMultiFile, SelectionRuleConfig config)
    {
        var policy     = fileClassificationPolicy ?? new DownloadFileClassificationPolicy(aniParser);
        var demoted    = new Dictionary<string, string>();
        var byEpisode  = new Dictionary<decimal, List<(string Path, ParseResult Parsed)>>();
        var multiScope = false;

        foreach (var file in torrent.Files)
        {
            var path = TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile);
            if (policy.Classify(path) is not
                { Kind: Core.Downloads.DownloadFileKind.Content, Parsed: { } parsed }) continue;
            if (!DownloadFileClassificationPolicy.IsVideo(path)) continue;
            if (parsed.Episode is not { } episode)
            {
                // 剧场版（无集数）或合集区间命名的正片：无法归入单集，整个种子按合集处理。
                multiScope = true;
                continue;
            }

            if (!byEpisode.TryGetValue(episode, out var group)) byEpisode[episode] = group = [];
            group.Add((path, parsed));
        }

        switch (byEpisode.Count)
        {
            case > 1 :
                multiScope = true;
                break;
            case 0 :
                return new TorrentFileAnalysis(demoted, null, []);
        }

        ParseResult? identity          = null;
        var          episodeIdentities = new List<(decimal Episode, ParseResult Parse)>();
        foreach (var (episode, files) in byEpisode)
        {
            var variants =
                files.GroupBy(value => (value.Parsed.Group, value.Parsed.Language, value.Parsed.Resolution,
                                        value.Parsed.Version))
                     .ToList();
            if (variants.Count == 1)
            {
                identity ??= variants[0].First().Parsed;
                episodeIdentities.Add((episode, variants[0].First().Parsed));
                continue;
            }

            var ranked = variants.Select((variant, index) => (
                                             Variant : variant,
                                             Candidate : BuildAdhocCandidate($"{infoHash}:{episode:0.###}:{index}",
                                                                             null, variant.First().Parsed,
                                                                             0))).ToList();
            var winnerStableId = (rankingPolicy.Select(ranked.Select(value => value.Candidate).ToList(), config)
                                               .Winner ?? ranked[0].Candidate).StableId;
            foreach (var (variant, candidate) in ranked)
            {
                if (candidate.StableId == winnerStableId)
                {
                    identity ??= variant.First().Parsed;
                    episodeIdentities.Add((episode, variant.First().Parsed));
                    continue;
                }

                foreach (var (path, _) in variant) demoted[path] = "同集包内择优落选";
            }
        }

        return new TorrentFileAnalysis(demoted, multiScope ? null : identity, episodeIdentities);
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
        var (torrentBytes, torrent, _) = await DownloadTorrentFileAsync(torrentUrl, proxyId, cancellationToken);
        return (torrentBytes, torrent.InfoHashes.V1!.ToHex().ToLowerInvariant());
    }

    private async Task<(byte[] TorrentBytes, Torrent Torrent, bool IsMultiFile)> DownloadTorrentFileAsync(
        string torrentUrl, Guid? proxyId, CancellationToken cancellationToken)
    {
        var torrentBytes = await torrentFileSource.DownloadAsync(new Uri(torrentUrl), proxyId, cancellationToken);
        return !TorrentDownloadPolicy.TryLoad(torrentBytes, out var torrent, out var isMultiFile)
            ? throw new InvalidOperationException("种子元数据无效或不包含受支持的 v1 info-hash。")
            : (torrentBytes, torrent, isMultiFile);
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

        if (!TorrentDownloadPolicy.TryLoad(torrentBytes, out var torrent, out var isMultiFile) ||
            torrent.InfoHashes.V1!.ToHex().ToLowerInvariant() != infoHash)
            throw new InvalidOperationException("种子元数据无效或 info-hash 不匹配。");

        // 同源同集逐集统一裁决：订阅条目身份（单集/剧场版）与文件级集数（含合集条目）并入同一
        // 口径跨路径对决，现行任保留、更优层才顶替；落选抛出带说明的异常，提交链会把它记入
        // IntentFailedReason 等待人工处理。
        var resolvedSource    = ResolveSiteSource(torrent, item.TorrentUrl, item.Subscription.FeedUrlProtected, null);
        var config            = await ruleProvider.GetAsync(cancellationToken);
        var fileAnalysis      = AnalyzeTorrentFiles(infoHash, torrent, isMultiFile, config);
        var settings          = await qbittorrentSettings.GetAsync(cancellationToken);
        var directorySettings = await GetDirectoriesAsync(cancellationToken);
        var task = new DownloadTask
        {
            Id          = Guid.NewGuid(),
            FeedItemId  = item.Id,
            SubjectId   = item.SubjectId,
            InfoHash    = infoHash,
            TorrentFile = torrentBytes,
            TorrentUrl  = item.TorrentUrl!,
            SavePath = SelectSavePath(settings.SavePath, torrent, isMultiFile, directorySettings,
                                      fileAnalysis.DemotedSeedPaths),
            DownloadRootPath  = settings.SavePath.Trim().TrimEnd('/', '\\'),
            MediaDirectory    = directorySettings.MediaDirectory,
            SeedDirectory     = directorySettings.SeedDirectory,
            SiteSource        = resolvedSource,
            NamingSnapshot    = item.NamingPreview!,
            NamingIngredients = item.NamingIngredients,
            Episode           = item.Episode,
            Version           = item.Version,
            Status            = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc      = timeProvider.GetUtcNow()
        };
        AddOriginalFiles(task, torrent, isMultiFile, fileAnalysis.DemotedSeedPaths);

        EpisodeCandidate? identityCandidate = null;
        if (item.MediaType is not null                                                  &&
            !string.Equals(item.MediaType, "MultipleEpisode", StringComparison.Ordinal) &&
            item.Episode is not null)
        {
            var language = Enum.TryParse(item.Language, true, out EnumLanguage parsedLanguage)
                ? parsedLanguage
                : (EnumLanguage?)null;
            var resolution = Enum.TryParse(item.Resolution, true, out EnumResolution parsedResolution)
                ? parsedResolution
                : EnumResolution.Unknown;
            identityCandidate = new EpisodeCandidate
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
        }

        var scopes = BuildEpisodeScopes(task, fileAnalysis, identityCandidate is null ? null : item.Episode,
                                        identityCandidate);
        var verdicts = await ArbitrateEpisodesAsync(db, config, scopes, item.SubjectId,
                                                    TitleNormalizer.Normalize(item.ParsedTitle),
                                                    resolvedSource, infoHash, item.SubjectId,
                                                    cancellationToken);
        ApplyLosingSelections(task, verdicts);
        if (AllContentLost(task, verdicts))
            throw new InvalidOperationException(ComposeLossReason(verdicts));
        await ApplyWinningEpisodesAsync(db, task, scopes, verdicts, cancellationToken);
        db.DownloadTasks.Add(task);
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
    private static string? ResolveSiteSource(
        Torrent torrent, string? torrentUrl, string? fallbackHost, string? manualSource) =>
        TorrentSiteSource.FromTrackers(torrent.AnnounceUrls.SelectMany(tier => tier)) ??
        TorrentSiteSource.FromUrl(torrentUrl) ??
        TorrentSiteSource.FromHost(fallbackHost) ??
        TorrentSiteSource.Sanitize(manualSource);

    private Task<DownloadDirectorySettings> GetDirectoriesAsync(CancellationToken cancellationToken) =>
        directorySettingsStore is null
            ? Task.FromResult(DownloadDirectorySettingsStore.Normalize(null, null))
            : directorySettingsStore.GetAsync(cancellationToken);

    private string SelectSavePath(string                              basePath, Torrent torrent, bool isMultiFile,
                                  DownloadDirectorySettings           directories,
                                  IReadOnlyDictionary<string, string> demotedSeedPaths)
    {
        var root   = basePath.Trim().Replace('\\', '/').TrimEnd('/');
        var policy = fileClassificationPolicy ?? new DownloadFileClassificationPolicy(aniParser);
        var hasSeed = torrent.Files.Any(file =>
        {
            var path = TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile);
            return demotedSeedPaths.ContainsKey(path) ||
                   policy.Classify(path).Kind == Core.Downloads.DownloadFileKind.Seed;
        });
        return hasSeed ? root : DownloadPathNamePolicy.JoinSavePath(root, directories.MediaDirectory);
    }

    private void AddOriginalFiles(
        DownloadTask task, Torrent torrent, bool isMultiFile, IReadOnlyDictionary<string, string> demotedSeedPaths)
    {
        var policy = fileClassificationPolicy ?? new DownloadFileClassificationPolicy(aniParser);
        for (var index = 0; index < torrent.Files.Count; index++)
        {
            var file           = torrent.Files[index];
            var path           = TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile);
            var classification = policy.Classify(path);
            var extension      = Path.GetExtension(path);
            var subtitle = extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".sup", StringComparison.OrdinalIgnoreCase);
            var kind = classification.Kind == Core.Downloads.DownloadFileKind.Seed ? DownloadFileKind.Seed :
                subtitle ? DownloadFileKind.Subtitle : DownloadFileKind.Anime;
            var reason = classification.Reason;

            // 包内择优落选与 Seed 附件分开记录：落选文件不承担下载（提交后设为不下载），
            // 普通附件仍默认下载，两者不能共用一个分类语义。
            var isSelected = true;
            if (demotedSeedPaths.TryGetValue(path, out var demotedReason))
            {
                kind       = DownloadFileKind.Seed;
                reason     = demotedReason;
                isSelected = false;
            }

            task.Files.Add(new DownloadTaskFile
            {
                Id                    = Guid.NewGuid(),
                DownloadTaskId        = task.Id,
                FileIndex             = index,
                OriginalRelativePath  = path,
                CurrentRelativePath   = path,
                Size                  = file.Length,
                Kind                  = kind,
                ClassificationReason  = reason,
                IsSelectedForDownload = isSelected,
                SelectionReason       = isSelected ? null : demotedSeedPaths.GetValueOrDefault(path),
                UpdatedAtUtc          = timeProvider.GetUtcNow()
            });
        }

        // 所选内容总大小：存在跳过文件时任务进度按所选内容计算。
        DownloadTaskLookups.RefreshSelectedTotals(task);
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

    /// <summary>逐集入擂范围：该集在新任务中的承载正片（包内择优后的胜者）与入擂候选身份。</summary>
    private sealed record EpisodeScope(
        decimal                         Episode,
        EpisodeCandidate                Incoming,
        IReadOnlyList<DownloadTaskFile> VideoFiles);

    /// <summary>
    ///     逐集裁决结论：胜出（含无占用者的缺集）或落选。占用者信息供落选原因、未完成占用者的
    ///     承接侧效应与每集登记表使用；IncumbentVideoFileId 仅已完成占用者有意义（其胜者正片）。
    /// </summary>
    private sealed record EpisodeVerdict(
        decimal Episode,
        bool    Winner,
        Guid?   IncumbentTaskId,
        string? IncumbentNaming,
        bool    IncumbentCompleted,
        Guid?   IncumbentVideoFileId);

    /// <summary>包内文件集分析结果：择优落选文件的路径到原因映射、标题解析失败时可充当任务身份的胜者解析，与每集包内胜者的解析。</summary>
    private sealed record TorrentFileAnalysis(
        IReadOnlyDictionary<string, string>                 DemotedSeedPaths,
        ParseResult?                                        IdentityParse,
        IReadOnlyList<(decimal Episode, ParseResult Parse)> EpisodeIdentities);
}
