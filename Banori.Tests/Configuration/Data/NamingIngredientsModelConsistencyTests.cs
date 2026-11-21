using Banori.Data;
using Banori.Data.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Configuration.Data;

public sealed class NamingIngredientsModelConsistencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestSnapshotMatchesCurrentProviderModel(bool mysql)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            if (mysql)
                options.UseMySql("Server=127.0.0.1;Port=3306;Database=model_only;User=model;Password=model;",
                                 ServerVersion.Parse("8.0.36"));
            else
                options.UseSqlite("Data Source=:memory:");
        });
        using var provider = services.BuildServiceProvider();
        using var scope    = provider.CreateScope();
        using var db       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var assembly      = db.GetService<IMigrationsAssembly>();
        var snapshot      = Assert.IsAssignableFrom<ModelSnapshot>(assembly.ModelSnapshot);
        var snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model);
        var designModel   = db.GetService<IDesignTimeModel>().Model;
        var differ        = db.GetService<IMigrationsModelDiffer>();
        var differences   = differ.GetDifferences(snapshotModel.GetRelationalModel(), designModel.GetRelationalModel());

        Assert.Empty(differences);
        foreach (var table in new[] { "FeedItems", "DownloadTasks" })
        {
            var property = db.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                             .Single(value => value.Name                         == "NamingIngredients" &&
                                              value.DeclaringType.GetTableName() == table);
            Assert.Null(property.GetMaxLength());
            Assert.Equal(mysql ? "longtext" : "TEXT", property.GetColumnType(), true);
        }

        // 观察类别列按字符串转换存枚举名：两 provider 一致限制 32 字符，nullable 供存量行为空。
        var categoryProperty = db.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                                 .Single(value => value.Name                         == "LastObservedCategory" &&
                                                  value.DeclaringType.GetTableName() == "DownloadTasks");
        Assert.Equal(32, categoryProperty.GetMaxLength());
        Assert.True(categoryProperty.IsNullable);

        var migration = assembly.Migrations.Keys.Last();
        // 迁移序列里必须同时能看到文件表与其后的逐集登记表迁移；新增迁移后更新这里的最新迁移名。
        // 新增 PublishedDownloadLayout（已发布成片布局列）后，最新迁移从 AddTorrentMissingCheck 顺延。
        Assert.Contains("PublishedDownloadLayout", migration);
        // 缺种巡检与待确认提交根列所在迁移仍须在序列中。
        Assert.Contains(assembly.Migrations.Keys, key => key.Contains("AddTorrentMissingCheck"));
        Assert.Contains(assembly.Migrations.Keys, key => key.Contains("AddSourceScopes"));
        Assert.Contains(assembly.Migrations.Keys, key => key.Contains("AddDownloadTaskObservedCategory"));
        Assert.Contains(assembly.Migrations.Keys, key => key.Contains("AddDownloadExternalStopProvenance"));
        Assert.Contains(assembly.Migrations.Keys, key => key.Contains("AddDataVerificationAndArchiveEvidence"));
        var fileEntity = db.Model.FindEntityType(typeof(DownloadTaskFile));
        Assert.NotNull(fileEntity);
        Assert.Contains(fileEntity.GetIndexes(),
                        index => index.IsUnique && index.Properties.Select(property => property.Name)
                                                        .SequenceEqual(["DownloadTaskId", "FileIndex"]));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(fileEntity.GetForeignKeys()).DeleteBehavior);

        // 每集登记表：Subject + 集数 + 来源作用域唯一，文件引用不设外键（承接回退由清理逻辑负责）。
        var episodeEntity = db.Model.FindEntityType(typeof(EpisodeVersion));
        Assert.NotNull(episodeEntity);
        Assert.Contains(episodeEntity.GetIndexes(),
                        index => index.IsUnique && index.Properties.Select(property => property.Name)
                                                        .SequenceEqual(["SubjectId", "Episode", "SourceScope"]));
        Assert.Empty(episodeEntity.GetForeignKeys());

        // 预选按来源拆组：同组可以每个来源各一行；FeedItem 只加普通索引供来源过滤。
        var candidateEntity = db.Model.FindEntityType(typeof(CandidateSelection));
        Assert.NotNull(candidateEntity);
        Assert.Contains(candidateEntity.GetIndexes(),
                        index => index.IsUnique && index.Properties.Select(property => property.Name)
                                                        .SequenceEqual(["SubjectId", "MediaType", "Episode", "SourceScope"]));
        var feedEntity = db.Model.FindEntityType(typeof(FeedItem));
        Assert.NotNull(feedEntity);
        Assert.Contains(feedEntity.GetIndexes(),
                        index => !index.IsUnique && index.Properties.Select(property => property.Name)
                                                         .SequenceEqual(["SubjectId", "MediaType", "Episode", "SourceScope"]));

        var sourceScopeProperties = db.Model.GetEntityTypes()
                                            .SelectMany(entity => entity.GetProperties())
                                            .Where(property => property.Name == "SourceScope")
                                            .ToList();
        Assert.Equal(3, sourceScopeProperties.Count);
        Assert.All(sourceScopeProperties, property =>
        {
            Assert.Equal(50, property.GetMaxLength());
            Assert.False(property.IsNullable);
            Assert.Equal("(legacy)", property.GetDefaultValue());
        });
    }
}
