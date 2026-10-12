using Banned.AniParser;
using Banned.Qbittorrent;
using Banned.Qbittorrent.Models;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Net;
using System.Text;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

public sealed class DownloadFileServiceTests
{
    [Fact]
    public async Task Initialize_UsesMetadataOriginalAndStableIndex_NotResponseOrder()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv",
                                                 "【喵萌奶茶屋】[Test Anime][02][1080p][简日双语].mkv");
        h.Store.FileIdentities[h.Task.InfoHash] =
            [new TorrentFileSnapshot(1, "changed-two.mkv", 1025), new TorrentFileSnapshot(0, "changed-one.mkv", 1024)];
        await h.Service.InitializeAsync(h.Task.Id);
        var view = await h.Service.ListAsync(h.Task.Id);
        Assert.Equal("Original/【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv", view.Files[0].OriginalPath);
        Assert.Equal("changed-one.mkv", view.Files[0].CurrentPath);
        Assert.Equal("changed-two.mkv", view.Files[1].CurrentPath);
        Assert.Empty(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task Initialize_PreseededExternalRenameIsRejected()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.InitializeAsync(h.Task.Id);
        h.Store.FileIdentities[h.Task.InfoHash][0] =
            h.Store.FileIdentities[h.Task.InfoHash][0] with { Path = "external.mkv" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InitializeAsync(h.Task.Id));
        Assert.Equal("Original/【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv",
                     (await h.Load()).Files.Single().OriginalRelativePath);
    }

    [Theory]
    [InlineData(-1, 1024)]
    [InlineData(0, 99)]
    public async Task Initialize_InvalidIndexOrSizeRejects(int index, long size)
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        h.Store.FileIdentities[h.Task.InfoHash] = [new TorrentFileSnapshot(index, "renamed.mkv", size)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InitializeAsync(h.Task.Id));
        Assert.Empty((await h.Load()).Files);
    }

    [Fact]
    public async Task Initialize_MissingTorrentMetadataNeverInventsOriginalPath()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Update(task => task.TorrentFile = []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.InitializeAsync(h.Task.Id));
        Assert.Empty((await h.Load()).Files);
    }

    [Fact]
    public async Task Archive_CleanRootIsBaseMedia_SkipsLocationMove()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        Assert.True(await h.Service.ArchiveCompletedAsync(h.Task.Id));
        var task = await h.Load();
        Assert.Equal("/downloads/Media/Anime", task.SavePath);
        Assert.Equal("E01.mkv", task.Files.Single().CurrentRelativePath);
        Assert.Empty(h.Store.SetLocationCalls);
    }

    [Fact]
    public async Task Archive_MixedFiles_SeedUsesFullOriginalRelativePath()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv", "Extras/notes.txt");
        Assert.True(await h.Service.ArchiveCompletedAsync(h.Task.Id));
        var task = await h.Load();
        Assert.Equal("/downloads", task.SavePath);
        Assert.Equal("Media/Anime/E01.mkv", task.Files.Single(file => file.FileIndex == 0).CurrentRelativePath);
        Assert.Equal("Seed/Original/Extras/notes.txt",
                     task.Files.Single(file => file.FileIndex == 1).CurrentRelativePath);
        Assert.DoesNotContain(h.Store.RenameFileCalls, call => call.NewPath.Contains(".."));
    }

    [Fact]
    public async Task Preview_DoesNotPersistOverrideOrPerformRemoteOperations()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.InitializeAsync(h.Task.Id);
        var preview =
            await h.Service.PreviewAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 2.5m));
        Assert.Equal("/downloads/Media/Anime/E02.5.mkv", preview.TargetPath);
        Assert.Equal("/downloads/Media/Anime/Original/【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv", preview.CurrentPath);
        Assert.Null((await h.Load()).Files.Single().EpisodeNumberOverride);
        Assert.Empty(h.Store.RenameFileCalls);
        Assert.Empty(h.Store.SetLocationCalls);
    }

    [Fact]
    public async Task AnimeUiKind_ForSubtitlePreservesCompoundLanguageSuffix()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].chi.jp.ass");
        var view = await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 3));
        Assert.Equal(DownloadFileKind.Subtitle, view.Files.Single().Kind);
        Assert.Equal("E03.chi.jp.ass", view.Files.Single().CurrentPath);
    }

    [Fact]
    public async Task Reidentify_ExplicitKindEpisodeRangeAndPathStillWin()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 90));
        var view = await h.Service.ApplyAsync(h.Task.Id, 0,
                                              new DownloadFileEditRequest(DownloadFileKind.Anime, true, 2.5m, 3.5m,
                                                                          "Manual/custom.mkv"));
        var file = view.Files.Single();
        Assert.Equal("Manual/custom.mkv", file.CurrentPath);
        Assert.Equal(2.5m, file.Episode);
        Assert.Equal(3.5m, file.EpisodeEnd);
        Assert.True((await h.Load()).Files.Single().IsManualOverride);
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Seed, true));
        Assert.Equal("Seed/Original/【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv",
                     (await h.Load()).Files.Single().CurrentRelativePath);
    }

    [Fact]
    public async Task Reidentify_ClearsPreviousOverridesAndUsesOriginalName()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 20));
        var view = await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime, true));
        Assert.Equal(1m, view.Files.Single().Episode);
        Assert.Equal("E01.mkv", view.Files.Single().CurrentPath);
        Assert.Null((await h.Load()).Files.Single().EpisodeNumberOverride);
    }

    [Fact]
    public async Task Apply_SeedRequestsWholeTorrentMove_WaitsForObservedLocation()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv",
                                                 "【喵萌奶茶屋】[Test Anime][02][1080p][简日双语].mkv");
        await h.Service.ArchiveCompletedAsync(h.Task.Id);
        h.Store.DeferLocation = true;
        var view    = await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Seed));
        var pending = await h.Load();
        Assert.Equal("/downloads/Media/Anime", pending.SavePath);
        Assert.Equal("/downloads", pending.PendingSavePath);
        Assert.Equal("E01.mkv", view.Files[0].CurrentPath);
        Assert.All(pending.Files, file => Assert.NotNull(file.PendingRenameTo));
        Assert.False(await h.Service.ArchiveCompletedAsync(h.Task.Id));
        h.Store.CompleteMoves();
        Assert.True(await h.Service.ArchiveCompletedAsync(h.Task.Id));
        var complete = await h.Load();
        Assert.Null(complete.PendingSavePath);
        Assert.Equal("/downloads", complete.SavePath);
        Assert.Equal("Media/Anime/E02.mkv",
                     complete.Files.Single(file => file.FileIndex == 1).CurrentRelativePath);
        Assert.Single(h.Store.SetLocationCalls);
    }

    [Fact]
    public async Task CompletedPendingRecovery_DoesNotReplanOrRenameAgain()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.ArchiveCompletedAsync(h.Task.Id);
        h.Store.DeferLocation = true;
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Seed));
        await h.Update(task => task.Status = DownloadTaskStatus.Completed);
        h.Store.CompleteMoves();
        var count = h.Store.RenameFileCalls.Count;
        using (await h.Gate.EnterAsync())
        await using (var session = await h.Sessions.CreateAsync())
        {
            Assert.True(await h.Service.RecoverPendingUnderGateAsync(session, h.Task.Id));
        }

        Assert.Equal(count, h.Store.RenameFileCalls.Count);
        Assert.Null((await h.Load()).PendingSavePath);
    }

    [Fact]
    public async Task RemoteSuccessDatabaseFailure_RecoversFromDurableJournalWithoutDuplicateRename()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.InitializeAsync(h.Task.Id);
        h.Store.AfterRenameFile = () => h.FailSave.Enabled = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => h.Service.ApplyAsync(h.Task.Id, 0,
                                                                               new DownloadFileEditRequest(DownloadFileKind
                                                                                  .Anime, Episode : 4)));
        h.FailSave.Enabled      = false;
        h.Store.AfterRenameFile = null;
        var pending = await h.Load();
        Assert.NotNull(pending.Files.Single().PendingRenameTo);
        Assert.Equal(4m, pending.Files.Single().EpisodeNumberOverride);
        using (await h.Gate.EnterAsync())
        await using (var session = await h.Sessions.CreateAsync())
        {
            Assert.True(await h.Service.RecoverPendingUnderGateAsync(session, h.Task.Id));
        }

        Assert.Single(h.Store.RenameFileCalls);
        Assert.Equal("E04.mkv", (await h.Load()).Files.Single().CurrentRelativePath);
    }

    [Fact]
    public async Task PendingRecovery_RechecksNewUnmanagedOccupancyBeforeMoving()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.ArchiveCompletedAsync(h.Task.Id);
        h.Store.DeferLocation = true;
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Seed));
        var pending     = await h.Load();
        var destination = pending.Files.Single().PendingRenameTo!;
        h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with { IsMoving = false };
        var hash = new string('b', 40);
        h.Store.AddTorrent(hash, "external", QbittorrentTorrentCategory.Seeding, 1, "/downloads",
                           [destination], ["unmanaged"]);
        // 大小与任务文件不同：不是同一资源的交叉做种，而是真实覆盖，必须拦截。
        h.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, destination, 4096)];
        var renameCount = h.Store.RenameFileCalls.Count;
        var moveCount   = h.Store.SetLocationCalls.Count;
        using (await h.Gate.EnterAsync())
        await using (var session = await h.Sessions.CreateAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    h.Service.RecoverPendingUnderGateAsync(session,
                                                                        h.Task.Id));
        }

        Assert.Equal(renameCount, h.Store.RenameFileCalls.Count);
        Assert.Equal(moveCount, h.Store.SetLocationCalls.Count);
        Assert.NotNull((await h.Load()).PendingSavePath);
    }

    [Fact]
    public async Task PendingRecovery_RechecksDatabasePendingDestinationAfterRemoteSuccess()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.InitializeAsync(h.Task.Id);
        h.Store.AfterRenameFile = () => h.FailSave.Enabled = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => h.Service.ApplyAsync(h.Task.Id, 0,
                                                                               new DownloadFileEditRequest(DownloadFileKind
                                                                                  .Anime, Episode : 4)));
        h.FailSave.Enabled      = false;
        h.Store.AfterRenameFile = null;
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var other = new DownloadTask
            {
                Id              = Guid.NewGuid(), InfoHash         = new string('b', 40), SavePath = "/other",
                PendingSavePath = "/downloads/Media/Anime", Status = DownloadTaskStatus.Completed
            };
            other.Files.Add(new DownloadTaskFile
            {
                Id                   = Guid.NewGuid(), DownloadTaskId   = other.Id, FileIndex = 0,
                OriginalRelativePath = "other.mkv", CurrentRelativePath = "other.mkv",
                PendingRenameFrom    = "other.mkv", PendingRenameTo     = "E04.mkv"
            });
            db.Add(other);
            await db.SaveChangesAsync();
        }

        var renameCount = h.Store.RenameFileCalls.Count;
        using (await h.Gate.EnterAsync())
        await using (var session = await h.Sessions.CreateAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    h.Service.RecoverPendingUnderGateAsync(session,
                                                                        h.Task.Id));
        }

        Assert.Equal(renameCount, h.Store.RenameFileCalls.Count);
        Assert.Empty(h.Store.SetLocationCalls);
        Assert.NotNull((await h.Load()).Files.Single().PendingRenameTo);
    }

    [Fact]
    public async Task LastSeedChangedToAnime_DoesNotMoveMixedRootBackToClean()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Seed));
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime));
        Assert.Equal("/downloads", (await h.Load()).SavePath);
        Assert.Equal("Media/Anime/E01.mkv", (await h.Load()).Files.Single().CurrentRelativePath);
        Assert.Single(h.Store.SetLocationCalls);
    }

    [Theory]
    [InlineData("../escape.mkv")]
    [InlineData("/absolute.mkv")]
    [InlineData("C:/escape.mkv")]
    [InlineData("Anime//file.mkv")]
    public async Task ManualPathRejectsTraversalAndAbsolutePaths(string path)
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.ApplyAsync(h.Task.Id, 0,
                                                                               new DownloadFileEditRequest(DownloadFileKind
                                                                                      .Anime,
                                                                                   AnimeRelativePath : path)));
        Assert.Empty(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task SameTorrentTargetCollisionRejectedBeforeRemoteOperations()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv",
                                                 "【喵萌奶茶屋】[Test Anime][02][1080p][简日双语].mkv");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ApplyAsync(h.Task.Id, 1,
                                                                new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                                    Episode : 1)));
        Assert.Empty(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task OtherDatabaseTaskOccupiedFileBlocksPlan()
    {
        await using var h = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var other = new DownloadTask
            {
                Id     = Guid.NewGuid(), InfoHash = new string('a', 40), SavePath = "/downloads/Media/Anime",
                Status = DownloadTaskStatus.Completed
            };
            other.Files.Add(new DownloadTaskFile
            {
                Id                   = Guid.NewGuid(), DownloadTaskId = other.Id, FileIndex = 0,
                OriginalRelativePath = "x.mkv", CurrentRelativePath   = "E01.mkv"
            });
            db.Add(other);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ArchiveCompletedAsync(h.Task.Id));
        Assert.Empty(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task ReplacedCompletedTaskWithoutQbTorrentDoesNotBlockMatchingArchiveFile()
    {
        await using var h           = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        var             incumbentId = Guid.NewGuid();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Add(new MediaSubject { Id = 123, Name = "Test Anime", NameCn = "Test Anime", Platform = "TV" });
            await db.SaveChangesAsync();
        }

        await h.Update(task =>
        {
            task.SubjectId      = 123;
            task.Version        = 2;
            task.ReplacesTaskId = incumbentId;
        });
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var incumbent = new DownloadTask
            {
                Id        = incumbentId, InfoHash = new string('a', 40), SavePath = "/downloads/Media/Anime",
                SubjectId = 123, Episode          = 1, Version = 1, Status = DownloadTaskStatus.Completed
            };
            incumbent.Files.Add(new DownloadTaskFile
            {
                Id                   = Guid.NewGuid(), DownloadTaskId = incumbent.Id, FileIndex = 0,
                OriginalRelativePath = "old.mkv", CurrentRelativePath = "E01.mkv", Size         = 1024
            });
            db.Add(incumbent);
            await db.SaveChangesAsync();
        }

        Assert.True(await h.Service.ArchiveCompletedAsync(h.Task.Id));
        Assert.Equal("E01.mkv", (await h.Load()).Files.Single().CurrentRelativePath);
        Assert.Single(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task UnmanagedQbTorrentAllTagsOccupiedFileBlocksPlan()
    {
        await using var h    = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        var             hash = new string('a', 40);
        h.Store.AddTorrent(hash, "unmanaged", QbittorrentTorrentCategory.Seeding, 1, "/downloads/Media/Anime",
                           ["E01.mkv"], ["another-source"]);
        // 大小与任务文件不同：不是同一资源的交叉做种，而是真实覆盖，必须拦截。
        h.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, "E01.mkv", 4096)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ArchiveCompletedAsync(h.Task.Id));
        Assert.Empty(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task UnderGateArchiveDoesNotReenterSharedGate()
    {
        await using var h       = await Harness.Create("【喵萌奶茶屋】[Test Anime][01][1080p][简日双语].mkv");
        using var       lease   = await h.Gate.EnterAsync();
        using var       timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var session = await h.Sessions.CreateAsync();
        Assert.True(await h.Service.ArchiveCompletedUnderGateAsync(session, h.Task.Id,
                                                                   h.Store.Torrents[h.Task.InfoHash], timeout.Token));
    }

    [Fact]
    public async Task DirectorySnapshotsRemainStableAfterSettingsChange()
    {
        await using var h = await Harness.Create("notes.txt");
        await h.Directories.SaveAsync("NewMedia", "NewSeed");
        await h.Service.ArchiveCompletedAsync(h.Task.Id);
        Assert.Equal("Seed/Original/notes.txt", (await h.Load()).Files.Single().CurrentRelativePath);
    }

    [Fact]
    public async Task UnparsedSeedViewDoesNotPretendTaskEpisodeWasIdentified()
    {
        await using var h    = await Harness.Create("unidentified.mkv");
        var             view = await h.Service.ListAsync(h.Task.Id);
        Assert.Equal(DownloadFileKind.Seed, view.Files.Single().Kind);
        Assert.Null(view.Files.Single().Episode);
    }

    [Fact]
    public async Task UnparsedLegacyFile_ManualEpisodeBuildsAndPersistsBoundSubjectTemplate()
    {
        await using var h = await Harness.Create("unidentified.mkv");
        await h.Templates.SaveAsync("{bgm.nameCn}");
        await h.Templates.SaveFileTemplateAsync("{bgm.nameCn} E{episode}");
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Add(new MediaSubject { Id = 123, Name = "Original Title", NameCn = "中文作品", Platform = "TV" });
            await db.SaveChangesAsync();
        }

        await h.Update(task =>
        {
            task.SubjectId         = 123;
            task.Episode           = null;
            task.NamingIngredients = null;
        });
        var view = await h.Service.ApplyAsync(h.Task.Id, 0,
                                              new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 2.5m));
        Assert.Equal("中文作品 E02.5.mkv", view.Files.Single().CurrentPath);
        var snapshot = TorrentNamingIngredients.FromJson((await h.Load()).NamingIngredients)!;
        Assert.Equal("{bgm.nameCn} E{episode}", snapshot.FileTemplate);
        Assert.Equal("中文作品", snapshot.BangumiNameCn);
        await h.Templates.SaveFileTemplateAsync("changed-{episode}");
        await h.Service.ApplyAsync(h.Task.Id, 0, new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 3.5m));
        Assert.Equal("中文作品 E03.5.mkv", (await h.Load()).Files.Single().CurrentRelativePath);
    }

    [Fact]
    public async Task UnparsedIntakeEmptyEpisodeSnapshot_ManualEpisodeUsesSavedTemplate()
    {
        await using var h = await Harness.Create("unidentified.mkv");
        await h.Update(task =>
        {
            task.Episode = null;
            task.NamingIngredients = new TorrentNamingIngredients("{bgm.nameCn} E{episode}", "中文作品", "", 1, null, null,
                                                                  FileTemplate : "{bgm.nameCn} E{episode}",
                                                                  BangumiNameCn : "中文作品").ToJson();
        });
        await h.Templates.SaveFileTemplateAsync("changed-{episode}");
        var view = await h.Service.ApplyAsync(h.Task.Id, 0,
                                              new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 4.5m));
        Assert.Equal("中文作品 E04.5.mkv", view.Files.Single().CurrentPath);
    }

    [Fact]
    public async Task QbAdapter_AddFixesOriginalLayout_AndPreservesProtocolFileIndices()
    {
        var       handler = new ProtocolHandler();
        using var http    = new HttpClient(handler);
        var client = await QBittorrentClient.Create("http://protocol.test", "admin", "password",
                                                    new QBittorrentClientOptions { HttpClient = http, MaxRetries = 1 });
        await using var session = new QbittorrentSession(client);
        Assert.Equal("Ok.", await session.AddTorrentAsync(TestTorrentBytes.Create(), "/downloads", [], null));
        Assert.Matches("name=\"?contentLayout\"?(?:\\r\\n|;)", handler.AddBody);
        Assert.Contains("Original", handler.AddBody);
        var files = await session.ListFilesAsync(new string('A', 40));
        Assert.Equal(7, files[0].Index);
        Assert.Equal(3, files[1].Index);
        Assert.Equal(12, files[0].Size);
        await session.SetLocationAsync(new string('A', 40), "/downloads/new");
        Assert.Contains("location=%2Fdownloads%2Fnew", handler.LocationBody);
        Assert.Contains(new string('a', 40), handler.LocationBody);
    }

    private sealed class ProtocolHandler : HttpMessageHandler
    {
        public string AddBody      { get; private set; } = "";
        public string LocationBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken  cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var text = path.EndsWith("auth/login") ? "Ok." : path.EndsWith("webapiVersion") ? "2.11.2" : "v5.0.0";
            if (path.EndsWith("torrents/add"))
            {
                AddBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                text    = "Ok.";
            }

            if (path.EndsWith("torrents/files"))
                text =
                    "[{\"index\":7,\"name\":\"seven.mkv\",\"size\":12},{\"index\":3,\"name\":\"three.mkv\",\"size\":99}]";
            if (path.EndsWith("torrents/setLocation"))
            {
                LocationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                text         = "";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
        }
    }

    private sealed class FailSaveInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData      eventData,
                                                                              InterceptionResult<int> result,
                                                                              CancellationToken cancellationToken =
                                                                                  default)
        {
            if (Enabled) throw new DbUpdateException("Injected acknowledgement save failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _path =
            Path.Combine(Path.GetTempPath(), "mm-files-" + Guid.NewGuid().ToString("N") + ".db");

        public FailSaveInterceptor                     FailSave    { get; }              = new();
        public IDbContextFactory<ApplicationDbContext> Factory     { get; private set; } = null!;
        public FakeTorrentStore                        Store       { get; }              = new();
        public FakeQbittorrentSessionFactory           Sessions    { get; private set; } = null!;
        public DownloadCoordinationGate                Gate        { get; }              = new();
        public MemoryApplicationSettingStore           Settings    { get; }              = new();
        public DownloadDirectorySettingsStore          Directories { get; private set; } = null!;
        public NamingTemplateStore                     Templates   { get; private set; } = null!;
        public DownloadFileService                     Service     { get; private set; } = null!;
        public DownloadTask                            Task        { get; private set; } = null!;

        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }

        public static async Task<Harness> Create(params string[] names)
        {
            var h = new Harness();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                         .UseSqlite("Data Source=" + h._path + ";Pooling=False")
                         .AddInterceptors(h.FailSave).Options;
            h.Factory  = new PooledDbContextFactory<ApplicationDbContext>(options);
            h.Sessions = new FakeQbittorrentSessionFactory(h.Store);
            var parser = new AniParser();
            h.Directories = new DownloadDirectorySettingsStore(h.Settings);
            h.Templates   = new NamingTemplateStore(h.Settings);
            h.Service = new DownloadFileService(h.Factory, h.Gate, h.Sessions, h.Directories, h.Templates,
                                                new DownloadFileClassificationPolicy(parser), parser,
                                                new ReleaseNamePreviewPolicy(),
                                                new SubjectEpisodeCorrectionPolicy(), TimeProvider.System);
            var bytes = MultiTorrent(names);
            Assert.True(TorrentInfoHashExtractor.TryExtractInfoHash(bytes, out var hash));
            h.Task = new DownloadTask
            {
                Id = Guid.NewGuid(), InfoHash = hash, TorrentFile = bytes, SavePath = "/downloads/Media/Anime",
                DownloadRootPath = "/downloads", MediaDirectory = "Media/Anime", SeedDirectory = "Seed",
                NamingSnapshot = "Anime", Episode = 1, Status = DownloadTaskStatus.Downloading,
                NamingIngredients =
                    new TorrentNamingIngredients("{title}", "Anime", "01", 1, null, null, FileTemplate : "E{episode}")
                       .ToJson()
            };
            await using (var db = await h.Factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Add(h.Task);
                await db.SaveChangesAsync();
            }

            h.Store.AddTorrent(hash, "Original", QbittorrentTorrentCategory.Seeding, 1, h.Task.SavePath);
            h.Store.FileIdentities[hash] =
                names.Select((name, i) => new TorrentFileSnapshot(i, "Original/" + name, 1024 + i)).ToList();
            return h;
        }

        public async Task<DownloadTask> Load()
        {
            await using var db = await Factory.CreateDbContextAsync();
            return await db.DownloadTasks.Include(task => task.Files).AsNoTracking()
                           .SingleAsync(task => task.Id == Task.Id);
        }

        public async Task Update(Action<DownloadTask> update)
        {
            await using var db   = await Factory.CreateDbContextAsync();
            var             task = await db.DownloadTasks.SingleAsync(task => task.Id == Task.Id);
            update(task);
            await db.SaveChangesAsync();
        }

        private static byte[] MultiTorrent(string[] names)
        {
            static string B(string text) => Encoding.UTF8.GetByteCount(text) + ":" + text;

            var files = string.Concat(names.Select((name, index) =>
                                                       "d6:lengthi" + (1024 + index) + "e4:pathl" +
                                                       string.Concat(name.Split('/').Select(B)) + "ee"));
            return Encoding.UTF8.GetBytes("d4:infod5:filesl" + files + "e4:name8:Original12:piece lengthi16384eee");
        }
    }
}
