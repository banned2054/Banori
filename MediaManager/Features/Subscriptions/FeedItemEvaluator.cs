using Banned.AniParser;
using Banned.AniParser.Models;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Subjects;
using MediaManager.Core.Torrents;
using MediaManager.Data;
using MediaManager.Features.Anime;
using MediaManager.Features.Downloads;
using MediaManager.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Subscriptions;

/// <summary>
///     条目评估：解析标题、关联 Subject、集数修正、硬性过滤与命名预览，并把结果写回条目。
///     评估只决定“解析与合格性”（FeedItemStatus），组内胜负由批量择优（CandidateSelectionService）决定。
///     后台刷新与人工指定 Subject 重评共用同一条链路；人工重评在评估完成后触发受影响组的重选。
/// </summary>
public sealed class FeedItemEvaluator(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ISubjectLinkResolver                    subjectLinkResolver,
    SubjectMetadataCache                    subjectCache,
    AniParser                               parser,
    ISelectionRuleProvider                  ruleProvider,
    CandidateEligibilityPolicy              eligibilityPolicy,
    SubjectEpisodeCorrectionPolicy          correctionPolicy,
    ReleaseNamePreviewPolicy                namingPolicy,
    NamingTemplateStore                     namingTemplateStore,
    CandidateSelectionService               selectionService,
    TimeProvider                            timeProvider)
{
    /// <summary>
    ///     评估一个已入库条目。Subject 按指定值优先、其次标题映射缓存、再次来源 feed 直接提供的
    ///     Subject（sourceSubjectId，如 AniBT 的 bgmId）、最后 Mikan 页面发现的顺序确定；
    ///     前面任一档命中时不会访问来源页面。标题映射始终记录修正前的原始 Subject，
    ///     集数修正得到的 Subject 只用于条目结果、筛选与命名。
    /// </summary>
    public async Task EvaluateAsync(Guid              itemId, int? requestedSubjectId,
                                    CancellationToken cancellationToken = default,
                                    int?              sourceSubjectId   = null)
    {
        var item = await LoadItemAsync(itemId, cancellationToken);

        var parsed = parser.Parse(item.Title);
        if (parsed is null)
        {
            await SaveAnalysisAsync(itemId,
                                    new FeedItemAnalysis
                                    {
                                        Status         = FeedItemStatus.NeedsReview,
                                        Reason         = "AniParser 无法识别该标题。",
                                        EvaluatedAtUtc = timeProvider.GetUtcNow()
                                    },
                                    cancellationToken);
            return;
        }

        var normalizedTitle = TitleNormalizer.Normalize(parsed.Title);
        var subjectId       = requestedSubjectId;
        if (subjectId is null && normalizedTitle.Length > 0)
            subjectId = await FindSubjectIdByTitleAsync(normalizedTitle, cancellationToken);

        if (subjectId is null)
            subjectId = sourceSubjectId;

        if (subjectId is null && Uri.TryCreate(item.Link, UriKind.Absolute, out var link))
            subjectId = await subjectLinkResolver.ResolveSubjectIdAsync(link, item.SubscriptionProxyId,
                                                                        cancellationToken);

        if (subjectId is null)
        {
            await SaveParsedNeedsReviewAsync(itemId, parsed, "未能关联 Bangumi Subject，需要人工指定。",
                                             cancellationToken);
            return;
        }

        await EvaluateWithSubjectAsync(itemId, parsed, subjectId.Value, normalizedTitle,
                                       item.PublishedAtUtc, item.StableId, item.FeedHost, cancellationToken);
    }

    /// <summary>
    ///     人工指定 Subject 后重新评估的入口，与刷新评估使用完全相同的规则。
    ///     评估完成后触发受影响组的重选：条目改组时旧组与新组都重选，
    ///     即使原条目不再出现在最新 RSS 中，旧组的优胜变化也会正确落库。
    /// </summary>
    public async Task ReevaluateAsync(Guid itemId, int subjectId, CancellationToken cancellationToken = default)
    {
        if (subjectId < 1) throw new ArgumentOutOfRangeException(nameof(subjectId));

        var before = await LoadItemAsync(itemId, cancellationToken);
        await EvaluateAsync(itemId, subjectId, cancellationToken);
        var after = await LoadItemAsync(itemId, cancellationToken);

        var keys = new List<SelectionGroupKey>();
        if (BuildGroupKey(before) is { } previousKey && !previousKey.Equals(BuildGroupKey(after)))
            keys.Add(previousKey);
        if (BuildGroupKey(after) is { } currentKey) keys.Add(currentKey);

        // 人工重评触发的重选视为人工动作，允许重试同一优胜已失败的种子登记。
        if (keys.Count > 0)
            await selectionService.SelectAndRegisterAsync(keys, true, cancellationToken);
    }

    private async Task EvaluateWithSubjectAsync(Guid              itemId,          ParseResult parsed, int subjectId,
                                                string            normalizedTitle, DateTimeOffset? publishedAtUtc,
                                                string            stableId,        string? feedHost,
                                                CancellationToken cancellationToken)
    {
        if (parsed.MediaType.ToString() == "MultipleEpisode" || parsed.Episode is null)
        {
            var storedSubjectId = await TryCacheSubjectAsync(subjectId, normalizedTitle, cancellationToken);
            await SaveParsedNeedsReviewAsync(itemId, parsed,
                                             parsed.Episode is null ? "未能识别集数，需要人工确认。" : "首版不自动处理多集合集。",
                                             cancellationToken, storedSubjectId);
            return;
        }

        var correction = correctionPolicy.Apply(subjectId, parsed.Episode.Value);
        if (correction.SubjectId is null)
        {
            await SaveParsedNeedsReviewAsync(itemId, parsed, "Subject 修正规则未得到有效 Subject。",
                                             cancellationToken);
            return;
        }

        SubjectMetadata subject;
        try
        {
            subject = await subjectCache.EnsureAsync(correction.SubjectId.Value, cancellationToken);
            if (correction.SubjectId.Value != subjectId)
                // 标题映射指向修正前的原始 Subject，其元数据一并落库以满足映射外键。
                await subjectCache.EnsureAsync(subjectId, cancellationToken);

            await subjectCache.SaveTitleMappingAsync(normalizedTitle, subjectId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await SaveParsedNeedsReviewAsync(itemId, parsed, "读取 Bangumi Subject 失败，可稍后重新评估。",
                                             cancellationToken);
            return;
        }

        // 硬性过滤只判定是否允许参与同集竞争；排名较低的字幕组或语言不在此提前排除，
        // 由批量择优统一比较。条目状态因此只表达“合格性”，不再是最终下载选择。
        var config = await ruleProvider.GetAsync(cancellationToken);
        var candidate = new EpisodeCandidate
        {
            FeedItemId     = itemId,
            SubjectId      = subject.Id,
            MediaType      = parsed.MediaType.ToString(),
            Episode        = correction.Episode,
            Group          = parsed.Group ?? string.Empty,
            Language       = parsed.Language,
            Resolution     = parsed.Resolution,
            Version        = parsed.Version,
            PublishedAtUtc = publishedAtUtc,
            StableId       = stableId
        };
        var eligibility = eligibilityPolicy.Evaluate(candidate, config);
        var status = eligibility.Status switch
        {
            EligibilityStatus.Eligible   => FeedItemStatus.Accepted,
            EligibilityStatus.Ineligible => FeedItemStatus.Rejected,
            _                            => FeedItemStatus.NeedsReview
        };
        var decisionStatus = eligibility.Status switch
        {
            EligibilityStatus.Eligible   => ReleaseDecisionStatus.Accepted,
            EligibilityStatus.Ineligible => ReleaseDecisionStatus.Rejected,
            _                            => ReleaseDecisionStatus.NeedsReview
        };
        // 命名后缀优先用字幕组名；同一规则下优胜确定时的命名快照与该预览一致。
        var preference       = string.IsNullOrWhiteSpace(parsed.Group) ? parsed.Language.ToString() : parsed.Group;
        var correctionReason = correction.Reason == "未应用集数修正规则。" ? string.Empty : $" {correction.Reason}";
        // 命名模板的 {source} 令牌取订阅 feed 的来源站点（mikan/anibt 等）。
        var siteSource     = TorrentSiteSource.FromHost(feedHost);
        var namingTemplate = await namingTemplateStore.GetAsync(cancellationToken);
        var fileTemplate   = await namingTemplateStore.GetFileTemplateAsync(cancellationToken);
        // 预览与命名要素同源生成，且分别快照 AniParser 与 Bangumi 字段。
        var ingredients = NamingIngredientsComposer.Compose(
                                                            namingTemplate, fileTemplate, parsed,
                                                            ReleaseNamePreviewPolicy
                                                               .FormatEpisodeText(correction.Episode), preference,
                                                            siteSource,
                                                            subject.Id, subject.Name, subject.NameCn, subject.Platform,
                                                            subject.AirDate);
        var namingPreview = namingPolicy.Create(ingredients);

        await SaveAnalysisAsync(itemId,
                                CreateAnalysis(parsed, status, subject.Id, subject.DisplayName,
                                               decisionStatus, preference,
                                               eligibility.Reason + correctionReason, namingPreview,
                                               correction.Episode, ingredients.ToJson()),
                                cancellationToken);
    }

    private async Task<ItemSnapshot> LoadItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FeedItems.AsNoTracking()
                       .Where(value => value.Id == itemId)
                       .Select(value => new ItemSnapshot(value.Id, value.Title, value.Link, value.SubjectId,
                                                         value.MediaType, value.Episode, value.PublishedAtUtc,
                                                         value.StableId, value.Subscription.ProxyId,
                                                         value.Subscription.FeedUrlProtected))
                       .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("找不到指定条目。");
    }

    /// <summary>由条目快照推导同集分组键；未关联 Subject、缺集数或多集合集不成组。</summary>
    private static SelectionGroupKey? BuildGroupKey(ItemSnapshot item)
    {
        if (item.SubjectId is null || item.MediaType is null || item.Episode is null) return null;
        if (string.Equals(item.MediaType, "MultipleEpisode", StringComparison.Ordinal)) return null;

        return new SelectionGroupKey(item.SubjectId.Value, item.MediaType, item.Episode.Value);
    }

    private async Task<int?> TryCacheSubjectAsync(int               subjectId, string normalizedTitle,
                                                  CancellationToken cancellationToken)
    {
        try
        {
            var subject = await subjectCache.EnsureAsync(subjectId, cancellationToken);
            await subjectCache.SaveTitleMappingAsync(normalizedTitle, subjectId, cancellationToken);
            return subject.Id;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private FeedItemAnalysis CreateAnalysis(ParseResult parsed,      FeedItemStatus status, int? subjectId,
                                            string?     subjectName, ReleaseDecisionStatus? decisionStatus,
                                            string?     preference,  string reason, string? namingPreview,
                                            decimal?    episode,     string? namingIngredients = null) =>
        new()
        {
            Status            = status,
            ParsedTitle       = parsed.Title,
            MediaType         = parsed.MediaType.ToString(),
            Episode           = episode,
            StartEpisode      = parsed.StartEpisode,
            EndEpisode        = parsed.EndEpisode,
            Version           = parsed.Version,
            Group             = parsed.Group,
            GroupType         = parsed.GroupType.ToString(),
            Language          = parsed.Language.ToString(),
            SubtitleType      = parsed.SubtitleType.ToString(),
            Resolution        = parsed.Resolution.ToString(),
            Source            = parsed.Source.ToString(),
            WebSource         = parsed.WebSource,
            VideoCodec        = parsed.VideoCodec,
            AudioCodec        = parsed.AudioCodec,
            ColorBitDepth     = parsed.ColorBitDepth,
            SubjectId         = subjectId,
            SubjectName       = subjectName,
            DecisionStatus    = decisionStatus,
            Preference        = preference,
            Reason            = reason,
            NamingPreview     = namingPreview,
            NamingIngredients = namingIngredients,
            EvaluatedAtUtc    = timeProvider.GetUtcNow()
        };

    private Task SaveParsedNeedsReviewAsync(Guid              itemId,            ParseResult parsed, string reason,
                                            CancellationToken cancellationToken, int?        subjectId = null) =>
        SaveAnalysisAsync(itemId,
                          CreateAnalysis(parsed, FeedItemStatus.NeedsReview, subjectId, null, null, null, reason,
                                         null, parsed.Episode), cancellationToken);

    private async Task<int?> FindSubjectIdByTitleAsync(string normalizedTitle, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SubjectTitleMappings.AsNoTracking()
                       .Where(item => item.NormalizedTitle == normalizedTitle)
                       .Select(item => (int?)item.SubjectId)
                       .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task SaveAnalysisAsync(Guid              itemId, FeedItemAnalysis analysis,
                                         CancellationToken cancellationToken)
    {
        await using var db   = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             item = await db.FeedItems.SingleAsync(value => value.Id == itemId, cancellationToken);
        item.Status            = analysis.Status;
        item.ParsedTitle       = analysis.ParsedTitle;
        item.MediaType         = analysis.MediaType;
        item.Episode           = analysis.Episode;
        item.StartEpisode      = analysis.StartEpisode;
        item.EndEpisode        = analysis.EndEpisode;
        item.Version           = analysis.Version;
        item.Group             = analysis.Group;
        item.GroupType         = analysis.GroupType;
        item.Language          = analysis.Language;
        item.SubtitleType      = analysis.SubtitleType;
        item.Resolution        = analysis.Resolution;
        item.Source            = analysis.Source;
        item.WebSource         = analysis.WebSource;
        item.VideoCodec        = analysis.VideoCodec;
        item.AudioCodec        = analysis.AudioCodec;
        item.ColorBitDepth     = analysis.ColorBitDepth;
        item.SubjectId         = analysis.SubjectId;
        item.DecisionStatus    = analysis.DecisionStatus;
        item.Preference        = analysis.Preference;
        item.Reason            = analysis.Reason;
        item.NamingPreview     = analysis.NamingPreview;
        item.NamingIngredients = analysis.NamingIngredients;
        item.EvaluatedAtUtc    = analysis.EvaluatedAtUtc;
        // 条目重新评估后不再合格时，旧的组内落选标记已失去意义，清除以免详情页误导；
        // 仍合格的条目保留原值，等待组内重选更新为优胜或落选。
        item.SelectionOutcome = analysis.Status == FeedItemStatus.Accepted
            ? item.SelectionOutcome
            : SelectionOutcome.None;
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record ItemSnapshot(
        Guid            Id,
        string          Title,
        string?         Link,
        int?            SubjectId,
        string?         MediaType,
        decimal?        Episode,
        DateTimeOffset? PublishedAtUtc,
        string          StableId,
        Guid?           SubscriptionProxyId,
        string?         FeedHost);
}
