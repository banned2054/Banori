using MediaManager.Features.Downloads;

namespace MediaManager.Components.Pages.Downloads;

/// <summary>下载任务页状态：任务列表与暂停 / 恢复 / 重试操作。随页面组件创建和释放。</summary>
public sealed class DownloadTasksPageViewModel(
    DownloadService                     downloadService,
    ILogger<DownloadTasksPageViewModel> logger)
{
    public IReadOnlyList<DownloadTaskView> Tasks { get; private set; } = [];

    public bool    Loaded    { get; private set; }
    public bool    Busy      { get; private set; }
    public string? Message   { get; private set; }
    public bool    Succeeded { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Tasks  = await downloadService.ListAsync(cancellationToken : cancellationToken);
        Loaded = true;
    }

    public async Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.PauseAsync(taskId, cancellationToken),
                                "任务已暂停。", cancellationToken);
    }

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.ResumeAsync(taskId, cancellationToken),
                                "任务已恢复。", cancellationToken);
    }

    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await RunOperationAsync(taskId, () => downloadService.RetryAsync(taskId, cancellationToken),
                                "任务已重新排队，等待提交。", cancellationToken);
    }

    private async Task RunOperationAsync(
        Guid taskId, Func<Task> operation, string successMessage, CancellationToken cancellationToken)
    {
        Busy    = true;
        Message = null;
        try
        {
            await operation();
            Succeeded = true;
            Message   = successMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "下载任务 {TaskId} 操作失败。", taskId);
            Succeeded = false;
            Message = exception.Message.Contains("下载器", StringComparison.Ordinal) ||
                      exception is InvalidOperationException
                ? exception.Message
                : "下载器操作失败，请查看服务日志。";
        }
        finally
        {
            Busy = false;
        }

        await LoadAsync(cancellationToken);
    }
}
