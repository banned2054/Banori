using MediaManager.Data;
using MediaManager.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.FeedItems;

/// <summary>条目列表查询，输出脱敏投影；订阅刷新链与测试用它核对评估结果。</summary>
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
                    .Select(ToView)
                    .ToList();
    }

    private static FeedItemView ToView(FeedItem item)
    {
        return new FeedItemView
        {
            Id               = item.Id,
            SubscriptionId   = item.SubscriptionId,
            SubscriptionName = item.Subscription.Name,
            Title            = item.Title,
            Link             = item.Link,
            TorrentUrl       = item.TorrentUrl,
            PublishedAtUtc   = item.PublishedAtUtc,
            FirstSeenAtUtc   = item.FirstSeenAtUtc,
            Status           = item.Status,
            ParsedTitle      = item.ParsedTitle,
            MediaType        = item.MediaType,
            Episode          = item.Episode,
            Version          = item.Version,
            Group            = item.Group,
            Language         = item.Language,
            Resolution       = item.Resolution,
            SubjectId        = item.SubjectId,
            SubjectName = item.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(item.Subject.NameCn) ? item.Subject.Name : item.Subject.NameCn,
            DecisionStatus   = item.DecisionStatus,
            Preference       = item.Preference,
            Reason           = item.Reason,
            NamingPreview    = item.NamingPreview,
            SelectionOutcome = item.SelectionOutcome
        };
    }
}
