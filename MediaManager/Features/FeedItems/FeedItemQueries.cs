using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.FeedItems;

/// <summary>条目列表与详情查询，输出页面脱敏投影。</summary>
public sealed class FeedItemQueries(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    public async Task<IReadOnlyList<FeedItemView>> ListAsync(FeedItemStatus?   status            = null, int take = 100,
                                                             CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.FeedItems.AsNoTracking()
                      .Include(item => item.Subscription)
                      .Include(item => item.Subject)
                      .AsQueryable();
        if (status is not null) query = query.Where(item => item.Status == status);
        var items                     = await query.ToListAsync(cancellationToken);
        return items.OrderByDescending(item => item.PublishedAtUtc ?? item.FirstSeenAtUtc)
                    .Take(Math.Clamp(take, 1, 500))
                    .Select(item => ToView(item))
                    .ToList();
    }

    public async Task<FeedItemView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.FeedItems.AsNoTracking()
                           .Include(value => value.Subscription)
                           .Include(value => value.Subject)
                           .SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (item is null) return null;
        var downloadHistory = await db.DownloadTasks.AsNoTracking()
                                      .Where(task => task.FeedItemId == id)
                                      .Select(task => new DownloadTaskBrief
                                       {
                                           Id             = task.Id,
                                           Status         = task.Status,
                                           IsUserPaused   = task.IsUserPaused,
                                           Progress       = task.Progress,
                                           NamingSnapshot = task.NamingSnapshot,
                                           FailedStep     = task.FailedStep,
                                           Error          = task.Error,
                                           CreatedAtUtc   = task.CreatedAtUtc
                                       })
                                      .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存取最近一条。
        var     download             = downloadHistory.OrderByDescending(task => task.CreatedAtUtc).FirstOrDefault();
        int?    selectionRuleVersion = null;
        string? selectionBasis       = null;
        if (item.SubjectId is not null && item.MediaType is not null && item.Episode is not null)
        {
            // 详情页展示所在同集组的选择依据与规则版本；列表页不查，保持列表轻量。
            var selection = await db.CandidateSelections.AsNoTracking()
                                    .Where(selected => selected.SubjectId == item.SubjectId &&
                                                       selected.MediaType == item.MediaType &&
                                                       selected.Episode   == item.Episode)
                                    .Select(selected => new { selected.RuleVersion, selected.RankingBasis })
                                    .SingleOrDefaultAsync(cancellationToken);
            if (selection is not null)
            {
                selectionRuleVersion = selection.RuleVersion;
                selectionBasis       = selection.RankingBasis;
            }
        }

        return ToView(item, await FindMappedSubjectIdAsync(db, item.ParsedTitle, cancellationToken), download,
                      selectionRuleVersion, selectionBasis);
    }

    /// <summary>标题映射按评估时使用的归一化标题记录修正前的原始 Subject；条目未评估或无映射时为 null。</summary>
    private static async Task<int?> FindMappedSubjectIdAsync(ApplicationDbContext db, string? parsedTitle,
                                                             CancellationToken    cancellationToken)
    {
        var normalizedTitle = FeedItemEvaluator.NormalizeTitle(parsedTitle);
        if (normalizedTitle.Length == 0) return null;
        return await db.SubjectTitleMappings.AsNoTracking()
                       .Where(mapping => mapping.NormalizedTitle == normalizedTitle)
                       .Select(mapping => (int?)mapping.SubjectId)
                       .SingleOrDefaultAsync(cancellationToken);
    }

    private static FeedItemView ToView(FeedItem           item, int? originalSubjectId = null,
                                       DownloadTaskBrief? download             = null,
                                       int?               selectionRuleVersion = null,
                                       string?            selectionBasis       = null)
    {
        return new FeedItemView
        {
            Id                = item.Id,
            SubscriptionId    = item.SubscriptionId,
            SubscriptionName  = item.Subscription.Name,
            Title             = item.Title,
            Link              = item.Link,
            TorrentUrl        = item.TorrentUrl,
            PublishedAtUtc    = item.PublishedAtUtc,
            FirstSeenAtUtc    = item.FirstSeenAtUtc,
            Status            = item.Status,
            ParsedTitle       = item.ParsedTitle,
            MediaType         = item.MediaType,
            Episode           = item.Episode,
            Version           = item.Version,
            Group             = item.Group,
            Language          = item.Language,
            Resolution        = item.Resolution,
            SubjectId         = item.SubjectId,
            OriginalSubjectId = originalSubjectId,
            SubjectName = item.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(item.Subject.NameCn) ? item.Subject.Name : item.Subject.NameCn,
            DecisionStatus   = item.DecisionStatus,
            Preference       = item.Preference,
            Reason           = item.Reason,
            NamingPreview    = item.NamingPreview,
            SelectionOutcome = item.SelectionOutcome,
            Download         = download,

            SelectionRuleVersion = selectionRuleVersion,
            SelectionBasis       = selectionBasis
        };
    }
}
