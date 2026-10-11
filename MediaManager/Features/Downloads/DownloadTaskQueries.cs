using MediaManager.Core.Torrents;
using MediaManager.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Downloads;

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
                            .ToListAsync(cancellationToken);
        // SQLite 不支持 DateTimeOffset 服务端排序，取回后内存排序并截取。
        tasks = tasks.OrderByDescending(task => task.CreatedAtUtc)
                     .Take(Math.Clamp(take, 1, 500))
                     .ToList();

        var relatedIds = tasks.SelectMany(task => new[] { task.ReplacesTaskId, task.ReplacedByTaskId })
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
        var ingredients = TorrentNamingIngredients.FromJson(task.NamingIngredients);
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
                : null
        };
    }
}
