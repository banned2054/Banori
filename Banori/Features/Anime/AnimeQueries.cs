using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Anime;

/// <summary>动画总览页的一张卡片：Subject 的展示名、封面与其下全部单集现任任务。</summary>
public sealed record AnimeCard(
    int                         SubjectId,
    string                      DisplayName,
    string?                     ImageUrl,
    IReadOnlyList<AnimeEpisode> Episodes);

/// <summary>一个同集（按任务上记录的集数分组）及其现任任务；被替换的旧版本不参与聚合。</summary>
public sealed record AnimeEpisode(decimal Episode, AnimeEpisodeLine Final);

/// <summary>单集的现任下载任务：字幕组、语言、版本与登记时间取自任务快照，状态取自执行链。</summary>
public sealed record AnimeEpisodeLine(
    Guid               TaskId,
    string             Group,
    string?            Language,
    int                Version,
    DateTimeOffset     CreatedAtUtc,
    DownloadTaskStatus Status);

/// <summary>
///     动画总览查询：把下载任务按 Subject 与集数聚合为封面墙数据。
///     只要有任务才出现：未产生下载的 RSS 条目不显示；被替换的旧版本任务也不显示，每集只剩现任。
/// </summary>
public sealed class AnimeQueries(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    public async Task<IReadOnlyList<AnimeCard>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DownloadTasks.AsNoTracking()
                           .Where(task => task.SubjectId != null &&
                                          task.Episode   != null &&
                                          task.Status    != DownloadTaskStatus.Replaced)
                           .Select(task => new
                            {
                                SubjectId = task.SubjectId!.Value,
                                Episode   = task.Episode!.Value,
                                task.Id,
                                task.NamingIngredients,
                                task.Version,
                                task.Status,
                                task.CreatedAtUtc
                            })
                           .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];

        var subjectIds = rows.Select(row => row.SubjectId).Distinct().ToList();
        var subjects = await db.MediaSubjects.AsNoTracking()
                               .Where(subject => subjectIds.Contains(subject.Id))
                               .Select(subject => new { subject.Id, subject.Name, subject.NameCn, subject.ImageUrl })
                               .ToDictionaryAsync(subject => subject.Id, cancellationToken);

        return rows.GroupBy(row => row.SubjectId)
                   .Select(group =>
                    {
                        subjects.TryGetValue(group.Key, out var subject);
                        var displayName = subject is null             ? $"#{group.Key}" :
                            string.IsNullOrWhiteSpace(subject.NameCn) ? subject.Name : subject.NameCn;
                        return new AnimeCard(group.Key, displayName, subject?.ImageUrl,
                                             group.GroupBy(row => row.Episode)
                                                  .OrderBy(episode => episode.Key)
                                                  .Select(episode => new AnimeEpisode(
                                                           episode.Key,
                                                           episode.Select(row =>
                                                                   {
                                                                       // 字幕组与语言取任务自带的命名要素快照；旧行没有合法 JSON 时回退占位字幕组。
                                                                       var ingredients =
                                                                           TorrentNamingIngredients
                                                                              .FromJson(row.NamingIngredients);
                                                                       var group = ingredients?.Group;
                                                                       return new AnimeEpisodeLine(
                                                                        row.Id,
                                                                        string.IsNullOrWhiteSpace(group)
                                                                            ? "未知字幕组"
                                                                            : group,
                                                                        ingredients?.Language, row.Version,
                                                                        row.CreatedAtUtc, row.Status);
                                                                   })
                                                                  .OrderByDescending(line => line.Version)
                                                                  .ThenByDescending(line => line.CreatedAtUtc)
                                                                  .First()))
                                                  .ToList());
                    })
                   .OrderBy(card => card.DisplayName, StringComparer.CurrentCulture)
                   .ToList();
    }
}
