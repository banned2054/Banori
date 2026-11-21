using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Banori.Tests.Downloads;

/// <summary>
///     P1-1：归档受阻后，界面「重新核对归档」应按本轮下载器布局核对保存根，
///     走完「受阻 → 解除占用 → 正式归档确认」，全程逐集现任只在正式路径被观察后切换。
/// </summary>
public sealed class DownloadArchiveRetryRegressionTests
{
    [Fact]
    public async Task RetryArchive_AfterConflictRemoved_PublishesFormalPathAndClearsReview()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = CreateTwoEpisodeTorrent();
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relatives = torrent.Files
                               .Select(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile))
                               .ToArray();
        Assert.Equal(2, relatives.Length);

        // 历史成片已改名；EpisodeVersion 逐集指向这两条已发布路径。
        h.Store.AddTorrent(task.InfoHash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath, relatives);
        h.Store.FileIdentities[task.InfoHash] = relatives
                                               .Select((path, index) =>
                                                           new TorrentFileSnapshot(index, path, 1024, 1.0))
                                               .ToList();
        await fileService.InitializeAsync(taskId);
        var targets    = await fileService.ComputeArchivalTargetsAsync(taskId);
        var formalRoot = ParentOf(targets[0].TargetPath);
        var oldPaths   = targets.Select(target => target.TargetPath + ".renamed").ToArray();
        h.Store.Torrents.Remove(task.InfoHash);
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status              = DownloadTaskStatus.Completed;
            entity.CompletedAtUtc      = h.Time.GetUtcNow();
            entity.TorrentMissingAtUtc = h.Time.GetUtcNow();
            entity.SavePath            = formalRoot;
            var files = await db.DownloadTaskFiles.Where(value => value.DownloadTaskId == taskId)
                                .OrderBy(value => value.FileIndex).ToListAsync();
            for (var index = 0; index < files.Count; index++)
            {
                var name = BaseNameOf(oldPaths[index]);
                files[index].CurrentRelativePath   = name;
                files[index].PublishedRelativePath = name;
                files[index].PublishedPath         = oldPaths[index];
                db.EpisodeVersions.Add(new EpisodeVersion
                {
                    SubjectId            = 42,
                    Episode              = index + 1,
                    MediaType            = "SingleEpisode",
                    SourceScope          = SourceScopes.Legacy,
                    IncumbentVideoFileId = files[index].Id,
                    UpdatedAtUtc         = h.Time.GetUtcNow()
                });
            }

            await db.SaveChangesAsync();
        }

        var blockedIncumbents = await IncumbentPathsAsync(h);
        Assert.Equal(oldPaths, blockedIncumbents);

        // 新一轮重下：下载器已在本轮任务专属根，暂存改名延迟到进度 100% 才确认。
        var staging     = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, taskId);
        var stagedNames = targets.Select(target => BaseNameOf(target.TargetPath)).ToArray();
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status                = DownloadTaskStatus.Downloading;
            entity.SubmittedAtUtc        = h.Time.GetUtcNow();
            entity.PendingSubmitSavePath = staging;
            var files = await db.DownloadTaskFiles.Where(value => value.DownloadTaskId == taskId)
                                .OrderBy(value => value.FileIndex).ToListAsync();
            foreach (var (file, relative) in files.Zip(relatives))
            {
                file.CurrentRelativePath = relative;
                file.PendingRenameFrom   = null;
                file.PendingRenameTo     = null;
            }

            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(task.InfoHash, "New", QbittorrentTorrentCategory.Downloading, 0.5, staging, relatives);
        h.Store.FileIdentities[task.InfoHash] = relatives
                                               .Select((path, index) =>
                                                           new TorrentFileSnapshot(index, path, 1024, 0.5))
                                               .ToList();
        h.Store.DeferRenames = true;
        await using var session = await h.Provider.GetRequiredService<IQbittorrentSessionFactory>().CreateAsync();
        Assert.False(await fileService.SettleNamingUnderGateAsync(
                                                                  session, taskId, h.Store.Torrents[task.InfoHash]));
        h.Store.CompleteRenames();
        string[] stagedRemoteNames;
        await using (var db = await h.CreateDbContextAsync())
        {
            var files = await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == taskId)
                                .OrderBy(file => file.FileIndex).ToListAsync();
            stagedRemoteNames = files.Select(file => file.PendingRenameTo!).ToArray();
        }

        h.Store.FileIdentities[task.InfoHash] = stagedRemoteNames
                                               .Select((path, index) =>
                                                           new TorrentFileSnapshot(index, path, 1024, 1.0))
                                               .ToList();
        h.Store.Torrents[task.InfoHash] = h.Store.Torrents[task.InfoHash] with
        {
            Progress = 1, Category = QbittorrentTorrentCategory.Seeding
        };
        Assert.True(await fileService.SettleNamingUnderGateAsync(
                                                                 session, taskId, h.Store.Torrents[task.InfoHash]));
        Assert.Equal(blockedIncumbents, await IncumbentPathsAsync(h));

        // 正式目标被外部种子占住：自动归档只登记 ArchiveReview，不发布暂存路径。
        var occupants = new[] { new string('e', 40), new string('f', 40) };
        for (var index = 0; index < occupants.Length; index++)
        {
            var name = stagedNames[index];
            h.Store.AddTorrent(occupants[index], "External " + index, QbittorrentTorrentCategory.Seeding,
                               1, formalRoot, [name]);
            h.Store.FileIdentities[occupants[index]] = [new TorrentFileSnapshot(0, name, 1024, 1.0)];
        }

        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status = DownloadTaskStatus.Completed;
            await db.SaveChangesAsync();
        }

        var snapshot = h.Store.Torrents[task.InfoHash];
        Assert.True(await fileService.ArchiveCompletedUnderGateAsync(session, taskId, snapshot));
        Assert.NotNull(DownloadArchiveReview.FromJson((await h.GetTaskAsync(taskId)).ArchiveReview));
        Assert.Equal(blockedIncumbents, await IncumbentPathsAsync(h));
        Assert.DoesNotContain(task.InfoHash, h.Store.SetLocationCalls.Select(call => call.Hash));

        // 解除占用：qB 位于已登记的本轮暂存根、未移动、所有选中文件均为 100%。
        foreach (var hash in occupants) h.Store.Torrents.Remove(hash);
        var stagedSnapshot = h.Store.Torrents[task.InfoHash];
        Assert.Equal(staging, stagedSnapshot.SavePath);
        Assert.False(stagedSnapshot.IsMoving);
        var stagedState = await h.GetTaskAsync(taskId);
        Assert.Equal(staging, stagedState.PendingSubmitSavePath);
        Assert.All(stagedState.Files.Where(file => file.IsSelectedForDownload),
                   file => Assert.Equal(stagedRemoteNames[file.FileIndex], file.CurrentRelativePath));
        Assert.All(h.Store.FileIdentities[task.InfoHash],
                   file => Assert.Equal(1.0, file.Progress));

        // 移动异步确认：正式路径观察前，逐集现任仍指向旧成片。
        h.Store.DeferRenames  = false;
        h.Store.DeferLocation = true;
        await fileService.RetryArchiveAsync(taskId);
        Assert.Contains(task.InfoHash, h.Store.SetLocationCalls.Select(call => call.Hash));
        var pendingIncumbents = await IncumbentPathsAsync(h);
        Assert.Equal(blockedIncumbents, pendingIncumbents);
        Assert.NotNull(DownloadArchiveReview.FromJson((await h.GetTaskAsync(taskId)).ArchiveReview));

        // 观察正式目标后再次走同一入口：现任切到正式成片，归档核对记录清除。
        h.Store.CompleteMoves();
        Assert.Equal(formalRoot, h.Store.Torrents[task.InfoHash].SavePath);
        await fileService.RetryArchiveAsync(taskId);
        var confirmedIncumbents = await IncumbentPathsAsync(h);
        Assert.Equal(targets.Select(target => target.TargetPath).ToArray(), confirmedIncumbents);
        Assert.Null(DownloadArchiveReview.FromJson((await h.GetTaskAsync(taskId)).ArchiveReview));
    }

    private static byte[] CreateTwoEpisodeTorrent()
    {
        static string B(string value) => Encoding.UTF8.GetByteCount(value) + ":" + value;
        var names = new[]
        {
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][01][1080p][简日双语].mp4",
            "【喵萌奶茶屋】★07月新番★[与你相恋到生命尽头][02][1080p][简日双语].mp4"
        };
        var entries = string.Concat(names.Select(name => "d6:lengthi1024e4:pathl" + B(name) + "ee"));
        return Encoding.UTF8.GetBytes("d4:infod5:filesl" + entries + "e4:name" + B("Dual Episode Bundle") +
                                      "12:piece lengthi16384e6:pieces" + B(new string('0', 20)) + "ee");
    }

    private static string ParentOf(string path) => path[..path.LastIndexOf('/')];

    private static string BaseNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static async Task<string[]> IncumbentPathsAsync(SchedulingHarness h)
    {
        await using var db = await h.CreateDbContextAsync();
        var rows = await db.EpisodeVersions.AsNoTracking()
                           .Where(value => value.SubjectId == 42 && value.IncumbentVideoFileId != null)
                           .OrderBy(value => value.Episode).ToListAsync();
        Assert.Equal(2, rows.Count);
        var ids = rows.Select(value => value.IncumbentVideoFileId!.Value).ToArray();
        var files = await db.DownloadTaskFiles.Include(value => value.DownloadTask).AsNoTracking()
                            .Where(value => ids.Contains(value.Id)).ToListAsync();
        return ids.Select(id =>
        {
            var file = files.Single(value => value.Id == id);
            return DownloadRoundLayout.PublishedAbsolute(file.DownloadTask!, file);
        }).ToArray();
    }
}
