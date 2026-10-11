namespace MediaManager.Features.Subscriptions;

public enum RefreshWorkStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public sealed record SubscriptionView(
    Guid               Id,
    string             Name,
    string             FeedUrlDisplay,
    bool               IsEnabled,
    int                RefreshIntervalMinutes,
    int?               FixedSubjectId,
    DateTimeOffset?    LastRefreshStartedAtUtc,
    DateTimeOffset?    LastRefreshCompletedAtUtc,
    DateTimeOffset?    NextRefreshAtUtc,
    string?            LastError,
    RefreshWorkStatus? LatestWorkStatus,
    Guid?              ProxyId = null);

public sealed record SaveSubscriptionRequest(
    Guid?   Id,
    string  Name,
    string? FeedUrl,
    bool    IsEnabled,
    int     RefreshIntervalMinutes,
    int?    FixedSubjectId,
    Guid?   ProxyId = null);

public sealed record RefreshWork(Guid Id, Guid SubscriptionId, int AttemptCount);

public sealed record RefreshResult(
    Guid    WorkId,
    int     AddedItems,
    int     DuplicateItems,
    int     FailedItems,
    string? Error = null);
