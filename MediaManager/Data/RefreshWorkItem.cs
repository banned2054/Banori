using MediaManager.Features.Subscriptions;

namespace MediaManager.Data;

public sealed class RefreshWorkItem
{
    public Guid              Id             { get; set; }
    public Guid              SubscriptionId { get; set; }
    public MediaSubscription Subscription   { get; set; } = null!;
    public RefreshWorkStatus Status         { get; set; }
    public int               AttemptCount   { get; set; }
    public DateTimeOffset    RequestedAtUtc { get; set; }
    public DateTimeOffset?   StartedAtUtc   { get; set; }
    public DateTimeOffset?   CompletedAtUtc { get; set; }
    public string?           Error          { get; set; }
}
