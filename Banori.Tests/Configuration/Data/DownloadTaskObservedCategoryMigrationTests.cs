using Banori.Data;
using Banori.Data.Migrations;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>
///     观察类别列的存量行迁移回归：旧任务升级后没有类别记录（界面按既有语义解释为普通下载），
///     不因升级误标任何阶段；新写入的枚举名按字符串转换往返。
/// </summary>
public sealed class DownloadTaskObservedCategoryMigrationTests
{
    [Fact]
    public async Task Migrate_LegacyRowsHaveNoObservedCategory()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.ObservedCategory.Migration.");
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
                // 回退到本列加入前的最后一个迁移，插入旧结构的下载中任务行。
                await db.GetService<IMigrator>().MigrateAsync("20261021175146_AddDownloadExternalStopProvenance");
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Downloading', 0, 0, {5})
                                                     """, id, new string('c', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.SingleAsync();
                Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
                // 存量任务无法追溯最近观察类别，保持无记录（不编造观察值）。
                Assert.Null(task.LastObservedCategory);

                // 新观察按字符串转换落库并往返为枚举，供投影与阶段派生使用。
                task.LastObservedCategory = QbittorrentTorrentCategory.Checking;
                await db.SaveChangesAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(QbittorrentTorrentCategory.Checking,
                            (await db.DownloadTasks.SingleAsync()).LastObservedCategory);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
