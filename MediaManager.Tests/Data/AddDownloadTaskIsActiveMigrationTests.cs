using MediaManager.Data;
using MediaManager.Data.Migrations;
using MediaManager.Features.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Data;

/// <summary>
///     AddDownloadTaskIsActive 迁移的数据验收：存量任务全部保留，
///     “同一 InfoHash 活动任务唯一、终态任务可并存”的约束由 ActiveFlag 计算列唯一索引继续成立。
/// </summary>
public sealed class AddDownloadTaskIsActiveMigrationTests
{
    /// <summary>AddDownloadTaskIsActive 的前一个迁移；新迁移插入到该位置之后时需同步更新。</summary>
    private const string PredecessorMigrationId = "20261005144234_RemoveOneShotSubscription";

    private const string SameHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Migrate_PreservesTasksAndKeepsActiveHashUnique()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Migration.ActiveFlag.Tests.");
        try
        {
            var             timestamp = new DateTimeOffset(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);
            var             services  = CreateServices(testDirectory, timestamp);
            await using var provider  = services.BuildServiceProvider();
            var             factory   = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            // 只应用到前一个迁移，构造旧约束（带 WHERE 的部分唯一索引）下的存量库。
            await using (var bootstrap = await factory.CreateDbContextAsync())
            {
                await bootstrap.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
            }

            var activeAId   = Guid.NewGuid();
            var terminalA1  = Guid.NewGuid();
            var terminalA2  = Guid.NewGuid();
            var activeBId   = Guid.NewGuid();
            var terminalBId = Guid.NewGuid();

            // 当前模型把 ActiveFlag 映射为计算列，而存量库尚无该列，EF 插入后的生成列回读会失败，改用原生 SQL。
            await using (var db = await factory.CreateDbContextAsync())
            {
                await InsertTaskAsync(db, activeAId, SameHash, DownloadTaskStatus.Downloading, timestamp);
                // 旧约束允许同 hash 的多条终态任务并存，迁移后必须继续允许。
                await InsertTaskAsync(db, terminalA1, SameHash, DownloadTaskStatus.Completed, timestamp);
                await InsertTaskAsync(db, terminalA2, SameHash, DownloadTaskStatus.Failed, timestamp);
                await InsertTaskAsync(db, activeBId, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                                      DownloadTaskStatus.PendingSubmit, timestamp);
                await InsertTaskAsync(db, terminalBId, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                                      DownloadTaskStatus.Completed, timestamp);
            }

            // 应用剩余迁移（含 AddDownloadTaskIsActive）。
            await using (var finisher = await factory.CreateDbContextAsync())
            {
                await finisher.GetService<IMigrator>().MigrateAsync();
            }

            await using var verify = await factory.CreateDbContextAsync();

            // 存量任务全部保留，状态不受影响。
            var tasks = await verify.DownloadTasks.AsNoTracking().ToListAsync();
            Assert.Equal(5, tasks.Count);
            Assert.Equal(DownloadTaskStatus.Downloading, tasks.Single(task => task.Id == activeAId).Status);
            Assert.Equal(DownloadTaskStatus.Completed, tasks.Single(task => task.Id   == terminalA1).Status);
            Assert.Equal(DownloadTaskStatus.Failed, tasks.Single(task => task.Id      == terminalA2).Status);

            // 计算列取值：活动行 1，终态行 NULL。
            var activeFlagCounts = await verify.Database
                                               .SqlQueryRaw<FlagCount>("""
                                                                       SELECT "ActiveFlag" AS "Flag", COUNT(*) AS "Count"
                                                                       FROM "DownloadTasks" GROUP BY "ActiveFlag"
                                                                       """)
                                               .ToListAsync();
            Assert.Equal(2, activeFlagCounts.Single(row => row.Flag == 1).Count);
            Assert.Equal(3, activeFlagCounts.Single(row => row.Flag == null).Count);

            // 同 hash 的第二个活动任务被唯一索引拒绝。
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.DownloadTasks.Add(new DownloadTask
                {
                    Id             = Guid.NewGuid(), InfoHash                      = SameHash, TorrentFile = [0x64],
                    TorrentUrl     = "https://example.com/again.torrent", SavePath = "/downloads/again",
                    NamingSnapshot = "重复登记", Status                                = DownloadTaskStatus.PendingSubmit,
                    CreatedAtUtc   = timestamp
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            // 同 hash 的又一条终态任务允许并存。
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.DownloadTasks.Add(new DownloadTask
                {
                    Id             = Guid.NewGuid(), InfoHash                      = SameHash, TorrentFile = [0x64],
                    TorrentUrl     = "https://example.com/again.torrent", SavePath = "/downloads/again",
                    NamingSnapshot = "又一终态", Status                                = DownloadTaskStatus.Completed,
                    CreatedAtUtc   = timestamp
                });
                await db.SaveChangesAsync();
            }

            // 撤销唯一索引的 Down 路径：回退到前一迁移后，部分唯一索引恢复且数据完好。
            await using (var downgrader = await factory.CreateDbContextAsync())
            {
                await downgrader.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
            }

            await using var reverted = await factory.CreateDbContextAsync();
            Assert.Equal(6, await reverted.DownloadTasks.AsNoTracking().CountAsync());
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private static async Task InsertTaskAsync(ApplicationDbContext db,     Guid           id, string infoHash,
                                              DownloadTaskStatus   status, DateTimeOffset timestamp)
    {
        await db.Database.ExecuteSqlRawAsync("""
                                             INSERT INTO "DownloadTasks"
                                                 ("Id", "InfoHash", "TorrentFile", "TorrentUrl", "SavePath",
                                                  "NamingSnapshot", "Version", "Status", "IsUserPaused",
                                                  "AttemptCount", "CreatedAtUtc")
                                             VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 1, {6}, 0, 0, {7})
                                             """,
                                             id, infoHash, new byte[] { 0x64 }, $"https://example.com/{id:N}.torrent",
                                             $"/downloads/{id:N}", id.ToString("N"), status.ToString(), timestamp);
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

    private sealed record FlagCount(int? Flag, int Count);

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
