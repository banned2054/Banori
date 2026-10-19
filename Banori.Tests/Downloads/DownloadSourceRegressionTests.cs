using Banori.Core.Torrents;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateUpload_LegacyU2SourceParticipatesInSameGroupArbitration(bool incomingIsBetter)
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        const string tracker        = "https://daydream.dmhy.best/announce";
        var          incumbentTitle = NekoTitle(incomingIsBetter ? "繁日双语" : "简日双语");
        var          incomingTitle  = NekoTitle(incomingIsBetter ? "简日双语" : "繁日双语");
        var          incumbentId    = await harness.CreateUploadAsync(TestTorrentBytes.Create(incumbentTitle, tracker));

        await using (var db = await harness.CreateDbContextAsync())
        {
            var incumbent = await db.DownloadTasks.SingleAsync(task => task.Id == incumbentId);
            incumbent.SiteSource = "U2";
            await db.SaveChangesAsync();
        }

        if (incomingIsBetter)
        {
            var incomingId = await harness.CreateUploadAsync(TestTorrentBytes.Create(incomingTitle, tracker));
            var incoming   = await harness.GetTaskAsync(incomingId);
            var incumbent  = await harness.GetTaskAsync(incumbentId);
            Assert.Equal("u2", incoming.SiteSource);
            Assert.Equal(incumbentId, incoming.ReplacesTaskId);
            Assert.Equal(DownloadTaskStatus.Replaced, incumbent.Status);
            Assert.Equal(incomingId, incumbent.ReplacedByTaskId);
        }
        else
        {
            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                        harness.CreateUploadAsync(TestTorrentBytes
                                                                                    .Create(incomingTitle, tracker)));
            Assert.Contains("同集已有更优任务", exception.Message);
            var incumbent = await harness.GetTaskAsync(incumbentId);
            Assert.Equal(DownloadTaskStatus.PendingSubmit, incumbent.Status);
            Assert.Null(incumbent.ReplacedByTaskId);
        }

        Assert.Equal("U2", (await harness.GetTaskAsync(incumbentId)).SiteSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"title\":\"作品\",\"episodeText\":\"\",\"source\":\"mikan\"}")]
    [InlineData("{\"title\":\"\",\"episodeText\":\"01\",\"source\":\"mikan\"}")]
    public async Task CreateDownload_InvalidNamingIngredientsRequiresReevaluation(string? invalidIngredients)
    {
        using var    harness = await CreateHarnessAsync();
        const string oldSnapshot = "Source Anime E01 密文残留";
        var          itemId = await SeedAcceptedItemAsync(harness, "legacy-naming", "Source Anime - 01", oldSnapshot);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var item = await db.FeedItems.SingleAsync(value => value.Id == itemId);
            item.NamingIngredients = invalidIngredients;
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));
        Assert.Contains("命名要素", exception.Message);
        Assert.Contains("重新评估", exception.Message);
        await harness.ReconcileAsync();
        Assert.Empty(harness.Store.AddCalls);

        await using (var db = await harness.CreateDbContextAsync())
        {
            Assert.Empty(await db.DownloadTasks.ToListAsync());
            Assert.Empty(await db.DownloadTaskFiles.ToListAsync());
            var item = await db.FeedItems.SingleAsync(value => value.Id == itemId);
            Assert.Equal(oldSnapshot, item.NamingPreview);
            Assert.Equal(invalidIngredients, item.NamingIngredients);

            // 模拟重新评估补齐要素；后续登记不再使用旧的错误快照。
            var ingredients = new TorrentNamingIngredients("{title} E{episode} {source} {site.source}",
                                                           "测试作品", "01", 1, "Sc", "mikan");
            item.NamingIngredients = ingredients.ToJson();
            item.NamingPreview     = "测试作品 E01 mikan mikan";
            await db.SaveChangesAsync();
        }

        var taskId = await harness.CreateDownloadAsync(itemId);
        var task   = await harness.GetTaskAsync(taskId);
        Assert.Equal("mikan", task.SiteSource);
        Assert.Equal("测试作品 E01 mikan mikan", task.NamingSnapshot);
    }
}
