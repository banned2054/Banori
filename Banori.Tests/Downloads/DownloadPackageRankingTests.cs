using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Tests.Downloads;

/// <summary>
///     平级合集决胜：同源同优先级同字幕组同语言同版本时，按该包初始候选正片文件数决胜；
///     字幕/Seed 不计，跨包落选不缩小包计数，不做胜者全取，整包退出仍取决于全部内容是否落选。
/// </summary>
public sealed partial class DownloadVerificationTests
{
    [Fact]
    public async Task PackageCount_ThreePartiallyOverlappingCollections_LargerCollectionWinsSharedEpisodes()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var (aId, bId, cId) = await CreateThreeCollectionsAsync(harness, "ABC");

        // A=4、B=3、C=5：C 赢 E1-E3，A 赢 E4，B/C 的独有集保留；A 只丢 3 个文件，不整包退出。
        Assert.Equal(new[] { false, false, false, true }, await SelectedFilesAsync(harness, aId));
        Assert.Equal(new[] { false, true, true }, await SelectedFilesAsync(harness, bId));
        Assert.Equal(new[] { true, true, true, true, true }, await SelectedFilesAsync(harness, cId));
        Assert.NotEqual(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(aId)).Status);
        Assert.NotEqual(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(bId)).Status);
    }

    [Fact]
    public async Task PackageCount_CollectionLosesAllEpisodes_IsRetiredOnlyAfterLastFile()
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var smallId = await harness.CreateUploadAsync(ContainmentBatch(
            ContainmentFileName(1), ContainmentFileName(2), ContainmentFileName(3), ContainmentFileName(4)));
        var bigId = await harness.CreateUploadAsync(ContainmentBatch(
            ContainmentFileName(1), ContainmentFileName(2), ContainmentFileName(3), ContainmentFileName(4),
            ContainmentFileName(5), ContainmentFileName(6)));

        Assert.All(await SelectedFilesAsync(harness, smallId), selected => Assert.False(selected));
        Assert.All(await SelectedFilesAsync(harness, bigId), selected => Assert.True(selected));
        var retired = await harness.GetTaskAsync(smallId);
        Assert.Equal(DownloadTaskStatus.Replaced, retired.Status);
        Assert.Equal(bigId, retired.ReplacedByTaskId);
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("BCA")]
    [InlineData("ACB")]
    public async Task PackageCount_RegistrationOrder_KeepsInitialCountsStable(string order)
    {
        using var harness = await VerificationHarness.CreateAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var (aId, bId, cId) = await CreateThreeCollectionsAsync(harness, order);

        // 包计数是初始候选正片数，不随前面裁决取消文件而缩小：A=4、B=3、C=5，无论登记顺序，
        // C 赢 E1-E3，A 的 4 个正片仍压过 B 的 3 个而保留 E4，A 不整包退出；B/C 独有集保留。
        Assert.Equal(new[] { false, false, false, true }, await SelectedFilesAsync(harness, aId));
        Assert.Equal(new[] { false, true, true }, await SelectedFilesAsync(harness, bId));
        Assert.Equal(new[] { true, true, true, true, true }, await SelectedFilesAsync(harness, cId));
        Assert.NotEqual(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(aId)).Status);
        Assert.NotEqual(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(bId)).Status);
    }

    private static async Task<(Guid A, Guid B, Guid C)> CreateThreeCollectionsAsync(
        VerificationHarness harness, string order)
    {
        var episodes = new Dictionary<char, int[]>
        {
            ['A'] = [1, 2, 3, 4],
            ['B'] = [4, 5, 6],
            ['C'] = [1, 2, 3, 7, 8]
        };
        var ids = new Dictionary<char, Guid>();
        foreach (var label in order)
            ids[label] = await harness.CreateUploadAsync(ContainmentBatch(
                episodes[label].Select(episode => ContainmentFileName(episode)).ToArray()));
        return (ids['A'], ids['B'], ids['C']);
    }

    private static async Task<bool[]> SelectedFilesAsync(VerificationHarness harness, Guid taskId)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.DownloadTaskFiles.AsNoTracking()
                       .Where(file => file.DownloadTaskId == taskId)
                       .OrderBy(file => file.FileIndex)
                       .Select(file => file.IsSelectedForDownload)
                       .ToArrayAsync();
    }
}
