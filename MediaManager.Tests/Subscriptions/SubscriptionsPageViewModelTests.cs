using MediaManager.Components.Pages.Subscriptions;
using MediaManager.Data;
using MediaManager.Features.Proxies;
using MediaManager.Features.Subscriptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Subscriptions;

/// <summary>订阅页 ViewModel 的一次性 RSS 保存流程：草稿转请求、服务端派生名称与校验错误回显。</summary>
public sealed class SubscriptionsPageViewModelTests
{
    [Fact]
    public async Task SaveOneShot_Success_DraftConvertsToRequestAndShowsStatus()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.OneShotVM.Success.Tests.");
        try
        {
            var services = CreateServices(testDirectory);
            await using var provider = services.BuildServiceProvider();
            await using var scope    = provider.CreateAsyncScope();
            await EnsureCreatedAsync(scope);

            var vm  = CreateViewModel(scope);
            await vm.LoadAsync();
            vm.OneShotDraft.FeedUrl = "https://example.com/rss.xml";
            vm.OneShotDraft.Name    = "  临时源  ";
            await vm.SaveOneShotAsync();

            Assert.True(vm.OperationSucceeded, vm.StatusMessage);
            Assert.Equal("一次性订阅已添加，正在立即抓取。", vm.StatusMessage);
            // 保存成功后清空草稿，方便连续添加。
            Assert.Equal(string.Empty, vm.OneShotDraft.FeedUrl);
            Assert.Null(vm.OneShotDraft.Name);
            Assert.True(vm.Loaded);

            var view = Assert.Single(vm.Subscriptions);
            Assert.True(view.IsOneShot);
            Assert.Equal("临时源", view.Name);
            Assert.True(view.IsEnabled);
            Assert.Null(view.NextRefreshAtUtc);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task SaveOneShot_EmptyName_ServerDerivesNameFromHost()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.OneShotVM.Name.Tests.");
        try
        {
            var services = CreateServices(testDirectory);
            await using var provider = services.BuildServiceProvider();
            await using var scope    = provider.CreateAsyncScope();
            await EnsureCreatedAsync(scope);

            var vm  = CreateViewModel(scope);
            await vm.LoadAsync();
            vm.OneShotDraft.FeedUrl = "https://example.com/rss.xml";
            vm.OneShotDraft.Name    = "   ";
            await vm.SaveOneShotAsync();

            Assert.True(vm.OperationSucceeded, vm.StatusMessage);
            var view = Assert.Single(vm.Subscriptions);
            Assert.True(view.IsOneShot);
            Assert.Equal("一次性 RSS：example.com", view.Name);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task SaveOneShot_InvalidUrl_ShowsErrorAndDoesNotPersist()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.OneShotVM.Invalid.Tests.");
        try
        {
            var services = CreateServices(testDirectory);
            await using var provider = services.BuildServiceProvider();
            await using var scope    = provider.CreateAsyncScope();
            await EnsureCreatedAsync(scope);

            var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
            var vm                  = CreateViewModel(scope);
            await vm.LoadAsync();
            vm.OneShotDraft.FeedUrl = "http://example.com/rss.xml";
            vm.OneShotDraft.Name    = "临时";
            await vm.SaveOneShotAsync();

            Assert.False(vm.OperationSucceeded);
            // ArgumentException 的 Message 带 " (Parameter 'request')" 后缀，页面原样展示。
            Assert.Contains("RSS 地址必须是 HTTPS 绝对地址", vm.StatusMessage);
            Assert.False(vm.Saving);
            // 校验失败不落库，草稿保留用户输入便于修正后重试。
            Assert.Empty(await subscriptionService.ListAsync());
            Assert.Equal("http://example.com/rss.xml", vm.OneShotDraft.FeedUrl);
            Assert.Equal("临时", vm.OneShotDraft.Name);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Save_ScheduledAnibtUrl_IsAccepted()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.AnibtVM.Accepted.Tests.");
        try
        {
            var services = CreateServices(testDirectory);
            await using var provider = services.BuildServiceProvider();
            await using var scope    = provider.CreateAsyncScope();
            await EnsureCreatedAsync(scope);

            var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
            await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "AniBT 订阅",
                                                                "https://anibt.net/rss/magnets.xml?limit=100",
                                                                true, 30, null));

            var view = Assert.Single(await subscriptionService.ListAsync());
            Assert.False(view.IsOneShot);
            Assert.Equal("AniBT 订阅", view.Name);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Save_WithUnknownProxyId_Throws()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.ProxyVM.Unknown.Tests.");
        try
        {
            var services = CreateServices(testDirectory);
            await using var provider = services.BuildServiceProvider();
            await using var scope    = provider.CreateAsyncScope();
            await EnsureCreatedAsync(scope);

            var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "坏代理订阅",
                    "https://anibt.net/rss/magnets.xml?limit=100", true, 30, null, ProxyId: Guid.NewGuid())));

            Assert.Empty(await subscriptionService.ListAsync());
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Save_ScheduledUnsupportedHost_ShowsSourceRestriction()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.AnibtVM.Restricted.Tests.");
        try
        {
            var services = CreateServices(testDirectory);
            await using var provider = services.BuildServiceProvider();
            await using var scope    = provider.CreateAsyncScope();
            await EnsureCreatedAsync(scope);

            var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "未支持源",
                    "https://nyaa.si/?page=rss", true, 30, null)));

            Assert.Contains("定时订阅目前仅支持 Mikan（mikanani.me）与 AniBT（anibt.net）", exception.Message);
            Assert.Empty(await subscriptionService.ListAsync());
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private static SubscriptionsPageViewModel CreateViewModel(IServiceScope scope)
    {
        return new SubscriptionsPageViewModel(scope.ServiceProvider.GetRequiredService<SubscriptionService>(),
                                              scope.ServiceProvider.GetRequiredService<ProxyService>(),
                                              NullLogger<SubscriptionsPageViewModel>.Instance);
    }

    private static async Task EnsureCreatedAsync(IServiceScope scope)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    private static ServiceCollection CreateServices(DirectoryInfo directory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
                .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(directory.FullName, "keys")));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(2026, 9, 27, 4, 0, 0, TimeSpan.Zero)));
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "vm.db")};Pooling=False"));
        // 与应用及订阅测试保持一致的 Identity 模型配置：缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<RefreshEnqueueGate>();
        services.AddSingleton<RefreshPulse>();
        services.AddSingleton<ProxyService>();
        services.AddScoped<SubscriptionService>();
        return services;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }
}
