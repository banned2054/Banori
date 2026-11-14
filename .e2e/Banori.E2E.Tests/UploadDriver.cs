using Banori.Features.Downloads;
using Banori.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.E2E.Tests;

/// <summary>
///     Banori 上传驱动与干跑：全部经 Banori 自身服务（与 UI 完全一致的传参，
///     不绕过任何被测逻辑）。
/// </summary>
public sealed class UploadDriver(AcceptanceHost host)
{
    /// <summary>
    ///     以与 UI 完全一致的参数调用 CreateFromUploadAsync。
    ///     UI 链路：Downloads.razor:546 UploadTorrentAsync（InputFile 流读入）→
    ///     DownloadTasksPageViewModel.UploadTorrentAsync(:574-594)：
    ///     intakeService.CreateFromUploadAsync(buffer.ToArray(), null, UploadSubjectId, ct)。
    ///     siteSource 传 null（由种子内 tracker 判定 byrbt），subjectId 仅在标题映射未命中时
    ///     由表单补 ID；冒烟走标题映射预种，传 null 验证映射命中链路。
    /// </summary>
    public async Task<Guid> UploadAsync(byte[] torrentBytes, int? subjectId = null,
                                        CancellationToken cancellationToken = default)
    {
        await using var scope  = host.Services.CreateAsyncScope();
        var             intake = scope.ServiceProvider.GetRequiredService<DownloadIntakeService>();
        return await intake.CreateFromUploadAsync(torrentBytes, null, subjectId, cancellationToken);
    }

    /// <summary>
    ///     干跑模式：上传登记后等对账推进到 Submitting 且 qB 侧出现该种子（stopped 添加），
    ///     即返回规划归档目标路径（DownloadFileService.ComputeArchivalTargetsAsync，
    ///     不触达下载器）；随后由调用方完整重置（ResetAsync）。超时默认 3 分钟
    ///     （对账 15s 空闲轮询 + 提交）。
    /// </summary>
    public async Task<DryRunResult> DryRunAsync(byte[] torrentBytes, QbApiClient qb, string evidencePath,
                                                int? subjectId = null, TimeSpan? timeout = null,
                                                CancellationToken cancellationToken = default)
    {
        var taskId   = await UploadAsync(torrentBytes, subjectId, cancellationToken);
        var observer = new EvidenceObserver(host, qb, evidencePath);
        await observer.ObserveUntilAsync(
            (task, torrents) =>
                task is { Status: DownloadTaskStatus.Submitting or DownloadTaskStatus.Downloading } &&
                torrents.Any(torrent =>
                                 string.Equals(torrent.Hash, task.InfoHash, StringComparison.OrdinalIgnoreCase)),
            timeout ?? TimeSpan.FromMinutes(3), cancellationToken: cancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
        var targets     = await fileService.ComputeArchivalTargetsAsync(taskId, cancellationToken);
        return new DryRunResult(taskId, targets);
    }
}

public sealed record DryRunResult(Guid TaskId, IReadOnlyList<(int FileIndex, string TargetPath)> Targets);
