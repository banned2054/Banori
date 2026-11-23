using Banori.Features.Downloads;

namespace Banori.Components.Shared;

/// <summary>Torrent 上传或一次性 RSS 链接下载的操作结果，供总览页与下载任务页统一展示反馈。</summary>
public sealed record DownloadIntakeFeedback(
    bool               Succeeded,
    string             Message,
    RssDownloadResult? RssResult = null);
