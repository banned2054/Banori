using MediaManager.Features.Downloads;

namespace MediaManager.Components.Pages.Downloads;

/// <summary>下载任务页状态：任务列表、上传种子与暂停 / 恢复 / 重试操作。随页面组件创建和释放。</summary>
public sealed class DownloadTasksPageViewModel(
    DownloadService                     downloadService,
    ILogger<DownloadTasksPageViewModel> logger)
{
    /// <summary>上传种子的大小上限：.torrent 通常远小于该值，超限基本说明选错了文件。</summary>
    public const long MaxTorrentUploadBytes = 10 * 1024 * 1024;

    public IReadOnlyList<DownloadTaskView> Tasks { get; private set; } = [];

    public bool    Loaded    { get; private set; }
    public bool    Busy      { get; private set; }
    public string? Message   { get; private set; }
    public bool    Succeeded { get; private set; }

    /// <summary>上传表单的来源站点标注（u2/byrbt/tjupt 等），随下一次上传一起提交；可留空。</summary>
    public string? UploadSiteSource { get; set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Tasks  = await downloadService.ListAsync(cancellationToken : cancellationToken);
        Loaded = true;
    }

    /// <summary>上传种子登记：读取浏览器流后交给服务校验与去重，创建即等待提交。</summary>
    public async Task UploadTorrentAsync(Stream torrentStream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await torrentStream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            NoteFailure("所选文件为空，请重新选择。");
            return;
        }

        if (buffer.Length > MaxTorrentUploadBytes)
        {
            NoteFailure("种子文件超过 10 MB，已拒绝。");
            return;
        }

        await RunCoreAsync(
                           () => downloadService.CreateFromUploadAsync(buffer.ToArray(), UploadSiteSource,
                                                                       cancellationToken),
                           "上传种子已登记，等待提交下载器。", cancellationToken,
                           exception => logger.LogWarning(exception, "上传种子登记失败。"));
    }

    /// <summary>浏览器文件读取失败（超过传输上限或流中断）时直接提示，不触发服务调用。</summary>
    public void NoteFailure(string message)
    {
        Succeeded = false;
        Message   = message;
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
        await RunCoreAsync(operation, successMessage, cancellationToken,
                           exception => logger.LogWarning(exception, "下载任务 {TaskId} 操作失败。", taskId));
    }

    private async Task RunCoreAsync(Func<Task> operation, string successMessage, CancellationToken cancellationToken,
                                    Action<Exception> logFailure)
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
            logFailure(exception);
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
