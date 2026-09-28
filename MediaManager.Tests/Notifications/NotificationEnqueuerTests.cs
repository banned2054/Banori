using MediaManager.Data;
using MediaManager.Features.Notifications;
using MediaManager.Features.Settings;
using MediaManager.Tests.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Notifications;

/// <summary>通知入队器回归：按渠道启用状态与事件订阅生成投递行，异常只记日志不外泄。</summary>
public sealed class NotificationEnqueuerTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryEnqueue_WritesRowsForEnabledChannelsRespectingEventSubscription()
    {
        using var harness = await CreateHarnessAsync();
        // Telegram 订阅全部事件；Gotify 只订阅下载开始。
        await harness.EnableTelegramAsync(NotificationEvents.All);
        await harness.EnableGotifyAsync([NotificationEventType.DownloadStarted]);

        await using (var db = await harness.CreateDbContextAsync())
        {
            await harness.Enqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted, "下载开始", "正文");
            await db.SaveChangesAsync();
        }

        await using var verifyDb   = await harness.CreateDbContextAsync();
        var             deliveries = await verifyDb.NotificationDeliveries.AsNoTracking().ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal(NotificationEventType.DownloadStarted, delivery.EventType);
            Assert.Equal(NotificationDeliveryStatus.Pending, delivery.Status);
            Assert.Equal("下载开始", delivery.Title);
            Assert.Equal(Timestamp, delivery.CreatedAtUtc);
        });
        Assert.Contains(deliveries, delivery => delivery.Channel == NotificationChannel.Telegram);
        Assert.Contains(deliveries, delivery => delivery.Channel == NotificationChannel.Gotify);

        // 未订阅事件不产生投递行。
        await using (var db = await harness.CreateDbContextAsync())
        {
            await harness.Enqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadFailed, "下载失败", "正文");
            await db.SaveChangesAsync();
        }

        await using var verifyDb2 = await harness.CreateDbContextAsync();
        var failedDeliveries = await verifyDb2.NotificationDeliveries.AsNoTracking()
                                              .Where(delivery =>
                                                         delivery.EventType ==
                                                         NotificationEventType.DownloadFailed)
                                              .ToListAsync();
        var telegramFailed = Assert.Single(failedDeliveries);
        Assert.Equal(NotificationChannel.Telegram, telegramFailed.Channel);
    }

    [Fact]
    public async Task TryEnqueue_NoChannelConfigured_WritesNothing()
    {
        using var harness = await CreateHarnessAsync();

        await using var db = await harness.CreateDbContextAsync();
        await harness.Enqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted, "下载开始", "正文");
        await db.SaveChangesAsync();

        await using var verifyDb = await harness.CreateDbContextAsync();
        Assert.Empty(await verifyDb.NotificationDeliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TryEnqueue_TruncatesOversizedContent()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync(NotificationEvents.All);

        await using var db = await harness.CreateDbContextAsync();
        await harness.Enqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted,
                                               new string('标', 500), new string('文', 3000));
        await db.SaveChangesAsync();

        await using var verifyDb = await harness.CreateDbContextAsync();
        var             delivery = await verifyDb.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationEnqueuer.TitleMaxLength, delivery.Title.Length);
        Assert.Equal(NotificationEnqueuer.MessageMaxLength, delivery.Message.Length);
    }

    [Fact]
    public async Task TryEnqueue_SettingsStoreThrows_SwallowsAndWritesNothing()
    {
        using var harness = await CreateHarnessAsync();
        harness.Store.ThrowOnGet = new InvalidOperationException("模拟设置存储故障。");

        await using var db = await harness.CreateDbContextAsync();
        await harness.Enqueuer.TryEnqueueAsync(db, NotificationEventType.DownloadStarted, "下载开始", "正文");
        await db.SaveChangesAsync();

        await using var verifyDb = await harness.CreateDbContextAsync();
        Assert.Empty(await verifyDb.NotificationDeliveries.AsNoTracking().ToListAsync());
    }

    private static async Task<EnqueuerHarness> CreateHarnessAsync()
    {
        var directory = Directory.CreateTempSubdirectory("MediaManager.Enqueue.Tests.");
        var services  = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "enqueue.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Timestamp));
        services.AddSingleton<MemoryApplicationSettingStore>();
        services.AddSingleton<IApplicationSettingStore>(provider =>
                                                            provider
                                                               .GetRequiredService<MemoryApplicationSettingStore>());
        services.AddNotificationTestFakes();
        // 与应用及下载/订阅测试保持一致的 Identity 模型配置，避免 ApplicationDbContext 模型缓存顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new EnqueuerHarness(directory, provider,
                                          provider.GetRequiredService<NotificationEnqueuer>(),
                                          provider.GetRequiredService<FakeTelegramSender>(),
                                          provider.GetRequiredService<FakeGotifySender>(),
                                          provider.GetRequiredService<MemoryApplicationSettingStore>());
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return harness;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }

    private sealed class EnqueuerHarness(
        DirectoryInfo                 directory,
        ServiceProvider               provider,
        NotificationEnqueuer          enqueuer,
        FakeTelegramSender            telegram,
        FakeGotifySender              gotify,
        MemoryApplicationSettingStore store) : IDisposable
    {
        public NotificationEnqueuer          Enqueuer { get; } = enqueuer;
        public FakeTelegramSender            Telegram { get; } = telegram;
        public FakeGotifySender              Gotify   { get; } = gotify;
        public MemoryApplicationSettingStore Store    { get; } = store;

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        public Task EnableTelegramAsync(IReadOnlyList<NotificationEventType> events)
        {
            var manage = new ManageTelegramSettings(Store, Telegram);
            return manage.SaveAsync(new TelegramSettings("42", null, true, events), "bot-token");
        }

        public Task EnableGotifyAsync(IReadOnlyList<NotificationEventType> events)
        {
            var manage = new ManageGotifySettings(Store, Gotify);
            return manage.SaveAsync(new GotifySettings("http://gotify.test", null, true, events), "app-token");
        }

        public Task<ApplicationDbContext> CreateDbContextAsync()
        {
            return provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        }
    }
}
