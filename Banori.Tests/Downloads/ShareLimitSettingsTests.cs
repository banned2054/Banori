using Banori.Components.Pages.Settings;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Downloads;

public sealed class ShareLimitSettingsTests
{
    [Fact]
    public async Task Store_SavesResolvesAndEnforcesExclusiveSiteSources()
    {
        var settingStore = new MemoryApplicationSettingStore();
        var store        = new ShareLimitSettingsStore(settingStore);

        var unlimitedId = Guid.NewGuid();
        var customId    = Guid.NewGuid();
        var saved = await store.SaveAsync(new ShareLimitSettings(
        [
            new ShareLimitProfile(unlimitedId, "PT 无限做种", TorrentShareLimitMode.Unlimited,
                                  false, 0, false, 0, false, 0, ["tjupt", "byrbt", "u2"]),
            new ShareLimitProfile(customId, "公网限流", TorrentShareLimitMode.Custom,
                                  true, 1.5d, true, 120, false, 0, ["mikan"])
        ]));

        // 规范顺序排列。
        Assert.Equal(["byrbt", "u2", "tjupt"], saved.Profiles[0].SiteSources);

        var loaded = await store.GetAsync();
        Assert.Equal(ResolvedShareLimit.Unlimited, loaded.ResolveForSiteSource("byrbt"));
        Assert.Equal(ResolvedShareLimit.Unlimited, loaded.ResolveForSiteSource("U2"));
        Assert.Equal(new ResolvedShareLimit(1.5d, 120, -1), loaded.ResolveForSiteSource("mikan"));
        // 未分配分组与未知来源一律回退全局分享限制。
        Assert.Equal(ResolvedShareLimit.Global, loaded.ResolveForSiteSource("anibt"));
        Assert.Equal(ResolvedShareLimit.Global, loaded.ResolveForSiteSource("other"));
        Assert.Equal(ResolvedShareLimit.Global, loaded.ResolveForSiteSource(null));

        // 同一站点来源不能被两个配置重复占用。
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ShareLimitSettings(
        [
            new ShareLimitProfile(Guid.NewGuid(), "方案 A", TorrentShareLimitMode.Unlimited,
                                  false, 0, false, 0, false, 0, ["byrbt"]),
            new ShareLimitProfile(Guid.NewGuid(), "方案 B", TorrentShareLimitMode.UseGlobal,
                                  false, 0, false, 0, false, 0, ["byrbt"])
        ])));

        // 自定义模式至少需勾选一项限制。
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ShareLimitSettings(
        [
            new ShareLimitProfile(Guid.NewGuid(), "空自定义", TorrentShareLimitMode.Custom,
                                  false, 0, false, 0, false, 0, ["u2"])
        ])));

        // 不允许未知站点来源。
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ShareLimitSettings(
        [
            new ShareLimitProfile(Guid.NewGuid(), "非法分组", TorrentShareLimitMode.Unlimited,
                                  false, 0, false, 0, false, 0, ["custom-tag"])
        ])));
    }

    [Fact]
    public async Task ViewModel_SaveAndDelete_SyncsAffectedTorrentsGloballyAndPreventsClaimOverlap()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       store   = harness.Provider.GetRequiredService<ShareLimitSettingsStore>();

        var byrbtHash = TestTorrentBytes.InfoHashOf("BYRBT Seed - 01");
        var mikanHash = TestTorrentBytes.InfoHashOf("Mikan Seed - 01");

        var byrbtTask = DownloadTaskListTestSeed.NewTask("BYRBT Seed - 01", DownloadTaskListTestSeed.At(0),
                                                         DownloadTaskStatus.Completed);
        byrbtTask.InfoHash   = byrbtHash;
        byrbtTask.SiteSource = "byrbt";

        var mikanTask = DownloadTaskListTestSeed.NewTask("Mikan Seed - 01", DownloadTaskListTestSeed.At(1),
                                                         DownloadTaskStatus.Completed);
        mikanTask.InfoHash   = mikanHash;
        mikanTask.SiteSource = "mikan";

        await DownloadTaskListTestSeed.SeedAsync(harness, [byrbtTask, mikanTask]);

        harness.Store.Torrents[byrbtHash] = new TorrentSnapshot(
            byrbtHash, "BYRBT Seed - 01", QbittorrentTorrentCategory.Seeding, 1.0, 1024, "/downloads/byrbt", ["byrbt"]);
        harness.Store.Torrents[mikanHash] = new TorrentSnapshot(
            mikanHash, "Mikan Seed - 01", QbittorrentTorrentCategory.Seeding, 1.0, 1024, "/downloads/mikan", ["mikan"]);

        var vm = new ShareLimitsPageViewModel(store, NullLogger<ShareLimitsPageViewModel>.Instance, harness.Service);
        await vm.LoadAsync();

        // 创建 PT 无限做种配置并保存，应立即同步到 qBittorrent 中的 byrbt 种子，不触及 mikan。
        vm.BeginCreate();
        vm.Draft.Name = "PT 无限做种";
        vm.Draft.Mode = TorrentShareLimitMode.Unlimited;
        vm.ToggleSiteSource("byrbt", true);
        vm.ToggleSiteSource("u2", true);
        await vm.SaveCurrentAsync();

        Assert.False(vm.HasError);
        var call = Assert.Single(harness.Store.ShareLimitCalls);
        Assert.Equal([byrbtHash], call.Hashes);
        Assert.Equal(-1d, call.RatioLimit);
        Assert.Equal(-1, call.SeedingTimeLimitMinutes);
        Assert.Equal(-1, call.InactiveSeedingTimeLimitMinutes);

        // 新建第二个配置时，byrbt 已被「PT 无限做种」占用，不允许再次勾选。
        vm.BeginCreate();
        Assert.Equal("PT 无限做种", vm.OwnerOfSiteSource("byrbt"));
        vm.ToggleSiteSource("byrbt", true);
        Assert.False(vm.IsSiteSourceSelected("byrbt"));
        vm.CancelCreate();

        // 删除配置后，原 byrbt 种子同步回退为 qB 全局分享限制（-2）。
        harness.Store.ShareLimitCalls.Clear();
        await vm.DeleteSelectedAsync();

        Assert.False(vm.HasError);
        Assert.Empty(vm.Profiles);
        var revertCall = Assert.Single(harness.Store.ShareLimitCalls);
        Assert.Equal([byrbtHash], revertCall.Hashes);
        Assert.Equal(-2d, revertCall.RatioLimit);
        Assert.Equal(-2, revertCall.SeedingTimeLimitMinutes);
        Assert.Equal(-2, revertCall.InactiveSeedingTimeLimitMinutes);
    }
}
