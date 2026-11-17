using Banori.Features.Downloads;

namespace Banori.Tests.Downloads;

/// <summary>状态筛选持久化：未保存过按全部勾选，“全不勾选”作为空集合保存，损坏内容回退全选。</summary>
public sealed class DownloadTaskFilterStoreTests
{
    [Fact]
    public async Task UnstoredFilter_ReadsAsAllGroups()
    {
        var store = new DownloadTaskFilterStore(new MemoryApplicationSettingStore());

        var groups = await store.GetStatusGroupsAsync();

        Assert.True(groups.SetEquals(DownloadStatusFilter.Choices));
    }

    [Fact]
    public async Task SubsetAndEmptySelection_RoundTripThroughSettings()
    {
        var settings = new MemoryApplicationSettingStore();
        var store    = new DownloadTaskFilterStore(settings);

        await store.SaveStatusGroupsAsync(new HashSet<DownloadStatusFilterGroup>
        {
            DownloadStatusFilterGroup.Failed, DownloadStatusFilterGroup.Lost
        });
        Assert.True((await store.GetStatusGroupsAsync()).SetEquals(
        [
            DownloadStatusFilterGroup.Failed, DownloadStatusFilterGroup.Lost
        ]));

        // 空集合必须与“未保存”区分：读取结果为空，而不是回退全选。
        await store.SaveStatusGroupsAsync(new HashSet<DownloadStatusFilterGroup>());
        Assert.Empty(await store.GetStatusGroupsAsync());
        Assert.Single(settings.Values);
    }

    [Theory]
    [InlineData("不是 JSON")]
    [InlineData("null")]
    [InlineData("[\"RemovedGroup\"]")]
    public async Task UnreadableStoredValue_FallsBackToAllGroups(string stored)
    {
        var settings = new MemoryApplicationSettingStore
        {
            Values =
            {
                [DownloadTaskFilterStore.StatusGroupsKey] = (stored, false)
            }
        };

        var groups = await new DownloadTaskFilterStore(settings).GetStatusGroupsAsync();

        Assert.True(groups.SetEquals(DownloadStatusFilter.Choices));
    }
}
