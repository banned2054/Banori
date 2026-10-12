using MediaManager.Data;
using MediaManager.Data.Migrations;
using MediaManager.Features.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Settings;

public sealed class EfApplicationSettingStoreTests
{
    [Fact]
    public async Task Store_PersistsPlainAndProtectedValues()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Settings.Tests.");

        try
        {
            var databasePath = Path.Combine(testDirectory.FullName, "settings.db");
            var keyDirectory = Directory.CreateDirectory(Path.Combine(testDirectory.FullName, "keys"));
            var timestamp    = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
            var services     = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection().PersistKeysToFileSystem(keyDirectory);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
            // 与 Program.cs 一致：按 provider 过滤迁移集，否则 MySQL 基线会被 SQLite 迁移器应用。
            services.AddDbContextFactory<ApplicationDbContext>(options =>
            {
                options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
                options.UseSqlite($"Data Source={databasePath};Pooling=False");
            });
            services.AddIdentityCore<ApplicationUser>(options =>
                                                          options.Stores.SchemaVersion =
                                                              IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            services.AddScoped<IApplicationSettingStore, EfApplicationSettingStore>();

            await using (var serviceProvider = services.BuildServiceProvider())
            {
                await using var scope = serviceProvider.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

                await using (var dbContext = await factory.CreateDbContextAsync())
                {
                    await dbContext.Database.MigrateAsync();
                }

                var store = scope.ServiceProvider.GetRequiredService<IApplicationSettingStore>();

                await store.SetAsync("General.Name", "家庭媒体库");
                await store.SetProtectedAsync("Integration.Token", "secret-token");

                Assert.Equal("家庭媒体库", await store.GetAsync("General.Name"));
                Assert.Equal("secret-token", await store.GetAsync("Integration.Token"));

                await using var verificationContext = await factory.CreateDbContextAsync();
                var plainSetting = await verificationContext.ApplicationSettings.FindAsync("General.Name");
                var protectedSetting = await verificationContext.ApplicationSettings.FindAsync("Integration.Token");

                Assert.NotNull(plainSetting);
                Assert.False(plainSetting.IsProtected);
                Assert.Equal("家庭媒体库", plainSetting.Value);
                Assert.Equal(timestamp, plainSetting.UpdatedAtUtc);

                Assert.NotNull(protectedSetting);
                Assert.True(protectedSetting.IsProtected);
                Assert.NotEqual("secret-token", protectedSetting.Value);
                Assert.Equal(timestamp, protectedSetting.UpdatedAtUtc);
            }

            await using (var restartedProvider = services.BuildServiceProvider())
            {
                await using var restartedScope = restartedProvider.CreateAsyncScope();
                var restartedStore = restartedScope.ServiceProvider.GetRequiredService<IApplicationSettingStore>();

                Assert.Equal("家庭媒体库", await restartedStore.GetAsync("General.Name"));
                Assert.Equal("secret-token", await restartedStore.GetAsync("Integration.Token"));
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
