using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>无 Seed 包的跨包落选文件继续按旧布局停靠，不因纯正片切根而丢失复用或替换语义。</summary>
public sealed partial class DownloadServiceTests
{
    [Fact]
    public async Task PureAnime_WithParkedLoser_CompletesFromReplacementStaging()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, selectedName, loserName) =
            await SeedParkedLoserAsync(harness, false, true);
        var archivedRelative = "Media/Anime/" + selectedName;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var rows = await db.EpisodeVersions.Where(row => row.SubjectId == 42).ToListAsync();
            Assert.Single(rows, row => row.PendingVideoFileId is not null && row.IncumbentVideoFileId is not null);
            var loaded = await db.DownloadTasks.Include(value => value.Files)
                                 .SingleAsync(value => value.Id == taskId);
            Assert.Equal(42, loaded.SubjectId);
            Assert.Equal("mikan", loaded.SiteSource);
        }

        var fileService = harness.Provider.GetRequiredService<DownloadFileService>();
        Assert.True(await fileService.ArchiveCompletedAsync(taskId));

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.True(task.ArchiveReview is null, DownloadArchiveReview.FromJson(task.ArchiveReview)?.Reason ?? "");
        Assert.Equal("/downloads", task.SavePath);
        Assert.Equal("/downloads", harness.Store.Torrents[hash].SavePath);
        Assert.Equal(archivedRelative, harness.Store.FileIdentities[hash].Single(file => file.Index == 0).Path);
        Assert.Equal(loserName, harness.Store.FileIdentities[hash].Single(file => file.Index        == 1).Path);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var files = await db.DownloadTaskFiles.AsNoTracking()
                                .Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
            Assert.Equal(archivedRelative, files[0].CurrentRelativePath);
            Assert.False(files[1].IsSelectedForDownload);
            Assert.Equal(loserName, files[1].CurrentRelativePath);
            Assert.NotNull(files[1].SupersededByTaskId);
        }

        // 旧版迁让仍生效：现任成片迁入 .replaced，而不是被纯正片新布局直接覆盖。
        Assert.Contains(harness.Store.RenameFileCalls,
                        call => call.NewPath.StartsWith(".replaced/", StringComparison.Ordinal));
        Assert.Contains(harness.Store.FileIdentities,
                        pair => pair.Value.Any(file => file.Path.StartsWith(".replaced/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PureAnime_WithParkedLoser_CompletesFromReusedVerification()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, selectedName, loserName) =
            await SeedParkedLoserAsync(harness, true, false);
        var archivedRelative = "Media/Anime/" + selectedName;
        var ownerPath        = harness.Store.FileIdentities[new string('e', 40)].Single();

        var fileService = harness.Provider.GetRequiredService<DownloadFileService>();
        Assert.True(await fileService.ArchiveCompletedAsync(taskId));

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Null(task.ArchiveReview);
        Assert.Equal("/downloads", task.SavePath);
        Assert.Equal("/downloads", harness.Store.Torrents[hash].SavePath);
        Assert.Equal(archivedRelative, harness.Store.FileIdentities[hash].Single(file => file.Index == 0).Path);
        Assert.Equal(loserName, harness.Store.FileIdentities[hash].Single(file => file.Index        == 1).Path);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var files = await db.DownloadTaskFiles.AsNoTracking()
                                .Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
            Assert.Equal(archivedRelative, files[0].CurrentRelativePath);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, files[0].VerificationOutcome);
            Assert.Equal("/downloads/" + archivedRelative, files[0].VerificationPath);
            Assert.False(files[1].IsSelectedForDownload);
            Assert.Equal(loserName, files[1].CurrentRelativePath);
            Assert.NotNull(files[1].SupersededByTaskId);
        }

        // 复用候选是共享数据：归档不得改名、移动或删除其所在种子。
        Assert.Equal(ownerPath, harness.Store.FileIdentities[new string('e', 40)].Single());
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.DeleteFiles);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.SetLocationCalls);
    }

    /// <summary>
    ///     登记一个纯正片包：第 12 集承担目标路径，第 13 集作为跨包落选文件停在旧版停靠目录；
    ///     replacement 场景放在任务专属根并登记同源现任，reuse 场景放在全局根并登记已验证复用证据。
    /// </summary>
    private static async Task<(Guid Id, string Hash, string SelectedName, string LoserName)> SeedParkedLoserAsync(
        TestHarness harness, bool reuseVerified, bool withIncumbent)
    {
        var bytes  = BatchTorrent(NekoTitle("简日双语", episode : 12), NekoTitle("简日双语", episode : 13));
        var taskId = await harness.CreateUploadAsync(bytes, "mikan");
        var targets = await harness.Provider.GetRequiredService<DownloadFileService>()
                                   .ComputeArchivalTargetsAsync(taskId);
        var selectedName = targets.Single(pair => pair.FileIndex == 0)
                                  .TargetPath["/downloads/Media/Anime/".Length..];
        var root      = reuseVerified ? "/downloads" : DownloadTaskPlacement.TaskRoot("/downloads", taskId);
        var loser     = ".tasks-park/E13.mkv";
        var hash      = (await harness.GetTaskAsync(taskId)).InfoHash;
        var ownerId   = Guid.NewGuid();
        var ownerHash = new string('c', 40);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.Include(value => value.Files).SingleAsync(value => value.Id == taskId);
            task.SubjectId      = 42;
            task.SiteSource     = "mikan";
            task.ReplacesTaskId = withIncumbent ? ownerId : null;
            task.SavePath       = root;
            task.Status         = DownloadTaskStatus.Completed;
            task.SubmittedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            task.CompletedAtUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
            var files = task.Files.OrderBy(value => value.FileIndex).ToList();
            files[0].CurrentRelativePath   = "Media/Anime/" + selectedName;
            files[0].VerificationOutcome   = reuseVerified ? DownloadFileVerificationOutcomes.ReusedVerified : null;
            files[0].VerificationPath      = reuseVerified ? "/downloads/Media/Anime/" + selectedName : null;
            files[1].IsSelectedForDownload = false;
            files[1].SelectionReason       = "跨包落选";
            files[1].SupersededByTaskId    = Guid.NewGuid();
            files[1].CurrentRelativePath   = loser;

            if (withIncumbent)
            {
                var owner = new DownloadTask
                {
                    Id = ownerId, InfoHash = ownerHash, TorrentFile = bytes, SavePath = "/downloads/Media/Anime",
                    DownloadRootPath = "/downloads", MediaDirectory = "Media/Anime", SeedDirectory = "Seed",
                    NamingSnapshot = "Incumbent", NamingIngredients = task.NamingIngredients,
                    SubjectId = 42, SiteSource = "mikan", Status = DownloadTaskStatus.Completed,
                    SubmittedAtUtc = task.SubmittedAtUtc, CompletedAtUtc = task.CompletedAtUtc
                };
                owner.Files.Add(new DownloadTaskFile
                {
                    Id                   = Guid.NewGuid(), DownloadTaskId                     = ownerId, FileIndex = 0,
                    OriginalRelativePath = files[0].OriginalRelativePath, CurrentRelativePath = selectedName,
                    Size                 = files[0].Size, Kind                                = DownloadFileKind.Anime
                });
                db.DownloadTasks.Add(owner);
                db.EpisodeVersions.Add(new EpisodeVersion
                {
                    SubjectId = 42, Episode = 12, MediaType = "TV", SourceScope = SourceScopes.FromSiteSource("mikan"),
                    IncumbentVideoFileId = owner.Files.Single().Id, PendingVideoFileId = files[0].Id,
                    UpdatedAtUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z")
                });
            }

            await db.SaveChangesAsync();
        }

        harness.Store.AddTorrent(hash, "Parked Loser", QbittorrentTorrentCategory.Seeding, 1, root,
                                 ["Media/Anime/" + selectedName, loser]);
        harness.Store.FileIdentities[hash] =
        [
            new TorrentFileSnapshot(0, "Media/Anime/" + selectedName, 1024, 1.0),
            new TorrentFileSnapshot(1, loser, 1024, 1.0)
        ];
        if (withIncumbent)
        {
            harness.Store.AddTorrent(ownerHash, "Incumbent", QbittorrentTorrentCategory.Seeding, 1,
                                     "/downloads/Media/Anime", [selectedName]);
            harness.Store.FileIdentities[ownerHash] = [new TorrentFileSnapshot(0, selectedName, 1024, 1.0)];
        }
        else
        {
            var externalHash = new string('e', 40);
            harness.Store.AddTorrent(externalHash, "Shared Owner", QbittorrentTorrentCategory.Seeding, 1,
                                     "/downloads/Media/Anime", [selectedName]);
            harness.Store.FileIdentities[externalHash] = [new TorrentFileSnapshot(0, selectedName, 1024, 1.0)];
        }

        return (taskId, hash, selectedName, loser);
    }
}
