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
///     第四阶段外部停止来源列的存量行迁移回归：旧任务升级后没有来源记录（界面按既有语义解释为
///     页面人工暂停），不因升级误标任何暂停任务。
/// </summary>
public sealed class DownloadExternalStopProvenanceMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyRowsHaveNoExternalStopProvenance()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.ExternalStop.Migration.");
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
                // 回退到本列加入前的最后一个迁移，插入旧结构的暂停任务行。
                await db.GetService<IMigrator>().MigrateAsync("20261020110545_AddDownloadSchedulingAndPathDecision");
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Paused', 1, 0, {5})
                                                     """, id, new string('c', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.SingleAsync();
                Assert.Equal(DownloadTaskStatus.Paused, task.Status);
                Assert.True(task.IsUserPaused);
                // 存量暂停任务无法追溯来源，保持无记录（不编造外部停止）。
                Assert.Null(task.ExternalStopObservedAtUtc);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
