using Banned.AniParser;
using Banned.AniParser.Models;
using Banned.AniParser.Models.Enums;
using Banned.Qbittorrent.Exceptions;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Torrents;
using MediaManager.Data;
using MediaManager.Features.Notifications;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace MediaManager.Features.Downloads;

/// <summary>
///     下载闭环：创建意图、提交下载器、对账至完成。
///     提交前后都按 info-hash 对账：下载器中已存在的同 hash 种子（自己上次提交未确认，或用户/外部工具预先添加）
///     直接接管推进，不重复提交；接管时补打来源 tag 并把隔离路径对齐为种子真实位置。
///     提交时随种子设置来源 tag 与命名快照名；数据库唯一约束兜底并发。单条任务失败不影响其他任务，更不影响既有资源。
/// </summary>
public sealed class DownloadService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    DownloadCoordinationGate                coordinationGate,
    ITorrentFileSource                      torrentFileSource,
    IFeedSourceReader                       feedSourceReader,
    IQbittorrentSessionFactory              sessionFactory,
    ManageQbittorrentSettings               qbittorrentSettings,
    NotificationEnqueuer                    notificationEnqueuer,
    AniParser                               aniParser,
    NamingTemplateStore                     namingTemplateStore,
    ReleaseNamePreviewPolicy                namingPolicy,
    CandidateEligibilityPolicy              eligibilityPolicy,
    CandidateRankingPolicy                  rankingPolicy,
    ISelectionRuleProvider                  ruleProvider,
    ISubjectMetadataProvider                subjectMetadataProvider,
    ISubjectLinkResolver                    subjectLinkResolver,
    TimeProvider                            timeProvider,
    ILogger<DownloadService>                logger,
    MediaManager.Core.Subjects.SubjectEpisodeCorrectionPolicy episodeCorrectionPolicy)
{
    /// <summary>响应丢失后的自动重新提交上限；达到上限视为提交失败，等待人工重试。</summary>
    public const int MaxAutoSubmitAttempts = 5;

    /// <summary>所有下载登记的前置门禁：解析 Bangumi 绑定，未绑定返回 null（调用方拒绝下载并要求补 ID）。</summary>
    private const string SubjectUnboundMessage = "未能绑定 Bangumi Subject，请在表单补充 Bangumi ID 后重试。";

    /// <summary>能解析出集数的内容文件扩展名：视频与字幕；其余一律视为附属文件。</summary>
    private static readonly string[] ContentFileExtensions =
        [".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".mov", ".flv", ".ass", ".ssa", ".srt", ".sup"];

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
    ///     命名优先按模板渲染（标题能被解析出集数时），并落命名要素供完成改名；
    ///     解析失败回退种子内 info.name（缺失时按 hash 派生）；来源站点优先按种子内 tracker 自动识别，
    ///     siteSource 仅在 tracker 无法判定时作为兜底。
    ///     解析出单集/剧场版标识时先过统一同集裁决：既有同源同集任务按规则更优则拒绝本次上传，
    ///     新上传更优则自动顶替（ReplacesTaskId 指向旧任务）。
    /// </summary>
    public async Task<Guid> CreateFromUploadAsync(byte[]            torrentBytes, string? siteSource = null,
                                                  int?              subjectId         = null,
                                                  CancellationToken cancellationToken = default)
    {
        if (torrentBytes.Length == 0) throw new InvalidOperationException("所选文件为空，请重新选择。");

        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            throw new InvalidOperationException("请先在设置页启用并配置 qBittorrent 下载器。");

        if (!TorrentInfoHashExtractor.TryExtractInfoHash(torrentBytes, out var infoHash))
            throw new InvalidOperationException("无法从种子文件中提取 info-hash，文件可能无效或已损坏。");

        var displayName = TorrentInfoHashExtractor.TryExtractDisplayName(torrentBytes, out var info)
            ? info
            : null;
        var resolvedSource = ResolveSiteSource(torrentBytes, null, null, siteSource);
        // 门禁：必须绑定 Bangumi Subject；标题映射未命中且未填 ID 时拒绝登记。
        var uploadedParsed = displayName is null ? null : aniParser.Parse(displayName);
        var binding = await ResolveSubjectBindingAsync(uploadedParsed, null, subjectId, null, null,
                                                       cancellationToken) ??
                      throw new InvalidOperationException(SubjectUnboundMessage);
        var (namingSnapshot, namingIngredients, namingParsed) =
            await BuildNamingAsync(displayName, null, resolvedSource, $"上传种子：{infoHash[..8]}",
                                   uploadedParsed, binding.DisplayName, cancellationToken);

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await FindActiveDuplicateIdAsync(db, infoHash, null, cancellationToken) is not null)
            throw new InvalidOperationException("同一资源已有下载任务（可能因更低优先级被替换终止）。");

        // 与条目路径一致：已完成的同资源同样不重复登记。
        if (await FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
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
                                                          FeedItemEvaluator.NormalizeTitle(identity.Title),
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
                    SavePath          = BuildSavePath(settings.SavePath, null, namingSnapshot, infoHash),
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
            SavePath          = BuildSavePath(settings.SavePath, null, namingSnapshot, infoHash),
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
        db.DownloadTasks.Add(task);
        // 登记即替换：同事务终止现任；qB 删除留给协调门内对账，避免在订阅外层事务中等待 I/O。
        if (replacesTaskId is { } incumbentId)
            await SupersedeIncumbentAsync(db, incumbentId, task.Id, cancellationToken, deleteDownloader: false);
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
        logger.LogInformation(
                              "RSS 链接下载完成：{FeedUrl} 新建 {Created} 个、跳过重复 {Duplicate} 个、择优跳过 {Superseded} 个、失败 {Failed} 个。",
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
                                                    SeriesKey : FeedItemEvaluator.NormalizeTitle(entry.Parsed.Title),
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
                                   binding.DisplayName, cancellationToken);

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await FindActiveDuplicateIdAsync(db, infoHash, null, cancellationToken) is not null ||
            await FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
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
                                                          FeedItemEvaluator.NormalizeTitle(identity.Title),
                                                          identity.Episode, resolvedSource, infoHash, null,
                                                          cancellationToken);
            if (!arbitration.Register)
            {
                // 落选仍留一条终止记录：同 hash 查重命中即知该资源已输给更高优先级任务，重复添加直接终止；
                // 顶替者被删时该记录回退重新排队。字段与正常任务一致，回退后可直接提交。
                var superseded = BuildTaskRecord(DownloadTaskStatus.Replaced, null,
                                                 arbitration.SupersededByTaskId);
                superseded.FailedStep = "择优";
                superseded.Error = "同集候选落选，保留终止记录供查重。";
                db.DownloadTasks.Add(superseded);
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("RSS 条目同集落选，已登记终止记录 {TaskId}（hash {InfoHash}）。",
                                      superseded.Id, infoHash);
                return (null, arbitration.SkipReason);
            }

            replacesTaskId = arbitration.ReplacesTaskId;
        }

        DownloadTask BuildTaskRecord(DownloadTaskStatus status, Guid? replaces, Guid? replacedBy)
        {
            return new DownloadTask
            {
                Id                = Guid.NewGuid(),
                InfoHash          = infoHash,
                TorrentFile       = torrentBytes,
                TorrentUrl        = item.TorrentUri!.AbsoluteUri,
                SavePath          = BuildSavePath(basePath, null, namingSnapshot, infoHash),
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
        }

        var task = BuildTaskRecord(DownloadTaskStatus.PendingSubmit, replacesTaskId, null);
        db.DownloadTasks.Add(task);
        // 登记即替换：同事务终止现任，低优先任务不再进入提交或继续下载。
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
        string?      primaryTitle,  string? fallbackTitle, string?           siteSource, string lastResort,
        ParseResult? primaryParsed, string? subjectName,   CancellationToken cancellationToken)
    {
        var template = await namingTemplateStore.GetAsync(cancellationToken);
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
            var ingredients = new TorrentNamingIngredients(template, subjectName ?? parsed.Title, episodeText,
                                                           parsed.Version, group, siteSource,
                                                           parsed.Language.ToString(), parsed.Resolution.ToString());
            return (namingPolicy.Create(ingredients), ingredients.ToJson(), parsed);
        }

        return (TruncateSnapshot(primaryTitle) ?? TruncateSnapshot(fallbackTitle) ?? lastResort, null, null);
    }

    /// <summary>
    ///     登记即替换未完成现任；已完成资源在新版本实际完成前仍是当前版本，必须保留 Completed，
    ///     否则后续候选的版本守卫无法将其作为替换链目标。已提交未完成资源才从下载器连文件删除。
    /// </summary>
    private async Task SupersedeIncumbentAsync(
        ApplicationDbContext db, Guid incumbentId, Guid newTaskId, CancellationToken cancellationToken,
        bool deleteDownloader = true)
    {
        var incumbent = await db.DownloadTasks.SingleOrDefaultAsync(value => value.Id == incumbentId,
                                                                    cancellationToken);
        if (incumbent is null                                         ||
            incumbent.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Replaced ||
            incumbent.ReplacedByTaskId != null)
            return;

        if (deleteDownloader && incumbent.Status is DownloadTaskStatus.Submitting or
            DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)
        {
            try
            {
                await using var session = await sessionFactory.CreateAsync(cancellationToken);
                var found = await session.FindTorrentsAsync([incumbent.InfoHash], cancellationToken);
                if (found.Count > 0)
                    await session.DeleteTorrentAsync(incumbent.InfoHash, true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "登记替换现任 {TaskId} 时删除下载器种子失败；保持现任不变，替换任务将回滚或记录失败。", incumbentId);
                throw new InvalidOperationException("下载器中旧任务删除失败，未能安全替换。", exception);
            }
        }

        incumbent.Status           = DownloadTaskStatus.Replaced;
        incumbent.ReplacedByTaskId = newTaskId;
        logger.LogInformation("任务 {TaskId} 因更高优先级任务 {NewTaskId} 登记被直接替换。", incumbentId, newTaskId);
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
        var seriesKey = parsed is null ? string.Empty : FeedItemEvaluator.NormalizeTitle(parsed.Title);
        if (requestedSubjectId is { } explicitId)
        {
            var binding = await EnsureSubjectAsync(explicitId, cancellationToken);
            await SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
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
            await SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
            return binding;
        }

        if (itemLink is not null)
        {
            var linkedSubjectId = await subjectLinkResolver.ResolveSubjectIdAsync(itemLink, proxyId,
                cancellationToken);
            if (linkedSubjectId is not null)
            {
                var binding = await EnsureSubjectAsync(linkedSubjectId.Value, cancellationToken);
                await SaveTitleMappingAsync(seriesKey, binding.SubjectId, cancellationToken);
                return binding;
            }
        }

        return null;
    }

    /// <summary>确保 Subject 元数据在本地可用：缓存行直接用，否则调 Bangumi API 拉取并落库（失败抛出，由调用方拒绝登记）。</summary>
    private async Task<SubjectBinding> EnsureSubjectAsync(int subjectId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cached = await db.MediaSubjects.AsNoTracking()
                             .Where(subject => subject.Id == subjectId)
                             .Select(subject => new { subject.Id, subject.Name, subject.NameCn })
                             .SingleOrDefaultAsync(cancellationToken);
        if (cached is not null)
            return new SubjectBinding(cached.Id,
                                      string.IsNullOrWhiteSpace(cached.NameCn) ? cached.Name : cached.NameCn);

        var metadata = await subjectMetadataProvider.GetAsync(subjectId, cancellationToken);
        await SaveSubjectMetadataAsync(metadata, cancellationToken);
        return new SubjectBinding(metadata.Id, metadata.DisplayName);
    }

    private async Task SaveSubjectMetadataAsync(SubjectMetadata subject, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MediaSubjects.SingleOrDefaultAsync(item => item.Id == subject.Id, cancellationToken);
        if (entity is null)
        {
            entity = new MediaSubject { Id = subject.Id };
            db.MediaSubjects.Add(entity);
        }

        entity.Name        = subject.Name;
        entity.NameCn      = subject.NameCn;
        entity.Platform    = subject.Platform;
        entity.AirDate     = subject.AirDate;
        entity.ImageUrl    = subject.ImageUrl;
        entity.CachedAtUtc = subject.CachedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>标题映射回写：解析出系列标题才落映射，让同系列后续条目直接命中。</summary>
    private async Task SaveTitleMappingAsync(string seriesKey, int subjectId, CancellationToken cancellationToken)
    {
        if (seriesKey.Length == 0) return;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mapping =
            await db.SubjectTitleMappings.SingleOrDefaultAsync(item => item.NormalizedTitle == seriesKey,
                                                               cancellationToken);
        if (mapping is null)
        {
            db.SubjectTitleMappings.Add(new SubjectTitleMapping
            {
                Id              = Guid.NewGuid(),
                NormalizedTitle = seriesKey,
                SubjectId       = subjectId,
                UpdatedAtUtc    = timeProvider.GetUtcNow()
            });
        }
        else
        {
            mapping.SubjectId    = subjectId;
            mapping.UpdatedAtUtc = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>下载种子文件并提取 info-hash；网络请求不置于数据库事务内。</summary>
    public async Task<(byte[] TorrentBytes, string InfoHash)> DownloadTorrentAsync(
        string torrentUrl, Guid? proxyId, CancellationToken cancellationToken)
    {
        var torrentBytes = await torrentFileSource.DownloadAsync(new Uri(torrentUrl), proxyId, cancellationToken);
        if (!TorrentInfoHashExtractor.TryExtractInfoHash(torrentBytes, out var infoHash))
            throw new InvalidOperationException("无法从种子文件中提取 info-hash，文件可能无效或已损坏。");

        return (torrentBytes, infoHash);
    }

    /// <summary>
    ///     在给定上下文内核验重复、决定替换对象并落库任务，由调用方控制事务提交。
    ///     自动登记与手动创建共用该核心，保证查重与替换判定一致。
    /// </summary>
    public async Task<Guid> CreateTaskWithinContextAsync(
        ApplicationDbContext db, FeedItem item, byte[] torrentBytes, string infoHash,
        CancellationToken    cancellationToken)
    {
        if (await FindActiveDuplicateIdAsync(db, infoHash, item.Id, cancellationToken) is not null)
            throw new InvalidOperationException("该条目或同一资源已有进行中的下载任务。");

        // 有效完成的同资源不自动重新下载；完成记录不因不属于活动唯一索引而被重复登记绕过。
        if (await FindCompletedDuplicateIdAsync(db, infoHash, cancellationToken) is not null)
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
                                                          FeedItemEvaluator.NormalizeTitle(item.ParsedTitle),
                                                          item.Episode, resolvedSource, infoHash, item.SubjectId,
                                                          cancellationToken);
            if (!arbitration.Register)
                throw new InvalidOperationException(arbitration.SkipReason!);
            arbitrationReplaces = arbitration.ReplacesTaskId;
        }

        // 替换对象在创建与完成两处共用同一规则，见 FindReplacementTargetAsync；
        // 同源占用者（规则裁决）优先，其余情况沿用跨源 Completed 的版本守卫目标。
        var replacesTaskId = arbitrationReplaces ??
                             await FindReplacementTargetAsync(db, item.SubjectId, item.Episode, infoHash, item.Version,
                                                              cancellationToken);

        var task = new DownloadTask
        {
            Id          = Guid.NewGuid(),
            FeedItemId  = item.Id,
            SubjectId   = item.SubjectId,
            InfoHash    = infoHash,
            TorrentFile = torrentBytes,
            TorrentUrl  = item.TorrentUrl!,
            SavePath = BuildSavePath((await qbittorrentSettings.GetAsync(cancellationToken)).SavePath,
                                     item.Subject?.NameCn ?? item.Subject?.Name,
                                     item.NamingPreview!, infoHash),
            SiteSource        = resolvedSource,
            NamingSnapshot    = item.NamingPreview!,
            NamingIngredients = item.NamingIngredients,
            Episode           = item.Episode,
            Version           = item.Version,
            Status            = DownloadTaskStatus.PendingSubmit,
            CreatedAtUtc      = timeProvider.GetUtcNow(),
            ReplacesTaskId    = replacesTaskId
        };
        db.DownloadTasks.Add(task);
        // 订阅事务由调用方提交；此处仅在同一 DbContext 中登记现任状态，保持原子落库。
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
                : FeedItemEvaluator.NormalizeTitle(ingredients.Title);
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
        if (verdict.Winner?.FeedItemId == incoming.FeedItemId)
            return new EpisodeArbitration(true, incumbent.TaskId, null);

        return new EpisodeArbitration(false, null, $"同集已有更优任务：{incumbent.Naming}", incumbent.TaskId);
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
        string stableId, DateTimeOffset? publishedAtUtc, ParseResult parsed, int subjectId)
    {
        return new EpisodeCandidate
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
    }

    /// <summary>由稳定标识派生确定性 Guid：合成候选的决胜输入在同批次与重试之间保持稳定。</summary>
    private static Guid StableIdGuid(string stableId)
    {
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(stableId))[..16]);
    }

    /// <summary>一轮对账：提交待提交任务、核对未确认与下载中任务。下载器未启用或未配置时静默跳过。</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return;
        List<DownloadTask> tasks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                           task.Status == DownloadTaskStatus.Submitting    ||
                                           task.Status == DownloadTaskStatus.Downloading   ||
                                           task.Status == DownloadTaskStatus.Paused)
                            .ToListAsync(cancellationToken);
        }

        // SQLite 不支持 DateTimeOffset 服务端排序；先创建先处理，在内存中排序。
        tasks = tasks.OrderBy(task => task.CreatedAtUtc).ToList();

        if (tasks.Count == 0) return;

        await using var session = await sessionFactory.CreateAsync(cancellationToken);

        // 登记期替换只落库，不在选择事务内等待 qB；在全局协调门内清理被替换且未完成的资源。
        List<DownloadTask> superseded;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
            superseded = await db.DownloadTasks.AsNoTracking()
                .Where(task => task.Status == DownloadTaskStatus.Replaced && task.ReplacedByTaskId != null &&
                               task.CompletedAtUtc == null)
                .ToListAsync(cancellationToken);
        foreach (var oldTask in superseded)
        {
            var stillOwned = await IsActiveTaskForHashAsync(oldTask.InfoHash, oldTask.Id, cancellationToken);
            if (stillOwned) continue;
            var foundOld = await session.FindTorrentsAsync([oldTask.InfoHash], cancellationToken);
            if (foundOld.Count > 0)
                await session.DeleteTorrentAsync(oldTask.InfoHash, true, cancellationToken);
        }

        var reconcileHashes = tasks
                             .Where(task => task.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)
                             .Select(task => task.InfoHash)
                             .ToList();
        var foundByHash = (await session.FindTorrentsAsync(reconcileHashes, cancellationToken))
                         .GroupBy(snapshot => snapshot.Hash)
                         .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks.Where(task => task.Status is DownloadTaskStatus.Downloading
                                                             or DownloadTaskStatus.Paused))
            try
            {
                await ReconcileTrackedAsync(session, task, foundByHash.GetValueOrDefault(task.InfoHash),
                                            cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "对账下载任务 {TaskId} 失败，下一轮重试。", task.Id);
            }

        foreach (var task in tasks.Where(task => task.Status is DownloadTaskStatus.PendingSubmit
                                                             or DownloadTaskStatus.Submitting))
            try
            {
                var current = await LoadTaskAsync(task.Id, cancellationToken);
                if (current.Status is not (DownloadTaskStatus.PendingSubmit or DownloadTaskStatus.Submitting)) continue;
                await SubmitAsync(session, current, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "提交下载任务 {TaskId} 失败，下一轮重试。", task.Id);
            }
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status != DownloadTaskStatus.Downloading) throw new InvalidOperationException("只有下载中的任务可以暂停。");

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.PauseAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.Paused;
            update.IsUserPaused = true;
        }, cancellationToken);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status != DownloadTaskStatus.Paused || !task.IsUserPaused)
            throw new InvalidOperationException("只有人工暂停的任务可以恢复。");

        await using var session = await sessionFactory.CreateAsync(cancellationToken);
        await session.ResumeAsync(task.InfoHash, cancellationToken);
        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.Downloading;
            update.IsUserPaused = false;
        }, cancellationToken);
    }

    /// <summary>人工重试：失败或丢失的任务重新进入待提交队列，沿用原决策与命名快照。</summary>
    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);
        if (task.Status is not (DownloadTaskStatus.Failed or DownloadTaskStatus.Lost))
            throw new InvalidOperationException("只有失败或丢失的任务可以重试。");

        await UpdateTaskAsync(taskId, task.Status, update =>
        {
            update.Status       = DownloadTaskStatus.PendingSubmit;
            update.AttemptCount = 0;
            update.FailedStep   = null;
            update.Error        = null;
        }, cancellationToken);
    }

    /// <summary>
    ///     删除面板的预检：下载器中当前是否存在该任务的种子。下载器未启用、未配置或暂时不可达时
    ///     返回 null（未知），面板仍让用户自行决定是否勾选删除下载器种子。
    /// </summary>
    public async Task<bool?> TorrentInDownloaderAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await LoadTaskAsync(taskId, cancellationToken);

        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured) return null;

        try
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            var             found   = await session.FindTorrentsAsync([task.InfoHash], cancellationToken);
            return found.Count > 0;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "删除预检：查询下载器中任务 {TaskId} 的种子失败，按未知处理。", taskId);
            return null;
        }
    }

    /// <summary>
    ///     删除任务记录。deleteFromDownloader 时先按 hash 删除下载器中的种子（可连带文件）；
    ///     下载器中已不存在该种子时跳过删除器步骤，整个操作幂等可重试。
    ///     被本任务替换的旧任务回退为已完成（其文件仍在，重新成为当前版本）；
    ///     其他任务对本任务的替换声明一并清除，不留悬空引用。
    /// </summary>
    public async Task DeleteAsync(Guid              taskId, bool deleteFromDownloader, bool deleteFiles,
                                  CancellationToken cancellationToken = default)
    {
        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        var task = await LoadTaskAsync(taskId, cancellationToken);

        if (deleteFromDownloader)
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            var             found   = await session.FindTorrentsAsync([task.InfoHash], cancellationToken);
            if (found.Count > 0)
                await session.DeleteTorrentAsync(task.InfoHash, deleteFiles, cancellationToken);
        }

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var tracked = await db.DownloadTasks.SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
            if (tracked is null) return;

            await ClearReplacementReferencesAsync(db, [taskId], cancellationToken);
            db.DownloadTasks.Remove(tracked);
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("已删除下载任务 {TaskId}（hash {InfoHash}，删除器种子：{DeleteTorrent}，连带文件：{DeleteFiles}）。",
                              taskId, task.InfoHash, deleteFromDownloader, deleteFiles);
    }

    /// <summary>批量删除面板预检：现存任务数与其中在下载器里能找到种子的数量；DownloaderChecked 为 false 表示下载器状态未知。</summary>
    public async Task<BulkDeletePrecheck> PrecheckBulkDeleteAsync(
        IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default)
    {
        List<string> hashes;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            hashes = await db.DownloadTasks.AsNoTracking()
                             .Where(task => taskIds.Contains(task.Id))
                             .Select(task => task.InfoHash)
                             .ToListAsync(cancellationToken);
        }

        var settings = await qbittorrentSettings.GetAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            return new BulkDeletePrecheck(hashes.Count, 0, false);

        try
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            var             found   = await session.FindTorrentsAsync(hashes, cancellationToken);
            return new BulkDeletePrecheck(hashes.Count, found.Count, true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "批量删除预检：查询下载器中 {Count} 个任务的种子失败，按未知处理。", hashes.Count);
            return new BulkDeletePrecheck(hashes.Count, 0, false);
        }
    }

    /// <summary>
    ///     批量删除：与单任务删除同一语义（先下载器后数据库、幂等、清理替换引用并回退被替换任务），
    ///     但一次会话完成下载器删除、一次落库完成记录删除；已不存在的任务 Id 静默跳过。
    /// </summary>
    public async Task<int> DeleteManyAsync(
        IReadOnlyCollection<Guid> taskIds, bool deleteFromDownloader, bool deleteFiles,
        CancellationToken         cancellationToken = default)
    {
        if (taskIds.Count == 0) return 0;

        List<DownloadTask> tasks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => taskIds.Contains(task.Id))
                            .ToListAsync(cancellationToken);
        }

        if (tasks.Count == 0) return 0;

        using var coordinationLease = await coordinationGate.EnterAsync(cancellationToken);
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
            tasks = await db.DownloadTasks.AsNoTracking()
                            .Where(task => taskIds.Contains(task.Id))
                            .ToListAsync(cancellationToken);
        if (tasks.Count == 0) return 0;

        if (deleteFromDownloader)
        {
            await using var session = await sessionFactory.CreateAsync(cancellationToken);
            var foundHashes = (await session.FindTorrentsAsync(tasks.Select(task => task.InfoHash).ToList(),
                                                               cancellationToken))
                             .Select(snapshot => snapshot.Hash)
                             .ToList();
            if (foundHashes.Count > 0)
                await session.DeleteTorrentsAsync(foundHashes, deleteFiles, cancellationToken);
        }

        var deletedCount = 0;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var ids     = tasks.Select(task => task.Id).ToList();
            var tracked = await db.DownloadTasks.Where(task => ids.Contains(task.Id)).ToListAsync(cancellationToken);
            deletedCount = tracked.Count;

            await ClearReplacementReferencesAsync(db, ids, cancellationToken);
            db.DownloadTasks.RemoveRange(tracked);
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("已批量删除 {Count} 个下载任务（删除器种子：{DeleteTorrent}，连带文件：{DeleteFiles}）。",
                              deletedCount, deleteFromDownloader, deleteFiles);
        return deletedCount;
    }

    /// <summary>清理指向待删任务的替换引用：替换者被删的旧任务回退为已完成，其余任务对待删任务的替换声明置空。</summary>
    private static async Task ClearReplacementReferencesAsync(
        ApplicationDbContext db, IReadOnlyCollection<Guid> deletedIds, CancellationToken cancellationToken)
    {
        var referencing = await db.DownloadTasks
                                  .Where(other => (other.ReplacesTaskId != null &&
                                                   deletedIds.Contains(other.ReplacesTaskId.Value)) ||
                                                  (other.ReplacedByTaskId != null &&
                                                   deletedIds.Contains(other.ReplacedByTaskId.Value)))
                                  .ToListAsync(cancellationToken);
        foreach (var other in referencing)
        {
            if (other.ReplacedByTaskId is { } replacedBy && deletedIds.Contains(replacedBy))
            {
                // 替换者已删除：仅确有完成记录的旧资源回退为已完成；未提交落选者保留
                // Replaced 墓碑，避免一次删除同时复活多个候选造成同集重复下载。
                other.ReplacedByTaskId = null;
                if (other.Status == DownloadTaskStatus.Replaced && other.CompletedAtUtc is not null)
                    other.Status = DownloadTaskStatus.Completed;
            }

            if (other.ReplacesTaskId is { } replaces && deletedIds.Contains(replaces))
                other.ReplacesTaskId = null;
        }
    }

    public async Task<IReadOnlyList<DownloadTaskView>> ListAsync(
        int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.DownloadTasks.AsNoTracking()
                            .Include(task => task.FeedItem)
                            .Include(task => task.Subject)
                            .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序并截取。
        tasks = tasks.OrderByDescending(task => task.CreatedAtUtc)
                     .Take(Math.Clamp(take, 1, 500))
                     .ToList();

        var relatedIds = tasks.SelectMany(task => new[] { task.ReplacesTaskId, task.ReplacedByTaskId })
                              .Where(value => value is not null)
                              .Select(value => value!.Value)
                              .Distinct()
                              .ToList();
        var relatedNames = relatedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.DownloadTasks.AsNoTracking()
                      .Where(task => relatedIds.Contains(task.Id))
                      .ToDictionaryAsync(task => task.Id, task => task.NamingSnapshot, cancellationToken);
        return tasks.Select(task => ToView(task, relatedNames)).ToList();
    }

    private async Task SubmitAsync(IQbittorrentSession session, DownloadTask task, CancellationToken cancellationToken)
    {
        // Reconcile 的任务列表可能在其它流程登记替换前读取；门禁内再次取当前状态，旧快照不得提交。
        task = await LoadTaskAsync(task.Id, cancellationToken);
        if (task.Status is not (DownloadTaskStatus.PendingSubmit or DownloadTaskStatus.Submitting)) return;

        // 提交前先按 hash 对账：下载器中已存在的同 hash 种子直接接管——既覆盖“自己上次提交已生效
        // 但未收到确认”，也覆盖“用户或其他工具预先添加的种子”。数据库活动唯一索引保证同 hash
        // 至多一个活动任务，接管不会与自身其他任务互相抢占。
        var existing = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken))
           .FirstOrDefault();
        if (existing is not null)
        {
            await AdoptExistingAsync(task, existing, session, cancellationToken);
            return;
        }

        if (task.Status == DownloadTaskStatus.Submitting && task.AttemptCount >= MaxAutoSubmitAttempts)
        {
            await MarkFailedAsync(task.Id, task.Status, "提交", "多次提交后仍未在下载器中发现该任务，请检查下载器后人工重试。",
                                  cancellationToken);
            return;
        }

        // 请求发出前先落库 Submitting：下载器已接收而应用在保存结果前退出时，重启按 Hash 对账而不是重复提交。
        await UpdateTaskAsync(task.Id, task.Status, update =>
        {
            update.Status         =   DownloadTaskStatus.Submitting;
            update.AttemptCount   +=  1;
            update.SubmittedAtUtc ??= timeProvider.GetUtcNow();
        }, cancellationToken);

        string response;
        try
        {
            response = await session.AddTorrentAsync(task.TorrentFile, task.SavePath,
                                                     TagsFor(task), task.NamingSnapshot, cancellationToken);
        }
        catch (Exception exception) when (exception is QbittorrentLoginFailedException
                                                    or QbittorrentUnauthorizedException
                                                    or QbittorrentForbiddenException)
        {
            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "认证",
                                  $"下载器认证失败，请检查设置页的地址与账号：{exception.Message}", cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is QbittorrentBadRequestException or QbittorrentNotFoundException)
        {
            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝了提交：{exception.Message}",
                                  cancellationToken);
            return;
        }
        catch (QbittorrentException exception)
        {
            // 服务器 5xx 或网络中断：Submitting 已在请求发出前落库，等待下一轮按 Hash 对账。
            logger.LogWarning(exception, "提交下载任务 {TaskId} 后未收到确认，等待按 Hash 对账。", task.Id);
            return;
        }

        // Add 可能在请求发出后被下载器接受，即使协调门禁按预期阻止并发登记；
        // 最终持久状态仍作一次权威检查，若任务在外部事务中已被替换则清除迟到资源。
        var afterAdd = await LoadTaskAsync(task.Id, cancellationToken);
        if (afterAdd.Status is not (DownloadTaskStatus.Submitting or DownloadTaskStatus.PendingSubmit))
        {
            var lateTorrent = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken)).FirstOrDefault();
            if (lateTorrent is not null)
                await session.DeleteTorrentAsync(task.InfoHash, true, cancellationToken);
            return;
        }

        var responseText = (response ?? string.Empty).Trim();
        if (responseText.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            // "Fails." 也可能是种子已被下载器接收（如外部预先添加、响应语义差异）：再按 hash 确认一次，找到即接管。
            var confirmed = (await session.FindTorrentsAsync([task.InfoHash], cancellationToken))
               .FirstOrDefault();
            if (confirmed is not null)
            {
                await AdoptExistingAsync(task, confirmed, session, cancellationToken);
                return;
            }

            await MarkFailedAsync(task.Id, DownloadTaskStatus.Submitting, "提交", $"下载器拒绝该种子：{responseText}",
                                  cancellationToken);
            return;
        }

        await UpdateTaskAsync(task.Id, DownloadTaskStatus.Submitting,
                              update => { update.Status = DownloadTaskStatus.Downloading; }, cancellationToken,
                              (db, updated) =>
                                  notificationEnqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted,
                                                                       "下载开始",
                                                                       $"{DescribeTask(updated)}：已提交到下载器开始下载。命名：{updated.NamingSnapshot}",
                                                                       cancellationToken));
    }

    /// <summary>接管下载器中已存在的同 hash 种子：补打来源 tag、把本地隔离路径对齐为种子真实位置，再按快照推进。</summary>
    private async Task AdoptExistingAsync(DownloadTask        task,    TorrentSnapshot   snapshot,
                                          IQbittorrentSession session, CancellationToken cancellationToken)
    {
        var siteSource = task.SiteSource;
        if (!string.IsNullOrWhiteSpace(siteSource) &&
            !snapshot.Tags.Contains(siteSource, StringComparer.OrdinalIgnoreCase))
            await session.AddTagAsync(task.InfoHash, siteSource, cancellationToken);

        if (!MatchesIsolationPath(snapshot.SavePath, task.SavePath))
            // 隔离路径让位于种子的真实位置：接管外部种子时它通常不在本应用约定的目录里，
            // 后续对账与完成改名都以真实位置为准。
            await UpdateTaskAsync(task.Id, task.Status,
                                  update => { update.SavePath = NormalizePath(snapshot.SavePath); },
                                  cancellationToken);

        await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
    }

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

    /// <summary>提交时随种子设置的下载器 tag：即来源站点标签（mikan/anibt/u2/byrbt/tjupt 等）。</summary>
    private static IReadOnlyList<string> TagsFor(DownloadTask task)
    {
        return string.IsNullOrWhiteSpace(task.SiteSource) ? [] : [task.SiteSource];
    }

    private async Task ReconcileTrackedAsync(IQbittorrentSession session, DownloadTask task, TorrentSnapshot? snapshot,
                                             CancellationToken   cancellationToken)
    {
        if (snapshot is null)
        {
            await UpdateTaskAsync(task.Id, task.Status, update =>
            {
                update.Status     = DownloadTaskStatus.Lost;
                update.FailedStep = "对账";
                update.Error      = "下载器中不存在该任务，可能已被手动删除。";
            }, cancellationToken);
            return;
        }

        // 同 hash 不代表同一次提交：隔离路径不一致时不接管，避免误动其他目录里的同资源任务。
        if (!MatchesIsolationPath(snapshot.SavePath, task.SavePath))
        {
            await MarkFailedAsync(task.Id, task.Status, "对账",
                                  "下载器中同 hash 任务的保存路径与本任务的隔离目录不一致，未接管，请人工检查下载器。",
                                  cancellationToken);
            return;
        }

        await ApplySnapshotAsync(task.Id, task.Status, snapshot, session, cancellationToken);
    }

    /// <summary>按下载器快照推进本地状态：完成改名、识别人工暂停、记录进度。</summary>
    private async Task ApplySnapshotAsync(Guid taskId, DownloadTaskStatus currentStatus, TorrentSnapshot snapshot,
                                          IQbittorrentSession session, CancellationToken cancellationToken)
    {
        switch (snapshot.Category)
        {
            case QbittorrentTorrentCategory.Error :
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status     = DownloadTaskStatus.Failed;
                    update.FailedStep = "下载器";
                    update.Error      = "下载器报告该任务出错或文件缺失，请检查下载器。";
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Seeding when snapshot.Progress     >= 1.0 :
            case QbittorrentTorrentCategory.Downloading when snapshot.Progress >= 1.0 :
            case QbittorrentTorrentCategory.Stopped when snapshot.Progress     >= 1.0 :
                await CompleteAsync(session, taskId, snapshot, currentStatus, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Stopped :
                // 人工在下载器或页面暂停的任务必须保留暂停状态，对账绝不自动恢复。
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status          = DownloadTaskStatus.Paused;
                    update.IsUserPaused    = true;
                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
                }, cancellationToken);
                return;

            case QbittorrentTorrentCategory.Downloading :
                await UpdateTaskAsync(taskId, currentStatus, update =>
                {
                    update.Status          = DownloadTaskStatus.Downloading;
                    update.IsUserPaused    = false;
                    update.Progress        = snapshot.Progress;
                    update.TotalSize       = snapshot.TotalSize;
                    update.LastSyncedAtUtc = timeProvider.GetUtcNow();
                }, cancellationToken);
                return;

            default :
                // Unknown 状态不推进，等下一轮确认。
                return;
        }
    }

    /// <summary>
    ///     完成收尾：按命名要素改名（参照 ByrTorrent 的整理方法：单文件改文件名；多文件逐文件按各自
    ///     集数改文件名、解析不出集数的附属文件挪进 Seed/、有公共根目录时改根目录名），并按当前状态
    ///     重新选择替换对象。登记期同集裁决顶替掉的下载中低优先任务在此停删（连文件），已完成的
    ///     旧任务沿用版本守卫语义仅标记、文件保留。没有命名要素的存量任务回退按命名快照改名
    ///     （散文件布局无法唯一映射时如实失败）。
    /// </summary>
    private async Task CompleteAsync(
        IQbittorrentSession session, Guid taskId, TorrentSnapshot snapshot, DownloadTaskStatus currentStatus,
        CancellationToken   cancellationToken)
    {
        string                    targetName;
        string                    savePath;
        string?                   siteSource;
        int?                      subjectId;
        decimal?                  taskEpisode;
        int                       taskVersion;
        TorrentNamingIngredients? ingredients;

        IReadOnlyList<string> fileNames;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var task = await db.DownloadTasks.AsNoTracking()
                               .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken);
            if (task is null || task.Status.IsFinal()) return;

            fileNames = await session.ListFileNamesAsync(snapshot.Hash, cancellationToken);
            // 种子名不带扩展名，与添加时 rename 参数设置的名称同形；扩展名只属于内容文件。
            ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
            targetName  = ingredients is not null ? namingPolicy.Create(ingredients) : task.NamingSnapshot;
            savePath    = task.SavePath;
            siteSource  = task.SiteSource;
            subjectId   = task.SubjectId;
            taskEpisode  = task.Episode;
            taskVersion = task.Version;
        }

        if (fileNames.Count == 0)
        {
            await MarkFailedAsync(taskId, currentStatus, "改名", "无法读取种子内文件列表，无法按命名快照改名，请人工处理。", cancellationToken);
            return;
        }

        // 冲突按完整目标路径精确判断：不同隔离目录中的同名资源互不影响，只有同目录同名才算冲突。
        var siblings   = await session.ListTorrentsAsync(siteSource, cancellationToken);
        var targetPath = JoinSavePath(savePath, targetName);
        if (siblings.Any(other => other.Hash != snapshot.Hash &&
                                  string.Equals(JoinSavePath(other.SavePath, other.Name), targetPath,
                                                StringComparison.Ordinal)))
        {
            await MarkFailedAsync(taskId, currentStatus, "改名", "目标路径与下载器中其他任务冲突，请人工处理。",
                                  cancellationToken);
            return;
        }

        try
        {
            if (fileNames.Count == 1)
            {
                // 单文件也按文件名实际集数应用 Subject 修正规则；无可解析集数时保留任务快照命名。
                var extension = Path.GetExtension(fileNames[0]);
                var singleIngredients = ingredients is null
                    ? null
                    : CorrectIngredientsForFile(ingredients, Path.GetFileName(fileNames[0]), null, subjectId, taskVersion);
                var singleName = singleIngredients is null ? targetName : namingPolicy.Create(singleIngredients);
                await session.RenameTorrentFileAsync(snapshot.Hash, fileNames[0],
                                                     AppendExtension(singleName, extension), cancellationToken);
            }
            else if (ingredients is not null)
            {
                // 多文件种子：逐文件按各自集数改名，附属文件挪进 Seed/，再改公共根目录名。
                await RenameContentFilesAsync(session, snapshot.Hash, fileNames, ingredients, subjectId, taskEpisode, taskVersion, cancellationToken);
                if (TryGetCommonRootFolder(fileNames, out var rootFolder))
                    await session.RenameTorrentFolderAsync(snapshot.Hash, rootFolder, targetName, cancellationToken);
            }
            else if (TryGetCommonRootFolder(fileNames, out var rootFolder))
            {
                // 无命名要素的存量任务：改公共根目录名，保证内容目录与命名快照一致。
                await session.RenameTorrentFolderAsync(snapshot.Hash, rootFolder, targetName, cancellationToken);
            }
            else
            {
                // 多个顶层散文件无法唯一映射到命名快照：如实失败等待人工处理，不标记完成。
                await MarkFailedAsync(taskId, currentStatus, "改名",
                                      "种子包含多个顶层文件，无法自动按命名快照改名，请人工处理。", cancellationToken);
                return;
            }

            await session.RenameTorrentAsync(snapshot.Hash, targetName, cancellationToken);
        }
        catch (QbittorrentNotFoundException)
        {
            await UpdateTaskAsync(taskId, currentStatus, update =>
            {
                update.Status     = DownloadTaskStatus.Lost;
                update.FailedStep = "对账";
                update.Error      = "完成对账时下载器中已不存在该任务。";
            }, cancellationToken);
            return;
        }

        await using var completionDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trackedTask = await completionDb.DownloadTasks
                                            .SingleAsync(value => value.Id == taskId, cancellationToken);
        if (trackedTask.Status.IsFinal()) return;

        trackedTask.Status          = DownloadTaskStatus.Completed;
        trackedTask.Progress        = 1;
        trackedTask.CompletedAtUtc  = timeProvider.GetUtcNow();
        trackedTask.LastSyncedAtUtc = trackedTask.CompletedAtUtc;
        trackedTask.FailedStep      = null;
        trackedTask.Error           = null;

        // 登记期已直接替换现任（见 SupersedeIncumbentAsync），此处是兜底：登记时删除下载器种子失败
        // 而退回完成期替换的、以及竞态残留的替换对象——未下完的连种子带文件删除（不留半成品冗余数据），
        // 已下完的沿用版本守卫语义仅标记、保留文件。删除下载器种子失败不阻断完成，留待人工处理。
        var supersededHashes = new List<string>();
        if (trackedTask.ReplacesTaskId is { } arbitrationTargetId)
        {
            var arbitrationTarget = await completionDb.DownloadTasks
                                                      .SingleOrDefaultAsync(value => value.Id == arbitrationTargetId,
                                                                            cancellationToken);
            if (arbitrationTarget is not null                                     &&
                arbitrationTarget.ReplacedByTaskId == null                        &&
                arbitrationTarget.Status           != DownloadTaskStatus.Replaced &&
                arbitrationTarget.Status           != DownloadTaskStatus.Failed   &&
                arbitrationTarget.Status           != DownloadTaskStatus.Lost)
            {
                var stillDownloading = arbitrationTarget.Status != DownloadTaskStatus.Completed;
                arbitrationTarget.Status           = DownloadTaskStatus.Replaced;
                arbitrationTarget.ReplacedByTaskId = taskId;
                if (stillDownloading) supersededHashes.Add(arbitrationTarget.InfoHash);
            }
        }

        // 创建时记录的替换对象可能已过期：完成时按当前状态重新选择并直接赋值；
        // 查不到候选（旧任务已被更晚完成的任务替换、或仅剩更高版本任务）时清除声明，避免保留过期关系。
        // 遵守版本守卫：新任务只替换不高于自身版本的已完成项。若登记期的旧目标已被
        // 更高版本取代，清除过期声明而不跳过守卫去替换更高版本。
        trackedTask.ReplacesTaskId = await FindReplacementTargetAsync(completionDb, trackedTask.SubjectId,
                                                                      trackedTask.Episode, trackedTask.InfoHash,
                                                                      trackedTask.Version, cancellationToken);

        if (trackedTask.ReplacesTaskId is { } oldTaskId)
        {
            var oldTask = await completionDb.DownloadTasks
                                            .SingleOrDefaultAsync(value => value.Id == oldTaskId, cancellationToken);
            if (oldTask is not null && oldTask.Status != DownloadTaskStatus.Replaced)
            {
                oldTask.Status           = DownloadTaskStatus.Replaced;
                oldTask.ReplacedByTaskId = taskId;
            }
        }

        await completionDb.SaveChangesAsync(cancellationToken);
        foreach (var supersededHash in supersededHashes)
            try
            {
                await session.DeleteTorrentAsync(supersededHash, true, cancellationToken);
                logger.LogInformation("高优先任务 {TaskId} 完成，已顶替删除下载器中的低优先种子 {Hash}。", taskId,
                                      supersededHash);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                                  "顶替删除下载器中的低优先种子 {Hash} 失败，任务已标记被替换，请人工删除种子。", supersededHash);
            }

        logger.LogInformation("下载任务 {TaskId} 已完成并改名为 {TargetName}。", taskId, targetName);
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

    private string BuildSavePath(string basePath, string? subjectName, string namingPreview, string infoHash)
    {
        // 基础路径按用户输入原样使用（可能是 Windows 或 Linux 路径），只净化追加的目录段。
        var directory      = basePath.Trim().TrimEnd('/', '\\');
        var subjectSegment = DownloadPathNamePolicy.SanitizePathSegment(subjectName, 100);
        var namingSegment  = DownloadPathNamePolicy.SanitizePathSegment(namingPreview);
        var hashSegment    = infoHash.Length >= 8 ? infoHash[..8] : infoHash;

        var middle = string.IsNullOrWhiteSpace(subjectSegment) ? string.Empty : $"/{subjectSegment}";
        return $"{directory}{middle}/{namingSegment}-{hashSegment}";
    }

    /// <summary>保存路径归一化分隔符后精确比较：大小写必须一致，避免在大小写敏感的 Linux 文件系统上把不同目录判成同一目录。</summary>
    private static bool MatchesIsolationPath(string snapshotPath, string taskPath)
    {
        return string.Equals(NormalizePath(snapshotPath), NormalizePath(taskPath), StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/').TrimEnd('/');
    }

    private static string JoinSavePath(string directory, string name)
    {
        return $"{NormalizePath(directory)}/{name.Trim().Replace('\\', '/')}";
    }

    /// <summary>所有文件都位于同一顶层目录时返回该目录；顶层散文件或多目录布局返回 false。</summary>
    private static bool TryGetCommonRootFolder(IReadOnlyList<string> fileNames, out string rootFolder)
    {
        rootFolder = string.Empty;
        if (fileNames.Count == 0) return false;

        var firstPath      = fileNames[0].Replace('\\', '/');
        var separatorIndex = firstPath.IndexOf('/');
        if (separatorIndex <= 0) return false;

        var candidate = firstPath[..separatorIndex];
        if (fileNames.Any(name => !name.Replace('\\', '/')
                                       .StartsWith(candidate + "/", StringComparison.Ordinal)))
            return false;

        rootFolder = candidate;
        return true;
    }

    /// <summary>目标名已带同名扩展名时原样返回，否则追加；RSS/上传回退的原始标题可能自带 .mp4 后缀。</summary>
    private static string AppendExtension(string name, string extension)
    {
        return name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;
    }

    /// <summary>
    ///     逐文件整理种子内容（ByrTorrent 同源方法）：文件名能解析出集数的视频/字幕按命名要素改名为
    ///     各自集数（标题、字幕组、来源沿用任务级令牌）；解析不出的附属文件挪进 Seed/，不混入成片命名。
    ///     同一种子内两文件解析出相同目标名时跳过改名保留原名，等待人工处理。
    /// </summary>
    private TorrentNamingIngredients? CorrectIngredientsForFile(
        TorrentNamingIngredients ingredients, string fileName, ParseResult? alreadyParsed, int? subjectId,
        int taskVersion)
    {
        var parsed = alreadyParsed ?? aniParser.Parse(fileName);
        if (parsed is null) return null;

        string? correctedText;
        if (parsed.Episode is { } episode)
        {
            correctedText = ReleaseNamePreviewPolicy.FormatEpisodeText(
                    episodeCorrectionPolicy.Apply(subjectId, episode).Episode);
        }
        else if (parsed.StartEpisode is { } start && parsed.EndEpisode is { } end)
        {
            var correctedStart = episodeCorrectionPolicy.Apply(subjectId, start).Episode;
            var correctedEnd = episodeCorrectionPolicy.Apply(subjectId, end).Episode;
            correctedText = correctedStart == correctedEnd
                ? ReleaseNamePreviewPolicy.FormatEpisodeText(correctedStart)
                : $"{ReleaseNamePreviewPolicy.FormatEpisodeText(correctedStart)}-E{ReleaseNamePreviewPolicy.FormatEpisodeText(correctedEnd)}";
        }
        else
        {
            correctedText = null;
        }

        if (correctedText is null) return null;
        return ingredients with
        {
            EpisodeText = correctedText,
            Version = parsed.Version > 1 ? parsed.Version : taskVersion > 1 ? taskVersion : ingredients.Version
        };
    }

    private async Task RenameContentFilesAsync(IQbittorrentSession      session, string infoHash,
                                               IReadOnlyList<string>    fileNames,
                                               TorrentNamingIngredients ingredients, int? subjectId, decimal? taskEpisode, int taskVersion,
                                               CancellationToken        cancellationToken)
    {
        var prefix      = TryGetCommonRootFolder(fileNames, out var rootFolder) ? $"{rootFolder}/" : string.Empty;
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in fileNames)
        {
            var    path        = rawPath.Replace('\\', '/');
            var    extension   = Path.GetExtension(path);
            var    parsed      = aniParser.Parse(Path.GetFileName(path));
            var    episodeText = parsed is null ? null : ReleaseNamePreviewPolicy.FormatEpisodeText(parsed);
            string newPath;
            if (episodeText is not null &&
                ContentFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                var fileIngredients = CorrectIngredientsForFile(ingredients, Path.GetFileName(path), parsed, subjectId, taskVersion);
                if (fileIngredients is null)
                {
                    newPath = path;
                    continue;
                }
                var candidate = prefix + namingPolicy.Create(fileIngredients) + extension;
                if (!usedTargets.Add(candidate))
                {
                    logger.LogWarning("种子 {InfoHash} 内 {Path} 的目标命名与其他文件冲突，保留原名等待人工处理。",
                                      infoHash, path);
                    continue;
                }

                newPath = candidate;
            }
            else
            {
                // 解析不出集数的附属文件（NCOP/ED、菜单、Extras 等）挪进 Seed/，不混入成片命名。
                var relativePath = prefix.Length > 0 ? path[rootFolder.Length..] : $"/{path}";
                newPath = $"{prefix}Seed{relativePath}";
            }

            if (!string.Equals(newPath, path, StringComparison.Ordinal))
                await session.RenameTorrentFileAsync(infoHash, path, newPath, cancellationToken);
        }
    }

    /// <summary>
    ///     活动任务查重：同 hash 一律冲突；因低优先级被替换的终止记录同样挡住重复登记（其存在即查重依据，
    ///     重复添加该资源直接终止）；条目路径额外覆盖同条目换源的情况，上传路径没有条目来源。
    /// </summary>
    private static async Task<Guid?> FindActiveDuplicateIdAsync(
        ApplicationDbContext db, string infoHash, Guid? feedItemId, CancellationToken cancellationToken)
    {
        var query = db.DownloadTasks.AsNoTracking()
                      .Where(task => task.Status == DownloadTaskStatus.PendingSubmit ||
                                     task.Status == DownloadTaskStatus.Submitting    ||
                                     task.Status == DownloadTaskStatus.Downloading   ||
                                     task.Status == DownloadTaskStatus.Paused        ||
                                     task.Status == DownloadTaskStatus.Replaced);
        query = feedItemId is null
            ? query.Where(task => task.InfoHash == infoHash)
            : query.Where(task => task.InfoHash == infoHash || task.FeedItemId == feedItemId);
        return await query.Select(task => (Guid?)task.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<Guid?> FindCompletedDuplicateIdAsync(
        ApplicationDbContext db, string infoHash, CancellationToken cancellationToken)
    {
        return await db.DownloadTasks.AsNoTracking()
                       .Where(task => task.InfoHash == infoHash &&
                                      task.Status   == DownloadTaskStatus.Completed)
                       .Select(task => (Guid?)task.Id)
                       .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    ///     查找本任务完成后应标记为被替换的旧任务：同 Subject + Episode、不同 hash、未被他任务替换的已完成任务，
    ///     优先更高版本。失败任务没有可供替代的旧文件；不越过比自己更高的版本，避免旧版本反向替换新资源。
    /// </summary>
    private static async Task<Guid?> FindReplacementTargetAsync(ApplicationDbContext db, int? subjectId,
                                                                decimal? episode, string infoHash, int version,
                                                                CancellationToken cancellationToken)
    {
        if (subjectId is null || episode is null) return null;

        var candidates = await db.DownloadTasks.AsNoTracking()
                                 .Where(candidate => candidate.SubjectId        == subjectId                    &&
                                                     candidate.Episode          == episode                      &&
                                                     candidate.InfoHash         != infoHash                     &&
                                                     candidate.ReplacedByTaskId == null                         &&
                                                     candidate.Status           == DownloadTaskStatus.Completed &&
                                                     candidate.Version          <= version)
                                 .Select(candidate => new { candidate.Id, candidate.Version, candidate.CreatedAtUtc })
                                 .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序（与代码库其余处理一致）。
        return candidates.OrderByDescending(candidate => candidate.Version)
                         .ThenByDescending(candidate => candidate.CreatedAtUtc)
                         .Select(candidate => (Guid?)candidate.Id)
                         .FirstOrDefault();
    }

    private async Task<bool> IsActiveTaskForHashAsync(
        string infoHash, Guid exceptTaskId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadTasks.AsNoTracking().AnyAsync(task => task.Id != exceptTaskId &&
            task.InfoHash == infoHash && (task.Status == DownloadTaskStatus.PendingSubmit ||
            task.Status == DownloadTaskStatus.Submitting || task.Status == DownloadTaskStatus.Downloading ||
            task.Status == DownloadTaskStatus.Paused), cancellationToken);
    }

    private async Task<DownloadTask> LoadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.DownloadTasks.AsNoTracking()
                       .SingleOrDefaultAsync(value => value.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到指定下载任务。");
    }

    /// <summary>
    ///     带状态保护的字段更新：只更新仍处于预期状态的任务，避免与人工操作交错。
    ///     onApplied 在状态变更生效后、与同一次 SaveChangesAsync 落库前执行，用于把通知投递意图写入同一本地事务。
    /// </summary>
    private async Task<bool> UpdateTaskAsync(Guid taskId, DownloadTaskStatus expectedStatus,
                                             Action<DownloadTask> update, CancellationToken cancellationToken,
                                             Func<ApplicationDbContext, DownloadTask, Task>? onApplied = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.DownloadTasks.Include(value => value.Subject)
                           .SingleAsync(value => value.Id == taskId, cancellationToken);
        if (task.Status != expectedStatus) return false;

        update(task);
        if (onApplied is not null) await onApplied(db, task);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task MarkFailedAsync(Guid taskId, DownloadTaskStatus expectedStatus, string step, string error,
                                       CancellationToken cancellationToken)
    {
        logger.LogWarning("下载任务 {TaskId} 在步骤 {Step} 失败：{Error}", taskId, step, error);
        await UpdateTaskAsync(taskId, expectedStatus, update =>
                              {
                                  update.Status     = DownloadTaskStatus.Failed;
                                  update.FailedStep = step;
                                  update.Error      = error.Length > 1000 ? error[..1000] : error;
                              }, cancellationToken,
                              (db, task) => notificationEnqueuer.TryEnqueueAsync(
                               db, NotificationEventType.DownloadFailed, "下载失败",
                               $"{DescribeTask(task)}：在「{step}」步骤失败。{error}", cancellationToken));
    }

    /// <summary>通知内容里对任务的描述：优先作品名（中文名优先），缺失时退回命名快照。</summary>
    private static string DescribeTask(DownloadTask task)
    {
        var subjectName = task.Subject is null
            ? null
            : string.IsNullOrWhiteSpace(task.Subject.NameCn)
                ? task.Subject.Name
                : task.Subject.NameCn;
        var episode = task.Episode is null ? string.Empty : $" 第{task.Episode.Value:0.###}集";
        var version = task.Version > 1 ? $"（v{task.Version}）" : string.Empty;
        return $"{subjectName ?? task.NamingSnapshot}{episode}{version}";
    }

    private static DownloadTaskView ToView(DownloadTask task, IReadOnlyDictionary<Guid, string> relatedNames)
    {
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        return new DownloadTaskView
        {
            Id          = task.Id,
            FeedItemId  = task.FeedItemId,
            TorrentName = task.FeedItem?.Title,
            SubjectId   = task.SubjectId,
            SubjectName = task.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(task.Subject.NameCn) ? task.Subject.Name : task.Subject.NameCn,
            Episode          = task.Episode,
            Version          = task.Version,
            SubtitleGroup    = ingredients?.Group,
            SubtitleLanguage = ingredients?.Language,
            Status           = task.Status,
            IsUserPaused     = task.IsUserPaused,
            Progress         = task.Progress,
            TotalSize        = task.TotalSize,
            NamingSnapshot   = task.NamingSnapshot,
            SiteSource       = task.SiteSource,
            FailedStep       = task.FailedStep,
            Error            = task.Error,
            CreatedAtUtc     = task.CreatedAtUtc,
            SubmittedAtUtc   = task.SubmittedAtUtc,
            CompletedAtUtc   = task.CompletedAtUtc,
            ReplacesTaskId   = task.ReplacesTaskId,
            ReplacesNaming = task.ReplacesTaskId is { } replacedId
                ? relatedNames.GetValueOrDefault(replacedId)
                : null,
            ReplacedByTaskId = task.ReplacedByTaskId,
            ReplacedByNaming = task.ReplacedByTaskId is { } replacedById
                ? relatedNames.GetValueOrDefault(replacedById)
                : null
        };
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
