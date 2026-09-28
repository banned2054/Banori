using MediaManager.Features.Proxies;
using MediaManager.Features.Subscriptions;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Components.Pages.Subscriptions;

/// <summary>订阅页状态：列表、编辑草稿、保存与刷新操作。随页面组件创建和释放。</summary>
public sealed class SubscriptionsPageViewModel(
    SubscriptionService                 subscriptionService,
    ProxyService                        proxyService,
    ILogger<SubscriptionsPageViewModel> logger)
{
    private readonly List<SubscriptionView> _subscriptions = [];
    private readonly List<ProxyView>        _proxies       = [];

    public IReadOnlyList<SubscriptionView> Subscriptions => _subscriptions;
    public IReadOnlyList<ProxyView>        Proxies       => _proxies;
    public SubscriptionDraft               Draft         { get; } = new();
    public OneShotSubscriptionDraft        OneShotDraft  { get; } = new();

    public bool    Loaded             { get; private set; }
    public bool    Editing            { get; private set; }
    public bool    EditingOneShot     { get; private set; }
    public bool    Saving             { get; private set; }
    public bool    OperationSucceeded { get; private set; }
    public string? StatusMessage      { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _subscriptions.Clear();
        _subscriptions.AddRange(await subscriptionService.ListAsync(cancellationToken));
        _proxies.Clear();
        _proxies.AddRange(await proxyService.ListAsync(cancellationToken));
        Loaded = true;
    }

    public void BeginCreate()
    {
        Draft.Reset();
        Editing        = true;
        EditingOneShot = false;
        StatusMessage  = null;
    }

    public void BeginEdit(SubscriptionView subscription)
    {
        Draft.Load(subscription);
        Editing        = true;
        EditingOneShot = subscription.IsOneShot;
        StatusMessage  = null;
    }

    public void CancelEdit()
    {
        Editing        = false;
        EditingOneShot = false;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Saving = true;
        try
        {
            await subscriptionService.SaveAsync(Draft.ToRequest(), cancellationToken);
            OperationSucceeded = true;
            StatusMessage      = "订阅已保存。";
            Editing            = false;
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "保存订阅失败。");
            OperationSucceeded = false;
            StatusMessage      = exception is ArgumentException ? exception.Message : "保存失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    /// <summary>保存一次性订阅：服务端保存后立即入队抓取，不参与定时刷新。</summary>
    public async Task SaveOneShotAsync(CancellationToken cancellationToken = default)
    {
        Saving = true;
        try
        {
            var request = new SaveSubscriptionRequest(null, OneShotDraft.Name?.Trim() ?? string.Empty,
                                                      OneShotDraft.FeedUrl.Trim(), true, 30, null, IsOneShot: true);
            await subscriptionService.SaveAsync(request, cancellationToken);
            OperationSucceeded = true;
            StatusMessage      = "一次性订阅已添加，正在立即抓取。";
            OneShotDraft.Reset();
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "保存一次性订阅失败。");
            OperationSucceeded = false;
            StatusMessage      = exception is ArgumentException ? exception.Message : "保存失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    public async Task RequestRefreshAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var workId = await subscriptionService.RequestRefreshAsync(id, cancellationToken);
            OperationSucceeded = true;
            StatusMessage      = $"刷新任务已进入队列：{workId}。";
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "创建刷新任务失败。");
            OperationSucceeded = false;
            StatusMessage      = "创建刷新任务失败。";
        }
    }

    public async Task DeleteAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await subscriptionService.DeleteAsync(id, cancellationToken);
            OperationSucceeded = true;
            StatusMessage      = "订阅已删除。";
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "删除订阅 {SubscriptionName} 失败。", name);
            OperationSucceeded = false;
            StatusMessage      = "删除失败，请查看服务日志。";
        }
    }
}

/// <summary>订阅编辑模型，绑定订阅表单并携带 DataAnnotations 校验。</summary>
public sealed class SubscriptionDraft
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "请输入订阅名称。")]
    [StringLength(100, ErrorMessage = "订阅名称不能超过 100 个字符。")]
    public string Name { get; set; } = string.Empty;

    public string FeedUrl { get; set; } = string.Empty;

    [Range(5, 10080, ErrorMessage = "刷新周期必须为 5 到 10080 分钟。")]
    public int RefreshIntervalMinutes { get; set; } = 30;

    public int? FixedSubjectId { get; set; }

    /// <summary>代理下拉的绑定值（代理 Id 的字符串形式）；空表示直连。</summary>
    public string? ProxyId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public void Reset()
    {
        Id                     = null;
        Name                   = string.Empty;
        FeedUrl                = string.Empty;
        RefreshIntervalMinutes = 30;
        FixedSubjectId         = null;
        ProxyId                = null;
        IsEnabled              = true;
    }

    public void Load(SubscriptionView subscription)
    {
        Id                     = subscription.Id;
        Name                   = subscription.Name;
        FeedUrl                = string.Empty;
        RefreshIntervalMinutes = subscription.RefreshIntervalMinutes;
        FixedSubjectId         = subscription.FixedSubjectId;
        ProxyId                = subscription.ProxyId?.ToString();
        IsEnabled              = subscription.IsEnabled;
    }

    public SaveSubscriptionRequest ToRequest()
    {
        return new SaveSubscriptionRequest(Id, Name, FeedUrl, IsEnabled, RefreshIntervalMinutes, FixedSubjectId,
                                           ProxyId: Guid.TryParse(ProxyId, out var proxyId) ? proxyId : null);
    }
}

/// <summary>一次性订阅添加表单：只需 RSS 地址，名称留空由服务端按域名派生。</summary>
public sealed class OneShotSubscriptionDraft
{
    [Required(ErrorMessage = "请填写 RSS 地址。")]
    public string FeedUrl { get; set; } = string.Empty;

    public string? Name { get; set; }

    public void Reset()
    {
        FeedUrl = string.Empty;
        Name    = null;
    }
}
