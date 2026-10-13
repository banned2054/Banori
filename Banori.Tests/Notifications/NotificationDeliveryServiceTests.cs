using Banori.Data;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Integrations.Notifications;
using Banori.Tests.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Notifications;

/// <summary>
///     通知投递服务验收：按渠道独立发送与记录、退避重试、重试用尽置 Failed 与人工重试。
///     使用替身发送器与真实 SQLite 临时库，不触发真实网络通知。
/// </summary>
public sealed class NotificationDeliveryServiceTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeliverPending_SendsToEachEnabledChannelAndRecordsDelivered()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync();
        await harness.EnableGotifyAsync();
        var telegramId = await harness.SeedDeliveryAsync(NotificationChannel.Telegram);
        var gotifyId   = await harness.SeedDeliveryAsync(NotificationChannel.Gotify);

        await harness.Service.DeliverPendingAsync();

        var telegramCall = Assert.Single(harness.Telegram.Calls);
        Assert.Equal("42", telegramCall.ChatId);
        Assert.Equal("bot-token", telegramCall.BotToken);
        Assert.Contains("下载开始", telegramCall.Text);
        Assert.Contains("正文内容", telegramCall.Text);
        var gotifyCall = Assert.Single(harness.Gotify.Calls);
        Assert.Equal("下载开始", gotifyCall.Title);
        Assert.Equal("正文内容", gotifyCall.Message);

        var telegramRow = await harness.GetDeliveryAsync(telegramId);
        Assert.Equal(NotificationDeliveryStatus.Delivered, telegramRow.Status);
        Assert.Equal(1, telegramRow.AttemptCount);
        Assert.Equal(Timestamp, telegramRow.DeliveredAtUtc);
        var gotifyRow = await harness.GetDeliveryAsync(gotifyId);
        Assert.Equal(NotificationDeliveryStatus.Delivered, gotifyRow.Status);
    }

    [Fact]
    public async Task DeliverPending_ChannelFailure_RecordedIndependentlyWithoutTouchingOtherChannel()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync();
        await harness.EnableGotifyAsync();
        harness.Telegram.SendException = new NotificationSendException("模拟网络中断");
        var telegramId = await harness.SeedDeliveryAsync(NotificationChannel.Telegram);
        var gotifyId   = await harness.SeedDeliveryAsync(NotificationChannel.Gotify);

        await harness.Service.DeliverPendingAsync();

        var telegramRow = await harness.GetDeliveryAsync(telegramId);
        Assert.Equal(NotificationDeliveryStatus.Pending, telegramRow.Status);
        Assert.Equal(1, telegramRow.AttemptCount);
        Assert.Contains("模拟网络中断", telegramRow.LastError);
        // 第 1 次失败后按指数退避等待 1 分钟。
        Assert.Equal(Timestamp.AddMinutes(1), telegramRow.NextAttemptAtUtc);
        var gotifyRow = await harness.GetDeliveryAsync(gotifyId);
        Assert.Equal(NotificationDeliveryStatus.Delivered, gotifyRow.Status);
        Assert.Single(harness.Gotify.Calls);
    }

    [Fact]
    public async Task DeliverPending_ChannelDisabled_SkipsAndKeepsPendingWithoutAttempts()
    {
        using var harness = await CreateHarnessAsync();
        // 只启用 Telegram；Gotify 未配置。
        await harness.EnableTelegramAsync();
        var telegramId = await harness.SeedDeliveryAsync(NotificationChannel.Telegram);
        var gotifyId   = await harness.SeedDeliveryAsync(NotificationChannel.Gotify);

        await harness.Service.DeliverPendingAsync();

        var telegramRow = await harness.GetDeliveryAsync(telegramId);
        Assert.Equal(NotificationDeliveryStatus.Delivered, telegramRow.Status);
        var gotifyRow = await harness.GetDeliveryAsync(gotifyId);
        Assert.Equal(NotificationDeliveryStatus.Pending, gotifyRow.Status);
        Assert.Equal(0, gotifyRow.AttemptCount);
        Assert.Null(gotifyRow.NextAttemptAtUtc);
        Assert.Empty(harness.Gotify.Calls);
    }

    [Fact]
    public async Task DeliverPending_WaitingBackoff_SkipsUntilDueTimeArrives()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync();
        var deliveryId = await harness.SeedDeliveryAsync(NotificationChannel.Telegram,
                                                         nextAttemptAtUtc : Timestamp.AddMinutes(4));

        await harness.Service.DeliverPendingAsync();
        Assert.Empty(harness.Telegram.Calls);
        Assert.Equal(0, (await harness.GetDeliveryAsync(deliveryId)).AttemptCount);

        // 退避到期后同一轮询继续投递（用时间推进表达下一次轮询）。
        harness.Clock.Advance(TimeSpan.FromMinutes(4));
        await harness.Service.DeliverPendingAsync();

        Assert.Single(harness.Telegram.Calls);
        Assert.Equal(NotificationDeliveryStatus.Delivered, (await harness.GetDeliveryAsync(deliveryId)).Status);
    }

    [Fact]
    public async Task DeliverPending_RetriesExhausted_MarksFailedAndManualRetryRequeues()
    {
        using var harness = await CreateHarnessAsync();
        await harness.EnableTelegramAsync();
        harness.Telegram.SendException = new NotificationSendException("渠道持续不可用");
        var deliveryId = await harness.SeedDeliveryAsync(NotificationChannel.Telegram,
                                                         attemptCount : NotificationDeliveryService.MaxAttempts - 1);

        // 最后一次自动重试仍失败：置为 Failed 终态并清除退避时间（异常发送不进入成功调用记录）。
        await harness.Service.DeliverPendingAsync();
        var failedRow = await harness.GetDeliveryAsync(deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Failed, failedRow.Status);
        Assert.Equal(NotificationDeliveryService.MaxAttempts, failedRow.AttemptCount);
        Assert.Null(failedRow.NextAttemptAtUtc);
        Assert.Contains("渠道持续不可用", failedRow.LastError);
        Assert.Empty(harness.Telegram.Calls);

        // 终态不再自动投递；人工重试后清零计数重新入队，渠道恢复后成功送达。
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.Service.DeliverPendingAsync();
        Assert.Empty(harness.Telegram.Calls);

        await harness.Service.RetryAsync(deliveryId);
        var requeuedRow = await harness.GetDeliveryAsync(deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Pending, requeuedRow.Status);
        Assert.Equal(0, requeuedRow.AttemptCount);
        Assert.Null(requeuedRow.NextAttemptAtUtc);
        Assert.Null(requeuedRow.LastError);

        harness.Telegram.SendException = null;
        await harness.Service.DeliverPendingAsync();
        Assert.Single(harness.Telegram.Calls);
        Assert.Equal(NotificationDeliveryStatus.Delivered, (await harness.GetDeliveryAsync(deliveryId)).Status);
    }

    [Fact]
    public async Task RetryAsync_PendingDelivery_Rejected()
    {
        using var harness = await CreateHarnessAsync();
        var       id      = await harness.SeedDeliveryAsync(NotificationChannel.Telegram);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.RetryAsync(id));
    }

    /// <summary>钉死重试策略常量：常量偏离设计（如有人调整上限）时此测试必须显式更新。</summary>
    [Fact]
    public void RetryPolicyConstants_ArePinnedToDesign()
    {
        Assert.Equal(10, NotificationDeliveryService.MaxAttempts);
        Assert.Equal(30, NotificationDeliveryService.BackoffCapMinutes);
    }

    /// <summary>钉死指数退避曲线：第 n 次失败后等待 2^(n-1) 分钟，封顶 30 分钟。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(7, 30)]
    [InlineData(10, 30)]
    public void BackoffMinutes_FollowsExponentialCurveCappedAt30(int attemptCount, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, NotificationDeliveryService.BackoffMinutes(attemptCount));
    }

    [Fact]
    public async Task DeliverPending_MissingToken_FailsWithConfigHintInsteadOfSending()
    {
        using var harness = await CreateHarnessAsync();
        // 绕过保存校验直接写入启用状态：模拟 Token 丢失的半配置渠道。
        await harness.Store.SetAsync(ManageTelegramSettings.EnabledKey, "true");
        await harness.Store.SetAsync(ManageTelegramSettings.ChatIdKey, "42");
        await harness.Store.SetAsync(ManageTelegramSettings.EventsKey,
                                     NotificationEvents.Format(NotificationEvents.All));
        var deliveryId = await harness.SeedDeliveryAsync(NotificationChannel.Telegram);

        await harness.Service.DeliverPendingAsync();

        Assert.Empty(harness.Telegram.Calls);
        var row = await harness.GetDeliveryAsync(deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains("Bot Token 未配置", row.LastError);
    }

    private static async Task<DeliveryHarness> CreateHarnessAsync()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.Notify.Tests.");
        var services  = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "notify.db")};Pooling=False"));
        var clock = new MutableTimeProvider(Timestamp);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<MemoryApplicationSettingStore>();
        services.AddSingleton<IApplicationSettingStore>(provider =>
                                                            provider
                                                               .GetRequiredService<MemoryApplicationSettingStore>());
        services.AddDataProtection();
        services.AddNotificationTestFakes();
        // 与应用及下载/订阅测试保持一致的 Identity 模型配置，避免 ApplicationDbContext 模型缓存顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new DeliveryHarness(directory, clock, provider,
                                          provider.GetRequiredService<NotificationDeliveryService>(),
                                          provider.GetRequiredService<FakeTelegramSender>(),
                                          provider.GetRequiredService<FakeGotifySender>(),
                                          provider.GetRequiredService<MemoryApplicationSettingStore>());
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return harness;
    }

    private sealed class MutableTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        private DateTimeOffset _now = timestamp;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta)
        {
            _now += delta;
        }
    }

    private sealed class DeliveryHarness(
        DirectoryInfo                 directory,
        MutableTimeProvider           clock,
        ServiceProvider               provider,
        NotificationDeliveryService   service,
        FakeTelegramSender            telegram,
        FakeGotifySender              gotify,
        MemoryApplicationSettingStore store) : IDisposable
    {
        public MutableTimeProvider           Clock    { get; } = clock;
        public NotificationDeliveryService   Service  { get; } = service;
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

        public Task EnableTelegramAsync()
        {
            var manage = new ManageTelegramSettings(Store, Telegram);
            return manage.SaveAsync(new TelegramSettings("42", null, true, NotificationEvents.All),
                                    "bot-token");
        }

        public Task EnableGotifyAsync()
        {
            var manage = new ManageGotifySettings(Store, Gotify);
            return manage.SaveAsync(new GotifySettings("http://gotify.test", null, true, NotificationEvents.All),
                                    "app-token");
        }

        public async Task<Guid> SeedDeliveryAsync(NotificationChannel channel,
                                                  NotificationEventType eventType =
                                                      NotificationEventType.DownloadStarted,
                                                  int attemptCount = 0, DateTimeOffset? nextAttemptAtUtc = null)
        {
            await using var db = await CreateDbContextAsync();
            var delivery = new NotificationDelivery
            {
                Id           = Guid.NewGuid(), Channel        = channel, EventType = eventType,
                Title        = "下载开始", Message                = "正文内容", Status     = NotificationDeliveryStatus.Pending,
                AttemptCount = attemptCount, NextAttemptAtUtc = nextAttemptAtUtc,
                CreatedAtUtc = Clock.GetUtcNow()
            };
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync();
            return delivery.Id;
        }

        public async Task<NotificationDelivery> GetDeliveryAsync(Guid id)
        {
            await using var db = await CreateDbContextAsync();
            return await db.NotificationDeliveries.AsNoTracking().SingleAsync(item => item.Id == id);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync() => provider
                                                                   .GetRequiredService<
                                                                        IDbContextFactory<ApplicationDbContext>>()
                                                                   .CreateDbContextAsync();
    }
}

/// <summary>事件开关解析与存储格式的纯逻辑回归。</summary>
public sealed class NotificationEventsTests
{
    [Fact]
    public void Parse_NullOrBlank_ReturnsAllEvents()
    {
        Assert.Equal(NotificationEvents.All, NotificationEvents.Parse(null));
        Assert.Equal(NotificationEvents.All, NotificationEvents.Parse("  "));
    }

    [Fact]
    public void Parse_IsCaseInsensitiveAndIgnoresUnknownNames()
    {
        var events = NotificationEvents.Parse("downloadFailed, Unknown, DownloadStarted");

        Assert.Equal([NotificationEventType.DownloadStarted, NotificationEventType.DownloadFailed], events);
    }

    [Fact]
    public void Format_IsStableEnumOrderRoundtrip()
    {
        var stored = NotificationEvents.Format([
            NotificationEventType.DownloadFailed,
            NotificationEventType.DownloadStarted
        ]);

        Assert.Equal("DownloadStarted,DownloadFailed", stored);
        Assert.Equal(NotificationEvents.All, NotificationEvents.Parse(stored));
    }

    [Fact]
    public void FromFlags_MapsCheckboxesOntoEvents()
    {
        Assert.Empty(NotificationEvents.FromFlags(false, false));
        Assert.Equal([NotificationEventType.DownloadFailed],
                     NotificationEvents.FromFlags(false, true));
        Assert.Equal([NotificationEventType.DownloadStarted, NotificationEventType.DownloadFailed],
                     NotificationEvents.FromFlags(true, true));
    }

    [Fact]
    public void Format_EmptySet_StoresNoneSentinel()
    {
        Assert.Equal("None", NotificationEvents.Format([]));
    }

    [Fact]
    public void Parse_NoneSentinel_ReturnsEmptySet()
    {
        Assert.Empty(NotificationEvents.Parse("None"));
        Assert.Empty(NotificationEvents.Parse(" none "));
    }

    [Fact]
    public void EmptySet_FormatParseRoundtrip_StaysEmptyInsteadOfFlippingToAll()
    {
        var roundtripped = NotificationEvents.Parse(NotificationEvents.Format([]));

        Assert.Empty(roundtripped);
    }
}
