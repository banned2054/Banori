using MediaManager.Core.Rules;
using MediaManager.Data;
using MediaManager.Data.Migrations;
using MediaManager.Features.Downloads;
using MediaManager.Features.Subscriptions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Data;

/// <summary>
///     RemoveOneShotSubscription 迁移的数据验收：一次性订阅及其条目、择优与刷新工作被清除，
///     但已产生的下载任务必须保留并脱离条目来源（FeedItemId 置空），普通订阅数据不受影响。
/// </summary>
public sealed class RemoveOneShotSubscriptionMigrationTests
{
    /// <summary>RemoveOneShotSubscription 的前一个迁移；新迁移插入到该位置之后时需同步更新。</summary>
    private const string PredecessorMigrationId = "20261005074809_LocalTorrentUpload";

    [Fact]
    public async Task Migrate_DetachesOneShotTasksAndRemovesOneShotData()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Migration.OneShot.Tests.");
        try
        {
            var             timestamp = new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero);
            var             services  = CreateServices(testDirectory, timestamp);
            await using var provider  = services.BuildServiceProvider();
            var             factory   = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            // 只应用到前一个迁移，构造带一次性数据的存量库。
            await using (var bootstrap = await factory.CreateDbContextAsync())
            {
                await bootstrap.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
            }

            var regularSubscriptionId    = Guid.NewGuid();
            var oneShotSubscriptionId    = Guid.NewGuid();
            var regularItemId            = Guid.NewGuid();
            var oneShotItemWithTaskId    = Guid.NewGuid();
            var oneShotItemWithoutTaskId = Guid.NewGuid();
            var taskWithSourceId         = Guid.NewGuid();
            var detachedTaskId           = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                // 当前模型已不映射 IsOneShot，且存量库中该列为 NOT NULL 无默认值：订阅行用原生 SQL 复原。
                // 表重建会丢失初始 ALTER 的列默认值，EF 种子插入无法绕过该列。
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "MediaSubscriptions"
                                                         ("Id", "Name", "FeedUrlProtected", "IsEnabled", "IsOneShot",
                                                          "RefreshIntervalMinutes", "FixedSubjectId", "ProxyId",
                                                          "CreatedAtUtc", "UpdatedAtUtc", "LastRefreshStartedAtUtc",
                                                          "LastRefreshCompletedAtUtc", "NextRefreshAtUtc", "LastError")
                                                     VALUES ({0}, {1}, {2}, 1, 0, 30, NULL, NULL, {3}, {3}, NULL, NULL, {4}, NULL)
                                                     """,
                                                     regularSubscriptionId, "常规订阅", "protected", timestamp,
                                                     timestamp.AddMinutes(30));
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "MediaSubscriptions"
                                                         ("Id", "Name", "FeedUrlProtected", "IsEnabled", "IsOneShot",
                                                          "RefreshIntervalMinutes", "FixedSubjectId", "ProxyId",
                                                          "CreatedAtUtc", "UpdatedAtUtc", "LastRefreshStartedAtUtc",
                                                          "LastRefreshCompletedAtUtc", "NextRefreshAtUtc", "LastError")
                                                     VALUES ({0}, {1}, {2}, 1, 1, 30, NULL, NULL, {3}, {3}, NULL, NULL, NULL, NULL)
                                                     """,
                                                     oneShotSubscriptionId, "一次性 RSS：example.com", "protected",
                                                     timestamp);
                db.MediaSubjects.Add(new MediaSubject
                {
                    Id = 42, Name = "Test Subject", NameCn = "测试作品", Platform = "TV", CachedAtUtc = timestamp
                });
                // 先落库 Subject，条目行的原生 SQL 外键引用它。
                await db.SaveChangesAsync();
                // 条目行同样用原生 SQL 复原：存量库停在 RemoveOneShotSubscription 之前，
                // 当前模型此后新增的 FeedItems 列（如 NamingIngredients）在存量表中尚不存在。
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "FeedItems"
                                                         ("Id", "SubscriptionId", "StableId", "Title", "FirstSeenAtUtc",
                                                          "Status", "DecisionStatus", "Reason", "EvaluatedAtUtc",
                                                          "SubjectId", "Episode", "Version")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8},
                                                             NULL, NULL, 1)
                                                     """,
                                                     regularItemId, regularSubscriptionId, "regular-1",
                                                     "常规条目", timestamp, FeedItemStatus.Accepted.ToString(),
                                                     ReleaseDecisionStatus.Accepted.ToString(), "测试。", timestamp);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "FeedItems"
                                                         ("Id", "SubscriptionId", "StableId", "Title", "FirstSeenAtUtc",
                                                          "Status", "DecisionStatus", "Reason", "EvaluatedAtUtc",
                                                          "SubjectId", "Episode", "Version")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8},
                                                             42, 1, 1)
                                                     """,
                                                     oneShotItemWithTaskId, oneShotSubscriptionId, "one-shot-1",
                                                     "一次性条目（有任务）", timestamp, FeedItemStatus.Accepted.ToString(),
                                                     ReleaseDecisionStatus.Accepted.ToString(), "测试。", timestamp);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "FeedItems"
                                                         ("Id", "SubscriptionId", "StableId", "Title", "FirstSeenAtUtc",
                                                          "Status", "DecisionStatus", "Reason", "EvaluatedAtUtc",
                                                          "SubjectId", "Episode", "Version")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8},
                                                             NULL, NULL, 1)
                                                     """,
                                                     oneShotItemWithoutTaskId, oneShotSubscriptionId, "one-shot-2",
                                                     "一次性条目（无任务）", timestamp,
                                                     FeedItemStatus.NeedsReview.ToString(),
                                                     ReleaseDecisionStatus.NeedsReview.ToString(), "测试。", timestamp);
                db.RefreshWorkItems.Add(new RefreshWorkItem
                {
                    Id     = Guid.NewGuid(), SubscriptionId              = oneShotSubscriptionId,
                    Status = RefreshWorkStatus.Succeeded, RequestedAtUtc = timestamp
                });
                db.CandidateSelections.Add(new CandidateSelection
                {
                    Id               = Guid.NewGuid(), SubjectId = 42, MediaType = "SingleEpisode", Episode = 1,
                    WinnerFeedItemId = oneShotItemWithTaskId, RuleVersion = 1, RankingBasis = "测试优胜",
                    NamingSnapshot   = "命名快照", SelectedAtUtc = timestamp
                });
                // 先落库 EF 实体（订阅条目等是任务行的外键来源）。
                await db.SaveChangesAsync();
                // 当前模型已映射 ActiveFlag 计算列而存量库没有该列，任务行同样用原生 SQL 复原。
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                         ("Id", "FeedItemId", "SubjectId", "InfoHash", "TorrentFile",
                                                          "TorrentUrl", "SavePath", "NamingSnapshot", "Episode",
                                                          "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, 42, {2}, {3}, {4}, {5}, {6}, 1,
                                                             1, {7}, 0, 0, {8})
                                                     """,
                                                     taskWithSourceId, oneShotItemWithTaskId,
                                                     "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                                                     new byte[] { 0x64 }, "https://example.com/a.torrent",
                                                     "/downloads/a-aaaaaaaa",
                                                     "一次性条目（有任务）", DownloadTaskStatus.Downloading.ToString(),
                                                     timestamp);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                         ("Id", "FeedItemId", "InfoHash", "TorrentFile",
                                                          "TorrentUrl", "SavePath", "NamingSnapshot",
                                                          "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6},
                                                             1, {7}, 0, 0, {8})
                                                     """,
                                                     detachedTaskId, regularItemId,
                                                     "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                                                     new byte[] { 0x64 }, "https://example.com/b.torrent",
                                                     "/downloads/b-bbbbbbbb",
                                                     "常规条目", DownloadTaskStatus.PendingSubmit.ToString(), timestamp);
            }

            // 应用剩余迁移（含 RemoveOneShotSubscription）。
            await using (var finisher = await factory.CreateDbContextAsync())
            {
                await finisher.GetService<IMigrator>().MigrateAsync();
            }

            await using var verify = await factory.CreateDbContextAsync();
            // 订阅行含 EF 不再映射的存量列，用原生 SQL 校验。
            var remainingSubscriptionIds = await verify.Database
                                                       .SqlQueryRaw<Guid>(
                                                                          "SELECT \"Id\" AS Value FROM \"MediaSubscriptions\"")
                                                       .ToListAsync();
            Assert.Equal([regularSubscriptionId], remainingSubscriptionIds);

            var items = await verify.FeedItems.AsNoTracking().ToListAsync();
            Assert.Equal(regularItemId, Assert.Single(items).Id);

            Assert.Empty(await verify.CandidateSelections.AsNoTracking().ToListAsync());
            Assert.Empty(await verify.RefreshWorkItems.AsNoTracking().ToListAsync());

            var tasks = await verify.DownloadTasks.AsNoTracking().ToListAsync();
            Assert.Equal(2, tasks.Count);

            // 一次性订阅条目产生的任务被保留，但脱离条目来源；快照与 Subject 关联不受影响。
            var preserved = tasks.Single(task => task.Id == taskWithSourceId);
            Assert.Null(preserved.FeedItemId);
            Assert.Equal(42, preserved.SubjectId);
            Assert.Equal(1m, preserved.Episode);
            Assert.Equal(DownloadTaskStatus.Downloading, preserved.Status);

            // 普通订阅条目的任务原样保留。
            var untouched = tasks.Single(task => task.Id == detachedTaskId);
            Assert.Equal(regularItemId, untouched.FeedItemId);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private static ServiceCollection CreateServices(DirectoryInfo directory, DateTimeOffset timestamp)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 与 Program.cs 一致：按 provider 过滤迁移集，否则 MySQL 基线会被 SQLite 迁移器应用。
        services.AddDbContextFactory<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "migration.db")};Pooling=False");
        });
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        return services;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }
}
