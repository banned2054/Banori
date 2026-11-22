using Banori.Features.Anime;
using Banori.Tests.Downloads;

namespace Banori.Tests.Anime;

/// <summary>排序持久化：未保存过按名称升序，字段与方向成对往返，损坏内容回退默认。</summary>
public sealed class AnimeSortStoreTests
{
    [Fact]
    public async Task UnstoredSort_ReadsAsNameAscending()
    {
        var store = new AnimeSortStore(new MemoryApplicationSettingStore());

        var preference = await store.GetAsync();

        Assert.Multiple(() => Assert.Equal(AnimeSortField.Name, preference.Field),
                        () => Assert.False(preference.Descending));
    }

    [Fact]
    public async Task Sort_RoundTripsThroughSettings()
    {
        var settings = new MemoryApplicationSettingStore();
        var store    = new AnimeSortStore(settings);

        await store.SaveAsync(AnimeSortField.LastAdded, true);
        var stored = await store.GetAsync();

        Assert.Multiple(() => Assert.Equal(AnimeSortField.LastAdded, stored.Field),
                        () => Assert.True(stored.Descending),
                        () => Assert.True(settings.Values.ContainsKey(AnimeSortStore.SortKey)));
    }

    [Theory]
    [InlineData("不是 JSON")]
    [InlineData("null")]
    [InlineData("{\"Field\":\"RemovedField\",\"Descending\":true}")]
    [InlineData("{\"Field\":99,\"Descending\":true}")]
    public async Task UnreadableStoredValue_FallsBackToDefault(string stored)
    {
        var settings = new MemoryApplicationSettingStore
        {
            Values =
            {
                [AnimeSortStore.SortKey] = (stored, false)
            }
        };

        var preference = await new AnimeSortStore(settings).GetAsync();

        Assert.Multiple(() => Assert.Equal(AnimeSortField.Name, preference.Field),
                        () => Assert.False(preference.Descending));
    }
}
