using Banori.Data;
using Banori.Data.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

/// <summary>
///     MySQL 迁移的离线验收：只生成 SQL，不连接真实服务。真实 MySQL 的 MigrateAsync、索引与
///     collation 行为仍需有服务环境验证，不能把本测试当作真实升级验收。
/// </summary>
public sealed class MySqlSourceScopeScriptTests
{
    private const string PredecessorMigrationId = "20261023093007_AddDownloadTaskObservedCategory";
    private const string LatestMigrationId      = "20261109102428_AddSourceScopes";

    [Fact]
    public void GenerateScript_UpAddsColumnsIndexesAndNormalizesSiteSource()
    {
        var sql = GenerateScript(PredecessorMigrationId, LatestMigrationId);

        Assert.Contains("ADD `SourceScope` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT '(legacy)'", sql);
        Assert.Contains("DROP INDEX `IX_EpisodeVersions_SubjectId_Episode`", sql);
        Assert.Contains("DROP INDEX `IX_CandidateSelections_SubjectId_MediaType_Episode`", sql);
        Assert.Contains(
            "CREATE UNIQUE INDEX `IX_EpisodeVersions_SubjectId_Episode_SourceScope` " +
            "ON `EpisodeVersions` (`SubjectId`, `Episode`, `SourceScope`)", sql);
        Assert.Contains(
            "CREATE UNIQUE INDEX `IX_CandidateSelections_SubjectId_MediaType_Episode_SourceScope` " +
            "ON `CandidateSelections` (`SubjectId`, `MediaType`, `Episode`, `SourceScope`)", sql);
        Assert.Contains(
            "CREATE INDEX `IX_FeedItems_SubjectId_MediaType_Episode_SourceScope` " +
            "ON `FeedItems` (`SubjectId`, `MediaType`, `Episode`, `SourceScope`)", sql);
        Assert.Contains("UPDATE `DownloadTasks` SET `SiteSource` = 'other'", sql);
        Assert.Contains("LOWER(TRIM(`SiteSource`))", sql);
    }

    [Fact]
    public void GenerateScript_DownDropsNewIndexesAndRestoresOldUniqueIndexes()
    {
        var sql = GenerateScript(LatestMigrationId, PredecessorMigrationId);

        Assert.Contains("DROP INDEX `IX_FeedItems_SubjectId_MediaType_Episode_SourceScope`", sql);
        Assert.Contains("DROP INDEX `IX_EpisodeVersions_SubjectId_Episode_SourceScope`", sql);
        Assert.Contains("DROP INDEX `IX_CandidateSelections_SubjectId_MediaType_Episode_SourceScope`", sql);
        Assert.Contains("DROP COLUMN `SourceScope`", sql);
        Assert.Contains(
            "CREATE UNIQUE INDEX `IX_EpisodeVersions_SubjectId_Episode` " +
            "ON `EpisodeVersions` (`SubjectId`, `Episode`)", sql);
        Assert.Contains(
            "CREATE UNIQUE INDEX `IX_CandidateSelections_SubjectId_MediaType_Episode` " +
            "ON `CandidateSelections` (`SubjectId`, `MediaType`, `Episode`)", sql);
    }

    private static string GenerateScript(string fromMigration, string toMigration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            options.UseMySql("Server=127.0.0.1;Port=3306;Database=model_only;User=model;Password=model;",
                             ServerVersion.Parse("8.0.36"));
        });
        using var provider = services.BuildServiceProvider();
        using var scope    = provider.CreateScope();
        using var db       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.GetService<IMigrator>().GenerateScript(fromMigration, toMigration);
    }
}
