using Banori.Core.Rules;
using Banori.Features.Downloads;
using Banori.Tests.Downloads;

namespace Banori.Tests.Settings;

public sealed class NamingTemplateStoreTests
{
    [Fact]
    public async Task Templates_AreIndependentAndEachDefaultsWhenUnset()
    {
        var settings = new MemoryApplicationSettingStore();
        var store    = new NamingTemplateStore(settings);

        Assert.Equal(ReleaseNamePreviewPolicy.DefaultTemplate, await store.GetAsync());
        Assert.Equal(ReleaseNamePreviewPolicy.DefaultFileTemplate, await store.GetFileTemplateAsync());
        Assert.Equal(ReleaseNamePreviewPolicy.DefaultTemplate, await store.GetCollectionTemplateAsync());
        Assert.Null(await store.GetConfiguredCollectionTemplateAsync());

        await store.SaveAsync("Seed {bgm.id}");
        await store.SaveFileTemplateAsync("File {ani.episode}");

        Assert.Equal("Seed {bgm.id}", await store.GetAsync());
        Assert.Equal("Seed {bgm.id}", await store.GetCollectionTemplateAsync());
        await store.SaveCollectionTemplateAsync("Collection {episode:ee-ee}");
        Assert.Equal("Collection {episode:ee-ee}", await store.GetCollectionTemplateAsync());
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
        await store.SaveCollectionTemplateAsync("Collection custom");

        await store.SaveFileTemplateAsync("  ");

        Assert.Equal("Seed custom", await store.GetAsync());
        Assert.Equal(ReleaseNamePreviewPolicy.DefaultFileTemplate, await store.GetFileTemplateAsync());
        Assert.Equal("Collection custom", await store.GetCollectionTemplateAsync());
        await store.SaveCollectionTemplateAsync("  ");
        Assert.Equal("Seed custom", await store.GetCollectionTemplateAsync());
        Assert.Null(await store.GetConfiguredCollectionTemplateAsync());
    }

    [Fact]
    public async Task SavingOverlongFileTemplateThrows()
    {
        var store = new NamingTemplateStore(new MemoryApplicationSettingStore());

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveFileTemplateAsync(new string('x', 201)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveCollectionTemplateAsync(new string('x', 201)));
    }
}
