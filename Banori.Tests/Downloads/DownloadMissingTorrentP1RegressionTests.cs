using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>
///     复审两项 P1 的入口回归：已发布成片布局独立于本轮下载器布局；待确认 Add 持续占用实际提交路径。
///     全部经 RetryAsync / ReconcileAsync / DownloadFileService 正常入口与 fake qB 驱动。
/// </summary>
public sealed class DownloadMissingTorrentP1RegressionTests
{
    [Fact]
    public async Task ArchivedRename_ReDownload_IncumbentPathSurvivesAddRestartFailureUntilArchive()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Incumbent Layout Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);
        var target           = Assert.Single(await fileService.ComputeArchivalTargetsAsync(taskId));
        var slash            = target.TargetPath.LastIndexOf('/');
        var archivedRoot     = target.TargetPath[..slash];
        var archivedRelative = target.TargetPath[(slash + 1)..] + ".renamed";
        var archivedPath     = archivedRoot                     + "/" + archivedRelative;

        h.Store.Torrents.Remove(task.InfoHash);
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id                 == taskId);
            var file   = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            entity.Status              = DownloadTaskStatus.Completed;
            entity.CompletedAtUtc      = h.Time.GetUtcNow();
            entity.TorrentMissingAtUtc = h.Time.GetUtcNow();
            entity.SavePath            = archivedRoot;
            file.CurrentRelativePath   = archivedRelative;
            file.VerificationPath      = null;
            // 真实的逐集现任引用指向这条已改名的成片文件行。
            db.EpisodeVersions.Add(new EpisodeVersion
            {
                SubjectId            = 42,
                Episode              = 1,
                MediaType            = "SingleEpisode",
                SourceScope          = SourceScopes.Legacy,
                IncumbentVideoFileId = file.Id,
                UpdatedAtUtc         = h.Time.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));

        await ServiceOf(h).RetryAsync(taskId);
        await h.ReconcileNowAsync();

        var afterAdd = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, afterAdd.Status);
        Assert.Equal(archivedRoot, afterAdd.SavePath);
        Assert.NotNull(afterAdd.PendingSubmitSavePath);
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(relative, file.CurrentRelativePath);
            Assert.Equal(archivedRelative, file.PublishedRelativePath);
            Assert.Equal(archivedPath, file.PublishedPath);
        }

        // Add 后、以及另建服务实例（重启）读取，现任仍解析到旧成片完整路径。
        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));
        Assert.Equal(archivedPath, await IncumbentPathViaNewServiceAsync(h, taskId));
        Assert.Equal(archivedPath, await IncumbentPathViaReopenedDatabaseAsync(h, taskId));

        // 新一轮失败：留在旧成片根与旧逐文件路径。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status     = DownloadTaskStatus.Failed;
            entity.FailedStep = "测试模拟失败";
            await db.SaveChangesAsync();
        }

        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));

        // 手动把任务恢复到本轮下载状态，继续验证成功归档后的发布。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status               = DownloadTaskStatus.Downloading;
            entity.IsSystemPaused       = true;
            entity.FailedStep           = null;
            entity.NextObservationAtUtc = null;
            await db.SaveChangesAsync();
        }

        // 新种子在暂存根完成并归档成功后才发布最终路径。
        var staging = afterAdd.PendingSubmitSavePath!;
        h.Store.Torrents.Remove(task.InfoHash);
        h.Store.StoppedAdds.Remove(task.InfoHash);
        h.Store.AddTorrent(task.InfoHash, "New", QbittorrentTorrentCategory.Seeding, 1, staging);
        h.Store.FileIdentities[task.InfoHash] =
            [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length, 1.0)];
        for (var i = 0; i < 8; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(60));
            await ServiceOf(h).ReconcileAsync();
        }

        var completed = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.PendingSubmitSavePath);
        Assert.Null(completed.ArchiveReview);
        Assert.Equal(target.TargetPath, await IncumbentPathAsync(h, taskId));
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(target.TargetPath, file.PublishedPath);
        }
    }

    [Fact]
    public async Task ArchivedRename_IsolationReAddAndPartialVerification_KeepUnfinishedOnPublishedPath()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Isolation Layout Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);
        var archivedRoot     = "/downloads/Media/Anime/Archived";
        var archivedRelative = "isolation-layout.renamed.mkv";
        var archivedPath     = archivedRoot + "/" + archivedRelative;
        h.Store.Torrents.Remove(task.InfoHash);
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id                 == taskId);
            var file   = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            entity.Status              = DownloadTaskStatus.Completed;
            entity.CompletedAtUtc      = h.Time.GetUtcNow();
            entity.TorrentMissingAtUtc = h.Time.GetUtcNow();
            entity.SavePath            = archivedRoot;
            file.CurrentRelativePath   = archivedRelative;
            db.EpisodeVersions.Add(new EpisodeVersion
            {
                SubjectId            = 42,
                Episode              = 2,
                SourceScope          = SourceScopes.Legacy,
                IncumbentVideoFileId = file.Id,
                UpdatedAtUtc         = h.Time.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));

        // 旧校验意图未收敛 → 人工重下载转任务专属隔离重加。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.VerificationIntent = new DownloadVerificationIntent
            {
                Round = 1, Step = DownloadVerificationStep.Unconfirmed, CreatedAtUtc = h.Time.GetUtcNow(), Files = []
            }.ToJson();
            await db.SaveChangesAsync();
        }

        var taskRoot = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, taskId);
        h.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
        {
            await using var probe   = await h.CreateDbContextAsync();
            var             pending = await probe.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
            Assert.Equal(taskRoot, pending.PendingSubmitSavePath);
        };
        await ServiceOf(h).RetryAsync(taskId);
        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));

        await h.ReconcileNowAsync();
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(taskId)).IsolationState);
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(taskRoot, Assert.Single(h.Store.AddCalls).SavePath);
        Assert.Equal(taskRoot, Assert.Single(h.Store.AddEntries).SavePath);
        h.Factory.SessionConfigurator = null;
        var readding = await h.GetTaskAsync(taskId);
        Assert.Equal(archivedRoot, readding.SavePath);
        Assert.Equal(taskRoot, readding.PendingSubmitSavePath);

        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        var restored = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, restored.IsolationState);
        Assert.Equal(archivedRoot, restored.SavePath);
        Assert.Equal(taskRoot, restored.PendingSubmitSavePath);
        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));

        // 部分校验通过：已确认文件登记 ReusedVerified，未完成文件仍沿旧发布路径。
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.VerificationOutcome = DownloadFileVerificationOutcomes.ReusedVerified;
            file.VerificationPath    = archivedPath;
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        Assert.Equal(archivedPath, await IncumbentPathAsync(h, taskId));
    }

    [Fact]
    public async Task PendingAdd_OriginalRelativePathBlocksSecondDifferentHashAtSameFullPath()
    {
        using var    h            = await SchedulingHarness.CreateAsync();
        var          first        = TestTorrentBytes.Create("Pending Original Anime - 01");
        var          firstId      = await h.CreateUploadAsync(first);
        var          firstTask    = await h.GetTaskAsync(firstId);
        const string archivedRoot = "/downloads/Media/Anime/Shared";
        const string sharedName   = "Shared Archive - 01.mkv";

        // 第一个 Add 已落库但暂不可见：文件行仍是归档名，提交根已占用实际路径。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == firstId);
            entity.Status                 = DownloadTaskStatus.Failed;
            entity.FailedStep             = "提交";
            entity.CompletedAtUtc         = h.Time.GetUtcNow();
            entity.SavePath               = "/downloads/Other";
            entity.PendingSubmitSavePath  = archivedRoot;
            entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow();
            await SeedFileAsync(db, firstId, sharedName, "not-the-archived-name.mkv", 1024, h.Time.GetUtcNow());
            await db.SaveChangesAsync();
        }

        var second     = TestTorrentBytes.Create(sharedName);
        var secondId   = await h.CreateUploadAsync(second);
        var secondHash = (await h.GetTaskAsync(secondId)).InfoHash;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == secondId);
            // 含 Seed 附件时提交根直接用 DownloadRootPath，正好是旧成片归档根。
            entity.DownloadRootPath = archivedRoot;
            entity.SavePath = archivedRoot;
            var own = await db.DownloadTaskFiles.SingleOrDefaultAsync(value => value.DownloadTaskId == secondId);
            if (own is not null) own.Kind = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        var secondAfter = await h.GetTaskAsync(secondId);
        Assert.Equal(DownloadTaskStatus.Failed, secondAfter.Status);
        Assert.Equal("提交路径", secondAfter.FailedStep);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == secondHash));
    }

    [Fact]
    public async Task PendingAdd_TaskStagingAndManualRetryNeverIssueDuplicateAdd()
    {
        using var    h          = await SchedulingHarness.CreateAsync();
        var          first      = TestTorrentBytes.Create("Staging Original Anime - 01");
        var          firstId    = await h.CreateUploadAsync(first);
        var          firstTask  = await h.GetTaskAsync(firstId);
        const string sharedName = "Staged Shared - 01.mkv";

        var second     = TestTorrentBytes.Create(sharedName);
        var secondId   = await h.CreateUploadAsync(second);
        var secondTask = await h.GetTaskAsync(secondId);
        var secondHash = secondTask.InfoHash;
        var taskRoot   = DownloadTaskPlacement.TaskRoot("/downloads", secondId);
        // 在 Add 方法入口回读 SQLite：实际提交根必须先落库，再向下载器发出隔离重加。
        h.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
        {
            await using var probe = await h.CreateDbContextAsync();
            var pending = await probe.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == secondId);
            Assert.Equal(taskRoot, pending.PendingSubmitSavePath);
        };

        // 另一个哈希走任务专属暂存；待确认根正是该任务根，原始文件名与占用种子相同。
        await using (var db = await h.CreateDbContextAsync())
        {
            var owner = await db.DownloadTasks.SingleAsync(value => value.Id == firstId);
            owner.Status                 = DownloadTaskStatus.Failed;
            owner.FailedStep             = "提交";
            owner.CompletedAtUtc         = h.Time.GetUtcNow();
            owner.SavePath               = "/downloads/Other";
            owner.PendingSubmitSavePath  = taskRoot;
            owner.LastSubmitAttemptAtUtc = h.Time.GetUtcNow();
            await SeedFileAsync(db, firstId, sharedName, "renamed-elsewhere.mkv", 1024, h.Time.GetUtcNow());
            // 相同路径与大小的复用例外不能放行仍待确认的 Add。
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == firstId);
            file.VerificationOutcome = DownloadFileVerificationOutcomes.ReusedVerified;
            file.VerificationPath    = taskRoot + "/" + sharedName;

            var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == secondId);
            challenger.Status                = DownloadTaskStatus.Downloading;
            challenger.IsSystemPaused        = true;
            challenger.DownloadRootPath      = "/downloads";
            challenger.IsolationState        = DownloadIsolationState.Deleting;
            challenger.PendingSubmitSavePath = null;
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync(); // Deleting → Readding
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(secondId)).IsolationState);
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        var blocked = await h.GetTaskAsync(secondId);
        Assert.Equal(DownloadTaskStatus.Failed, blocked.Status);
        Assert.Equal("提交路径", blocked.FailedStep);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == secondHash));
        h.Factory.SessionConfigurator = null;

        // 真正的明确拒绝：首次提交（此前没有未决占用）返回 Fails. 且按 hash 查无，应收尾释放占用。
        var rejectedId = await h.CreateUploadAsync(TestTorrentBytes.Create("Explicit Reject Anime - 01"));
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == rejectedId);
            entity.Status                = DownloadTaskStatus.PendingSubmit;
            entity.PendingSubmitSavePath = null;
            await db.SaveChangesAsync();
        }

        h.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await ServiceOf(h).ReconcileAsync();
        var rejected = await h.GetTaskAsync(rejectedId);
        Assert.Equal(DownloadTaskStatus.Failed, rejected.Status);
        Assert.Equal("提交", rejected.FailedStep);
        Assert.Null(rejected.PendingSubmitSavePath);
    }

    [Fact]
    public async Task AcceptedButHiddenAdd_ManualRetryWithinWindow_IssuesNoSecondAdd()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       bytes  = TestTorrentBytes.Create("Hidden Add Anime - 01");
        var       taskId = await h.CreateUploadAsync(bytes);
        var       task   = await h.GetTaskAsync(taskId);
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status              = DownloadTaskStatus.Completed;
            entity.CompletedAtUtc      = h.Time.GetUtcNow();
            entity.TorrentMissingAtUtc = h.Time.GetUtcNow();
            await db.SaveChangesAsync();
        }

        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(taskId);
        await ServiceOf(h).ReconcileAsync();
        h.Store.HiddenTorrents.Add(task.InfoHash);

        var submitting = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Submitting, submitting.Status);
        Assert.NotNull(submitting.PendingSubmitSavePath);

        // 失败状态下的人工重试同样要尊重仍在生效的 Add 可见性窗口，不能立即补发。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status     = DownloadTaskStatus.Failed;
            entity.FailedStep = "提交";
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOf(h).RetryAsync(taskId));
        Assert.Contains("可见", error.Message);
        Assert.Single(h.Store.AddCalls);
        Assert.NotNull((await h.GetTaskAsync(taskId)).PendingSubmitSavePath);
    }

    /// <summary>
    ///     P1-1 双集独立复现：下载期暂存日志确认时整种已 100%，但正式归档仍被外部种子阻挡。
    ///     在 Mapping 改名日志确认、归档受阻、解除阻塞并确认正式移动后，逐步从数据库重读两个现任文件，
    ///     断言只有正式路径被实际观察后才从旧成片切走。
    /// </summary>
    [Fact]
    public async Task DualEpisode_StagedRenameConfirmedAtFullProgress_KeepsBothIncumbentsUntilFormalMove()
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

        h.Store.AddTorrent(task.InfoHash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath,
                           relatives);
        h.Store.FileIdentities[task.InfoHash] = relatives
                                               .Select((path, index) =>
                                                           new TorrentFileSnapshot(index, path, 1024, 1.0))
                                               .ToList();
        await fileService.InitializeAsync(taskId);
        var targets = await fileService.ComputeArchivalTargetsAsync(taskId);
        Assert.Equal(2, targets.Count);
        var formalRoot = ParentOf(targets[0].TargetPath);
        Assert.All(targets, target => Assert.Equal(formalRoot, ParentOf(target.TargetPath)));

        var oldPaths = targets.Select(target => target.TargetPath + ".renamed").ToArray();
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

        Assert.Equal(oldPaths, await IncumbentPathsAsync(h, taskId));

        // 人工重下载：Add 已在任务专属暂存根生效，但成片路径仍是旧发布路径；
        // 暂存目录中的 Mapping 改名日志已确认，整种随后报告 100%。
        var staging     = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, taskId);
        var stagedNames = targets.Select(target => BaseNameOf(target.TargetPath)).ToArray();
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status                = DownloadTaskStatus.Downloading;
            entity.SubmittedAtUtc        = h.Time.GetUtcNow();
            entity.PendingSubmitSavePath = staging;
            entity.PendingSavePath       = staging;
            var files = await db.DownloadTaskFiles.Where(value => value.DownloadTaskId == taskId)
                                .OrderBy(value => value.FileIndex).ToListAsync();
            for (var index = 0; index < files.Count; index++)
            {
                files[index].CurrentRelativePath = relatives[index];
                files[index].PendingRenameFrom   = relatives[index];
                files[index].PendingRenameTo     = stagedNames[index];
            }

            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(task.InfoHash, "New", QbittorrentTorrentCategory.Seeding, 1, staging, stagedNames);
        h.Store.FileIdentities[task.InfoHash] = stagedNames
                                               .Select((path, index) =>
                                                           new TorrentFileSnapshot(index, path, 1024, 1.0))
                                               .ToList();

        // 正式目标被两个外部种子占据；这一步只应留下 ArchiveReview，不能发布暂存路径。
        var occupants = new[] { new string('e', 40), new string('f', 40) };
        for (var index = 0; index < occupants.Length; index++)
        {
            var name = BaseNameOf(targets[index].TargetPath);
            h.Store.AddTorrent(occupants[index], "External " + index, QbittorrentTorrentCategory.Seeding,
                               1, formalRoot, [name]);
            h.Store.FileIdentities[occupants[index]] = [new TorrentFileSnapshot(0, name, 1024, 1.0)];
        }

        await using var session = await h.Provider.GetRequiredService<IQbittorrentSessionFactory>().CreateAsync();
        using var       lease = await h.Provider.GetRequiredService<DownloadCoordinationGate>().EnterAsync();
        var             snapshot = h.Store.Torrents[task.InfoHash];
        var             confirmedStaging = await fileService.SettleNamingUnderGateAsync(session, taskId, snapshot);
        Assert.True(confirmedStaging);
        Assert.Equal(oldPaths, await IncumbentPathsAsync(h, taskId));

        var blocked = await fileService.ArchiveCompletedUnderGateAsync(session, taskId, snapshot);
        Assert.True(blocked);
        Assert.NotNull(DownloadArchiveReview.FromJson((await h.GetTaskAsync(taskId)).ArchiveReview));
        Assert.Equal(oldPaths, await IncumbentPathsAsync(h, taskId));
        Assert.DoesNotContain(task.InfoHash, h.Store.SetLocationCalls.Select(call => call.Hash));

        // 解除占用并观察到种子真正迁入正式根后，两个现任才一起切到正式成片路径。
        foreach (var hash in occupants) h.Store.Torrents.Remove(hash);
        h.Store.DeferLocation = true;
        await fileService.ArchiveCompletedUnderGateAsync(session, taskId, snapshot);
        Assert.Contains(task.InfoHash, h.Store.SetLocationCalls.Select(call => call.Hash));
        Assert.Equal(oldPaths, await IncumbentPathsAsync(h, taskId));

        h.Store.CompleteMoves();
        snapshot = h.Store.Torrents[task.InfoHash] with { SavePath = formalRoot };
        Assert.True(await fileService.ArchiveCompletedUnderGateAsync(session, taskId, snapshot));
        Assert.Equal(targets.Select(target => target.TargetPath).ToArray(), await IncumbentPathsAsync(h, taskId));
    }

    private static async Task SeedFileAsync(
        ApplicationDbContext db, Guid taskId, string original, string current, long size, DateTimeOffset now)
    {
        var file = await db.DownloadTaskFiles.SingleOrDefaultAsync(value => value.DownloadTaskId == taskId);
        if (file is null)
        {
            file = new DownloadTaskFile
            {
                Id                    = Guid.NewGuid(),
                DownloadTaskId        = taskId,
                FileIndex             = 0,
                Kind                  = DownloadFileKind.Anime,
                ClassificationReason  = string.Empty,
                IsSelectedForDownload = true
            };
            db.DownloadTaskFiles.Add(file);
        }

        file.OriginalRelativePath = original;
        file.CurrentRelativePath  = current;
        file.Size                 = size;
        file.UpdatedAtUtc         = now;
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

    private static async Task<string[]> IncumbentPathsAsync(SchedulingHarness h, Guid taskId)
    {
        await using var db = await h.CreateDbContextAsync();
        var rows = await db.EpisodeVersions.AsNoTracking()
                           .Where(value => value.SubjectId == 42 && value.IncumbentVideoFileId != null)
                           .OrderBy(value => value.Episode)
                           .ToListAsync();
        Assert.Equal(2, rows.Count);
        var ids = rows.Select(value => value.IncumbentVideoFileId!.Value).ToArray();
        var files = await db.DownloadTaskFiles.Include(value => value.DownloadTask).AsNoTracking()
                            .Where(value => ids.Contains(value.Id))
                            .ToListAsync();
        return ids.Select(id =>
        {
            var file = files.Single(value => value.Id == id);
            Assert.Equal(taskId, file.DownloadTaskId);
            return DownloadRoundLayout.PublishedAbsolute(file.DownloadTask!, file);
        }).ToArray();
    }

    private static DownloadService ServiceOf(SchedulingHarness h) =>
        h.Provider.GetRequiredService<DownloadService>();

    private static async Task<string> IncumbentPathAsync(SchedulingHarness h, Guid taskId)
    {
        await using var db = await h.CreateDbContextAsync();
        return await IncumbentPathAsync(db, taskId);
    }

    private static async Task<string> IncumbentPathViaNewServiceAsync(SchedulingHarness h, Guid taskId)
    {
        // 新作用域读取同一持久层，模拟进程重启后的路径解析。
        _ = ServiceOf(h);
        await using var db = await h.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                    .CreateDbContextAsync();
        return await IncumbentPathAsync(db, taskId);
    }

    private static async Task<string> IncumbentPathViaReopenedDatabaseAsync(SchedulingHarness h, Guid taskId)
    {
        // 独立 DbContextFactory 从磁盘重新打开同一 SQLite 文件，模拟另一个进程读取持久状态。
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                     .UseSqlite($"Data Source={h.DatabasePath};Pooling=False").Options;
        var             factory = new PooledDbContextFactory<ApplicationDbContext>(options);
        await using var db      = await factory.CreateDbContextAsync();
        return await IncumbentPathAsync(db, taskId);
    }

    private static async Task<string> IncumbentPathAsync(ApplicationDbContext db, Guid taskId)
    {
        var row = await db.EpisodeVersions.AsNoTracking().SingleAsync(value => value.SubjectId            == 42 &&
                                                                               value.IncumbentVideoFileId != null);
        var file = await db.DownloadTaskFiles.Include(value => value.DownloadTask).AsNoTracking()
                           .SingleAsync(value => value.Id == row.IncumbentVideoFileId!.Value);
        Assert.Equal(taskId, file.DownloadTaskId);
        return DownloadRoundLayout.PublishedAbsolute(file.DownloadTask!, file);
    }
}
