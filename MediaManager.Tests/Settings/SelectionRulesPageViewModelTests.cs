using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using MediaManager.Components.Pages.Settings;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Subjects;
using MediaManager.Data;
using MediaManager.Features.Anime;
using MediaManager.Features.Downloads;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Tests.Downloads;
using MediaManager.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Settings;

/// <summary>
///     择优规则页 ViewModel 的加载、保存与预览回归：走真实设置存储与同集择优服务链路，
///     使用内存设置存储与临时 SQLite 数据库。
/// </summary>
public sealed class SelectionRulesPageViewModelTests
{
    private const int     SubjectId = 42;
    private const string  MediaType = "SingleEpisode";
    private const decimal Episode   = 12m;

    [Fact]
    public async Task Load_WithSavedConfig_FillsDraftAndVersion()
    {
        using var       harness      = await CreateHarnessAsync();
        await using var scope        = harness.CreateScope();
        var             ruleSettings = scope.ServiceProvider.GetRequiredService<ManageSelectionRuleSettings>();

        var saved = await ruleSettings.SaveAsync(new SelectionRuleConfig
        {
            Mode           = SelectionMode.Combined,
            GroupOrder     = ["ANi"],
            LanguageOrder  = [EnumLanguage.Sc],
            DimensionOrder = SelectionDimensionOrder.LanguageFirst,
            CombinedEntries =
            [
                new CombinedRuleEntry("LoliHouse", EnumLanguage.JpSc),
                new CombinedRuleEntry(null, null)
            ],
            LanguageMatch      = LanguageMatchMode.Contains,
            Unmatched          = UnmatchedPolicy.NeedsReview,
            AllowedResolutions = []
        });

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();

        Assert.True(vm.Loaded);
        Assert.False(vm.HasError);
        Assert.Equal(saved.Config.Version, vm.CurrentVersion);
        Assert.Equal(SelectionMode.Combined, vm.Draft.Mode);
        Assert.Equal(["ANi"], vm.Draft.GroupOrder);
        Assert.Equal([EnumLanguage.Sc], vm.Draft.LanguageOrder);
        Assert.Equal(SelectionDimensionOrder.LanguageFirst, vm.Draft.DimensionOrder);
        var entries = Assert.IsType<List<CombinedRuleDraftEntry>>(vm.Draft.CombinedEntries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("LoliHouse", entries[0].Group);
        Assert.Equal(EnumLanguage.JpSc, entries[0].Language);
        Assert.Null(entries[1].Group);
        Assert.Null(entries[1].Language);
        Assert.Equal(LanguageMatchMode.Contains, vm.Draft.LanguageMatch);
        Assert.Equal(UnmatchedPolicy.NeedsReview, vm.Draft.Unmatched);
        Assert.Empty(vm.Draft.AllowedResolutions);
    }

    [Fact]
    public async Task Save_WhenRuleChanged_TriggersReselectionAndUpdatesWinner()
    {
        using var       harness          = await CreateHarnessAsync();
        await using var scope            = harness.CreateScope();
        var             selectionService = scope.ServiceProvider.GetRequiredService<CandidateSelectionService>();
        var (aniItemId, loliHouseItemId) = await harness.AddEpisodeCandidatesAsync();

        // 默认规则（LoliHouse 优先）先落库一次选择：当前优胜是 LoliHouse，版本 1。
        await selectionService.SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();
        Assert.Equal(1, vm.CurrentVersion);

        // 草稿改为 ANi 优先后保存：触发重选，优胜应换成 ANi，规则版本递增。
        vm.Draft.GroupOrder.Clear();
        vm.Draft.GroupOrder.Add("ANi");
        vm.Draft.GroupOrder.Add("LoliHouse");
        await vm.SaveAsync();

        Assert.False(vm.HasError);
        Assert.Contains("重选", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("版本 2", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(2, vm.CurrentVersion);

        await using var db        = await harness.CreateDbContextAsync();
        var             selection = await db.CandidateSelections.AsNoTracking().SingleAsync();
        Assert.Equal(aniItemId, selection.WinnerFeedItemId);
        Assert.Equal(2, selection.RuleVersion);
        Assert.Equal(SelectionOutcome.Winner,
                     (await db.FeedItems.SingleAsync(item => item.Id == aniItemId)).SelectionOutcome);
        Assert.Equal(SelectionOutcome.RunnerUp,
                     (await db.FeedItems.SingleAsync(item => item.Id == loliHouseItemId)).SelectionOutcome);
    }

    [Fact]
    public async Task Save_WhenRuleUnchanged_KeepsRuleVersionWithoutReselection()
    {
        using var       harness          = await CreateHarnessAsync();
        await using var scope            = harness.CreateScope();
        var             selectionService = scope.ServiceProvider.GetRequiredService<CandidateSelectionService>();
        await harness.AddEpisodeCandidatesAsync();

        await selectionService.SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();
        await vm.SaveAsync();

        Assert.False(vm.HasError);
        Assert.Contains("未变化", vm.StatusMessage, StringComparison.Ordinal);

        await using var db        = await harness.CreateDbContextAsync();
        var             selection = await db.CandidateSelections.AsNoTracking().SingleAsync();
        Assert.Equal(1, selection.RuleVersion);
        Assert.Equal(1, vm.CurrentVersion);
    }

    [Fact]
    public async Task Preview_WithChangedDraft_FillsResultWithExpectedWinner()
    {
        using var       harness          = await CreateHarnessAsync();
        await using var scope            = harness.CreateScope();
        var             selectionService = scope.ServiceProvider.GetRequiredService<CandidateSelectionService>();
        var (aniItemId, loliHouseItemId) = await harness.AddEpisodeCandidatesAsync();

        await selectionService.SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();
        vm.Draft.GroupOrder.Clear();
        vm.Draft.GroupOrder.Add("ANi");
        vm.Draft.GroupOrder.Add("LoliHouse");
        await vm.RunPreviewAsync();

        Assert.False(vm.HasError);
        var preview = vm.Preview;
        Assert.NotNull(preview);
        Assert.Equal(1, preview.GroupCount);
        Assert.Equal(1, preview.ChangedCount);
        var group = Assert.Single(preview.Groups);
        Assert.Equal(SubjectId, group.Key.SubjectId);
        Assert.Equal(MediaType, group.Key.MediaType);
        Assert.Equal(Episode, group.Key.Episode);
        Assert.Equal(loliHouseItemId, group.CurrentWinnerFeedItemId);
        Assert.Equal(aniItemId, group.DraftWinner!.FeedItemId);
        Assert.True(group.WinnerChanged);
    }

    [Fact]
    public async Task Save_WithBlankGroupName_IsRejectedWithoutPersisting()
    {
        using var       harness = await CreateHarnessAsync();
        await using var scope   = harness.CreateScope();
        var store = (MemoryApplicationSettingStore)scope.ServiceProvider
                                                        .GetRequiredService<IApplicationSettingStore>();

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();
        vm.Draft.GroupOrder.Add("   ");
        await vm.SaveAsync();

        Assert.True(vm.HasError);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.Equal(1, vm.CurrentVersion);
        // 校验失败不落库：设置存储里不应出现规则配置键。
        Assert.False(store.Values.ContainsKey(ManageSelectionRuleSettings.SettingKey));
    }

    [Fact]
    public async Task Save_CombinedModeWithoutEntries_IsRejectedWithoutPersisting()
    {
        // 回归：组合模式删光条件后保存必须被拦截，否则择优计算抛异常、批量规划静默变 0 组。
        using var       harness = await CreateHarnessAsync();
        await using var scope   = harness.CreateScope();
        var store = (MemoryApplicationSettingStore)scope.ServiceProvider
                                                        .GetRequiredService<IApplicationSettingStore>();

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();
        vm.Draft.Mode = SelectionMode.Combined;
        vm.Draft.CombinedEntries.Clear();
        await vm.SaveAsync();

        Assert.True(vm.HasError);
        Assert.Contains("组合模式", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(store.Values.ContainsKey(ManageSelectionRuleSettings.SettingKey));
    }

    [Fact]
    public async Task Save_ReselectInterruptedByAppShutdown_ReportsPartialCompletion()
    {
        // 回归：规则已落库后重选挂应用停机令牌；停机打断时如实报告，不当作保存失败。
        using var       harness          = await CreateHarnessAsync();
        await using var scope            = harness.CreateScope();
        var             selectionService = scope.ServiceProvider.GetRequiredService<CandidateSelectionService>();
        await harness.AddEpisodeCandidatesAsync();
        await selectionService.SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, MediaType, Episode)]);

        var vm = harness.CreateViewModel(scope);
        await vm.LoadAsync();
        vm.Draft.GroupOrder.Clear();
        vm.Draft.GroupOrder.Add("ANi");
        vm.Draft.GroupOrder.Add("LoliHouse");
        harness.Lifetime.TriggerStopping();

        await vm.SaveAsync();

        Assert.False(vm.HasError);
        Assert.Contains("停机中断", vm.StatusMessage, StringComparison.Ordinal);
        // 规则保存本身已完成：草稿版本已随保存结果刷新。
        Assert.Equal(2, vm.CurrentVersion);
    }

    private static async Task<SelectionRulesHarness> CreateHarnessAsync()
    {
        var directory = Directory.CreateTempSubdirectory("MediaManager.P4.RulesPage.Tests.");
        var timestamp = new DateTimeOffset(2026, 9, 27, 4, 0, 0, TimeSpan.Zero);
        var services  = new ServiceCollection();
        var lifetime  = new FakeHostApplicationLifetime();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "rules-page.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
        services.AddSingleton(new FakeTorrentStore());
        services.AddSingleton<FakeQbittorrentSessionFactory>();
        services.AddSingleton<IQbittorrentSessionFactory>(provider =>
                                                              provider
                                                                 .GetRequiredService<FakeQbittorrentSessionFactory>());
        services.AddSingleton<ITorrentFileSource>(_ => new FakeTorrentFileSource(_ => TestTorrentBytes.Create()));
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddNotificationTestFakes();
        services.AddSingleton<IFeedSourceReader>(new FakeFeedSourceReader());
        services.AddSingleton<AniParser>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        services.AddScoped<NamingTemplateStore>();
        // 规则页链路不触达 Subject 绑定解析，空实现只满足 DownloadService 构造签名。
        services.AddSingleton<ISubjectMetadataProvider>(NullSubjectMetadataProvider.Instance);
        services.AddScoped<SubjectMetadataCache>();
        services.AddSingleton<ISubjectLinkResolver>(NullSubjectLinkResolver.Instance);
        services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        services.AddSingleton<DownloadCoordinationGate>();
        services.AddScoped<DownloadService>();
        services.AddScoped<DownloadIntakeService>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        // 规则设置服务经 DI 构造（依赖 IApplicationSettingStore），不硬编码其构造签名。
        services.AddScoped<ManageSelectionRuleSettings>();
        // 择优服务经设置存储读取当前生效规则：保存后的新规则立即对重选与预览可见。
        services.AddScoped<SettingsBackedRuleProvider>();
        services.AddScoped<ISelectionRuleProvider>(provider =>
                                                       provider.GetRequiredService<SettingsBackedRuleProvider>());
        services.AddSingleton<SelectionGroupGate>();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddScoped<CandidateSelectionService>();
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：Identity 服务注册影响
        // ApplicationDbContext 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        await using (var db = await provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                            .CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.MediaSubjects.Add(new MediaSubject
            {
                Id = SubjectId, Name = "Test Subject", NameCn = "测试作品", Platform = "TV", CachedAtUtc = timestamp
            });
            await db.SaveChangesAsync();
        }

        return new SelectionRulesHarness(directory, timestamp, provider, lifetime);
    }

    /// <summary>测试用规则提供者：从规则设置服务读取当前生效配置，保存后立即生效。</summary>
    private sealed class SettingsBackedRuleProvider(ManageSelectionRuleSettings settings) : ISelectionRuleProvider
    {
        public async ValueTask<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default) =>
            await settings.GetAsync(cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    /// <summary>测试用应用生命周期：只关心停机令牌的取消时机。</summary>
    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts = new();

        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped  => CancellationToken.None;

        public void StopApplication()
        {
            _stoppingCts.Cancel();
        }

        public void TriggerStopping()
        {
            _stoppingCts.Cancel();
        }
    }

    private sealed class SelectionRulesHarness(
        DirectoryInfo               directory,
        DateTimeOffset              timestamp,
        ServiceProvider             provider,
        FakeHostApplicationLifetime lifetime) : IDisposable
    {
        public DirectoryInfo Directory { get; } = directory;

        public DateTimeOffset Timestamp { get; } = timestamp;

        /// <summary>测试用应用生命周期：控制规则保存后重选的停机令牌；与 DI 注册是同一实例。</summary>
        public FakeHostApplicationLifetime Lifetime { get; } = lifetime;

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                Directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        public AsyncServiceScope CreateScope() => provider.CreateAsyncScope();

        public SelectionRulesPageViewModel CreateViewModel(IServiceScope scope) =>
            new(scope.ServiceProvider.GetRequiredService<ManageSelectionRuleSettings>(),
                scope.ServiceProvider.GetRequiredService<CandidateSelectionService>(),
                NullLogger<SelectionRulesPageViewModel>.Instance);

        /// <summary>插入同一同集组的两个合格候选：ANi 与 LoliHouse（默认规则优胜 LoliHouse）。</summary>
        public async Task<(Guid AniItemId, Guid LoliHouseItemId)> AddEpisodeCandidatesAsync()
        {
            var aniItemId       = await AddItemAsync("guid:ani", "ANi");
            var loliHouseItemId = await AddItemAsync("guid:loli", "LoliHouse");
            return (aniItemId, loliHouseItemId);
        }

        private async Task<Guid> AddItemAsync(string stableId, string group)
        {
            await using var db     = await CreateDbContextAsync();
            var             itemId = Guid.NewGuid();
            db.FeedItems.Add(new FeedItem
            {
                Id             = itemId,
                SubscriptionId = await EnsureSubscriptionAsync(db),
                StableId       = stableId,
                Title          = $"[{group}] 测试作品 - 12",
                TorrentUrl     = $"https://mikanani.me/Download/{stableId}.torrent",
                FirstSeenAtUtc = Timestamp,
                Status         = FeedItemStatus.Accepted,
                DecisionStatus = ReleaseDecisionStatus.Accepted,
                ParsedTitle    = $"[{group}] 测试作品 - 12",
                MediaType      = MediaType,
                Episode        = Episode,
                Version        = 1,
                Group          = group,
                Language       = "JpSc",
                Resolution     = "R1080P",
                SubjectId      = SubjectId,
                Reason         = "通过硬性过滤。",
                NamingPreview  = $"测试作品 E{Episode:00} v1 [{group}]",
                EvaluatedAtUtc = Timestamp
            });
            await db.SaveChangesAsync();
            return itemId;
        }

        public async Task<ApplicationDbContext> CreateDbContextAsync() =>
            await provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                          .CreateDbContextAsync();

        private async Task<Guid> EnsureSubscriptionAsync(ApplicationDbContext db)
        {
            var existing = await db.MediaSubscriptions.FirstOrDefaultAsync();
            if (existing is not null) return existing.Id;

            var subscription = new MediaSubscription
            {
                Id                     = Guid.NewGuid(), Name = "测试订阅", FeedUrlProtected = "unused",
                RefreshIntervalMinutes = 30
            };
            db.MediaSubscriptions.Add(subscription);
            await db.SaveChangesAsync();
            return subscription.Id;
        }
    }
}
