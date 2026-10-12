using Banned.AniParser;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Notifications;

/// <summary>
///     下载闭环的通知钩子验收：提交成功入队下载开始、失败入队下载失败，
///     投递意图与任务状态在同一个本地事务落库；渠道未配置或未订阅时不产生投递行。
/// </summary>
public sealed class NotificationHookTests
{
    private const string NamingV1 = "测试作品 E01 [Sc]";

    private static readonly DateTimeOffset Timestamp = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reconcile_SubmitSuccess_EnqueuesDownloadStartedAndDelivers()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync(NotificationEvents.All);
        var taskId = await harness.SeedAndCreateAsync();

        await harness.DownloadService.ReconcileAsync();
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);

        await using var db       = await harness.CreateDbContextAsync();
        var             delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationChannel.Telegram, delivery.Channel);
        Assert.Equal(NotificationEventType.DownloadStarted, delivery.EventType);
        Assert.Equal("下载开始", delivery.Title);
        // 内容使用作品名与命名快照，而非内部 ID。
        Assert.Contains("测试作品 第1集", delivery.Message);
        Assert.Contains(NamingV1, delivery.Message);

        // 投递服务把入队内容真实发给渠道。
        await harness.DeliveryService.DeliverPendingAsync();
        var call = Assert.Single(harness.Telegram.Calls);
        Assert.Contains(NamingV1, call.Text);
    }

    [Fact]
    public async Task Reconcile_SubmitRejected_EnqueuesDownloadFailedForBothSubscribedChannels()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync(NotificationEvents.All);
        await harness.EnableGotifyAsync(NotificationEvents.All);
        harness.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        var taskId = await harness.SeedAndCreateAsync();

        await harness.DownloadService.ReconcileAsync();
        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Equal("提交", task.FailedStep);

        await using var db         = await harness.CreateDbContextAsync();
        var             deliveries = await db.NotificationDeliveries.AsNoTracking().ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal(NotificationEventType.DownloadFailed, delivery.EventType);
            Assert.Contains("在「提交」步骤失败", delivery.Message);
            Assert.Contains("下载器拒绝该种子", delivery.Message);
        });

        await harness.DeliveryService.DeliverPendingAsync();
        Assert.Single(harness.Telegram.Calls);
        Assert.Single(harness.Gotify.Calls);
    }

    [Fact]
    public async Task Reconcile_NoChannelConfigured_WritesNoDeliveries()
    {
        using var harness = await CreateHarnessAsync();
        var       taskId  = await harness.SeedAndCreateAsync();

        await harness.DownloadService.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.NotificationDeliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Reconcile_ChannelNotSubscribedToStarted_WritesNoStartedDelivery()
    {
        using var harness = await CreateHarnessAsync();
        // Telegram 只订阅下载失败：提交成功不产生投递行。
        await harness.EnableTelegramAsync([NotificationEventType.DownloadFailed]);
        await harness.SeedAndCreateAsync();

        await harness.DownloadService.ReconcileAsync();

        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.NotificationDeliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Reconcile_NotificationSettingsBroken_DownloadStillSucceedsWithoutDeliveries()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync(NotificationEvents.All);
        // 只让通知相关的设置键读取失败：入队异常必须被吞掉，下载主流程照常。
        harness.SettingsStore.ThrowOnGetKeyPrefix = "Notification.";
        var taskId = await harness.SeedAndCreateAsync();

        await harness.DownloadService.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.NotificationDeliveries.AsNoTracking().ToListAsync());
        harness.SettingsStore.ThrowOnGetKeyPrefix = null;
    }

    private static async Task<HookHarness> CreateHarnessAsync()
    {
        var directory = Directory.CreateTempSubdirectory("MediaManager.NotifyHook.Tests.");
        var services  = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "notifyhook.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Timestamp));
        services.AddSingleton<MemoryApplicationSettingStore>();
        services.AddSingleton<IApplicationSettingStore>(provider =>
                                                            provider
                                                               .GetRequiredService<MemoryApplicationSettingStore>());
        var store = new FakeTorrentStore();
        services.AddSingleton(store);
        services.AddSingleton<FakeQbittorrentSessionFactory>();
        services.AddSingleton<IQbittorrentSessionFactory>(provider =>
                                                              provider
                                                                 .GetRequiredService<FakeQbittorrentSessionFactory>());
        services.AddSingleton<ITorrentFileSource>(_ => new FakeTorrentFileSource(uri =>
                                                      TestTorrentBytes
                                                         .Create(Path.GetFileNameWithoutExtension(uri.Segments
                                                                     [^1]))));
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddDataProtection();
        services.AddNotificationTestFakes();
        services.AddSingleton<IFeedSourceReader>(new FakeFeedSourceReader());
        services.AddSingleton<AniParser>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        services.AddScoped<ManageSelectionRuleSettings>();
        services.AddScoped<ISelectionRuleProvider, SettingsSelectionRuleProvider>();
        services.AddScoped<NamingTemplateStore>();
        // 通知链路不触达 Subject 绑定解析，空实现只满足 DownloadService 构造签名。
        services.AddSingleton<ISubjectMetadataProvider>(NullSubjectMetadataProvider.Instance);
        services.AddScoped<SubjectMetadataCache>();
        services.AddSingleton<ISubjectLinkResolver>(NullSubjectLinkResolver.Instance);
        services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        services.AddSingleton<DownloadCoordinationGate>();
        services.AddScoped<DownloadService>();
        services.AddScoped<DownloadIntakeService>();
        // 与应用及下载/订阅测试保持一致的 Identity 模型配置，避免 ApplicationDbContext 模型缓存顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new HookHarness(directory, provider, store,
                                      provider.GetRequiredService<FakeQbittorrentSessionFactory>(),
                                      provider.GetRequiredService<DownloadService>(),
                                      provider.GetRequiredService<DownloadIntakeService>(),
                                      provider.GetRequiredService<NotificationDeliveryService>(),
                                      provider.GetRequiredService<FakeTelegramSender>(),
                                      provider.GetRequiredService<FakeGotifySender>(),
                                      provider.GetRequiredService<MemoryApplicationSettingStore>());
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.MediaSubjects.Add(new MediaSubject
        {
            Id = 42, Name = "Test Subject", NameCn = "测试作品", Platform = "TV", CachedAtUtc = Timestamp
        });
        await db.SaveChangesAsync();

        // 启用下载器，让对账可以真实提交（到假下载器）。
        await new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(), harness.Factory)
           .SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", "/downloads", true), "secret");
        return harness;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class HookHarness(
        DirectoryInfo                 directory,
        ServiceProvider               provider,
        FakeTorrentStore              store,
        FakeQbittorrentSessionFactory factory,
        DownloadService               downloadService,
        DownloadIntakeService         intakeService,
        NotificationDeliveryService   deliveryService,
        FakeTelegramSender            telegram,
        FakeGotifySender              gotify,
        MemoryApplicationSettingStore settingsStore) : IDisposable
    {
        public FakeTorrentStore              Store           { get; } = store;
        public FakeQbittorrentSessionFactory Factory         { get; } = factory;
        public DownloadService               DownloadService { get; } = downloadService;
        public DownloadIntakeService         IntakeService   { get; } = intakeService;
        public NotificationDeliveryService   DeliveryService { get; } = deliveryService;
        public FakeTelegramSender            Telegram        { get; } = telegram;
        public FakeGotifySender              Gotify          { get; } = gotify;
        public MemoryApplicationSettingStore SettingsStore   { get; } = settingsStore;

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

        public Task EnableTelegramAsync(IReadOnlyList<NotificationEventType> events) =>
            new ManageTelegramSettings(SettingsStore, Telegram)
               .SaveAsync(new TelegramSettings("42", null, true, events), "bot-token");

        public Task EnableGotifyAsync(IReadOnlyList<NotificationEventType> events) =>
            new ManageGotifySettings(SettingsStore, Gotify)
               .SaveAsync(new GotifySettings("http://gotify.test", null, true, events), "app-token");

        /// <summary>播种一条优胜条目并创建下载任务，返回任务 ID。</summary>
        public async Task<Guid> SeedAndCreateAsync(string stableId = "item-1")
        {
            await using var db             = await CreateDbContextAsync();
            var             subscriptionId = Guid.NewGuid();
            db.MediaSubscriptions.Add(new MediaSubscription
            {
                Id        = subscriptionId, Name          = $"订阅-{stableId}", FeedUrlProtected = "unused",
                IsEnabled = false, RefreshIntervalMinutes = 30
            });
            var itemId = Guid.NewGuid();
            db.FeedItems.Add(new FeedItem
            {
                Id = itemId, SubscriptionId = subscriptionId, StableId = stableId, Title = "Test Anime - 01",
                TorrentUrl = $"https://mikanani.me/Download/{stableId}.torrent",
                FirstSeenAtUtc = Timestamp.AddHours(-1), Status = FeedItemStatus.Accepted,
                DecisionStatus = ReleaseDecisionStatus.Accepted, ParsedTitle = "Test Anime - 01",
                MediaType = "SingleEpisode", Episode = 1, Version = 1, Group = "S1",
                Language = "Sc", Resolution = "R1080P", SubjectId = 42, Reason = "测试决策。",
                NamingPreview = NamingV1, EvaluatedAtUtc = Timestamp
            });
            db.CandidateSelections.Add(new CandidateSelection
            {
                Id               = Guid.NewGuid(), SubjectId = 42, MediaType       = "SingleEpisode", Episode = 1,
                WinnerFeedItemId = itemId, RuleVersion       = 1, RankingBasis     = "测试优胜",
                NamingSnapshot   = NamingV1, IntentTaskId    = null, SelectedAtUtc = Timestamp
            });
            await db.SaveChangesAsync();
            return await IntakeService.CreateForFeedItemAsync(itemId);
        }

        public async Task<DownloadTask> GetTaskAsync(Guid taskId)
        {
            await using var db = await CreateDbContextAsync();
            return await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync() => provider
                                                                   .GetRequiredService<
                                                                        IDbContextFactory<ApplicationDbContext>>()
                                                                   .CreateDbContextAsync();
    }
}
