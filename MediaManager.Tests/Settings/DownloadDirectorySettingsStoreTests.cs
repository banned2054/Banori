using MediaManager.Components.Pages.Settings;
using MediaManager.Core.Downloads;
using MediaManager.Features.Downloads;
using MediaManager.Features.Settings;
using MediaManager.Tests.Downloads;
using MediaManager.Tests.Notifications;
using MediaManager.Tests.Proxies;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Settings;

public sealed class DownloadDirectorySettingsStoreTests
{
    [Fact]
    public async Task DefaultsSaveNormalizeAndRestore_AreIndependent()
    {
        var settings = new MemoryApplicationSettingStore();
        var store    = new DownloadDirectorySettingsStore(settings);
        Assert.Equal(new DownloadDirectorySettings("Media/Anime", "Seed"), await store.GetAsync());
        await store.SaveAsync(" Library\\Anime ", " Seeding ");
        var snapshot = await store.GetAsync();
        Assert.Equal(new DownloadDirectorySettings("Library/Anime", "Seeding"), snapshot);
        Assert.Single(settings.Values);
        await store.SaveAsync(null, "Extras");
        Assert.Equal(new DownloadDirectorySettings("Media/Anime", "Extras"), await store.GetAsync());
        Assert.Equal(new DownloadDirectorySettings("Library/Anime", "Seeding"), snapshot);
        await store.SaveAsync(" ", null);
        Assert.Equal(new DownloadDirectorySettings("Media/Anime", "Seed"), await store.GetAsync());
    }

    [Theory]
    [InlineData("/absolute", "Seed")]
    [InlineData("\\absolute", "Seed")]
    [InlineData("C:\\Anime", "Seed")]
    [InlineData("C:Anime", "Seed")]
    [InlineData("../Anime", "Seed")]
    [InlineData("Media/../Anime", "Seed")]
    [InlineData("Media//Anime", "Seed")]
    [InlineData("Media/Anime/", "Seed")]
    [InlineData(".", "Seed")]
    [InlineData("Anime", "ANIME")]
    [InlineData("Media", "media/Seed")]
    [InlineData("Seed/Anime", "Seed")]
    [InlineData("Anime", "..")]
    public async Task InvalidOrOverlappingDirectories_RejectBeforeWriting(string media, string seed)
    {
        var settings = new MemoryApplicationSettingStore();
        var store    = new DownloadDirectorySettingsStore(settings);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(media, seed));
        Assert.Empty(settings.Values);
    }

    [Fact]
    public async Task SiblingPrefixes_AreAllowedAndOverlongPathsAreRejected()
    {
        var store = new DownloadDirectorySettingsStore(new MemoryApplicationSettingStore());
        await store.SaveAsync("Anime", "AnimeSeed");
        Assert.Equal(new DownloadDirectorySettings("Anime", "AnimeSeed"), await store.GetAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new string('a', 501), "Seed"));
    }

    [Fact]
    public async Task SettingsPage_SavesDirectoriesAndRejectsInvalidDraftBeforeOtherWrites()
    {
        var settings    = new MemoryApplicationSettingStore();
        var directories = new DownloadDirectorySettingsStore(settings);
        var vm = new SettingsPageViewModel(
                                           new ManageQbittorrentSettings(settings,
                                                                         new
                                                                             FakeQbittorrentSessionFactory(new
                                                                                 FakeTorrentStore())),
                                           new NamingTemplateStore(settings), directories,
                                           new ManageTelegramSettings(settings, new FakeTelegramSender()),
                                           new ManageGotifySettings(settings, new FakeGotifySender()),
                                           ProxyServiceTestFactory.CreateEphemeral(),
                                           NullLogger<SettingsPageViewModel>.Instance);
        vm.Downloader.MediaDirectory = "Library\\Anime";
        vm.Downloader.SeedDirectory  = "Seeding";
        await vm.SaveAsync();
        Assert.True(vm.SaveSucceeded);
        Assert.Equal("Library/Anime", vm.Downloader.MediaDirectory);
        Assert.Equal(new DownloadDirectorySettings("Library/Anime", "Seeding"), await directories.GetAsync());

        var savedValues = settings.Values.ToArray();
        vm.Downloader.MediaDirectory = "../outside";
        await vm.SaveAsync();
        Assert.False(vm.SaveSucceeded);
        Assert.Equal(savedValues, settings.Values.ToArray());
        Assert.Contains("相对目录", vm.StatusMessage);
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a?b")]
    [InlineData("Media/ Anime")]
    public void InvalidCrossPlatformSegments_AreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => RelativeDownloadPathPolicy.Normalize(path));
    }
}
