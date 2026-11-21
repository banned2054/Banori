using Banori.Data;
using Banori.Data.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>
///     缺种巡检字段的存量行迁移回归：新增各列与独立索引对旧任务保持「未巡检、未清理」语义，
///     Down 干净。与 DownloadSchedulingMigrationTests 同形，覆盖 SQLite provider。
/// </summary>
public sealed class DownloadMissingCheckMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyRowsGetMissingCheckDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.MissingCheck.Migration.");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddIdentityCore<ApplicationUser>(options =>
                                                          options.Stores.SchemaVersion =
                                                              IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
            {
                options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
                options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "migration.db")};Pooling=False");
            });
            await using var provider = services.BuildServiceProvider();
            var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var             id       = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>()
                        .MigrateAsync("20261111144219_AddDownloadRunRecovery");
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Completed', 0, 0, {5})
                                                     """, id, new string('c', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
                var applied   = await db.Database.GetAppliedMigrationsAsync();
                var available = db.Database.GetMigrations();
                Assert.True(applied.Contains("20261120120000_AddTorrentMissingCheck"),
                            $"available=[{string.Join(",", available)}]; applied=[{string.Join(",", applied)}]");
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.SingleAsync();
                Assert.Null(task.TorrentMissingAtUtc);
                Assert.Null(task.NextMissingCheckAtUtc);
                Assert.Null(task.MissingCheckPendingAtUtc);
                Assert.Null(task.MissingCheckConfigKey);
                Assert.Null(task.MissingCleanupConfirmedAtUtc);
                Assert.Null(task.MissingCleanupScopeKey);
                Assert.Null(task.MissingCleanupReplacedById);
                Assert.Null(task.PendingSubmitSavePath);
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>()
                        .MigrateAsync("20261111144219_AddDownloadRunRecovery");
                var columns =
                    await db.Database
                            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('DownloadTasks')")
                            .ToListAsync();
                Assert.DoesNotContain("TorrentMissingAtUtc", columns);
                Assert.DoesNotContain("NextMissingCheckAtUtc", columns);
                Assert.DoesNotContain("MissingCheckPendingAtUtc", columns);
                Assert.DoesNotContain("MissingCheckConfigKey", columns);
                Assert.DoesNotContain("MissingCleanupConfirmedAtUtc", columns);
                Assert.DoesNotContain("MissingCleanupScopeKey", columns);
                Assert.DoesNotContain("MissingCleanupReplacedById", columns);
                Assert.DoesNotContain("PendingSubmitSavePath", columns);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
