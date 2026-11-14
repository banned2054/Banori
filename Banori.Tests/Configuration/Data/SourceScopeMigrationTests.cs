using Banori.Core.Torrents;
using Banori.Data;
using Banori.Data.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>
///     AddSourceScopes 迁移的数据验收：存量行一个不少地落入 legacy 隔离域，DownloadTask.SiteSource
///     完成 bt→other 与 trim/lower 归一；同一组的两个来源可以并存，同组同源的重复行仍被唯一索引拒绝。
/// </summary>
public sealed class SourceScopeMigrationTests
{
    /// <summary>AddSourceScopes 的前一个 SQLite 迁移；插入新迁移后需同步更新。</summary>
    private const string PredecessorMigrationId = "20261023093000_AddDownloadTaskObservedCategory";

    private static readonly DateTimeOffset Timestamp = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migrate_BackfillsLegacyRowsAndAllowsOneRowPerSource()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.SourceScope.Migration.");
        try
        {
            var             services = CreateServices(directory);
            await using var provider = services.BuildServiceProvider();
            var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var             ids      = new SeedIds();

            await using (var bootstrap = await factory.CreateDbContextAsync())
            {
                await bootstrap.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
                await SeedLegacyAsync(bootstrap, ids);
            }

            await using (var finisher = await factory.CreateDbContextAsync())
            {
                await finisher.GetService<IMigrator>().MigrateAsync();
            }

            await using var verify = await factory.CreateDbContextAsync();
            var tasks = await verify.DownloadTasks.AsNoTracking().ToListAsync();
            Assert.Equal(3, tasks.Count);
            Assert.Equal(SourceScopes.Other, tasks.Single(task => task.Id == ids.BtTaskId).SiteSource);
            Assert.Equal("u2", tasks.Single(task => task.Id == ids.U2TaskId).SiteSource);
            Assert.Null(tasks.Single(task => task.Id == ids.NullSourceTaskId).SiteSource);

            // 三张表的存量行数量不变，且全部落入 legacy 隔离域。
            Assert.Equal(SourceScopes.Legacy, (await verify.FeedItems.AsNoTracking().SingleAsync()).SourceScope);
            Assert.Equal(SourceScopes.Legacy, (await verify.CandidateSelections.AsNoTracking().SingleAsync()).SourceScope);
            Assert.Equal(SourceScopes.Legacy, (await verify.EpisodeVersions.AsNoTracking().SingleAsync()).SourceScope);

            // 同一 Subject/MediaType/Episode 可以各来源一行。
            verify.CandidateSelections.Add(new CandidateSelection
            {
                Id               = Guid.NewGuid(),
                SubjectId        = 42,
                MediaType        = "SingleEpisode",
                Episode          = 1,
                SourceScope      = "mikan",
                WinnerFeedItemId = ids.FeedItemId,
                RuleVersion      = 1,
                RankingBasis     = "同组另一来源",
                NamingSnapshot   = "命名快照",
                SelectedAtUtc    = Timestamp
            });
            verify.EpisodeVersions.Add(new EpisodeVersion
            {
                SubjectId     = 42,
                Episode       = 1,
                MediaType     = "SingleEpisode",
                SourceScope   = "mikan",
                UpdatedAtUtc  = Timestamp
            });
            await verify.SaveChangesAsync();

            // 同组同来源的第二行被新的唯一索引拒绝。
            await AssertRejectsCandidateAsync(factory, ids, "mikan");
            await AssertRejectsEpisodeVersionAsync(factory);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Down_OnCleanCopyRestoresOldIndexesAndDropsColumns()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.SourceScope.Migration.Down.");
        try
        {
            var             services = CreateServices(directory);
            await using var provider = services.BuildServiceProvider();
            var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var             ids      = new SeedIds();

            await using (var bootstrap = await factory.CreateDbContextAsync())
            {
                await bootstrap.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
                await SeedLegacyAsync(bootstrap, ids);
                await bootstrap.GetService<IMigrator>().MigrateAsync();
                // 干净副本：尚未写入跨来源新行时，Down 恢复旧唯一索引。
                await bootstrap.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
            }

            await using var verify = await factory.CreateDbContextAsync();
            var feedColumns = await ColumnNamesAsync(verify, "FeedItems");
            Assert.DoesNotContain("SourceScope", feedColumns);
            Assert.DoesNotContain("SourceScope", await ColumnNamesAsync(verify, "CandidateSelections"));
            Assert.DoesNotContain("SourceScope", await ColumnNamesAsync(verify, "EpisodeVersions"));

            var candidateIndexes = await IndexNamesAsync(verify, "CandidateSelections");
            Assert.Contains("IX_CandidateSelections_SubjectId_MediaType_Episode", candidateIndexes);
            Assert.DoesNotContain("IX_CandidateSelections_SubjectId_MediaType_Episode_SourceScope", candidateIndexes);

            var episodeIndexes = await IndexNamesAsync(verify, "EpisodeVersions");
            Assert.Contains("IX_EpisodeVersions_SubjectId_Episode", episodeIndexes);
            Assert.DoesNotContain("IX_EpisodeVersions_SubjectId_Episode_SourceScope", episodeIndexes);

            var feedIndexes = await IndexNamesAsync(verify, "FeedItems");
            Assert.Contains("IX_FeedItems_SubjectId_Episode_Status", feedIndexes);
            Assert.DoesNotContain("IX_FeedItems_SubjectId_MediaType_Episode_SourceScope", feedIndexes);

            // 旧结构的数据量不变。
            Assert.Equal(3, await CountAsync(verify, "DownloadTasks"));
            Assert.Equal(1, await CountAsync(verify, "FeedItems"));
            Assert.Equal(1, await CountAsync(verify, "CandidateSelections"));
            Assert.Equal(1, await CountAsync(verify, "EpisodeVersions"));
            // 旧结构上不做实体查询：模型随后续迁移加入了新列（如启动恢复 RunRecovery），
            // Down 后的旧表没有这些列，实体 SELECT 会因缺列失败；业务结论改用原生 SQL 验证。
            var revertedSiteSource = await verify.Database
                .SqlQuery<string>($"SELECT \"SiteSource\" AS \"Value\" FROM \"DownloadTasks\" WHERE \"Id\" = {ids.BtTaskId}")
                .SingleAsync();
            Assert.Equal(SourceScopes.Other, revertedSiteSource);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task AssertRejectsCandidateAsync(
        IDbContextFactory<ApplicationDbContext> factory, SeedIds ids, string sourceScope)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CandidateSelections.Add(new CandidateSelection
        {
            Id               = Guid.NewGuid(),
            SubjectId        = 42,
            MediaType        = "SingleEpisode",
            Episode          = 1,
            SourceScope      = sourceScope,
            WinnerFeedItemId = ids.FeedItemId,
            RuleVersion      = 1,
            RankingBasis     = "重复来源",
            NamingSnapshot   = "命名快照",
            SelectedAtUtc    = Timestamp
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task AssertRejectsEpisodeVersionAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.EpisodeVersions.Add(new EpisodeVersion
        {
            SubjectId     = 42,
            Episode       = 1,
            MediaType     = "SingleEpisode",
            SourceScope   = "mikan",
            UpdatedAtUtc  = Timestamp
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task SeedLegacyAsync(ApplicationDbContext db, SeedIds ids)
    {
        await db.Database.ExecuteSqlRawAsync("""
                                             INSERT INTO "MediaSubscriptions"
                                                 ("Id", "Name", "FeedUrlProtected", "IsEnabled",
                                                  "RefreshIntervalMinutes", "CreatedAtUtc", "UpdatedAtUtc")
                                             VALUES ({0}, '旧订阅', 'protected', 1, 30, {1}, {1})
                                             """, ids.SubscriptionId, Timestamp);
        await db.Database.ExecuteSqlRawAsync("""
                                             INSERT INTO "MediaSubjects" ("Id", "Name", "NameCn", "Platform", "CachedAtUtc")
                                             VALUES (42, '旧作品', '旧作品', 'TV', {0})
                                             """, Timestamp);
        await db.Database.ExecuteSqlRawAsync("""
                                             INSERT INTO "FeedItems"
                                                 ("Id", "SubscriptionId", "StableId", "Title", "FirstSeenAtUtc",
                                                  "Status", "Reason", "SubjectId", "Episode", "Version")
                                             VALUES ({0}, {1}, 'legacy-1', '旧条目', {2}, 'Accepted', '迁移测试。', 42, 1, 1)
                                             """, ids.FeedItemId, ids.SubscriptionId, Timestamp);
        await db.Database.ExecuteSqlRawAsync("""
                                             INSERT INTO "CandidateSelections"
                                                 ("Id", "SubjectId", "MediaType", "Episode", "WinnerFeedItemId",
                                                  "RuleVersion", "RankingBasis", "NamingSnapshot", "SelectedAtUtc")
                                             VALUES ({0}, 42, 'SingleEpisode', 1, {1}, 1, '旧优胜', '旧快照', {2})
                                             """, ids.CandidateSelectionId, ids.FeedItemId, Timestamp);
        await db.Database.ExecuteSqlRawAsync("""
                                             INSERT INTO "EpisodeVersions"
                                                 ("SubjectId", "Episode", "MediaType", "UpdatedAtUtc")
                                             VALUES (42, 1, 'SingleEpisode', {0})
                                             """, Timestamp);

        // bt、U2、NULL 三种历史 SiteSource：迁移后分别归 other、u2、保持 NULL。
        await InsertTaskAsync(db, ids.BtTaskId, 'a', "bt");
        await InsertTaskAsync(db, ids.U2TaskId, 'b', "U2");
        await InsertTaskAsync(db, ids.NullSourceTaskId, 'c', null);
    }

    private static Task InsertTaskAsync(ApplicationDbContext db, Guid id, char hashPrefix, string? siteSource)
    {
        // 不把 null 作为参数传给 raw SQL（EF 无法为 null 推断存储类型）；SiteSource 为空时直接省略该列。
        if (siteSource is null)
            return db.Database.ExecuteSqlRawAsync("""
                                                  INSERT INTO "DownloadTasks"
                                                      ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                       "Version", "Status", "IsUserPaused", "AttemptCount",
                                                       "CreatedAtUtc")
                                                  VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Completed', 0, 0, {5})
                                                  """, id, new string(hashPrefix, 40), new byte[] { 0x64 },
                                                  $"/downloads/{id:N}", "迁移测试", Timestamp);

        return db.Database.ExecuteSqlRawAsync("""
                                              INSERT INTO "DownloadTasks"
                                                  ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                   "Version", "Status", "IsUserPaused", "AttemptCount",
                                                   "CreatedAtUtc", "SiteSource")
                                              VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Completed', 0, 0, {5}, {6})
                                              """, id, new string(hashPrefix, 40), new byte[] { 0x64 },
                                              $"/downloads/{id:N}", "迁移测试", Timestamp, siteSource);
    }

    private static async Task<List<string>> ColumnNamesAsync(ApplicationDbContext db, string table) =>
        await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info({0})", table)
                .ToListAsync();

    private static async Task<List<string>> IndexNamesAsync(ApplicationDbContext db, string table) =>
        await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_index_list({0})", table)
                .ToListAsync();

    private static Task<int> CountAsync(ApplicationDbContext db, string table) => table switch
    {
        "DownloadTasks" => db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM \"DownloadTasks\"")
                             .SingleAsync(),
        "FeedItems" => db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM \"FeedItems\"").SingleAsync(),
        "CandidateSelections" => db.Database
                                   .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM \"CandidateSelections\"")
                                   .SingleAsync(),
        "EpisodeVersions" => db.Database
                               .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM \"EpisodeVersions\"")
                               .SingleAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    private static ServiceCollection CreateServices(DirectoryInfo directory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 与 Program.cs 一致：按 provider 过滤迁移集，否则 MySQL 基线会被 SQLite 迁移器应用。
        services.AddDbContextFactory<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "migration.db")};Pooling=False");
        });
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Timestamp));
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        return services;
    }

    private sealed class SeedIds
    {
        public Guid BtTaskId             { get; } = Guid.NewGuid();
        public Guid U2TaskId             { get; } = Guid.NewGuid();
        public Guid NullSourceTaskId     { get; } = Guid.NewGuid();
        public Guid SubscriptionId       { get; } = Guid.NewGuid();
        public Guid FeedItemId           { get; } = Guid.NewGuid();
        public Guid CandidateSelectionId { get; } = Guid.NewGuid();
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
