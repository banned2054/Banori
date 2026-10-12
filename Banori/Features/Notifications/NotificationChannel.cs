namespace Banori.Features.Notifications;

/// <summary>通知渠道。数据库存储为字符串，新增渠道是向后兼容的扩展点。</summary>
public enum NotificationChannel
{
    Telegram,
    Gotify
}

/// <summary>触发通知的事件。数据库存储为字符串；新增事件只需增加枚举值并接入对应钩子。</summary>
public enum NotificationEventType
{
    /// <summary>种子已提交到下载器并确认开始下载。</summary>
    DownloadStarted,

    /// <summary>下载任务在任一步骤失败。</summary>
    DownloadFailed
}

/// <summary>投递状态。Pending 含等待退避重试的记录；Failed 是重试用尽的终态，可在投递记录页人工重试。</summary>
public enum NotificationDeliveryStatus
{
    Pending,
    Delivered,
    Failed
}
