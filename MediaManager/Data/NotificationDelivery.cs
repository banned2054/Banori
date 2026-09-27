using MediaManager.Features.Notifications;

namespace MediaManager.Data;

/// <summary>
///     持久化的通知投递意图：入队时定格标题与内容，重试沿用原内容，不回读易变状态。
///     按渠道独立记录发送结果；远端已接收而响应丢失时会重复投递（at-least-once），这是设计边界。
/// </summary>
public sealed class NotificationDelivery
{
    public Guid Id { get; set; }

    public NotificationChannel Channel { get; set; }

    public NotificationEventType EventType { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public NotificationDeliveryStatus Status { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>下一次自动重试时间；null 表示立即可投。</summary>
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? DeliveredAtUtc { get; set; }
}
