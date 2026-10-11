using MediaManager.Data;
using MediaManager.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MediaManager.Features.Anime;

/// <summary>
///     Subject 本地缓存与标题映射的唯一写入方：MediaSubject 元数据 upsert、SubjectTitleMapping
///     回写与缓存优先的元数据读取。订阅评估与下载登记共用同一实现，不再各自维护重复副本；
///     何时回写映射仍由调用方决定（评估始终记录修正前的原始 Subject，登记仅在解析档命中时回写）。
/// </summary>
public sealed class SubjectMetadataCache(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ISubjectMetadataProvider                metadataProvider,
    TimeProvider                            timeProvider)
{
    /// <summary>读取本地缓存的 Subject 元数据；未缓存返回 null，不访问 Bangumi API。</summary>
    public async Task<SubjectMetadata?> GetCachedAsync(int               subjectId,
                                                       CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subject = await db.MediaSubjects.AsNoTracking()
                              .SingleOrDefaultAsync(item => item.Id == subjectId, cancellationToken);
        return subject is null
            ? null
            : new SubjectMetadata(subject.Id, subject.Name, subject.NameCn, subject.Platform, subject.AirDate,
                                  subject.ImageUrl, subject.CachedAtUtc);
    }

    /// <summary>缓存优先确保 Subject 元数据可用：命中直接返回，否则调 Bangumi API 拉取并落库（失败抛出）。</summary>
    public async Task<SubjectMetadata> EnsureAsync(int subjectId, CancellationToken cancellationToken = default)
    {
        var cached = await GetCachedAsync(subjectId, cancellationToken);
        if (cached is not null) return cached;

        var metadata = await metadataProvider.GetAsync(subjectId, cancellationToken);
        await SaveAsync(metadata, cancellationToken);
        return metadata;
    }

    /// <summary>把 Bangumi 元数据写入本地缓存：无则新增，有则逐字段覆盖。</summary>
    public async Task SaveAsync(SubjectMetadata subject, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.MediaSubjects.SingleOrDefaultAsync(item => item.Id == subject.Id, cancellationToken);
        if (entity is null)
        {
            entity = new MediaSubject { Id = subject.Id };
            db.MediaSubjects.Add(entity);
        }

        entity.Name        = subject.Name;
        entity.NameCn      = subject.NameCn;
        entity.Platform    = subject.Platform;
        entity.AirDate     = subject.AirDate;
        entity.ImageUrl    = subject.ImageUrl;
        entity.CachedAtUtc = subject.CachedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>标题映射回写：解析出系列标题才落映射，让同系列后续条目直接命中。</summary>
    public async Task SaveTitleMappingAsync(string            normalizedTitle, int subjectId,
                                            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)) return;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mapping =
            await db.SubjectTitleMappings.SingleOrDefaultAsync(item => item.NormalizedTitle == normalizedTitle,
                                                               cancellationToken);
        if (mapping is null)
        {
            db.SubjectTitleMappings.Add(new SubjectTitleMapping
            {
                Id              = Guid.NewGuid(),
                NormalizedTitle = normalizedTitle,
                SubjectId       = subjectId,
                UpdatedAtUtc    = timeProvider.GetUtcNow()
            });
        }
        else
        {
            mapping.SubjectId    = subjectId;
            mapping.UpdatedAtUtc = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
