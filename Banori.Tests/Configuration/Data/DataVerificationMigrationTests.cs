using Banori.Data;
using Banori.Data.Migrations;
using Banori.Features.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>
///     校验与归档证据字段的存量行迁移回归：默认值必须让旧任务保持「无校验意图、不在隔离、无待核对」
///     语义，Down 干净。
/// </summary>
public sealed class DataVerificationMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyRowsGetConservativeDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.Verification.Migration.");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
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
                // 回退到本批字段加入前的最后一个迁移，插入旧结构的任务与文件行。
                await db.GetService<IMigrator>().MigrateAsync("20261018041454_AddEpisodeVersionRegistry");
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Downloading', 0, 0, {5})
                                                     """, id, new string('b', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTaskFiles"
                                                       ("Id", "DownloadTaskId", "FileIndex", "OriginalRelativePath",
                                                        "CurrentRelativePath", "Size", "Kind", "IsManualOverride",
                                                        "ClassificationReason", "IsSelectedForDownload", "UpdatedAtUtc")
                                                     VALUES ({0}, {1}, 0, 'Video/episode01.mkv', 'Video/episode01.mkv',
                                                             1024, 'Anime', 0, '正片', 1, {2})
                                                     """, Guid.NewGuid(), id, DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.Include(task => task.Files).SingleAsync();
                // 存量任务不因升级进入校验/隔离/待核对状态，也不获得任何复用证据。
                Assert.Equal(DownloadIsolationState.None, task.IsolationState);
                Assert.Null(task.VerificationIntent);
                Assert.Null(task.ArchiveReview);
                var file = Assert.Single(task.Files);
                Assert.Null(file.VerificationOutcome);
                Assert.Null(file.VerificationPath);
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261018041454_AddEpisodeVersionRegistry");
                var taskColumns = await ColumnNamesAsync(db, "DownloadTasks");
                Assert.DoesNotContain("IsolationState", taskColumns);
                Assert.DoesNotContain("VerificationIntent", taskColumns);
                Assert.DoesNotContain("ArchiveReview", taskColumns);
                var fileColumns = await ColumnNamesAsync(db, "DownloadTaskFiles");
                Assert.DoesNotContain("VerificationOutcome", fileColumns);
                Assert.DoesNotContain("VerificationPath", fileColumns);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static Task<List<string>> ColumnNamesAsync(ApplicationDbContext db, string table) => table switch
    {
        "DownloadTasks" => QueryAsync(db, "SELECT name AS Value FROM pragma_table_info('DownloadTasks')"),
        _               => QueryAsync(db, "SELECT name AS Value FROM pragma_table_info('DownloadTaskFiles')")
    };

    private static async Task<List<string>> QueryAsync(ApplicationDbContext db, string sql) =>
        await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
}
