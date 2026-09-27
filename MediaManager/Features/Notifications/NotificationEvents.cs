namespace MediaManager.Features.Notifications;

/// <summary>事件开关的存储格式（逗号分隔事件名）与集合互转；未配置时视为订阅全部事件。</summary>
public static class NotificationEvents
{
    public static IReadOnlyList<NotificationEventType> All { get; } =
        Enum.GetValues<NotificationEventType>();

    public static IReadOnlyList<NotificationEventType> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return All;

        // 统一按枚举声明顺序去重输出，保证存储格式稳定；无法识别的名称如实忽略。
        var parsed = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(name => Enum.TryParse<NotificationEventType>(name, true, out var value)
                                               ? value
                                               : (NotificationEventType?)null)
                           .Where(value => value is not null)
                           .Select(value => value!.Value)
                           .ToHashSet();
        return All.Where(parsed.Contains).ToList();
    }

    public static string Format(IReadOnlyList<NotificationEventType> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return string.Join(',', All.Where(events.Contains));
    }

    /// <summary>按开关组合构造事件列表，供设置草稿的复选框使用。</summary>
    public static IReadOnlyList<NotificationEventType> FromFlags(bool downloadStarted, bool downloadFailed)
    {
        var events = new List<NotificationEventType>();
        if (downloadStarted) events.Add(NotificationEventType.DownloadStarted);
        if (downloadFailed)  events.Add(NotificationEventType.DownloadFailed);
        return events;
    }
}
