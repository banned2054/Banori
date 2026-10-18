using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Fact]
    public async Task Create_SingleEpisodeChallenger_DoesNotDeselectManuallyOverriddenRange()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await harness.CreateUploadAsync(BatchTorrent(
                                                                 NekoTitle("繁日双语", episode : 12),
                                                                 NekoTitle("繁日双语", episode : 14)), "mikan");
        await harness.ReconcileAsync();
        var files = harness.Provider.GetRequiredService<DownloadFileService>();
        await files.ApplyAsync(oldId, 0,
                               new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 12, EpisodeEnd : 13));

        await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语", episode : 12)), "mikan");

        await using var db = await harness.CreateDbContextAsync();
        var range = await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == oldId && file.FileIndex == 0);
        Assert.True(range.IsSelectedForDownload);
        Assert.Null(range.SupersededByTaskId);
        Assert.Equal(13m, range.EpisodeEndOverride);
    }

    [Fact]
    public async Task Create_Challenger_DoesNotUseStaleTaskEpisodeForManuallyOverriddenRange()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        await harness.ReconcileAsync();
        await harness.Provider.GetRequiredService<DownloadFileService>().ApplyAsync(oldId, 0,
                 new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 12, EpisodeEnd : 13));

        await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");

        await using var db    = await harness.CreateDbContextAsync();
        var             range = await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == oldId);
        Assert.True(range.IsSelectedForDownload);
        Assert.Null(range.SupersededByTaskId);
        Assert.Equal(DownloadTaskStatus.Downloading, (await harness.GetTaskAsync(oldId)).Status);
    }

    [Fact]
    public async Task Reconcile_ManuallyOverriddenRange_DoesNotPublishStaleTaskEpisodeAsSingleIncumbent()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        var       id      = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        await harness.ReconcileAsync();
        await harness.Provider.GetRequiredService<DownloadFileService>().ApplyAsync(id, 0,
                 new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 12, EpisodeEnd : 13));

        CompleteTorrent(harness, (await harness.GetTaskAsync(id)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(id)).Status);
        Assert.Null(await FindEpisodeVersionAsync(harness, 12));
    }

    [Fact]
    public async Task Create_Challenger_DoesNotClaimDifferentManuallyOverriddenEpisode()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("繁日双语")), "mikan");
        await harness.ReconcileAsync();
        await harness.Provider.GetRequiredService<DownloadFileService>().ApplyAsync(oldId, 0,
                 new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 14));

        await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")), "mikan");

        await using var db         = await harness.CreateDbContextAsync();
        var             overridden = await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == oldId);
        Assert.True(overridden.IsSelectedForDownload);
        Assert.Null(overridden.SupersededByTaskId);
        Assert.Equal(14m, overridden.EpisodeNumberOverride);
    }

    [Fact]
    public async Task Reconcile_BatchIncumbentVersion_IsNotDowngradedByDefaultTaskVersion()
    {
        using var harness = await CreateHarnessAsync();
        var       v3      = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v3]", StringComparison.Ordinal);
        var incumbentId = await harness.CreateUploadAsync(BatchTorrent(v3,
                                                                       NekoTitle("简日双语", episode : 13)), "mikan");
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(incumbentId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(1, (await harness.GetTaskAsync(incumbentId)).Version);
        var incumbentFile = await FindAnimeFileIdAsync(harness, incumbentId);

        var v2 = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v2]", StringComparison.Ordinal);
        var challengerId = await harness.CreateUploadAsync(BatchTorrent(v2,
                                                                        NekoTitle("简日双语", episode : 14)), "byrbt");
        Assert.Null((await FindEpisodeVersionAsync(harness, 12))!.PendingVideoFileId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(challengerId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(challengerId)).Status);
        Assert.Equal(incumbentFile, (await FindEpisodeVersionAsync(harness, 12))!.IncumbentVideoFileId);
    }
}
