using Banori.Data;
using Banori.Data.Migrations;
using Banori.Features.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>准备流程字段的存量行迁移回归：默认值必须让旧任务保持“无需准备、全部下载”语义。</summary>
public sealed class TorrentPreparationMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyRowsGetConservativeDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.Preparation.Migration.");
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
                // 回退到准备流程字段加入前的最后一个迁移，插入旧结构的任务与文件行。
                await db.GetService<IMigrator>().MigrateAsync("20261011084920_AddDownloadTaskFiles");
                var fileId = Guid.NewGuid();
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot", "Version", "Status",
                                                        "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Downloading', 0, 0, {5})
                                                     """, id, new string('b', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTaskFiles"
                                                       ("Id", "DownloadTaskId", "FileIndex", "OriginalRelativePath",
                                                        "CurrentRelativePath", "Size", "Kind", "IsManualOverride",
                                                        "ClassificationReason", "UpdatedAtUtc")
                                                     VALUES ({0}, {1}, 0, 'Video/episode01.mkv', 'Video/episode01.mkv',
                                                             1024, 'Anime', 0, '正片', {2})
                                                     """, fileId, id, DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.Include(task => task.Files).SingleAsync();
                // 存量任务不被误判为系统准备暂停，也不会因准备阶段缺省而卡住。
                Assert.False(task.IsSystemPaused);
                Assert.Equal(DownloadPreparationStage.None, task.PreparationStage);
                Assert.Null(task.SelectedTotalBytes);
                // 存量文件默认承担下载，不因升级被静默跳过。
                var file = Assert.Single(task.Files);
                Assert.True(file.IsSelectedForDownload);
                Assert.Null(file.SelectionReason);
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                // Down 干净：回退后新列消失。
                await db.GetService<IMigrator>().MigrateAsync("20261011084920_AddDownloadTaskFiles");
                var taskColumns = await ColumnNamesAsync(db, "DownloadTasks");
                Assert.DoesNotContain("IsSystemPaused", taskColumns);
                Assert.DoesNotContain("PreparationStage", taskColumns);
                var fileColumns = await ColumnNamesAsync(db, "DownloadTaskFiles");
                Assert.DoesNotContain("IsSelectedForDownload", fileColumns);
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
