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
///     第二阶段调度字段的存量行迁移回归：默认值必须让旧任务保持「立即观察、无布局请求台账、
///     无路径决策记录」语义——不因升级把任何任务推迟或误判，Down 干净。
/// </summary>
public sealed class DownloadSchedulingMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyRowsGetSchedulingDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.Scheduling.Migration.");
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
                // 回退到本批字段加入前的最后一个迁移，插入旧结构的任务行。
                await db.GetService<IMigrator>().MigrateAsync("20261018113143_AddDataVerificationAndArchiveEvidence");
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Downloading', 0, 0, {5})
                                                     """, id, new string('b', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.SingleAsync();
                // 存量任务不因升级被推迟观察、不获得布局请求台账或路径决策。
                Assert.Null(task.NextObservationAtUtc);
                Assert.Null(task.LastSubmitAttemptAtUtc);
                Assert.Null(task.PendingLayoutRequestedAtUtc);
                Assert.Equal(0, task.PendingLayoutRequestCount);
                Assert.Equal(0, task.SelectionRequestCount);
                Assert.Null(task.PathDecision);
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261018113143_AddDataVerificationAndArchiveEvidence");
                var columns =
                    await db.Database
                            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('DownloadTasks')")
                            .ToListAsync();
                Assert.DoesNotContain("NextObservationAtUtc", columns);
                Assert.DoesNotContain("LastSubmitAttemptAtUtc", columns);
                Assert.DoesNotContain("PendingLayoutRequestedAtUtc", columns);
                Assert.DoesNotContain("PendingLayoutRequestCount", columns);
                Assert.DoesNotContain("SelectionRequestCount", columns);
                Assert.DoesNotContain("PathDecision", columns);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
