using Banori.Core.Torrents;
using Banori.Data;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>下载任务列表的只读投影查询：页面展示不依赖登记编排与下载执行的客户端。</summary>
public sealed class DownloadTaskQueries(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    public async Task<IReadOnlyList<DownloadTaskView>> ListAsync(
        int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.DownloadTasks.AsNoTracking()
                            .Include(task => task.FeedItem)
                            .Include(task => task.Subject)
                            .Include(task => task.Files)
                            .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序并截取。
        tasks = tasks.OrderByDescending(task => task.CreatedAtUtc)
                     .Take(Math.Clamp(take, 1, 500))
                     .ToList();

        // 替换关系与路径决策都可能指向其他任务，统一反查命名快照供解释展示。
        var relatedIds = tasks.SelectMany(task =>
                               {
                                   var decision = DownloadPathDecision.FromJson(task.PathDecision);
                                   return new[] { task.ReplacesTaskId, task.ReplacedByTaskId, decision?.RelatedTaskId };
                               })
                              .Where(value => value is not null)
                              .Select(value => value!.Value)
                              .Distinct()
                              .ToList();
        var relatedNames = relatedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.DownloadTasks.AsNoTracking()
                      .Where(task => relatedIds.Contains(task.Id))
                      .ToDictionaryAsync(task => task.Id, task => task.NamingSnapshot, cancellationToken);
        return tasks.Select(task => ToView(task, relatedNames)).ToList();
    }

    private static DownloadTaskView ToView(DownloadTask task, IReadOnlyDictionary<Guid, string> relatedNames)
    {
        var ingredients   = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        var archiveReview = DownloadArchiveReview.FromJson(task.ArchiveReview);
        var intent        = DownloadVerificationIntent.FromJson(task.VerificationIntent);
        var decision      = DownloadPathDecision.FromJson(task.PathDecision);
        return new DownloadTaskView
        {
            Id          = task.Id,
            FeedItemId  = task.FeedItemId,
            TorrentName = task.FeedItem?.Title,
            SubjectId   = task.SubjectId,
            SubjectName = task.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(task.Subject.NameCn) ? task.Subject.Name : task.Subject.NameCn,
            Episode          = task.Episode,
            Version          = task.Version,
            SubtitleGroup    = ingredients?.Group,
            SubtitleLanguage = ingredients?.Language,
            Status           = task.Status,
            IsUserPaused     = task.IsUserPaused,
            IsSystemPaused   = task.IsSystemPaused,
            Progress         = task.Progress,
            TotalSize        = task.TotalSize,
            NamingSnapshot   = task.NamingSnapshot,
            SiteSource       = task.SiteSource,
            FailedStep       = task.FailedStep,
            Error            = task.Error,
            CreatedAtUtc     = task.CreatedAtUtc,
            SubmittedAtUtc   = task.SubmittedAtUtc,
            CompletedAtUtc   = task.CompletedAtUtc,
            ReplacesTaskId   = task.ReplacesTaskId,
            ReplacesNaming = task.ReplacesTaskId is { } replacedId
                ? relatedNames.GetValueOrDefault(replacedId)
                : null,
            ReplacedByTaskId = task.ReplacedByTaskId,
            ReplacedByNaming = task.ReplacedByTaskId is { } replacedById
                ? relatedNames.GetValueOrDefault(replacedById)
                : null,
            PreparationStage          = task.PreparationStage,
            ArchiveReviewReason       = archiveReview?.Reason,
            IsolationState            = task.IsolationState,
            VerificationStep          = intent?.Step,
            VerificationRound         = intent?.Round            ?? 0,
            ObservedChecking          = intent?.ObservedChecking ?? false,
            StopConfirmPending        = intent?.TimeoutStopRequestedAtUtc is not null ||
                                        intent?.PauseRequestedAtUtc is not null,
            ResultStabilizing         = intent?.StabilityObservedAtUtc is not null,
            ExternalStopObservedAtUtc = task.ExternalStopObservedAtUtc,
            LastSyncedAtUtc           = task.LastSyncedAtUtc,
            LastObservedCategory      = task.LastObservedCategory,
            PathDecisionKind          = decision?.Kind,
            PathDecisionReason        = decision?.Reason,
            PathDecisionRelatedTaskId = decision?.RelatedTaskId,
            PathDecisionRelatedNaming = decision?.RelatedTaskId is { } decisionRelatedId
                ? relatedNames.GetValueOrDefault(decisionRelatedId)
                : null,
            HasPendingFileMoves       = task.PendingSavePath is not null ||
                                        task.Files.Any(file => file.PendingRenameTo is not null)
        };
    }
}
