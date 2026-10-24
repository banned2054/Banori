using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>
///     可步进时钟 + 生产默认时间策略的对账 harness（原第二阶段调度测试专用，供需要真实窗口语义的
///     阶段测试共用）：RoundAsync = 推进 1 秒再对账（正常采样节奏）；
///     ReconcileNowAsync = 同一时刻立即对账（模拟脉冲风暴）。时钟与 <see cref="StepTimeProvider" /> 共享。
/// </summary>
internal sealed class SchedulingHarness(
    ServiceProvider               provider,
    DirectoryInfo                 directory,
    FakeTorrentStore              store,
    FakeQbittorrentSessionFactory factory,
    DownloadService               service,
    DownloadIntakeService         intake,
    StepTimeProvider              time) : IDisposable
{
    private const string DownloadRoot = "/downloads";

    public IServiceProvider              Provider { get; } = provider;
    public FakeTorrentStore              Store    { get; } = store;
    public FakeQbittorrentSessionFactory Factory  { get; } = factory;
    public StepTimeProvider              Time     { get; } = time;

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

    public static async Task<SchedulingHarness> CreateAsync(DownloadTimingPolicy? timing = null)
    {
        timing ??= DownloadTimingPolicy.Default;
        var directory = Directory.CreateTempSubdirectory("Banori.Scheduling.Tests.");
        var services  = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "scheduling.db")};Pooling=False"));
        var time = new StepTimeProvider();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(timing);
        services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
        var store = new FakeTorrentStore();
        services.AddSingleton(store);
        var factory = new FakeQbittorrentSessionFactory(store);
        services.AddSingleton(factory);
        services.AddSingleton<IQbittorrentSessionFactory>(factory);
        services.AddSingleton<IFeedSourceReader>(new FakeFeedSourceReader());
        services.AddSingleton<ITorrentFileSource>(new FakeTorrentFileSource(_ => TestTorrentBytes.Create()));
        services.AddSingleton<ISubjectMetadataProvider, FixedSubjectProvider>();
        services.AddScoped<SubjectMetadataCache>();
        services.AddSingleton<ISubjectLinkResolver, NullSubjectLinkResolver>();
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddNotificationTestFakes();
        services.AddSingleton<AniParser>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        services.AddScoped<ManageSelectionRuleSettings>();
        services.AddScoped<ISelectionRuleProvider, SettingsSelectionRuleProvider>();
        // worker 每轮先恢复同集选择，其依赖与下载服务一并注册。
        services.AddSingleton<SelectionGroupGate>();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime());
        services.AddScoped<CandidateSelectionService>();
        services.AddScoped<NamingTemplateStore>();
        services.AddSingleton<DownloadCoordinationGate>();
        services.AddScoped<DownloadDirectorySettingsStore>();
        services.AddSingleton<DownloadFileClassificationPolicy>();
        services.AddScoped<DownloadFileService>();
        services.AddScoped<DownloadService>();
        services.AddScoped<DownloadIntakeService>();
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new SchedulingHarness(provider, directory, store, factory,
                                            provider.GetRequiredService<DownloadService>(),
                                            provider.GetRequiredService<DownloadIntakeService>(), time);
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.MediaSubjects.Add(new MediaSubject
        {
            Id          = 42, Name = "Scheduling Subject", NameCn = "调度作品", Platform = "TV",
            CachedAtUtc = DateTimeOffset.Parse("2026-10-20T08:00:00Z")
        });
        await db.SaveChangesAsync();
        var settingsService =
            new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(), factory);
        await settingsService.SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", DownloadRoot,
                                                                true), "secret");
        await new NamingTemplateStore(provider.GetRequiredService<IApplicationSettingStore>()).SaveAsync("{title}");
        await new NamingTemplateStore(provider.GetRequiredService<IApplicationSettingStore>())
           .SaveFileTemplateAsync("{title} E{episode}");
        // 简日优先规则：让同集上传的简日挑战者能在登记裁决中胜出（替换路径测试依赖）。
        await new ManageSelectionRuleSettings(provider.GetRequiredService<IApplicationSettingStore>(),
                                              NullLogger<ManageSelectionRuleSettings>.Instance)
           .SaveAsync(SelectionRuleConfig.CreateDefault() with
            {
                GroupOrder = ["喵萌奶茶屋"], LanguageOrder = [EnumLanguage.JpSc, EnumLanguage.Sc]
            });
        return harness;
    }

    public Task ReconcileNowAsync() => service.ReconcileAsync();

    /// <summary>推进一个采样间隔（1 秒）再对账：驱动需要时间流逝的正常流程。</summary>
    public async Task RoundAsync()
    {
        time.Advance(TimeSpan.FromSeconds(1));
        await service.ReconcileAsync();
    }

    public async Task AdvanceAsync(TimeSpan by)
    {
        time.Advance(by);
        await service.ReconcileAsync();
    }

    public Task<Guid> CreateUploadAsync(byte[] torrentBytes) =>
        intake.CreateFromUploadAsync(torrentBytes, null, 42);

    public async Task<DownloadTask> GetTaskAsync(Guid taskId)
    {
        await using var db = await CreateDbContextAsync();
        return await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
    }

    public Task<ApplicationDbContext> CreateDbContextAsync() =>
        Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();

    private sealed class FixedSubjectProvider : ISubjectMetadataProvider
    {
        public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubjectMetadata(subjectId, "Scheduling Subject", "调度作品", "TV",
                                                "2026-01-01", null,
                                                DateTimeOffset.Parse("2026-10-20T08:00:00Z")));
    }
}
