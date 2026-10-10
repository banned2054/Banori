using Banned.AniParser;
using MediaManager.Components.Pages.FeedItems;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Subjects;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Features.FeedItems;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Tests.Downloads;
using MediaManager.Tests.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel.DataAnnotations;

namespace MediaManager.Tests.FeedItems;

/// <summary>详情页 ViewModel 的加载与重评回归：表单必须使用原始关联值，不得默认集数修正后的结果。</summary>
public sealed class FeedItemDetailsPageViewModelTests
{
    private const string Episode13Title =
        "[S1百综字幕组] 死灵子的宇宙恐怖秀 / Necronomico no Cosmic Horror Show[13][1080p][WebRip][AVC_AAC][CHS][简体内嵌]";

    [Fact]
    public async Task Load_PrefillsOriginalSubjectFromTitleMapping()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Details.Load.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 20, 4, 0, 0, TimeSpan.Zero);
            var services = CreateServices(testDirectory, timestamp, 467461);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var itemId = await SeedEpisode13Async(factory, timestamp);

            // 走真实评估链路：第 13 集按集数修正规则落到 529431，标题映射保留原始 Subject 467461。
            var evaluator = scope.ServiceProvider.GetRequiredService<FeedItemEvaluator>();
            await evaluator.EvaluateAsync(itemId, null);

            var vm = CreateViewModel(scope, evaluator);
            await vm.LoadAsync(itemId);

            Assert.True(vm.Loaded);
            Assert.Equal(FeedItemStatus.Accepted, vm.Item!.Status);
            Assert.Equal(529431, vm.Item.SubjectId);
            Assert.Equal(467461, vm.Correction.SubjectId);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Reevaluate_WithoutEditingPrefilledDraft_KeepsOriginalSubjectInTitleMapping()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Details.Resubmit.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 20, 5, 0, 0, TimeSpan.Zero);
            var services = CreateServices(testDirectory, timestamp, 467461);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var itemId = await SeedEpisode13Async(factory, timestamp);

            var evaluator = scope.ServiceProvider.GetRequiredService<FeedItemEvaluator>();
            await evaluator.EvaluateAsync(itemId, null);

            // 不修改预填表单直接重评：不得把标题映射改写成修正后的 529431。
            var vm = CreateViewModel(scope, evaluator);
            await vm.LoadAsync(itemId);
            await vm.ReevaluateAsync(itemId);

            Assert.True(vm.Succeeded, vm.Message);
            Assert.Equal(FeedItemStatus.Accepted, vm.Item!.Status);
            Assert.Equal(529431, vm.Item.SubjectId);
            await using var verification = await factory.CreateDbContextAsync();
            var mapping = Assert.Single(await verification.SubjectTitleMappings.AsNoTracking().ToListAsync());
            Assert.Equal(467461, mapping.SubjectId);

            // 重评成功后的重新加载不得把表单改回修正结果。
            Assert.Equal(467461, vm.Correction.SubjectId);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Load_LeavesDraftEmptyWhenOriginalSubjectUnavailable()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Details.Empty.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 20, 6, 0, 0, TimeSpan.Zero);
            var services = CreateServices(testDirectory, timestamp, null);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var itemId = await SeedEpisode13Async(factory, timestamp);

            // 页面发现也失败：评估无法关联 Subject，不存在标题映射。
            var evaluator = scope.ServiceProvider.GetRequiredService<FeedItemEvaluator>();
            await evaluator.EvaluateAsync(itemId, null);

            var vm = CreateViewModel(scope, evaluator);
            await vm.LoadAsync(itemId);

            Assert.Equal(FeedItemStatus.NeedsReview, vm.Item!.Status);
            Assert.Null(vm.Item.SubjectId);
            Assert.Null(vm.Correction.SubjectId);

            // 留空草稿无法通过校验，必须显式填写后才能提交。
            var results = new List<ValidationResult>();
            var isValid = Validator.TryValidateObject(vm.Correction, new ValidationContext(vm.Correction),
                                                      results, true);
            Assert.False(isValid);
            Assert.Contains(results, result => result.ErrorMessage == "请填写 Subject ID。");

            vm.Correction.SubjectId = 467461;
            results.Clear();
            isValid = Validator.TryValidateObject(vm.Correction, new ValidationContext(vm.Correction),
                                                  results, true);
            Assert.True(isValid);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task Evaluate_ItemNoLongerEligible_ClearsStaleSelectionOutcome()
    {
        var testDirectory = Directory.CreateTempSubdirectory("MediaManager.Details.Outcome.Tests.");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 20, 7, 0, 0, TimeSpan.Zero);
            // 页面发现与标题映射都不可用：重评后条目进入待确认，不再参与组内竞争。
            var services = CreateServices(testDirectory, timestamp, null);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            var itemId = await SeedEpisode13Async(factory, timestamp);

            // 旧状态：该条目曾是合格候选并在组内落选。
            await using (var db = await factory.CreateDbContextAsync())
            {
                var item = await db.FeedItems.SingleAsync(value => value.Id == itemId);
                item.Status           = FeedItemStatus.Accepted;
                item.SelectionOutcome = SelectionOutcome.RunnerUp;
                await db.SaveChangesAsync();
            }

            var evaluator = scope.ServiceProvider.GetRequiredService<FeedItemEvaluator>();
            await evaluator.EvaluateAsync(itemId, null);

            await using var verification = await factory.CreateDbContextAsync();
            var reevaluated = await verification.FeedItems.AsNoTracking().SingleAsync(value => value.Id == itemId);
            Assert.Equal(FeedItemStatus.NeedsReview, reevaluated.Status);
            // 不再合格的条目不得继续显示“组内落选”。
            Assert.Equal(SelectionOutcome.None, reevaluated.SelectionOutcome);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private static async Task<Guid> SeedEpisode13Async(
        IDbContextFactory<ApplicationDbContext> factory, DateTimeOffset timestamp)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var subscriptionId = Guid.NewGuid();
        db.MediaSubscriptions.Add(new MediaSubscription
        {
            Id                     = subscriptionId,
            Name                   = "详情页测试",
            FeedUrlProtected       = "unused",
            IsEnabled              = false,
            RefreshIntervalMinutes = 30
        });
        var itemId = Guid.NewGuid();
        db.FeedItems.Add(new FeedItem
        {
            Id             = itemId,
            SubscriptionId = subscriptionId,
            StableId       = "guid:details-13",
            Title          = Episode13Title,
            Link           = "https://mikanani.me/Home/Episode/details-13",
            FirstSeenAtUtc = timestamp.AddHours(-1),
            Status         = FeedItemStatus.Pending,
            Reason         = "等待处理。"
        });
        await db.SaveChangesAsync();
        return itemId;
    }

    private static FeedItemDetailsPageViewModel CreateViewModel(IServiceScope scope, FeedItemEvaluator evaluator)
    {
        return new FeedItemDetailsPageViewModel(scope.ServiceProvider.GetRequiredService<FeedItemQueries>(), evaluator,
                                                scope.ServiceProvider.GetRequiredService<DownloadService>(),
                                                NullLogger<FeedItemDetailsPageViewModel>.Instance);
    }

    private static ServiceCollection CreateServices(
        DirectoryInfo directory, DateTimeOffset timestamp, int? resolverSubjectId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "details.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        services.AddSingleton<ISubjectLinkResolver>(new CountingSubjectLinkResolver(resolverSubjectId));
        services.AddSingleton<ISubjectMetadataProvider>(new FakeSubjectProvider(timestamp));
        services.AddSingleton<AniParser>();
        services.AddSingleton<CandidateEligibilityPolicy>();
        services.AddSingleton<CandidateRankingPolicy>();
        services.AddSingleton<ISelectionRuleProvider, DefaultSelectionRuleProvider>();
        services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        services.AddSingleton<ReleaseNamePreviewPolicy>();
        services.AddSingleton<SelectionGroupGate>();
        services.AddScoped<CandidateSelectionService>();
        services.AddScoped<FeedItemEvaluator>();
        services.AddScoped<FeedItemQueries>();
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
        services.AddScoped<DownloadService>();
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：Identity 服务注册影响
        // ApplicationDbContext 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        return services;
    }

    private sealed class CountingSubjectLinkResolver(int? subjectId) : ISubjectLinkResolver
    {
        public Task<int?> ResolveSubjectIdAsync(Uri               episodeUri, Guid? proxyId = default,
                                                CancellationToken cancellationToken = default)
        {
            return Task.FromResult(subjectId);
        }
    }

    private sealed class FakeSubjectProvider(DateTimeOffset timestamp) : ISubjectMetadataProvider
    {
        public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SubjectMetadata(subjectId, "Necronomico", "死灵子的宇宙恐怖秀", "TV",
                                                       "2026-07-01", null, timestamp));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }
}
