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
///     启动恢复子状态列（RunRecovery）迁移回归：存量行升级后没有启动恢复记录（null 表示没有待恢复的
///     启动），不因升级追溯或改写任何已完成任务；Down 可干净回退（只保证迁移后尚未写入恢复记录的回退）。
/// </summary>
public sealed class RunRecoveryMigrationTests
{
    /// <summary>AddDownloadRunRecovery 的前一个 SQLite 迁移；插入新迁移后需同步更新。</summary>
    private const string PredecessorMigrationId = "20261109102357_AddSourceScopes";

    private static IServiceCollection CreateServices(string directory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                                    IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            options.UseSqlite($"Data Source={Path.Combine(directory, "runrecovery.db")};Pooling=False");
        });
        return services;
    }

    [Fact]
    public async Task Migrate_LegacyRowsHaveNoRunRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.RunRecovery.Migration.");
        try
        {
            await using var provider = CreateServices(directory.FullName).BuildServiceProvider();
            var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                // 回退到本列加入前的最后一个迁移，插入旧结构任务行。
                await db.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot",
                                                        "Version", "Status", "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Completed', 0, 0, {5})
                                                     """, Guid.NewGuid(), new string('a', 40), new byte[] { 1 },
                                                     "/legacy/path", "legacy", DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                var task = await db.DownloadTasks.SingleAsync();
                Assert.Equal(DownloadTaskStatus.Completed, task.Status);
                // 存量任务无法追溯启动状态，保持无记录（不编造「做种已恢复」）。
                Assert.Null(task.RunRecovery);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Down_OnCleanCopyDropsColumn()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.RunRecovery.Migration.Down.");
        try
        {
            await using var provider = CreateServices(directory.FullName).BuildServiceProvider();
            var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync();
                // 迁移后尚未写入恢复记录：Down 只保证这种干净回退。
                await db.GetService<IMigrator>().MigrateAsync(PredecessorMigrationId);
            }

            await using var verify = await factory.CreateDbContextAsync();
            var columns = await ColumnsAsync(verify, "DownloadTasks");
            Assert.DoesNotContain("RunRecovery", columns);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task<List<string>> ColumnsAsync(
        ApplicationDbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT \"name\" FROM pragma_table_info('{table}')";
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
