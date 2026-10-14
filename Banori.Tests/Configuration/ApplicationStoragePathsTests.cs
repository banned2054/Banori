using Banori.Configuration;
using Microsoft.Data.Sqlite;

namespace Banori.Tests.Configuration;

public sealed class ApplicationStoragePathsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("Banori.StoragePaths.Tests.");

    private string ContentRoot     => _directory.FullName;
    private string ConfigDirectory => Path.Combine(ContentRoot, "config");

    public void Dispose() => _directory.Delete(true);

    [Theory]
    [InlineData(DatabaseProvider.Sqlite, "Data Source=config/banori.db")]
    [InlineData(DatabaseProvider.MySql, "Server=localhost;Database=banori;User=test;Password=test")]
    public void FreshInstallation_ReturnsConfigDirectoryWithoutCreatingData(
        DatabaseProvider provider, string connectionString)
    {
        AssertResolves(provider, connectionString);

        Assert.Empty(Directory.EnumerateFileSystemEntries(ContentRoot));
    }

    [Theory]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("App_Data", false)]
    [InlineData("App_Data", true)]
    public void LegacyDatabase_WithUnmigratedDefaultDatabase_IsRejected(string legacyName, bool absolutePath)
    {
        CreateFile(legacyName, "banori.db");
        var databasePath = absolutePath ? Path.Combine(ConfigDirectory, "banori.db") : "./config/banori.db";

        AssertMigrationRequired(legacyName, DatabaseProvider.Sqlite, SqliteConnectionString(databasePath));
    }

    [Fact]
    public void DataSourceDirectoryWithoutDatabaseOrKeys_IsAllowed()
    {
        CreateFile("Data", "ApplicationDbContext.cs");
        CreateFile("Data", "Migrations", "InitialCreate.cs");

        AssertResolves(DatabaseProvider.Sqlite, "Data Source=config/banori.db");

        Assert.False(Directory.Exists(ConfigDirectory));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("App_Data")]
    public void MigratedDefaultDatabaseAndKeys_WithLegacyCopiesRemaining_AreAllowed(string legacyName)
    {
        CreateFile(legacyName, "banori.db");
        CreateFile(legacyName, "keys", "key-test.xml");
        CreateFile("config", "banori.db");
        CreateFile("config", "keys", "key-test.xml");

        AssertResolves(DatabaseProvider.Sqlite, "Data Source=config/banori.db");
    }

    [Theory]
    [InlineData("data", DatabaseProvider.Sqlite, false)]
    [InlineData("data", DatabaseProvider.Sqlite, true)]
    [InlineData("App_Data", DatabaseProvider.Sqlite, false)]
    [InlineData("App_Data", DatabaseProvider.Sqlite, true)]
    [InlineData("data", DatabaseProvider.MySql, false)]
    [InlineData("data", DatabaseProvider.MySql, true)]
    [InlineData("App_Data", DatabaseProvider.MySql, false)]
    [InlineData("App_Data", DatabaseProvider.MySql, true)]
    public void LegacyKeysWithoutNewKeys_AreRejectedEvenWithoutLegacyDatabase(
        string legacyName, DatabaseProvider provider, bool emptyNewKeysDirectory)
    {
        CreateFile(legacyName, "keys", "key-test.xml");
        if (emptyNewKeysDirectory)
            Directory.CreateDirectory(Path.Combine(ConfigDirectory, "keys"));
        var connectionString = provider == DatabaseProvider.Sqlite
            ? "Data Source=config/banori.db"
            : "Server=localhost;Database=banori;User=test;Password=test";

        AssertMigrationRequired(legacyName, provider, connectionString);
    }

    [Theory]
    [InlineData("data", DatabaseProvider.Sqlite)]
    [InlineData("App_Data", DatabaseProvider.Sqlite)]
    [InlineData("data", DatabaseProvider.MySql)]
    [InlineData("App_Data", DatabaseProvider.MySql)]
    public void NonKeyFilesInNewKeysDirectory_DoNotCountAsMigratedKeys(string legacyName, DatabaseProvider provider)
    {
        CreateFile(legacyName, "keys", "key-test.xml");
        CreateFile("config", "keys", "placeholder.txt");
        var connectionString = provider == DatabaseProvider.Sqlite
            ? "Data Source=config/banori.db"
            : "Server=localhost;Database=banori;User=test;Password=test";

        AssertMigrationRequired(legacyName, provider, connectionString);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("App_Data")]
    public void MigratedDefaultDatabaseWithoutMigratedKeys_IsStillRejected(string legacyName)
    {
        CreateFile(legacyName, "keys", "key-test.xml");
        CreateFile("config", "banori.db");
        Directory.CreateDirectory(Path.Combine(ConfigDirectory, "keys"));

        AssertMigrationRequired(legacyName, DatabaseProvider.Sqlite, "Data Source=config/banori.db");
    }

    [Theory]
    [InlineData("data")]
    [InlineData("App_Data")]
    public void MigratedKeysWithoutMigratedDefaultDatabase_IsStillRejected(string legacyName)
    {
        CreateFile(legacyName, "banori.db");
        CreateFile(legacyName, "keys", "key-test.xml");
        CreateFile("config", "keys", "key-test.xml");

        AssertMigrationRequired(legacyName, DatabaseProvider.Sqlite, "Data Source=config/banori.db");
    }

    [Theory]
    [InlineData("data", false, false)]
    [InlineData("data", false, true)]
    [InlineData("data", true, false)]
    [InlineData("data", true, true)]
    [InlineData("App_Data", false, false)]
    [InlineData("App_Data", false, true)]
    [InlineData("App_Data", true, false)]
    [InlineData("App_Data", true, true)]
    public void CustomSqliteDatabase_IsNotBlockedByUnrelatedLegacyDatabase(
        string legacyName, bool absolutePath, bool customDatabaseExists)
    {
        CreateFile(legacyName, "banori.db");
        if (customDatabaseExists)
            CreateFile("custom", "application.db");
        var databasePath =
            absolutePath ? Path.Combine(ContentRoot, "custom", "application.db") : "custom/application.db";

        AssertResolves(DatabaseProvider.Sqlite, SqliteConnectionString(databasePath));
    }

    [Theory]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("App_Data", false)]
    [InlineData("App_Data", true)]
    public void CustomSqliteDatabase_StillRequiresLegacyKeysToBeMigrated(string legacyName, bool absolutePath)
    {
        CreateFile(legacyName, "banori.db");
        CreateFile(legacyName, "keys", "key-test.xml");
        Directory.CreateDirectory(Path.Combine(ConfigDirectory, "keys"));
        var databasePath =
            absolutePath ? Path.Combine(ContentRoot, "custom", "application.db") : "custom/application.db";
        var connectionString = SqliteConnectionString(databasePath);

        AssertMigrationRequired(legacyName, DatabaseProvider.Sqlite, connectionString);

        CreateFile("config", "keys", "key-test.xml");

        AssertResolves(DatabaseProvider.Sqlite, connectionString);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("App_Data")]
    public void MySql_IsNotBlockedByUnrelatedLegacyDatabase(string legacyName)
    {
        CreateFile(legacyName, "banori.db");

        AssertResolves(DatabaseProvider.MySql, "Server=localhost;Database=banori;User=test;Password=test");
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=config/banori.db;Mode=Memory")]
    [InlineData("Data Source=file:config/banori.db")]
    [InlineData("Data Source=FILE:config/banori.db")]
    [InlineData("Data Source=file::memory:?cache=shared")]
    [InlineData("Data Source=file:config/banori.db?mode=memory&cache=shared")]
    [InlineData("Data Source=")]
    public void MemoryAndFileUriDataSources_AreNotTreatedAsOrdinaryDatabasePaths(string connectionString)
    {
        CreateFile("data", "banori.db");
        CreateFile("App_Data", "banori.db");

        AssertResolves(DatabaseProvider.Sqlite, connectionString);

        // 数据源不是普通文件路径也不能绕过旧 keys 的迁移检查。
        CreateFile("data", "keys", "key-test.xml");
        Directory.CreateDirectory(Path.Combine(ConfigDirectory, "keys"));

        AssertMigrationRequired("data", DatabaseProvider.Sqlite, connectionString);
    }

    [WindowsFact]
    public void Windows_MixedCaseDataSourceAndLegacyDatabase_AreDistinguished()
    {
        CreateFile("Data", "ApplicationDbContext.cs");
        CreateFile("Data", "Migrations", "InitialCreate.cs");
        const string connectionString = "Data Source=CoNfIg/BaNoRi.Db";

        AssertResolves(DatabaseProvider.Sqlite, connectionString);

        CreateFile("dAtA", "BaNoRi.Db");

        AssertMigrationRequired("data", DatabaseProvider.Sqlite, connectionString);
    }

    [WindowsFact]
    public void Windows_MixedCaseLegacyKeys_RequireMigrationDespiteDataSourceFiles()
    {
        CreateFile("Data", "ApplicationDbContext.cs");
        CreateFile("dAtA", "KeYs", "KeY-test.XmL");
        Directory.CreateDirectory(Path.Combine(ConfigDirectory, "keys"));

        AssertMigrationRequired("data", DatabaseProvider.Sqlite, "Data Source=config/banori.db");

        CreateFile("CoNfIg", "KeYs", "KeY-test.XmL");

        AssertResolves(DatabaseProvider.Sqlite, "Data Source=config/banori.db");
    }

    private void AssertResolves(DatabaseProvider provider, string connectionString)
    {
        var before = Snapshot();

        var result = ApplicationStoragePaths.ResolveConfigDirectory(ContentRoot, provider, connectionString);

        Assert.Equal(ConfigDirectory, result);
        Assert.Equal(before, Snapshot());
    }

    private void AssertMigrationRequired(string legacyName, DatabaseProvider provider, string connectionString)
    {
        var before = Snapshot();

        var error = Assert.Throws<InvalidOperationException>(() =>
                                                                 ApplicationStoragePaths
                                                                    .ResolveConfigDirectory(ContentRoot, provider,
                                                                              connectionString));

        Assert.Contains(Path.Combine(ContentRoot, legacyName), error.Message);
        Assert.Contains(ConfigDirectory, error.Message);
        Assert.Contains("迁移", error.Message);
        Assert.Equal(before, Snapshot());
    }

    private void CreateFile(params string[] segments)
    {
        var path = Path.Combine(ContentRoot, Path.Combine(segments));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 只需检测文件存在；不打开数据库，也不生成或读取真实 Data Protection keys。
        File.WriteAllText(path, "ApplicationStoragePaths test fixture: " + string.Join('/', segments));
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(ContentRoot, "*", SearchOption.AllDirectories)
                                            .Select(path => Directory.Exists(path)
                                                        ? "directory:" + Path.GetRelativePath(ContentRoot, path)
                                                        : "file:" + Path.GetRelativePath(ContentRoot, path) + ":" +
                                                          File.ReadAllText(path))
                                            .OrderBy(entry => entry, StringComparer.Ordinal)
                                            .ToArray();

    private static string SqliteConnectionString(string dataSource) =>
        new SqliteConnectionStringBuilder { DataSource = dataSource }.ToString();

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Requires Windows case-insensitive filesystem path handling.";
        }
    }
}
