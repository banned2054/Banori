using MediaManager.Data;
using MediaManager.Data.Migrations;
using MediaManager.Features.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Data;

public sealed class DownloadTaskFilesMigrationTests
{
    [Fact]
    public async Task Migrate_PreservesLegacyTask_AndFileIdentityIsUniqueAndCascades()
    {
        var directory = Directory.CreateTempSubdirectory("MediaManager.Files.Migration.");
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
                await db.GetService<IMigrator>().MigrateAsync("20261010091200_UnlimitNamingIngredients");
                await db.Database.ExecuteSqlRawAsync("""
                                                     INSERT INTO "DownloadTasks"
                                                       ("Id", "InfoHash", "TorrentFile", "SavePath", "NamingSnapshot", "Version", "Status",
                                                        "IsUserPaused", "AttemptCount", "CreatedAtUtc")
                                                     VALUES ({0}, {1}, {2}, {3}, {4}, 1, 'Completed', 0, 0, {5})
                                                     """, id, new string('a', 40), new byte[] { 100 }, "/legacy/path",
                                                     "legacy", DateTimeOffset.UtcNow);
                await db.GetService<IMigrator>().MigrateAsync();
                var task = await db.DownloadTasks.SingleAsync();
                Assert.Equal(id, task.Id);
                Assert.Equal("/legacy/path", task.SavePath);
                Assert.Null(task.DownloadRootPath);
                Assert.Empty(await db.DownloadTaskFiles.ToListAsync());
                db.DownloadTaskFiles.Add(CreateFile(id));
                await db.SaveChangesAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                db.DownloadTaskFiles.Add(CreateFile(id));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.DownloadTasks.Where(task => task.Id == id).ExecuteDeleteAsync();
                Assert.Empty(await db.DownloadTaskFiles.ToListAsync());
                await db.GetService<IMigrator>().MigrateAsync("20261010091200_UnlimitNamingIngredients");
                var tables = await db
                                  .Database
                                  .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
                                  .ToListAsync();
                Assert.DoesNotContain("DownloadTaskFiles", tables);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static DownloadTaskFile CreateFile(Guid taskId)
    {
        return new DownloadTaskFile
        {
            Id = Guid.NewGuid(), DownloadTaskId = taskId, FileIndex = 0,
            OriginalRelativePath = "Original/Subs/episode01.chi.ass",
            CurrentRelativePath = "Seed/Original/Subs/episode01.chi.ass",
            Kind = DownloadFileKind.Seed, ClassificationReason = "未识别", UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
