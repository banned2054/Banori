using MediaManager.Data;
using MediaManager.Data.Migrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Data;

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
        var snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model, true);
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

        var migration = assembly.Migrations.Keys.Last();
        Assert.Contains(migration, assembly.Migrations.Keys);
        Assert.Contains("AddDownloadTaskFiles", migration);
        var fileEntity = db.Model.FindEntityType(typeof(DownloadTaskFile));
        Assert.NotNull(fileEntity);
        Assert.Contains(fileEntity.GetIndexes(), index => index.IsUnique &&
                                                          index.Properties.Select(property => property.Name)
                                                               .SequenceEqual(new[] { "DownloadTaskId", "FileIndex" }));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(fileEntity.GetForeignKeys()).DeleteBehavior);
    }
}
