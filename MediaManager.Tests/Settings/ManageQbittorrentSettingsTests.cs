using MediaManager.Features.Settings;
using MediaManager.Tests.Downloads;

namespace MediaManager.Tests.Settings;

/// <summary>下载器设置的读写、校验与连接测试入口回归。</summary>
public sealed class ManageQbittorrentSettingsTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsAndProtectsPassword()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);

        await settings.SaveAsync(new QbittorrentSettings("http://qb.test:8080/", "admin", "/downloads", true),
                                 "secret-password");

        var loaded = await settings.GetAsync();
        Assert.True(loaded.Enabled);
        Assert.Equal("http://qb.test:8080", loaded.BaseUrl);
        Assert.Equal("admin", loaded.Username);
        Assert.Equal("/downloads", loaded.SavePath);
        // 内存存储不做加密（加密行为由 EfApplicationSettingStoreTests 覆盖），这里验证受保护标记位。
        Assert.True(store.Values[ManageQbittorrentSettings.PasswordKey].IsProtected);
    }

    [Fact]
    public async Task Save_WithBlankPassword_KeepsStoredValue()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = CreateService(store);
        await settings.SaveAsync(new QbittorrentSettings("http://qb.test", "admin", "/downloads", true),
                                 "old-password");

        await settings.SaveAsync(new QbittorrentSettings("http://qb.test", "root", "/downloads", true));

        Assert.Equal("old-password", store.Values[ManageQbittorrentSettings.PasswordKey].Value);
        Assert.Equal("root", (await settings.GetAsync()).Username);
    }

    [Theory]
    [InlineData("ftp://qb.test")]
    [InlineData("not-a-url")]
    public async Task Save_WithInvalidBaseUrl_Throws(string baseUrl)
    {
        var settings = CreateService(new MemoryApplicationSettingStore());
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        settings.SaveAsync(new QbittorrentSettings(baseUrl, "admin",
                                                                               "/downloads", true)));
    }

    [Fact]
    public async Task Save_EnabledWithoutSavePath_Throws()
    {
        var settings = CreateService(new MemoryApplicationSettingStore());
        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        settings.SaveAsync(new QbittorrentSettings("http://qb.test",
                                                                               "admin", string.Empty, true)));
    }

    [Fact]
    public async Task TestConnection_ReportsFactoryResult()
    {
        var factory  = new FakeQbittorrentSessionFactory(new FakeTorrentStore());
        var settings = new ManageQbittorrentSettings(new MemoryApplicationSettingStore(), factory);

        var (succeeded, message) = await settings.TestConnectionAsync(
                                                                      new QbittorrentSettings("http://qb.test", "admin",
                                                                          "/downloads", true), "pw");

        Assert.True(succeeded);
        Assert.Contains("v5.0.0-fake", message);

        factory.ThrowOnCreate = true;
        var (failed, failure) = await settings.TestConnectionAsync(
                                                                   new QbittorrentSettings("http://qb.test", "admin",
                                                                       "/downloads", true), "pw");
        Assert.False(failed);
        Assert.Contains("模拟下载器不可达", failure);
    }

    private static ManageQbittorrentSettings CreateService(MemoryApplicationSettingStore store)
    {
        return new ManageQbittorrentSettings(store, new FakeQbittorrentSessionFactory(new FakeTorrentStore()));
    }
}
