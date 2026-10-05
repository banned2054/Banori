using MediaManager.Core.Rules;
using MediaManager.Data;
using MediaManager.Features.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Anime;

/// <summary>动画总览页的一张卡片：Subject 的展示名、封面与其下全部单集候选。</summary>
public sealed record AnimeCard(int SubjectId, string DisplayName, string? ImageUrl,
                               IReadOnlyList<AnimeEpisode> Episodes);

/// <summary>一个同集（按条目上记录的集数分组）及其最终版本：优胜优先，其次更高版本。</summary>
public sealed record AnimeEpisode(decimal Episode, AnimeEpisodeLine Final);

/// <summary>单集下的一条候选：字幕组、语言、版本与发布时间；胜负标记取自最近一次组内择优。</summary>
public sealed record AnimeEpisodeLine(Guid FeedItemId, string Group, string? Language, int Version,
                                      DateTimeOffset? PublishedAtUtc, SelectionOutcome Outcome);

/// <summary>动画总览查询：把已关联 Subject 的条目按 Subject 与集数聚合为封面墙数据。</summary>
public sealed class AnimeQueries(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    public async Task<IReadOnlyList<AnimeCard>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.FeedItems.AsNoTracking()
                           .Where(item => item.SubjectId != null && item.Episode != null)
                           .Select(item => new
                           {
                               SubjectId  = item.SubjectId!.Value,
                               Episode    = item.Episode!.Value,
                               item.Id,
                               Group      = item.Group ?? string.Empty,
                               item.Language,
                               item.Version,
                               Published  = item.PublishedAtUtc ?? item.FirstSeenAtUtc,
                               item.SelectionOutcome
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
                       var displayName = subject is null                          ? $"#{group.Key}" :
                           string.IsNullOrWhiteSpace(subject.NameCn) ? subject.Name : subject.NameCn;
                       return new AnimeCard(group.Key, displayName, subject?.ImageUrl,
                                            group.GroupBy(row => row.Episode)
                                                 .OrderBy(episode => episode.Key)
                                                 .Select(episode => new AnimeEpisode(
                                                     episode.Key,
                                                     episode.Select(row => new AnimeEpisodeLine(
                                                                         row.Id,
                                                                         string.IsNullOrWhiteSpace(row.Group)
                                                                             ? "未知字幕组"
                                                                             : row.Group,
                                                                         row.Language, row.Version,
                                                                         row.Published, row.SelectionOutcome))
                                                            .OrderByDescending(line =>
                                                                 line.Outcome == SelectionOutcome.Winner)
                                                            .ThenByDescending(line => line.Version)
                                                            .ThenBy(line => line.Group, StringComparer.CurrentCulture)
                                                            .ThenBy(line => line.Language, StringComparer.CurrentCulture)
                                                            .First()))
                                                 .ToList());
                   })
                   .OrderBy(card => card.DisplayName, StringComparer.CurrentCulture)
                   .ToList();
    }
}
