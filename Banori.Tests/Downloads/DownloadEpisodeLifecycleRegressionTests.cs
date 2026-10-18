using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeVersionGuard_UsesEpisodeFileVersionForPendingAndCompletion(bool replacePending)
    {
        using var h = await CreateHarnessAsync();
        await h.SaveSelectionRulesAsync(NekoRules());
        var oldName = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v2]");
        var oldId   = await h.CreateUploadAsync(TestTorrentBytes.Create(oldName), "byrbt");
        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(oldId)).InfoHash);
        await h.ReconcileAsync();

        var v3   = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v3]");
        var next = await h.CreateUploadAsync(BatchTorrent(v3, NekoTitle("简日双语", episode : 13)), "mikan");
        Assert.Equal(1, (await h.GetTaskAsync(next)).Version);
        var nextFile = await FindAnimeFileIdAsync(h, next);
        Assert.Equal(nextFile, (await FindEpisodeVersionAsync(h, 12))!.PendingVideoFileId);

        if (replacePending)
        {
            var v4     = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v4]");
            var latest = await h.CreateUploadAsync(BatchTorrent(v4, NekoTitle("简日双语", episode : 14)), "tjupt");
            Assert.Equal(await FindAnimeFileIdAsync(h, latest),
                         (await FindEpisodeVersionAsync(h, 12))!.PendingVideoFileId);
        }

        await h.ReconcileAsync();
        CompleteTorrent(h, (await h.GetTaskAsync(next)).InfoHash);
        await h.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await h.GetTaskAsync(next)).Status);
        Assert.Equal(nextFile, (await FindEpisodeVersionAsync(h, 12))!.IncumbentVideoFileId);
        if (replacePending)
        {
            var current = await FindEpisodeVersionAsync(h, 12);
            Assert.NotEqual(nextFile, current!.PendingVideoFileId);
        }
    }

    [Fact]
    public async Task EpisodeVersionGuard_UnparseableSingleVideoWithSubtitle_KeepsTaskVersionFallback()
    {
        using var harness     = await CreateHarnessAsync();
        var       incumbentId = await harness.CreateUploadAsync(BatchTorrent("feature.mkv", "feature.ass"), "mikan");
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.Include(value => value.Files)
                               .SingleAsync(value => value.Id == incumbentId);
            task.Episode = 12;
            task.Version = 2;
            foreach (var file in task.Files)
            {
                file.Kind                  = file.FileIndex == 0 ? DownloadFileKind.Anime : DownloadFileKind.Subtitle;
                file.EpisodeNumberOverride = 12;
                file.IsManualOverride      = true;
            }

            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(incumbentId)).InfoHash);
        await harness.ReconcileAsync();
        var incumbentFile = await FindAnimeFileIdAsync(harness, incumbentId);

        var challengerId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                                        NekoTitle("简日双语", episode : 13)), "byrbt");
        Assert.Null((await FindEpisodeVersionAsync(harness, 12))!.PendingVideoFileId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(challengerId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(challengerId)).Status);
        Assert.Equal(incumbentFile, (await FindEpisodeVersionAsync(harness, 12))!.IncumbentVideoFileId);
    }

}
