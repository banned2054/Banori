using Banori.Components.Pages.Downloads;
using Banori.Features.Downloads;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Banori.Tests.Downloads;

/// <summary>下面板「详细信息」Tab 的渲染：核心字段优先、异常信息与恢复按钮同位。</summary>
public sealed class DownloadTaskDetailsTests
{
    [Fact]
    public async Task Render_PrioritizesQbNameAndHashAndDemotesRssTitle()
    {
        var task = CreateTask();
        var html = await RenderAsync(task);

        Assert.Contains("qB 种子名", html);
        Assert.Contains("[Anime]FX战士久留美 E08 byrbt", html);
        Assert.Contains("qB Hash", html);
        Assert.Contains("0123456789abcdef", html);
        Assert.Contains("复制 qB Hash", html);
        Assert.Contains("RSS 标题", html);
        Assert.Contains("Rss Source Title", html);
        Assert.DoesNotContain("原始种子名", html);
        Assert.DoesNotContain("命名快照", html);
        Assert.True(html.IndexOf("qB 种子名", StringComparison.Ordinal) <
                    html.IndexOf("动画名", StringComparison.Ordinal));
        Assert.True(html.IndexOf("qB Hash", StringComparison.Ordinal) <
                    html.IndexOf("动画名", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Render_ShowsStatusDetailsErrorAndRecoveryButtonTogether()
    {
        var task = CreateTask() with
        {
            Status     = DownloadTaskStatus.Failed,
            Error      = "下载器拒绝提交",
            FailedStep = "提交"
        };
        var html = await RenderAsync(task);

        Assert.Contains("失败", html);
        Assert.Contains("当前等待", html);
        Assert.Contains("任务已失败，等待处理", html);
        Assert.Contains("提交：", html);
        Assert.Contains("下载器拒绝提交", html);
        Assert.Contains("重试", html);
    }

    [Fact]
    public async Task Render_FailedTaskWithoutErrorStillOffersRetry()
    {
        var task = CreateTask() with { Status = DownloadTaskStatus.Failed, Error = null };
        var html = await RenderAsync(task);

        Assert.Contains("尚未记录详细错误", html);
        Assert.Contains("重试", html);
    }

    [Fact]
    public async Task Render_ShowsArchiveReviewAndVerificationAlerts()
    {
        var task = CreateTask() with
        {
            Status             = DownloadTaskStatus.Completed,
            ArchiveReviewReason = "目标目录被占用"
        };
        var html = await RenderAsync(task);
        Assert.Contains("归档待核对", html);
        Assert.Contains("目标目录被占用", html);
        Assert.Contains("重新核对归档", html);

        var unconfirmed = CreateTask() with
        {
            Status               = DownloadTaskStatus.Paused,
            VerificationStep      = DownloadVerificationStep.Unconfirmed,
            VerificationRequestState = DownloadVerificationRequestState.SendFailed
        };
        var unconfirmedHtml = await RenderAsync(unconfirmed);
        Assert.Contains("校验未确认", unconfirmedHtml);
        Assert.Contains("请求发送失败", unconfirmedHtml);
        Assert.Contains("重新校验", unconfirmedHtml);
    }

    private static DownloadTaskView CreateTask() => new()
    {
        Id             = Guid.NewGuid(),
        FeedItemId     = null,
        InfoHash       = "0123456789abcdef0123456789abcdef01234567",
        NamingSnapshot = "[Anime]FX战士久留美 E08 byrbt",
        TorrentName    = "Rss Source Title",
        SubjectName    = "FX战士久留美",
        Episode        = 8m,
        Version        = 1,
        SiteSource     = "byrbt",
        Status         = DownloadTaskStatus.Downloading,
        CreatedAtUtc   = DateTimeOffset.UtcNow
    };

    private static async Task<string> RenderAsync(DownloadTaskView task)
    {
        using var       provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DownloadTaskDetails>(ParameterView.FromDictionary(
                          new Dictionary<string, object?>
                          {
                              [nameof(DownloadTaskDetails.Task)]           = task,
                              [nameof(DownloadTaskDetails.CopyTextAsync)] = new Func<string, Task<bool>>(
                                  _ => Task.FromResult(true))
                          }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
