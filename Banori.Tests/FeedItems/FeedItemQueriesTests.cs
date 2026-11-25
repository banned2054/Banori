using Banori.Data;
using Banori.Features.Dashboard;
using Banori.Features.FeedItems;
using Banori.Features.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.FeedItems;

public sealed class FeedItemQueriesTests
{
    [Fact]
    public async Task ListNeedsAttentionAsync_MatchesDashboardCountAndReturnsNewestFirst()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<FeedItemQueries>();
        services.AddScoped<DashboardQueries>();
        await using var provider = services.BuildServiceProvider();
        var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 11, 25, 3, 0, 0, TimeSpan.Zero);
            var subscription = new MediaSubscription
            {
                Id                     = Guid.NewGuid(),
                Name                   = "测试订阅",
                FeedUrlProtected       = "protected",
                RefreshIntervalMinutes = 30,
                IsEnabled              = true,
                CreatedAtUtc           = now,
                UpdatedAtUtc           = now
            };
            db.MediaSubscriptions.Add(subscription);
            db.FeedItems.AddRange(
                CreateItem(subscription.Id, "accepted", FeedItemStatus.Accepted, "已通过", now.AddMinutes(3)),
                CreateItem(subscription.Id, "rejected", FeedItemStatus.Rejected, "已过滤", now.AddMinutes(2)),
                CreateItem(subscription.Id, "needs-review", FeedItemStatus.NeedsReview, "未能关联 Bangumi Subject",
                           now.AddMinutes(1)),
                CreateItem(subscription.Id, "failed", FeedItemStatus.Failed, "多集合集未自动处理", now.AddMinutes(4)));
            await db.SaveChangesAsync();
        }

        await using var scope     = provider.CreateAsyncScope();
        var             dashboard = await scope.ServiceProvider.GetRequiredService<DashboardQueries>().GetSnapshotAsync();
        var             items     = await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListNeedsAttentionAsync();

        Assert.Equal(2, dashboard.NeedsAttentionItems);
        Assert.Equal(dashboard.NeedsAttentionItems, items.Count);
        Assert.Equal(["failed", "needs-review"], items.Select(item => item.Title).ToArray());
        Assert.Equal(["多集合集未自动处理", "未能关联 Bangumi Subject"], items.Select(item => item.Reason).ToArray());
        Assert.All(items, item => Assert.Equal("测试订阅", item.SubscriptionName));
    }

    private static FeedItem CreateItem(
        Guid subscriptionId, string title, FeedItemStatus status, string reason, DateTimeOffset publishedAtUtc) => new()
    {
        Id             = Guid.NewGuid(),
        SubscriptionId = subscriptionId,
        StableId       = $"stable-{title}",
        Title          = title,
        Status         = status,
        Reason         = reason,
        FirstSeenAtUtc = publishedAtUtc,
        PublishedAtUtc = publishedAtUtc
    };
}
