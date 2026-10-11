using MediaManager.Core.Rules;
using MediaManager.Features.Downloads;
using MediaManager.Tests.Downloads;

namespace MediaManager.Tests.Settings;

public sealed class NamingTemplateStoreTests
{
    [Fact]
    public async Task Templates_AreIndependentAndEachDefaultsWhenUnset()
    {
        var settings = new MemoryApplicationSettingStore();
        var store    = new NamingTemplateStore(settings);

        Assert.Equal(ReleaseNamePreviewPolicy.DefaultTemplate, await store.GetAsync());
        Assert.Equal(ReleaseNamePreviewPolicy.DefaultFileTemplate, await store.GetFileTemplateAsync());

        await store.SaveAsync("Seed {bgm.id}");
        await store.SaveFileTemplateAsync("File {ani.episode}");

        Assert.Equal("Seed {bgm.id}", await store.GetAsync());
        Assert.Equal("File {ani.episode}", await store.GetFileTemplateAsync());
        Assert.Equal("Seed {bgm.id}", settings.Values[NamingTemplateStore.Key].Value);
        Assert.Equal("File {ani.episode}", settings.Values[NamingTemplateStore.FileKey].Value);
    }

    [Fact]
    public async Task ClearingOneTemplateRestoresOnlyItsDefault()
    {
        var store = new NamingTemplateStore(new MemoryApplicationSettingStore());
        await store.SaveAsync("Seed custom");
        await store.SaveFileTemplateAsync("File custom");

        await store.SaveFileTemplateAsync("  ");

        Assert.Equal("Seed custom", await store.GetAsync());
        Assert.Equal(ReleaseNamePreviewPolicy.DefaultFileTemplate, await store.GetFileTemplateAsync());
    }

    [Fact]
    public async Task SavingOverlongFileTemplateThrows()
    {
        var store = new NamingTemplateStore(new MemoryApplicationSettingStore());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveFileTemplateAsync(new string('x', 201)));
    }
}
