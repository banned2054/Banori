using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Fact]
    public async Task Reconcile_LosingBatchEpisode_DoesNotBlockSelectedEpisodeArchive()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        // 旧合集承担第 11、12 集，新合集只有第 12 集相同内容并带来缺集第 13 集：部分重叠不构成
        // 包含承接（旧包仍独有第 11 集职责），第 12 集沿用现任保留语义落选。
        var incumbentId = await harness.CreateUploadAsync(BatchTorrent(
                                                                   NekoTitle("简日双语", episode : 11),
                                                                   NekoTitle("简日双语", episode : 12)), "mikan");
        await harness.ReconcileAsync();
        var incumbentHash = (await harness.GetTaskAsync(incumbentId)).InfoHash;
        CompleteTorrent(harness, incumbentHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(incumbentId)).Status);

        var batchId = await harness.CreateUploadAsync(BatchTorrent(
                                                                   NekoTitle("简日双语", episode : 12),
                                                                   NekoTitle("简日双语", episode : 13)), "mikan");
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        var batchHash = (await harness.GetTaskAsync(batchId)).InfoHash;
        CompleteTorrent(harness, batchHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(batchId)).Status);
        await using var db = await harness.CreateDbContextAsync();
        var skipped =
            await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == batchId && file.FileIndex == 0);
        Assert.False(skipped.IsSelectedForDownload);
        Assert.DoesNotContain(harness.Store.RenameFileCalls,
                              call => call.Hash == batchHash && call.NewPath == "测试作品 E12.mp4");
        // 现任合集的第 12 集正片（FileIndex 1；首个正片是第 11 集）仍是该集现任。
        var incumbentEp12 =
            await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == incumbentId && file.FileIndex == 1);
        Assert.Equal(incumbentEp12.Id, (await FindEpisodeVersionAsync(harness, 12))!.IncumbentVideoFileId);
        Assert.NotNull((await FindEpisodeVersionAsync(harness, 13))!.IncumbentVideoFileId);
        // 部分重叠双方保留：旧合集仍有第 11 集独有职责，不被翻转或移除。
        var incumbentTask = await harness.GetTaskAsync(incumbentId);
        Assert.Equal(DownloadTaskStatus.Completed, incumbentTask.Status);
        Assert.Null(incumbentTask.ReplacedByTaskId);
        Assert.Empty(harness.Store.DeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconcile_CompletedEpisodeReplacement_MovesAssociatedSubtitlesWithVideo(bool multipleTracks)
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        static string Subtitle(string language, string track) => NekoSubtitleName(language, 12)[..^4] + track + ".ass";
        var tracks = multipleTracks ? new[] { ".chs", ".eng" } : new[] { string.Empty };
        var oldPaths = new[] { NekoTitle("繁日双语", episode : 12) }
                      .Concat(tracks.Select(track => Subtitle("繁日双语", track))).ToArray();
        var incumbentId = await harness.CreateUploadAsync(BatchTorrent(oldPaths), "mikan");
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(incumbentId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(incumbentId)).Status);

        var newPaths = new[] { NekoTitle("简日双语", episode : 12) }
                      .Concat(tracks.Select(track => Subtitle("简日双语", track)))
                      .Append(NekoTitle("简日双语", episode : 13)).ToArray();
        var replacementBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BatchTorrent(newPaths))
                                                              .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                                       StringComparison.Ordinal));
        var replacementId = await harness.CreateUploadAsync(replacementBytes, "mikan");
        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(replacementId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(replacementId)).Status);
        // 旧任务的成片与关联字幕一并迁入 .replaced，下载期间保留旧版，完成后才迁让。
        await using var db = await harness.CreateDbContextAsync();
        var oldFiles = await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == incumbentId).ToListAsync();
        // 旧任务的原始身份保持不变，当前路径已移入保留目录。
        Assert.All(oldFiles, file => Assert.StartsWith(".replaced/", file.CurrentRelativePath));
        // 完成后，替换方的视频与关联字幕自动一同迁入正式位置（字幕随视频整体移动）。
        var newSubtitles = await db.DownloadTaskFiles.Where(file => file.DownloadTaskId == replacementId &&
                                                                    file.Kind           == DownloadFileKind.Subtitle)
                                   .ToListAsync();
        Assert.Equal(tracks.Select(track => "Media/Anime/测试作品 E12" + track + ".ass").Order(),
                     newSubtitles.Select(file => file.CurrentRelativePath).Order());
        Assert.Equal(await FindAnimeFileIdAsync(harness, replacementId),
                     (await FindEpisodeVersionAsync(harness, 12))!.IncumbentVideoFileId);
    }

    [Fact]
    public async Task Reconcile_PartiallySupersededBatchCompletion_KeepsDormantFileAtRetainedPath()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var oldId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("繁日双语", episode : 12),
                                                                 NekoTitle("繁日双语", episode : 13)), "mikan");
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        var oldHash = (await harness.GetTaskAsync(oldId)).InfoHash;
        var replacementBytes = Encoding.UTF8.GetBytes(Encoding.UTF8
                                                              .GetString(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                                             NekoTitle("简日双语", episode : 14)))
                                                              .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                                       StringComparison.Ordinal));
        var replacementId = await harness.CreateUploadAsync(replacementBytes, "mikan");
        for (var round = 0; round < 4; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(replacementId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(replacementId)).Status);

        await using var db = await harness.CreateDbContextAsync();
        var dormant = await db.DownloadTaskFiles.AsNoTracking()
                              .SingleAsync(file => file.DownloadTaskId == oldId && file.FileIndex == 0);
        // 休眠数据仍占正式路径时先迁入 .replaced，保持数据并让出新版本目标。
        Assert.StartsWith(".replaced/", dormant.CurrentRelativePath);
        CompleteTorrent(harness, oldHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(oldId)).Status);
        Assert.Equal(dormant.CurrentRelativePath, (await db.DownloadTaskFiles.AsNoTracking()
                                                           .SingleAsync(file => file.Id == dormant.Id))
                    .CurrentRelativePath);
        Assert.Equal(await FindAnimeFileIdAsync(harness, replacementId),
                     (await FindEpisodeVersionAsync(harness, 12))!.IncumbentVideoFileId);
    }

    [Fact]
    public async Task Create_SingleEpisodeChallenger_DoesNotDeselectManuallyOverriddenRange()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldId = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("繁日双语", episode : 12),
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
    public async Task Apply_MediaRootBecomesMixed_KeepsSkippedContentAtSameAbsolutePath()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var id = await harness.CreateUploadAsync(BatchTorrent(NekoTitle("简日双语", episode : 12),
                                                              NekoTitle("简日双语", episode : 13)), "mikan");
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var skipped =
                await db.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == id && file.FileIndex == 0);
            skipped.IsSelectedForDownload = false;
            await db.SaveChangesAsync();
        }

        await harness.Provider.GetRequiredService<DownloadFileService>().ApplyAsync(id, 1,
            new DownloadFileEditRequest(DownloadFileKind.Seed));

        var task = await harness.GetTaskAsync(id);
        Assert.Equal("/downloads", task.SavePath);
        await using var resultDb = await harness.CreateDbContextAsync();
        var retained =
            await resultDb.DownloadTaskFiles.SingleAsync(file => file.DownloadTaskId == id && file.FileIndex == 0);
        Assert.Equal("Media/Anime/测试作品 E12.mp4", retained.CurrentRelativePath);
    }

    [Fact]
    public async Task Reconcile_BatchIncumbentVersion_IsNotDowngradedByDefaultTaskVersion()
    {
        using var harness = await CreateHarnessAsync();
        var v3 = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v3]", StringComparison.Ordinal);
        var incumbentId = await harness.CreateUploadAsync(BatchTorrent(v3, NekoTitle("简日双语", episode : 13)), "mikan");
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(incumbentId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(1, (await harness.GetTaskAsync(incumbentId)).Version);
        var incumbentFile = await FindAnimeFileIdAsync(harness, incumbentId);

        var v2           = NekoTitle("简日双语", episode : 12).Replace("[12]", "[12v2]", StringComparison.Ordinal);
        var challengerId = await harness.CreateUploadAsync(BatchTorrent(v2, NekoTitle("简日双语", episode : 14)), "byrbt");
        Assert.Null((await FindEpisodeVersionAsync(harness, 12))!.PendingVideoFileId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(challengerId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(challengerId)).Status);
        Assert.Equal(incumbentFile, (await FindEpisodeVersionAsync(harness, 12))!.IncumbentVideoFileId);
    }
}
