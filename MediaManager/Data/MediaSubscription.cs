namespace MediaManager.Data;

public sealed class MediaSubscription
{
    public Guid            Id                        { get; set; }
    public string          Name                      { get; set; } = string.Empty;
    public string          FeedUrlProtected          { get; set; } = string.Empty;
    public bool            IsEnabled                 { get; set; }

    /// <summary>一次性订阅：仅网页添加与手动刷新时抓取，不参与定时调度（NextRefreshAtUtc 恒为 null）。</summary>
    public bool            IsOneShot                 { get; set; }
    public int             RefreshIntervalMinutes    { get; set; }
    public int?            FixedSubjectId            { get; set; }

    /// <summary>可选代理（ProxyServers.Id）；null 表示直连。删除代理时由外键 SetNull 自动置空。</summary>
    public Guid?           ProxyId                   { get; set; }
    public ProxyServer?    Proxy                     { get; set; }
    public DateTimeOffset  CreatedAtUtc              { get; set; }
    public DateTimeOffset  UpdatedAtUtc              { get; set; }
    public DateTimeOffset? LastRefreshStartedAtUtc   { get; set; }
    public DateTimeOffset? LastRefreshCompletedAtUtc { get; set; }
    public DateTimeOffset? NextRefreshAtUtc          { get; set; }
    public string?         LastError                 { get; set; }

    public ICollection<RefreshWorkItem> RefreshWorkItems { get; set; } = [];
    public ICollection<FeedItem>        FeedItems        { get; set; } = [];
}
