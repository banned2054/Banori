using MediaManager.Data;
using MediaManager.Features.Notifications;

namespace MediaManager.Components.Pages.Notifications;

/// <summary>通知投递记录页状态：最近投递列表与人工重试操作。随页面组件创建和释放。</summary>
public sealed class NotificationsPageViewModel(NotificationDeliveryService deliveryService)
{
    public IReadOnlyList<NotificationDelivery> Deliveries { get; private set; } = [];

    public bool    Loaded    { get; private set; }
    public bool    Retrying  { get; private set; }
    public bool    Succeeded { get; private set; }
    public string? Message   { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Deliveries = await deliveryService.ListAsync(cancellationToken : cancellationToken);
        Loaded     = true;
    }

    /// <summary>把重试用尽的失败记录重新放回待发队列并刷新列表。</summary>
    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Retrying = true;
        Message  = null;
        try
        {
            await deliveryService.RetryAsync(id, cancellationToken);
            Succeeded = true;
            Message   = "已重新进入待发队列，稍后自动投递。";
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Succeeded = false;
            Message   = exception.Message;
        }
        finally
        {
            Retrying = false;
        }
    }
}
