using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>旧版纯正片布局一次性迁移：绝对路径保持、幂等、重启恢复与各类安全跳过。</summary>
public sealed partial class DownloadServiceTests
{
    [Fact]
    public async Task LegacyLayout_PureAnime_MigratesOnceAndKeepsAbsolutePaths()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, absolute) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        var renameCountBefore   = harness.Store.RenameFileCalls.Count;
        var locationCountBefore = harness.Store.SetLocationCalls.Count;

        await harness.ReconcileAsync();

        var migrated = await harness.GetTaskAsync(taskId);
        Assert.Equal("/downloads/Media/Anime", migrated.SavePath);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.DoesNotContain("Media/Anime/", file.CurrentRelativePath, StringComparison.Ordinal);
            Assert.Equal(absolute, "/downloads/Media/Anime/" + file.CurrentRelativePath);
        }

        Assert.Contains(harness.Store.SetLocationCalls,
                        call => call.Hash == hash && call.SavePath == "/downloads/Media/Anime");
        Assert.True(harness.Store.RenameFileCalls.Count > renameCountBefore);
        Assert.Equal(locationCountBefore + 1, harness.Store.SetLocationCalls.Count);

        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyLayout_DeferredMove_ResumesFromJournalWithoutReplanning()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, absolute) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        harness.Store.DeferLocation = true;

        await harness.ReconcileAsync();

        var pending = await harness.GetTaskAsync(taskId);
        Assert.Equal("/downloads", pending.SavePath);
        Assert.Equal("/downloads/Media/Anime", pending.PendingSavePath);
        Assert.NotNull(DownloadPathDecision.FromJson(pending.PathDecision));
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.StartsWith("Media/Anime/", file.CurrentRelativePath, StringComparison.Ordinal);
            Assert.NotNull(file.PendingRenameFrom);
            Assert.NotNull(file.PendingRenameTo);
        }

        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;
        // 新的服务实例模拟进程重启：只按持久化日志恢复，不重新扫描或重新规划。
        var restarted     = harness.Provider.GetRequiredService<DownloadService>();
        await restarted.ReconcileAsync();
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);

        harness.Store.CompleteMoves();
        await restarted.ReconcileAsync();
        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal("/downloads/Media/Anime", completed.SavePath);
        Assert.Null(completed.PendingSavePath);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(absolute, "/downloads/Media/Anime/" + file.CurrentRelativePath);
            Assert.Null(file.PendingRenameFrom);
            Assert.Null(file.PendingRenameTo);
        }

        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
    }

    [Fact]
    public async Task LegacyLayout_SeedContainingTask_IsSkipped()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, _, _) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.Kind = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();

        Assert.Equal("/downloads", (await harness.GetTaskAsync(taskId)).SavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyLayout_ExternalTakeover_IsSkipped()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, _) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { SavePath = "/external" };

        await harness.ReconcileAsync();

        Assert.Equal("/downloads", (await harness.GetTaskAsync(taskId)).SavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyLayout_IdentityMismatch_IsSkipped()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, _) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;
        harness.Store.FileIdentities[hash] =
            harness.Store.FileIdentities[hash].Select(file => file with { Size = 2048 }).ToList();

        await harness.ReconcileAsync();

        Assert.Equal("/downloads", (await harness.GetTaskAsync(taskId)).SavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyLayout_TargetConflict_IsSkipped()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, _, absolute) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;
        var externalHash  = TestTorrentBytes.InfoHashOf("External occupant " + taskId);
        var occupied      = absolute["/downloads/".Length..];
        harness.Store.AddTorrent(externalHash, "External occupant", QbittorrentTorrentCategory.Seeding, 1,
                                 "/downloads", [occupied]);
        harness.Store.FileIdentities[externalHash] = [new TorrentFileSnapshot(0, occupied, 1024, 1)];

        await harness.ReconcileAsync();

        Assert.Equal("/downloads", (await harness.GetTaskAsync(taskId)).SavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyLayout_MarkerStopsRescanOfLaterTasks()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        await harness.ReconcileAsync();
        var (laterId, _, _) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 13)),
                                           false);
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;

        await harness.ReconcileAsync();

        Assert.Equal("/downloads", (await harness.GetTaskAsync(laterId)).SavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyArchiveJournal_ResumesAfterUpgrade_WithoutReplanningOrDuplicateRequests(bool remoteApplied)
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, original, target) =
            await SeedLegacyArchiveJournalAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)), remoteApplied);
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;

        await harness.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Null(task.FailedStep);
        Assert.Null(task.PendingSavePath);
        Assert.Equal("/downloads", task.SavePath);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(target, file.CurrentRelativePath);
            Assert.Null(file.PendingRenameFrom);
            Assert.Null(file.PendingRenameTo);
        }

        Assert.Equal("/downloads", harness.Store.Torrents[hash].SavePath);
        Assert.Equal(target, harness.Store.FileIdentities[hash].Single().Path);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.Equal(remoteApplied ? renameCount : renameCount + 1, harness.Store.RenameFileCalls.Count);
        if (!remoteApplied)
            Assert.Contains(harness.Store.RenameFileCalls,
                            call => call.Hash == hash && call.OldPath == original && call.NewPath == target);

        var afterRecovery = harness.Store.RenameFileCalls.Count;
        await harness.ReconcileAsync();
        Assert.Equal(afterRecovery, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyArchiveJournal_InvalidTarget_IsRejectedAndStaysPending()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, _, original, _) =
            await SeedLegacyArchiveJournalAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)), false);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            task.PendingSavePath = "/downloads/.legacy";
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.PendingRenameTo = "Outside/Archive/" + original;
            await db.SaveChangesAsync();
        }

        var renameCount = harness.Store.RenameFileCalls.Count;
        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("文件整理", failed.FailedStep);
        Assert.NotNull(failed.PendingSavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
    }

    [Fact]
    public async Task LegacyLayout_InvalidCandidate_IsIsolatedFromOtherTasks()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (validId, validHash, _) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        var invalidId   = await SeedInvalidLegacyTaskAsync(harness, validId);
        var pendingId   = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 13)));
        var invalidHash = new string('b', 40);
        await ClearMigrationMarkerAsync(harness);

        await harness.ReconcileAsync();

        // 坏记录的 ../outside.mkv 会让路径规范化抛异常；它只被单独跳过，不影响同轮其他任务。
        Assert.DoesNotContain(harness.Store.SetLocationCalls,
                              call => call.Hash == invalidHash);
        Assert.DoesNotContain(harness.Store.RenameFileCalls,
                              call => call.Hash == invalidHash);
        Assert.Equal("/downloads/Media/Anime", (await harness.GetTaskAsync(validId)).SavePath);
        Assert.Contains(harness.Store.SetLocationCalls,
                        call => call.Hash == validHash && call.SavePath == "/downloads/Media/Anime");
        var pending = await harness.GetTaskAsync(pendingId);
        Assert.NotEqual(DownloadTaskStatus.PendingSubmit, pending.Status);
        Assert.True(harness.Store.Torrents.ContainsKey(pending.InfoHash));
        Assert.Equal("/downloads", (await harness.GetTaskAsync(invalidId)).SavePath);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    [Fact]
    public async Task LegacyLayout_CaseOnlyPathDifference_SkipsMigrationWithoutRemoteWrites()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var (taskId, hash, absolute) =
            await SeedLegacyAnimeTaskAsync(harness, BatchTorrent(NekoTitle("简日双语", episode : 12)));
        const string lower = "media/anime/";
        var          name  = absolute[(absolute.LastIndexOf('/') + 1)..];
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.CurrentRelativePath = lower + name;
            await db.SaveChangesAsync();
        }

        harness.Store.FileIdentities[hash] =
            harness.Store.FileIdentities[hash]
                   .Select(file => file with { Path = lower + file.Path["Media/Anime/".Length..] })
                   .ToList();
        harness.Store.Files[hash] = [lower + name];
        var renameCount   = harness.Store.RenameFileCalls.Count;
        var locationCount = harness.Store.SetLocationCalls.Count;

        await harness.ReconcileAsync();

        Assert.Equal("/downloads", (await harness.GetTaskAsync(taskId)).SavePath);
        Assert.Equal(renameCount, harness.Store.RenameFileCalls.Count);
        Assert.Equal(locationCount, harness.Store.SetLocationCalls.Count);
        Assert.True(await HasMigrationMarkerAsync(harness));
    }

    /// <summary>造一份升级前已落库的旧版归档日志；remoteApplied 表示 qB 改名已生效但数据库尚未确认。</summary>
    private static async Task<(Guid Id, string Hash, string Original, string Target)> SeedLegacyArchiveJournalAsync(
        TestHarness harness, byte[] bytes, bool remoteApplied)
    {
        var (taskId, hash, _) = await SeedLegacyAnimeTaskAsync(harness, bytes);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.Include(value => value.Files).SingleAsync(value => value.Id == taskId);
            var name = task.Files.Single().CurrentRelativePath["Media/Anime/".Length..];
            task.PathDecision = new DownloadPathDecision
            {
                Root         = "/downloads", Kind = DownloadPathDecisionKinds.FinalRoot,
                DecidedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Reason = "升级前旧版归档"
            }.ToJson();
            task.PendingSavePath = "/downloads";
            if (remoteApplied) task.SavePath = DownloadTaskPlacement.TaskRoot("/downloads", taskId);
            foreach (var file in task.Files)
            {
                file.PendingRenameFrom   = name;
                file.PendingRenameTo     = "Media/Anime/" + name;
                file.CurrentRelativePath = remoteApplied ? file.PendingRenameTo : name;
            }

            await db.SaveChangesAsync();
            var target = "Media/Anime/" + name;
            harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
            {
                SavePath = "/downloads", IsMoving = false
            };
            harness.Store.FileIdentities[hash] =
                harness.Store.FileIdentities[hash].Select(file => file with
                {
                    Path = remoteApplied ? target : name, Progress = 1.0
                }).ToList();
            harness.Store.Files[hash] = [remoteApplied ? target : name];
            await ClearMigrationMarkerAsync(harness);
            return (taskId, hash, name, target);
        }
    }

    /// <summary>复制一条合法旧任务，只把旧路径改成不属于旧布局的目录，验证单条坏记录被隔离。</summary>
    private static async Task<Guid> SeedInvalidLegacyTaskAsync(TestHarness harness, Guid sourceId)
    {
        var             invalidId = Guid.NewGuid();
        await using var db        = await harness.CreateDbContextAsync();
        var source = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                             .SingleAsync(value => value.Id == sourceId);
        var original = source.Files.Single();
        var invalid = new DownloadTask
        {
            Id                = invalidId,
            InfoHash          = new string('b', 40),
            TorrentFile       = source.TorrentFile,
            SavePath          = "/downloads",
            DownloadRootPath  = "/downloads",
            MediaDirectory    = source.MediaDirectory,
            SeedDirectory     = source.SeedDirectory,
            NamingSnapshot    = source.NamingSnapshot,
            NamingIngredients = source.NamingIngredients,
            Status            = DownloadTaskStatus.Completed,
            SubmittedAtUtc    = source.SubmittedAtUtc,
            CompletedAtUtc    = source.CompletedAtUtc,
            CreatedAtUtc      = source.CreatedAtUtc
        };
        invalid.Files.Add(new DownloadTaskFile
        {
            Id                   = Guid.NewGuid(), DownloadTaskId = invalidId, FileIndex = original.FileIndex,
            OriginalRelativePath = original.OriginalRelativePath, CurrentRelativePath = "../outside.mkv",
            Size                 = original.Size, Kind = DownloadFileKind.Anime
        });
        db.DownloadTasks.Add(invalid);
        await db.SaveChangesAsync();
        return invalidId;
    }

    /// <summary>造一个已完成、qB 与数据库都处于旧版“全局根 + 媒体前缀”的纯正片任务。</summary>
    private static async Task<(Guid Id, string Hash, string AbsolutePath)> SeedLegacyAnimeTaskAsync(
        TestHarness harness, byte[] bytes, bool resetMarker = true)
    {
        var taskId = await harness.CreateUploadAsync(bytes, "mikan");
        for (var i = 0; i < 3; i++) await harness.ReconcileAsync();
        var task = await harness.GetTaskAsync(taskId);
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();

        await using var db = await harness.CreateDbContextAsync();
        var tracked = await db.DownloadTasks.Include(value => value.Files).SingleAsync(value => value.Id == taskId);
        Assert.Equal("/downloads/Media/Anime", tracked.SavePath);
        var name = Assert.Single(tracked.Files).CurrentRelativePath;
        tracked.SavePath                           = "/downloads";
        tracked.Files.Single().CurrentRelativePath = "Media/Anime/" + name;
        await db.SaveChangesAsync();

        harness.Store.Torrents[task.InfoHash] = harness.Store.Torrents[task.InfoHash] with
        {
            SavePath = "/downloads", IsMoving = false
        };
        harness.Store.FileIdentities[task.InfoHash] =
            harness.Store.FileIdentities[task.InfoHash]
                   .Select(file => file with { Path = "Media/Anime/" + file.Path }).ToList();
        if (resetMarker) await ClearMigrationMarkerAsync(harness);
        return (taskId, task.InfoHash, "/downloads/Media/Anime/" + name);
    }

    private static async Task ClearMigrationMarkerAsync(TestHarness harness)
    {
        await using var db = await harness.CreateDbContextAsync();
        var marker =
            await db.ApplicationSettings.SingleOrDefaultAsync(setting => setting.Key ==
                                                                         "Downloads.LegacyLayoutMigration.Completed");
        if (marker is null) return;
        db.ApplicationSettings.Remove(marker);
        await db.SaveChangesAsync();
    }

    private static async Task<bool> HasMigrationMarkerAsync(TestHarness harness)
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.ApplicationSettings
                       .AnyAsync(setting => setting.Key == "Downloads.LegacyLayoutMigration.Completed");
    }
}
