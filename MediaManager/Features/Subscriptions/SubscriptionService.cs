using MediaManager.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Subscriptions;

/// <summary>
///     订阅的查询、增删改与刷新入队。RSS 地址在此加密保存，页面只获得脱敏输出。
/// </summary>
public sealed class SubscriptionService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IDataProtectionProvider                 dataProtectionProvider,
    RefreshEnqueueGate                      enqueueGate,
    RefreshPulse                            refreshPulse,
    TimeProvider                            timeProvider)
{
    private readonly IDataProtector _urlProtector = FeedUrlProtection.CreateProtector(dataProtectionProvider);

    public async Task<IReadOnlyList<SubscriptionView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db            = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             subscriptions = await db.MediaSubscriptions.AsNoTracking().ToListAsync(cancellationToken);
        var latestStatuses = (await db.RefreshWorkItems.AsNoTracking()
                                      .Select(item => new { item.SubscriptionId, item.Status, item.RequestedAtUtc })
                                      .ToListAsync(cancellationToken))
                            .GroupBy(item => item.SubscriptionId)
                            .ToDictionary(group => group.Key,
                                          group => (RefreshWorkStatus?)group
                                                                      .OrderByDescending(item => item.RequestedAtUtc)
                                                                      .First().Status);
        return subscriptions.OrderBy(item => item.Name)
                            .Select(item => ToView(item, latestStatuses.GetValueOrDefault(item.Id)))
                            .ToList();
    }

    public async Task<SubscriptionView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MediaSubscriptions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id,
                 cancellationToken);
        if (item is null) return null;
        // SQLite 不支持按 DateTimeOffset 服务端排序，取回后在内存中排序。
        var latestStatus = (await db.RefreshWorkItems.AsNoTracking()
                                    .Where(value => value.SubscriptionId == id)
                                    .Select(value => new { value.Status, value.RequestedAtUtc })
                                    .ToListAsync(cancellationToken))
                          .OrderByDescending(value => value.RequestedAtUtc)
                          .Select(value => (RefreshWorkStatus?)value.Status)
                          .FirstOrDefault();
        return ToView(item, latestStatus);
    }

    public async Task<Guid> SaveAsync(SaveSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Validate(request);
        var now        = timeProvider.GetUtcNow();

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (normalized.ProxyId is { } proxyId &&
            !await db.ProxyServers.AnyAsync(item => item.Id == proxyId, cancellationToken))
            throw new ArgumentException("指定的代理不存在，请先在代理设置中创建。", nameof(request));

        if (normalized.Id is null)
        {
            var entity = new MediaSubscription
            {
                Id                     = Guid.NewGuid(),
                Name                   = normalized.Name,
                FeedUrlProtected       = _urlProtector.Protect(normalized.FeedUrl!),
                IsEnabled              = normalized.IsEnabled,
                RefreshIntervalMinutes = normalized.RefreshIntervalMinutes,
                FixedSubjectId         = normalized.FixedSubjectId,
                ProxyId                = normalized.ProxyId,
                CreatedAtUtc           = now,
                UpdatedAtUtc           = now,
                NextRefreshAtUtc       = normalized.IsEnabled ? now : null
            };
            db.MediaSubscriptions.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            if (normalized.IsEnabled) await RequestRefreshAsync(entity.Id, cancellationToken);

            return entity.Id;
        }

        var existing = await db.MediaSubscriptions.SingleOrDefaultAsync(item => item.Id == normalized.Id,
                                                                        cancellationToken) ??
                       throw new KeyNotFoundException("找不到指定订阅。");
        var wasEnabled = existing.IsEnabled;
        existing.Name                   = normalized.Name;
        existing.IsEnabled              = normalized.IsEnabled;
        existing.RefreshIntervalMinutes = normalized.RefreshIntervalMinutes;
        existing.FixedSubjectId         = normalized.FixedSubjectId;
        existing.ProxyId                = normalized.ProxyId;
        existing.UpdatedAtUtc           = now;
        if (normalized.FeedUrl is not null) existing.FeedUrlProtected = _urlProtector.Protect(normalized.FeedUrl);

        if (!normalized.IsEnabled) existing.NextRefreshAtUtc = null;
        else if (!wasEnabled) existing.NextRefreshAtUtc      = now;
        await db.SaveChangesAsync(cancellationToken);
        return existing.Id;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MediaSubscriptions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return;
        db.MediaSubscriptions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>手动请求立即刷新；已有等待中或执行中的工作时复用该工作。入队后唤醒刷新线程立即处理。</summary>
    public async Task<Guid> RequestRefreshAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var workId = await enqueueGate.RunExclusiveAsync(async token =>
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(token);
            return await EnqueueSingleAsync(db, id, timeProvider.GetUtcNow(), token);
        }, cancellationToken);
        refreshPulse.Pulse();
        return workId;
    }

    /// <summary>到期调度：为已启用且到达刷新时间的订阅补齐待执行工作，与手动请求共用入队检查。</summary>
    public Task<int> EnqueueDueRefreshesAsync(CancellationToken cancellationToken = default)
    {
        return enqueueGate.RunExclusiveAsync(async token =>
        {
            var             now = timeProvider.GetUtcNow();
            await using var db  = await dbContextFactory.CreateDbContextAsync(token);
            var dueIds = (await db.MediaSubscriptions.AsNoTracking()
                                  .Where(item => item.IsEnabled)
                                  .Select(item => new { item.Id, item.NextRefreshAtUtc })
                                  .ToListAsync(token))
                        .Where(item => item.NextRefreshAtUtc is null || item.NextRefreshAtUtc <= now)
                        .Select(item => item.Id)
                        .ToList();
            if (dueIds.Count == 0) return 0;

            var activeIds = await db.RefreshWorkItems
                                    .Where(item => (item.Status == RefreshWorkStatus.Pending ||
                                                    item.Status == RefreshWorkStatus.Running) &&
                                                   dueIds.Contains(item.SubscriptionId))
                                    .Select(item => item.SubscriptionId)
                                    .Distinct()
                                    .ToListAsync(token);
            foreach (var subscriptionId in dueIds.Except(activeIds))
                db.RefreshWorkItems.Add(new RefreshWorkItem
                {
                    Id             = Guid.NewGuid(),
                    SubscriptionId = subscriptionId,
                    Status         = RefreshWorkStatus.Pending,
                    RequestedAtUtc = now
                });

            return await db.SaveChangesAsync(token);
        }, cancellationToken);
    }

    private static async Task<Guid> EnqueueSingleAsync(ApplicationDbContext db, Guid subscriptionId,
                                                       DateTimeOffset       requestedAtUtc,
                                                       CancellationToken    cancellationToken)
    {
        if (!await db.MediaSubscriptions.AnyAsync(item => item.Id == subscriptionId, cancellationToken))
            throw new KeyNotFoundException("找不到指定订阅。");

        // SQLite 不支持按 DateTimeOffset 服务端排序，取回后在内存中排序。
        var activeId = (await db.RefreshWorkItems
                                .Where(item => item.SubscriptionId == subscriptionId &&
                                               (item.Status == RefreshWorkStatus.Pending ||
                                                item.Status == RefreshWorkStatus.Running))
                                .Select(item => new { item.Id, item.RequestedAtUtc })
                                .ToListAsync(cancellationToken))
                      .OrderBy(item => item.RequestedAtUtc)
                      .Select(item => (Guid?)item.Id)
                      .FirstOrDefault();
        if (activeId is not null) return activeId.Value;

        var work = new RefreshWorkItem
        {
            Id             = Guid.NewGuid(),
            SubscriptionId = subscriptionId,
            Status         = RefreshWorkStatus.Pending,
            RequestedAtUtc = requestedAtUtc
        };
        db.RefreshWorkItems.Add(work);
        await db.SaveChangesAsync(cancellationToken);
        return work.Id;
    }

    private static SaveSubscriptionRequest Validate(SaveSubscriptionRequest request)
    {
        string? feedUrl = null;
        if (!string.IsNullOrWhiteSpace(request.FeedUrl))
        {
            if (!Uri.TryCreate(request.FeedUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                 !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new ArgumentException("RSS 地址必须是 HTTPS 绝对地址（本机测试可使用 HTTP）。", nameof(request));

            if (!uri.IsLoopback && !IsSupportedSubscriptionHost(uri.Host))
                throw new ArgumentException("订阅目前仅支持 Mikan（mikanani.me）与 AniBT（anibt.net）的 RSS 地址。",
                                            nameof(request));

            feedUrl = uri.AbsoluteUri;
        }
        else if (request.Id is null)
        {
            throw new ArgumentException("新建订阅时必须填写 RSS 地址。", nameof(request));
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100) throw new ArgumentException("订阅名称长度必须为 1 到 100 个字符。", nameof(request));

        if (request.RefreshIntervalMinutes is < 5 or > 10080)
            throw new ArgumentException("刷新周期必须为 5 到 10080 分钟。", nameof(request));

        if (request.FixedSubjectId is < 1) throw new ArgumentException("Subject ID 必须为正整数。", nameof(request));

        return request with { Name = name, FeedUrl = feedUrl };
    }

    private static bool IsSupportedSubscriptionHost(string host)
    {
        return MatchesHost(host, "mikanani.me") || MatchesHost(host, "anibt.net");

        static bool MatchesHost(string value, string baseHost)
        {
            return value.Equals(baseHost, StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith($".{baseHost}", StringComparison.OrdinalIgnoreCase);
        }
    }

    private SubscriptionView ToView(MediaSubscription item, RefreshWorkStatus? latestStatus)
    {
        var uri = new Uri(_urlProtector.Unprotect(item.FeedUrlProtected));
        var display = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}" +
                      (string.IsNullOrEmpty(uri.Query) ? string.Empty : "?•••");
        return new SubscriptionView(item.Id, item.Name, display, item.IsEnabled,
                                    item.RefreshIntervalMinutes,
                                    item.FixedSubjectId,
                                    item.LastRefreshStartedAtUtc, item.LastRefreshCompletedAtUtc, item.NextRefreshAtUtc,
                                    item.LastError, latestStatus, item.ProxyId);
    }
}
