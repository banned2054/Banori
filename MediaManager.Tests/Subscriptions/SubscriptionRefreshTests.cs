using Banned.AniParser;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Subjects;
using MediaManager.Data;
using MediaManager.Data.Migrations;
using MediaManager.Features.Anime;
using MediaManager.Features.Dashboard;
using MediaManager.Features.Downloads;
using MediaManager.Features.FeedItems;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Tests.Downloads;
using MediaManager.Tests.Notifications;
using MediaManager.Workers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Subscriptions;

public sealed class SubscriptionRefreshTests
{
    [Fact]
    public async Task Refresh_PersistsDecisionAndDeduplicatesRepeatedFeedItem()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 12, 4, 0, 0, TimeSpan.Zero);
            var sourceItem = new FeedSourceItem("guid:test-1", "test-1",
                                                "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                                                new Uri("https://mikanani.me/Home/Episode/test-1"),
                                                new Uri("https://mikanani.me/Download/test-1.torrent"),
                                                timestamp.AddHours(-1));
            var services = CreateServices(testDirectory, timestamp, () => [sourceItem]);

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();

                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                var subscriptionId = await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "测试订阅",
                                                                             "https://mikanani.me/RSS/MyBangumi?token=secret",
                                                                             true, 30, 42));
                var queuedWorkId = await subscriptionService.RequestRefreshAsync(subscriptionId);
                Assert.Equal(queuedWorkId, await subscriptionService.RequestRefreshAsync(subscriptionId));
                var refreshService = scope.ServiceProvider.GetRequiredService<MikanRefreshService>();
                var first          = await refreshService.ProcessNextAsync();

                Assert.NotNull(first);
                Assert.Null(first.Error);
                Assert.Equal(1, first.AddedItems);
                var firstItems = await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync();
                var item       = Assert.Single(firstItems);
                Assert.Equal(FeedItemStatus.Accepted, item.Status);
                Assert.Equal(42, item.SubjectId);
                Assert.Contains("E12", item.NamingPreview);

                var subscription = Assert.Single(await subscriptionService.ListAsync());
                Assert.DoesNotContain("secret", subscription.FeedUrlDisplay);
                Assert.Contains("?•••", subscription.FeedUrlDisplay);
                var storedSubscription = await db.MediaSubscriptions.AsNoTracking().SingleAsync();
                Assert.DoesNotContain("secret", storedSubscription.FeedUrlProtected);

                var dashboard = await scope.ServiceProvider.GetRequiredService<DashboardQueries>().GetSnapshotAsync();
                Assert.Equal(1, dashboard.EnabledSubscriptions);
                Assert.Equal(1, dashboard.AcceptedItems);

                await subscriptionService.RequestRefreshAsync(subscriptionId);
                var second = await refreshService.ProcessNextAsync();
                Assert.NotNull(second);
                Assert.Equal(0, second.AddedItems);
                Assert.Equal(1, second.DuplicateItems);
                Assert.Single(await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync());
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_UsesFeedProvidedSourceSubjectIdWithoutPageResolution()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Anibt.FeedId.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 28, 4, 0, 0, TimeSpan.Zero);
            var sourceItem = new FeedSourceItem("guid:anibt-1", "rel_anibt-1",
                                                "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                                                new Uri("https://anibt.net/release/rel_anibt-1"),
                                                new Uri("https://anibt.net/api/torrent/rel_anibt-1.torrent"),
                                                timestamp.AddHours(-1),
                                                975);
            var resolver = new CountingSubjectLinkResolver(42);
            var services = CreateServices(testDirectory, timestamp, () => [sourceItem], resolver);

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();

                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "AniBT 订阅",
                                                                                    "https://anibt.net/rss/magnets.xml?limit=100",
                                                                                    true, 30, null));
                var refreshService = scope.ServiceProvider.GetRequiredService<MikanRefreshService>();
                var result         = await refreshService.ProcessNextAsync();

                Assert.NotNull(result);
                Assert.Null(result.Error);
                // feed 直接给出 bgmId：评估用它关联 Subject，且不触发来源页面解析。
                var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync());
                Assert.Equal(FeedItemStatus.Accepted, item.Status);
                Assert.Equal(975, item.SubjectId);
                Assert.Equal(0, resolver.CallCount);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_PassesSubscriptionProxyToReaderAndResolver()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Proxy.Passthrough.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
            var sourceItem = new FeedSourceItem("guid:proxy-1", "proxy-1",
                                                "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                                                new Uri("https://mikanani.me/Home/Episode/proxy-1"),
                                                new Uri("https://mikanani.me/Download/proxy-1.torrent"),
                                                timestamp.AddHours(-1));
            var resolver = new CountingSubjectLinkResolver(42);
            var services = CreateServices(testDirectory, timestamp, () => [sourceItem], resolver);

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();

                var proxyId = Guid.NewGuid();
                db.ProxyServers.Add(new ProxyServer
                {
                    Id           = proxyId,
                    Name         = "测试代理",
                    Protocol     = ProxyProtocol.Http,
                    Host         = "127.0.0.1",
                    Port         = 7890,
                    CreatedAtUtc = timestamp,
                    UpdatedAtUtc = timestamp
                });
                await db.SaveChangesAsync();

                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "代理订阅",
                                                                                    "https://mikanani.me/RSS/Bangumi?token=secret",
                                                                                    true, 30, null, proxyId));
                var result = await scope.ServiceProvider.GetRequiredService<MikanRefreshService>().ProcessNextAsync();

                Assert.NotNull(result);
                Assert.Null(result.Error);
                var feedReader = (FakeFeedReader)scope.ServiceProvider.GetRequiredService<IFeedSourceReader>();
                Assert.Equal(proxyId, feedReader.LastProxyId);
                // 无映射无 bgmId：评估走到页面解析档，解析器拿到同一代理并成功关联。
                Assert.Equal(1, resolver.CallCount);
                Assert.Equal(proxyId, resolver.LastProxyId);
                var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync());
                Assert.Equal(42, item.SubjectId);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_PrefersTitleMappingOverFeedProvidedSourceSubjectId()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Anibt.Mapping.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 28, 5, 0, 0, TimeSpan.Zero);
            const string title =
                "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]";
            var seededItem = new FeedSourceItem("guid:seed-1", "seed-1", title,
                                                new Uri("https://mikanani.me/Home/Episode/seed-1"),
                                                new Uri("https://mikanani.me/Download/seed-1.torrent"),
                                                timestamp.AddHours(-2));
            var hintItem = new FeedSourceItem("guid:hint-1", "hint-1", title,
                                              new Uri("https://anibt.net/release/hint-1"),
                                              new Uri("https://anibt.net/api/torrent/hint-1.torrent"),
                                              timestamp.AddHours(-1),
                                              975);
            var resolver = new CountingSubjectLinkResolver(42);
            var services = CreateServices(testDirectory, timestamp, () => [seededItem], resolver);

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();

                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                var refreshService      = scope.ServiceProvider.GetRequiredService<MikanRefreshService>();

                // 先以固定 Subject=100 评估该标题并写入映射；随后同标题条目即使带 bgmId 也应命中映射。
                await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "种子订阅",
                                                                                    "https://mikanani.me/RSS/Bangumi?token=secret",
                                                                                    true, 30, 100));
                Assert.NotNull(await refreshService.ProcessNextAsync());

                var feedReader =
                    (FakeFeedReader)scope.ServiceProvider.GetRequiredService<IFeedSourceReader>();
                feedReader.SetItems([hintItem]);
                var hintSubscriptionId =
                    await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "AniBT 提示源",
                                                            "https://anibt.net/rss/magnets.xml?limit=100",
                                                            true, 30, null));
                var result = await refreshService.ProcessNextAsync();

                Assert.NotNull(result);
                Assert.Null(result.Error);
                Assert.Equal(1, result.AddedItems);
                var items     = await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync();
                var hintEntry = items.Single(item => item.SubscriptionId == hintSubscriptionId);
                Assert.Equal(FeedItemStatus.Accepted, hintEntry.Status);
                Assert.Equal(100, hintEntry.SubjectId);
                Assert.Equal(0, resolver.CallCount);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Migrations_CreateP2TablesOnEmptyDatabase()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2.Migrations.Tests.");
        try
        {
            var             services = CreateServices(testDirectory, DateTimeOffset.UtcNow, () => []);
            await using var provider = services.BuildServiceProvider();
            var             factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db       = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();

            var tableNames = await db
                                  .Database
                                  .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table'")
                                  .ToListAsync();
            Assert.Contains("MediaSubscriptions", tableNames);
            Assert.Contains("FeedItems", tableNames);
            Assert.Contains("MediaSubjects", tableNames);
            Assert.Contains("RefreshWorkItems", tableNames);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_RecoversPendingItemLeftInDatabaseEvenWhenAbsentFromFeed()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.Recovery.Tests.");
        try
        {
            var             timestamp = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
            var             services  = CreateServices(testDirectory, timestamp, () => []);
            await using var provider  = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();

                await using (var db = await factory.CreateDbContextAsync())
                {
                    await db.Database.EnsureCreatedAsync();
                    var subscriptionId = await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "恢复测试",
                                                                                 "https://mikanani.me/RSS/MyBangumi?token=secret",
                                                                                 true, 30, null));
                    db.FeedItems.Add(new FeedItem
                    {
                        Id             = Guid.NewGuid(),
                        SubscriptionId = subscriptionId,
                        StableId       = "guid:interrupted",
                        Title =
                            "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                        Link           = "https://mikanani.me/Home/Episode/interrupted",
                        FirstSeenAtUtc = timestamp.AddHours(-2),
                        Status         = FeedItemStatus.Pending,
                        Reason         = "等待处理。"
                    });
                    await db.SaveChangesAsync();
                }

                await subscriptionService.RequestRefreshAsync((await subscriptionService.ListAsync()).Single().Id);
                var result = await scope.ServiceProvider.GetRequiredService<MikanRefreshService>()
                                        .ProcessNextAsync();

                Assert.NotNull(result);
                Assert.Equal(0, result.AddedItems);
                var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<FeedItemQueries>()
                                                    .ListAsync());
                Assert.Equal(FeedItemStatus.Accepted, item.Status);
                Assert.Equal(42, item.SubjectId);
                Assert.Contains("E12", item.NamingPreview);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task StartupRecovery_RestoresInterruptedRunningWorkForConsumption()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.Startup.Tests.");
        try
        {
            var             timestamp = new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero);
            var             services  = CreateServices(testDirectory, timestamp, () => []);
            await using var provider  = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                var urlProtector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                                        .CreateProtector("MediaManager.MediaSubscriptions.FeedUrl.v1");
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                var subscription = new MediaSubscription
                {
                    Id                     = Guid.NewGuid(),
                    Name                   = "启动恢复",
                    FeedUrlProtected       = urlProtector.Protect("https://mikanani.me/RSS/recovery"),
                    IsEnabled              = false,
                    RefreshIntervalMinutes = 30
                };
                db.MediaSubscriptions.Add(subscription);
                db.RefreshWorkItems.Add(new RefreshWorkItem
                {
                    Id             = Guid.NewGuid(),
                    SubscriptionId = subscription.Id,
                    Status         = RefreshWorkStatus.Running,
                    RequestedAtUtc = timestamp.AddMinutes(-10),
                    StartedAtUtc   = timestamp.AddMinutes(-9)
                });
                await db.SaveChangesAsync();

                var refreshService = scope.ServiceProvider.GetRequiredService<MikanRefreshService>();
                await refreshService.RecoverInterruptedWorksAsync();
                var result = await refreshService.ProcessNextAsync();

                Assert.NotNull(result);
                Assert.Null(result.Error);
                await using var verification = await factory.CreateDbContextAsync();
                var             work         = await verification.RefreshWorkItems.AsNoTracking().SingleAsync();
                Assert.Equal(RefreshWorkStatus.Succeeded, work.Status);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Worker_RetriesStartupRecoveryAfterFailureAndConsumesOnlyAfterSuccess()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.StartupRetry.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);
            var services  = CreateServices(testDirectory, timestamp, () => []);
            services.AddSingleton<RecoveryTestConsumer>();
            services.AddSingleton<IRefreshWorkConsumer>(sp => sp.GetRequiredService<RecoveryTestConsumer>());
            await using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var urlProtector = provider.GetRequiredService<IDataProtectionProvider>()
                                       .CreateProtector("MediaManager.MediaSubscriptions.FeedUrl.v1");
            Guid subscriptionId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                subscriptionId = Guid.NewGuid();
                db.MediaSubscriptions.Add(new MediaSubscription
                {
                    Id                     = subscriptionId,
                    Name                   = "恢复重试",
                    FeedUrlProtected       = urlProtector.Protect("https://mikanani.me/RSS/retry"),
                    IsEnabled              = false,
                    RefreshIntervalMinutes = 30
                });
                db.RefreshWorkItems.Add(new RefreshWorkItem
                {
                    Id             = Guid.NewGuid(),
                    SubscriptionId = subscriptionId,
                    Status         = RefreshWorkStatus.Running,
                    RequestedAtUtc = timestamp.AddMinutes(-10),
                    StartedAtUtc   = timestamp.AddMinutes(-9)
                });
                await db.SaveChangesAsync();
            }

            var testConsumer = provider.GetRequiredService<RecoveryTestConsumer>();
            var worker = new SubscriptionRefreshWorker(provider.GetRequiredService<IServiceScopeFactory>(),
                                                       new RefreshPulse(),
                                                       new ImmediateTimeProvider(timestamp),
                                                       NullLogger<SubscriptionRefreshWorker>.Instance);
            using var stopping = new CancellationTokenSource();
            await worker.StartAsync(stopping.Token);
            try
            {
                // 第一次恢复失败后，第二次恢复被挡在放行点：工作必须保持 Running，且不允许开始消费。
                await WaitUntilAsync(() =>
                                         Task.FromResult(testConsumer.BlockedBeforeSecondRecovery));
                Assert.Equal(0, testConsumer.ConsumeAttempts);
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var work = await db.RefreshWorkItems.AsNoTracking().SingleAsync();
                    Assert.Equal(RefreshWorkStatus.Running, work.Status);
                }

                // 放行第二次恢复后，遗留工作应被恢复为待执行并正常消费完成。
                testConsumer.ReleaseRecovery();
                await WaitUntilAsync(async () =>
                {
                    await using var db = await factory.CreateDbContextAsync();
                    var work = await db.RefreshWorkItems.AsNoTracking()
                                       .SingleAsync(item => item.SubscriptionId == subscriptionId);
                    return work.Status == RefreshWorkStatus.Succeeded;
                });
            }
            finally
            {
                await stopping.CancelAsync();
                try
                {
                    await worker.StopAsync(CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
            }

            Assert.Equal(2, testConsumer.RecoveryAttempts);
            var failedAt  = testConsumer.Events.IndexOf(RecoveryTestConsumer.FirstRecoveryFailed);
            var succeeded = testConsumer.Events.IndexOf(RecoveryTestConsumer.RecoverySucceeded);
            var consumed  = testConsumer.Events.IndexOf(RecoveryTestConsumer.ConsumptionStarted);
            Assert.True(failedAt >= 0 && failedAt < succeeded && succeeded < consumed,
                        "消费必须发生在恢复成功之后，事件顺序：" + string.Join(" → ", testConsumer.Events));
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Claim_DoesNotReclaimRunningWorkDuringNormalOperation()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.NoReclaim.Tests.");
        try
        {
            var             timestamp = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
            var             services  = CreateServices(testDirectory, timestamp, () => []);
            await using var provider  = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                var subscription = new MediaSubscription
                {
                    Id                     = Guid.NewGuid(),
                    Name                   = "运行中",
                    FeedUrlProtected       = "unused",
                    IsEnabled              = false,
                    RefreshIntervalMinutes = 30
                };
                db.MediaSubscriptions.Add(subscription);
                db.RefreshWorkItems.Add(new RefreshWorkItem
                {
                    Id             = Guid.NewGuid(),
                    SubscriptionId = subscription.Id,
                    Status         = RefreshWorkStatus.Running,
                    RequestedAtUtc = timestamp.AddHours(-2),
                    StartedAtUtc   = timestamp.AddHours(-2)
                });
                await db.SaveChangesAsync();

                var result = await scope.ServiceProvider.GetRequiredService<MikanRefreshService>()
                                        .ProcessNextAsync();
                Assert.Null(result);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_SubjectCacheHit_SkipsMikanPageDiscovery()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.Cache.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);
            FeedSourceItem[] firstFeed =
            [
                new("guid:cache-1", "cache-1",
                    "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                    new Uri("https://mikanani.me/Home/Episode/cache-1"),
                    new Uri("https://mikanani.me/Download/cache-1.torrent"), timestamp.AddHours(-1))
            ];
            FeedSourceItem[] secondFeed =
            [
                new("guid:cache-2", "cache-2",
                    "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                    new Uri("https://mikanani.me/Home/Episode/cache-2"),
                    new Uri("https://mikanani.me/Download/cache-2.torrent"), timestamp.AddMinutes(-30))
            ];
            var             resolver = new CountingSubjectLinkResolver(42);
            var             services = CreateServices(testDirectory, timestamp, () => firstFeed, resolver);
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();

                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                var refreshService      = scope.ServiceProvider.GetRequiredService<MikanRefreshService>();
                var subscriptionId = await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "缓存测试",
                                                                             "https://mikanani.me/RSS/MyBangumi?token=secret",
                                                                             true, 30, null));

                await subscriptionService.RequestRefreshAsync(subscriptionId);
                var first = await refreshService.ProcessNextAsync();
                Assert.NotNull(first);
                Assert.Equal(1, first.AddedItems);

                var feedReader = (FakeFeedReader)scope.ServiceProvider.GetRequiredService<IFeedSourceReader>();
                feedReader.SetItems(secondFeed);
                await subscriptionService.RequestRefreshAsync(subscriptionId);
                var second = await refreshService.ProcessNextAsync();
                Assert.NotNull(second);
                Assert.Equal(1, second.AddedItems);

                var items = await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync();
                Assert.Equal(2, items.Count);
                Assert.All(items, item =>
                {
                    Assert.Equal(FeedItemStatus.Accepted, item.Status);
                    Assert.Equal(42, item.SubjectId);
                });
                Assert.Equal(1, resolver.CallCount);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_EpisodeCorrectionKeepsOriginalSubjectInTitleMapping()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.Mapping.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 14, 4, 0, 0, TimeSpan.Zero);
            const string episode13Title =
                "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[13][1080p][WebRip][AVC_AAC][CHS][简体内嵌]";
            const string episode10Title =
                "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[10][1080p][WebRip][AVC_AAC][CHS][简体内嵌]";
            FeedSourceItem[] firstFeed =
            [
                new("guid:mapping-13", "mapping-13", episode13Title,
                    new Uri("https://mikanani.me/Home/Episode/mapping-13"),
                    new Uri("https://mikanani.me/Download/mapping-13.torrent"), timestamp.AddHours(-1))
            ];
            FeedSourceItem[] secondFeed =
            [
                new("guid:mapping-10", "mapping-10", episode10Title,
                    new Uri("https://mikanani.me/Home/Episode/mapping-10"),
                    new Uri("https://mikanani.me/Download/mapping-10.torrent"), timestamp.AddMinutes(-30))
            ];
            var             resolver = new CountingSubjectLinkResolver(467461);
            var             services = CreateServices(testDirectory, timestamp, () => firstFeed, resolver);
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();

                var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                var refreshService      = scope.ServiceProvider.GetRequiredService<MikanRefreshService>();
                // 订阅未固定 Subject：Subject 只能来自标题映射缓存或 Mikan 页面发现（这里固定返回 467461）。
                var subscriptionId = await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "标题映射测试",
                                                                             "https://mikanani.me/RSS/MyBangumi?token=secret",
                                                                             true, 30, null));
                var normalizedTitle = TitleNormalizer.Normalize(
                                                                scope.ServiceProvider
                                                                     .GetRequiredService<AniParser>()
                                                                     .Parse(episode13Title)!.Title);

                // 第 13 集先到：条目按集数修正规则落到 529431，标题映射必须保留页面发现的原始 Subject 467461。
                await subscriptionService.RequestRefreshAsync(subscriptionId);
                var first = await refreshService.ProcessNextAsync();
                Assert.NotNull(first);
                Assert.Equal(1, first.AddedItems);
                Assert.Equal(1, resolver.CallCount);

                await using (var verification = await factory.CreateDbContextAsync())
                {
                    var item = Assert.Single(await verification.FeedItems.AsNoTracking().ToListAsync());
                    Assert.Equal(FeedItemStatus.Accepted, item.Status);
                    Assert.Equal(529431, item.SubjectId);
                    Assert.Equal(13m, item.Episode);
                    var mapping = Assert.Single(await verification.SubjectTitleMappings.AsNoTracking().ToListAsync());
                    Assert.Equal(normalizedTitle, mapping.NormalizedTitle);
                    Assert.Equal(467461, mapping.SubjectId);
                    var subjectIds = await verification.MediaSubjects.AsNoTracking()
                                                       .Select(value => value.Id)
                                                       .ToListAsync();
                    Assert.Equal(2, subjectIds.Count);
                    Assert.Contains(467461, subjectIds);
                    Assert.Contains(529431, subjectIds);
                }

                // 第 10 集后到：同标题命中标题缓存、不再访问 Mikan 页面，条目使用映射中的原始 Subject。
                ((FakeFeedReader)scope.ServiceProvider.GetRequiredService<IFeedSourceReader>()).SetItems(secondFeed);
                await subscriptionService.RequestRefreshAsync(subscriptionId);
                var second = await refreshService.ProcessNextAsync();
                Assert.NotNull(second);
                Assert.Equal(1, second.AddedItems);
                Assert.Equal(1, resolver.CallCount);

                var items = await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync();
                Assert.Equal(2, items.Count);
                var episode10 = items.Single(value => value.Episode == 10);
                Assert.Equal(FeedItemStatus.Accepted, episode10.Status);
                Assert.Equal(467461, episode10.SubjectId);

                // 人工以原始 Subject 重评第 13 集：指定值优先并重新应用修正规则，映射不被修正结果改写。
                Guid episode13ItemId;
                await using (var verification = await factory.CreateDbContextAsync())
                {
                    episode13ItemId = (await verification.FeedItems.AsNoTracking()
                                                         .SingleAsync(value => value.StableId == "guid:mapping-13")).Id;
                }

                var evaluator = scope.ServiceProvider.GetRequiredService<FeedItemEvaluator>();
                await evaluator.ReevaluateAsync(episode13ItemId, 467461);
                var reevaluated = await scope.ServiceProvider.GetRequiredService<FeedItemQueries>().ListAsync();
                var episode13   = reevaluated.Single(value => value.Episode == 13);
                Assert.Equal(FeedItemStatus.Accepted, episode13.Status);
                Assert.Equal(529431, episode13.SubjectId);
                await using (var verification = await factory.CreateDbContextAsync())
                {
                    var mapping = await verification.SubjectTitleMappings.AsNoTracking().SingleAsync();
                    Assert.Equal(467461, mapping.SubjectId);
                }
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Refresh_RssFailureStillRecoversPendingItemsAndReportsFetchFailure()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.FetchFail.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
            var services = CreateServices(testDirectory, timestamp,
                                          () => throw new HttpRequestException("网络不可达"));
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var  factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                var  subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                Guid subscriptionId;
                await using (var db = await factory.CreateDbContextAsync())
                {
                    await db.Database.EnsureCreatedAsync();
                    subscriptionId = await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "抓取失败恢复",
                                                                             "https://mikanani.me/RSS/MyBangumi?token=secret",
                                                                             true, 30, null));
                    db.FeedItems.Add(new FeedItem
                    {
                        Id             = Guid.NewGuid(),
                        SubscriptionId = subscriptionId,
                        StableId       = "guid:pending-while-failing",
                        Title =
                            "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[12][1080p][WebRip][AVC_AAC][CHS][简体内嵌]",
                        Link           = "https://mikanani.me/Home/Episode/pending-while-failing",
                        FirstSeenAtUtc = timestamp.AddHours(-1),
                        Status         = FeedItemStatus.Pending,
                        Reason         = "等待处理。"
                    });
                    await db.SaveChangesAsync();
                }

                await subscriptionService.RequestRefreshAsync(subscriptionId);
                var result = await scope.ServiceProvider.GetRequiredService<MikanRefreshService>()
                                        .ProcessNextAsync();

                Assert.NotNull(result);
                Assert.Equal("无法连接 RSS 源。", result.Error);
                var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<FeedItemQueries>()
                                                    .ListAsync());
                Assert.Equal(FeedItemStatus.Accepted, item.Status);

                await using var verification = await factory.CreateDbContextAsync();
                var work = await verification.RefreshWorkItems.AsNoTracking()
                                             .SingleAsync(item => item.SubscriptionId == subscriptionId);
                Assert.Equal(RefreshWorkStatus.Failed, work.Status);
                Assert.Equal("无法连接 RSS 源。", work.Error);
                var subscription = await verification.MediaSubscriptions.AsNoTracking().SingleAsync();
                Assert.Equal("无法连接 RSS 源。", subscription.LastError);
                Assert.NotNull(subscription.NextRefreshAtUtc);
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task ManualRequestAndDueScheduling_CreateSingleActiveWork()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P2R.Enqueue.Tests.");
        try
        {
            var             timestamp = new DateTimeOffset(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);
            var             services  = CreateServices(testDirectory, timestamp, () => []);
            await using var provider  = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
            }

            var scope2 = provider.CreateAsyncScope();
            await using (scope2)
            {
                var subscriptionService = scope2.ServiceProvider.GetRequiredService<SubscriptionService>();
                var subscriptionId = await subscriptionService.SaveAsync(new SaveSubscriptionRequest(null, "并发入队",
                                                                             "https://mikanani.me/RSS/MyBangumi?token=secret",
                                                                             true, 30, null));

                for (var round = 0; round < 3; round++)
                {
                    await Task.WhenAll(subscriptionService.RequestRefreshAsync(subscriptionId),
                                       subscriptionService.RequestRefreshAsync(subscriptionId),
                                       subscriptionService.EnqueueDueRefreshesAsync());

                    await using var db = await factoryOf(provider).CreateDbContextAsync();
                    var activeCount = await db.RefreshWorkItems
                                              .CountAsync(item => item.SubscriptionId == subscriptionId &&
                                                                  (item.Status == RefreshWorkStatus.Pending ||
                                                                   item.Status == RefreshWorkStatus.Running));
                    Assert.Equal(1, activeCount);

                    // 消费掉本轮工作，让下一轮从无活动工作的状态开始。
                    var consumed = await scope2.ServiceProvider.GetRequiredService<MikanRefreshService>()
                                               .ProcessNextAsync();
                    Assert.NotNull(consumed);
                }
            }
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Save_HttpNonLoopback_Rejected()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.P4.HttpNonLoopback.Tests.");
        try
        {
            var             services            = CreateServices(testDirectory, DateTimeOffset.UtcNow, () => []);
            await using var provider            = services.BuildServiceProvider();
            await using var scope               = provider.CreateAsyncScope();
            var             subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();

            await Assert.ThrowsAsync<ArgumentException>(() => subscriptionService.SaveAsync(
                                                         new SaveSubscriptionRequest(null, "临时",
                                                             "http://example.com/rss.xml",
                                                             true, 30, null)));
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private static IDbContextFactory<ApplicationDbContext> factoryOf(IServiceProvider provider) =>
        provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待测试条件超时。");
            await Task.Delay(25);
        }
    }

    private static ServiceCollection CreateServices(DirectoryInfo directory, DateTimeOffset timestamp,
                                                    Func<IReadOnlyList<FeedSourceItem>> feedFactory,
                                                    ISubjectLinkResolver? resolver = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
                .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(directory.FullName, "keys")));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        // 与 Program.cs 一致：按 provider 过滤迁移集，否则 MySQL 基线会被 SQLite 迁移器应用。
        services.AddDbContextFactory<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "p2r.db")};Pooling=False");
        });
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<RefreshEnqueueGate>();
        services.AddSingleton<RefreshPulse>();
        services.AddSingleton<IFeedSourceReader>(new FakeFeedReader(feedFactory));
        services.AddSingleton(resolver ?? new CountingSubjectLinkResolver(42));
        services.AddSingleton<ISubjectMetadataProvider>(new FakeSubjectProvider(timestamp));
        services.AddScoped<SubjectMetadataCache>();
        services.AddSingleton<AniParser>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        services.AddSingleton<ISelectionRuleProvider, DefaultSelectionRuleProvider>();
        services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        // 择优后登记下载意图需要下载服务；未配置下载器时只落库选择，不会触达这些替身。
        services.AddScoped<IApplicationSettingStore, EfApplicationSettingStore>();
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddNotificationTestFakes();
        services.AddSingleton<ITorrentFileSource>(new FakeTorrentFileSource(_ =>
                                                                                throw new
                                                                                    InvalidOperationException("刷新测试不应下载种子。")));
        services.AddSingleton<IQbittorrentSessionFactory>(new FakeQbittorrentSessionFactory(new FakeTorrentStore()));
        services.AddScoped<NamingTemplateStore>();
        services.AddScoped<DownloadService>();
        services.AddScoped<DownloadIntakeService>();
        services.AddSingleton<SelectionGroupGate>();
        services.AddSingleton<DownloadCoordinationGate>();
        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddScoped<CandidateSelectionService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<MikanRefreshService>();
        services.AddScoped<FeedItemEvaluator>();
        services.AddScoped<FeedItemQueries>();
        services.AddScoped<DashboardQueries>();
        return services;
    }

    private sealed class FakeFeedReader(Func<IReadOnlyList<FeedSourceItem>> itemsFactory) : IFeedSourceReader
    {
        private IReadOnlyList<FeedSourceItem>? _items;

        /// <summary>最近一次读取请求携带的订阅代理 Id，供传递断言使用。</summary>
        public Guid? LastProxyId { get; private set; }

        public Task<IReadOnlyList<FeedSourceItem>>
            ReadAsync(Uri feedUri, Guid? proxyId = default, CancellationToken cancellationToken = default)
        {
            LastProxyId = proxyId;
            return Task.FromResult(_items ?? itemsFactory());
        }

        public void SetItems(IReadOnlyList<FeedSourceItem> items)
        {
            _items = items;
        }
    }

    private sealed class CountingSubjectLinkResolver(int? subjectId) : ISubjectLinkResolver
    {
        public int CallCount { get; private set; }

        /// <summary>最近一次解析请求携带的订阅代理 Id，供传递断言使用。</summary>
        public Guid? LastProxyId { get; private set; }

        public Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = default,
                                                CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastProxyId = proxyId;
            return Task.FromResult(subjectId);
        }
    }

    private sealed class FakeSubjectProvider(DateTimeOffset timestamp) : ISubjectMetadataProvider
    {
        public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubjectMetadata(subjectId, "Necronomico", "死灵子的宇宙恐怖秀", "TV",
                                                "2026-07-01", null, timestamp));
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    /// <summary>让 Worker 的退避等待立即到期，避免测试真实睡眠。</summary>
    private sealed class ImmediateTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime,
                                           TimeSpan      period) =>
            new ImmediateTimer(callback, state, dueTime);

        private sealed class ImmediateTimer : ITimer
        {
            public ImmediateTimer(TimerCallback callback, object? state, TimeSpan dueTime)
            {
                if (dueTime != Timeout.InfiniteTimeSpan && dueTime >= TimeSpan.Zero) Task.Run(() => callback(state));
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     首次启动恢复抛出临时错误并停在放行点，放行后委托真实服务的测试替身，
    ///     用于验证 Worker 在恢复成功前不消费、失败后重试。
    /// </summary>
    private sealed class RecoveryTestConsumer(IServiceScopeFactory scopeFactory) : IRefreshWorkConsumer
    {
        public const string FirstRecoveryFailed = "第一次恢复失败";
        public const string RecoverySucceeded   = "恢复成功";
        public const string ConsumptionStarted  = "开始消费";

        private const string WaitingForRelease = "等待恢复放行";

        private readonly TaskCompletionSource _recoveryGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Events           { get; } = [];
        public int          RecoveryAttempts { get; private set; }
        public int          ConsumeAttempts  { get; private set; }

        public bool BlockedBeforeSecondRecovery =>
            Events.Contains(WaitingForRelease) && !_recoveryGate.Task.IsCompleted;

        public async Task RecoverInterruptedWorksAsync(CancellationToken cancellationToken = default)
        {
            RecoveryAttempts++;
            if (RecoveryAttempts == 1)
            {
                Events.Add(FirstRecoveryFailed);
                throw new IOException("数据库暂时不可用（测试模拟的临时错误）。");
            }

            Events.Add(WaitingForRelease);
            await _recoveryGate.Task.WaitAsync(cancellationToken);
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MikanRefreshService>()
                       .RecoverInterruptedWorksAsync(cancellationToken);
            Events.Add(RecoverySucceeded);
        }

        public async Task<RefreshResult?> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            ConsumeAttempts++;
            Events.Add(ConsumptionStarted);
            await using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MikanRefreshService>()
                              .ProcessNextAsync(cancellationToken);
        }

        public void ReleaseRecovery()
        {
            _recoveryGate.TrySetResult();
        }
    }
}
