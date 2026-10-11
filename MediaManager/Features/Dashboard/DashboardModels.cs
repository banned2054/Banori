namespace MediaManager.Features.Dashboard;

/// <summary>总览页的计数快照：由 DashboardQueries 一次性聚合。</summary>
public sealed record DashboardSnapshot(
    int             EnabledSubscriptions,
    int             PendingWorkItems,
    int             AcceptedItems,
    int             NeedsAttentionItems,
    int             ActiveDownloads,
    DateTimeOffset? LastRefreshAtUtc);
