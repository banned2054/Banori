using MediaManager.Core.Rules;
using MediaManager.Features.Downloads;
using MediaManager.Features.FeedItems;
using MediaManager.Features.Subscriptions;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Components.Pages.FeedItems;

/// <summary>条目详情页状态：条目数据、人工 Subject 修正草稿、下载任务入口。随页面组件创建和释放。</summary>
public sealed class FeedItemDetailsPageViewModel(
    FeedItemQueries                       queries,
    FeedItemEvaluator                     evaluator,
    DownloadService                       downloadService,
    ILogger<FeedItemDetailsPageViewModel> logger)
{
    public FeedItemView?          Item       { get; private set; }
    public SubjectCorrectionDraft Correction { get; } = new();
    public bool                   Loaded     { get; private set; }
    public bool                   Saving     { get; private set; }
    public bool                   Succeeded  { get; private set; }
    public string?                Message    { get; private set; }

    /// <summary>是否允许为该条目创建下载任务：决策通过且没有活动中的下载任务。</summary>
    public bool CanCreateDownload =>
        Item is
        {
            Status        : FeedItemStatus.Accepted,
            DecisionStatus: ReleaseDecisionStatus.Accepted,
            TorrentUrl    : not null,
            Episode       : not null,
            SubjectId     : not null,
            NamingPreview : not null
        } &&
        (Item.Download is null || !Item.Download.Status.IsActive());

    public async Task LoadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Item = await queries.GetAsync(id, cancellationToken);
        // 预填标题映射记录的原始 Subject；条目未评估或无映射时留空，要求显式填写，不得默认最终结果。
        Correction.SubjectId = Item?.OriginalSubjectId;
        Loaded               = true;
    }

    public async Task ReevaluateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Saving = true;
        try
        {
            var subjectId = Correction.SubjectId ?? throw new InvalidOperationException("请先填写 Subject ID 再重新评估。");
            await evaluator.ReevaluateAsync(id, subjectId, cancellationToken);
            Succeeded = true;
            Message   = "已使用指定 Subject 重新评估。";
            await LoadAsync(id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "重新评估条目失败。");
            Succeeded = false;
            Message   = "重新评估失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    /// <summary>把当前条目的决策登记为下载任务；重复点击由服务的活动任务去重拦截。</summary>
    public async Task CreateDownloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Saving = true;
        try
        {
            await downloadService.CreateForFeedItemAsync(id, cancellationToken);
            Succeeded = true;
            Message   = "已创建下载任务，提交进度见下载任务页。";
            await LoadAsync(id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "创建下载任务失败。");
            Succeeded = false;
            Message = exception is InvalidOperationException
                ? exception.Message
                : "创建下载任务失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }
}

/// <summary>人工 Subject 修正的编辑模型。留空或填原始关联值，避免把集数修正后的结果当作原始 Subject 提交。</summary>
public sealed class SubjectCorrectionDraft
{
    [Required(ErrorMessage = "请填写 Subject ID。")]
    [Range(1, int.MaxValue, ErrorMessage = "Subject ID 必须为正整数。")]
    public int? SubjectId { get; set; }
}
