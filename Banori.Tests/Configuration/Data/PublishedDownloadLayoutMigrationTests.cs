using Banori.Data;
using Banori.Data.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>
///     已发布/本轮双布局列的存量行迁移回归：旧成片布局仍由成片根与当前相对路径表达，
///     Down 干净。快照与当前模型的一致性由 NamingIngredientsModelConsistencyTests 覆盖两种 provider。
/// </summary>
public sealed class PublishedDownloadLayoutMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyFileRowsGetNullPublishedLayout()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.PublishedLayout.Migration.");
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
            var             taskId   = Guid.NewGuid();
            var             fileId   = Guid.NewGuid();
            const string    baseline = "20261120120000_AddTorrentMissingCheck";
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync(baseline);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Completed', 0, 0, {5})
                                                     """, taskId, new string('c', 40), new byte[] { 100 },
                                                     "/legacy/archive", "legacy", DateTimeOffset.UtcNow);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTaskFiles"
                                                       ("Id", "DownloadTaskId", "FileIndex", "OriginalRelativePath",
                                                        "CurrentRelativePath", "Size", "Kind", "ClassificationReason",
                                                        "IsSelectedForDownload", "IsManualOverride", "UpdatedAtUtc")
                                                     VALUES ({0}, {1}, 0, {2}, {3}, 1024, 'Anime', '', 1, 0, {4})
                                                     """, fileId, taskId, "raw.mkv", "archived.mkv",
                                                     DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.Contains("20261120130000_PublishedDownloadLayout",
                                await db.Database.GetAppliedMigrationsAsync());
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var file = await db.DownloadTaskFiles.SingleAsync();
                Assert.Null(file.PublishedPath);
                Assert.Null(file.PublishedRelativePath);
                Assert.Equal("archived.mkv", file.CurrentRelativePath);
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync(baseline);
                var columns = await db.Database
                                       .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('DownloadTaskFiles')")
                                       .ToListAsync();
                Assert.DoesNotContain("PublishedPath", columns);
                Assert.DoesNotContain("PublishedRelativePath", columns);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
