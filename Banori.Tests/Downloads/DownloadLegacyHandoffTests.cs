using System.Text;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>
///     旧 EpisodeVersion 行在升级回填后的完成交接：同源 + 悬空 Superseded 迁移后由完成链正常切换并退休旧任务；
///     跨来源拆行后完成只写自己的来源行，不顶替、不清理另一来源。
/// </summary>
public sealed partial class DownloadServiceTests
{
    [Fact]
    public async Task DownloadLegacyHandoffTests_SameSourceWithDanglingSuperseded_BackfillCompletesHandoff()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");

        // 旧任务 v1 先完成并归档，成为 byrbt 第 12 集现任。
        var oldTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语"), "https://tracker.byr.pt/announce.php?passkey=old"));
        var oldTask = await harness.GetTaskAsync(oldTaskId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, oldTask.InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(oldTaskId)).Status);
        var oldFileId = await FindAnimeFileIdAsync(harness, oldTaskId);
        Assert.NotNull(oldFileId);

        // v2 是同一来源的待完成优胜；构造与现有替换用例相同的同名不同大小种子。
        var replacementName = NekoTitle("简日双语").Replace("[12]", "[12v2]", StringComparison.Ordinal);
        var replacementBytes = Encoding.UTF8.GetBytes(Encoding.UTF8
                                                              .GetString(TestTorrentBytes.Create(
                                                                  replacementName,
                                                                  "https://tracker.byr.pt/announce.php?passkey=old"))
                                                              .Replace("6:lengthi1024e", "6:lengthi2048e",
                                                                       StringComparison.Ordinal));
        var newTaskId = await harness.CreateUploadAsync(replacementBytes);
        var newTask   = await harness.GetTaskAsync(newTaskId);
        var newFileId = await FindAnimeFileIdAsync(harness, newTaskId);
        Assert.NotNull(newFileId);

        // 模拟迁移前遗留的单行：byrbt 现任 + 待完成优胜同处 legacy，并带一个悬空 Superseded。
        var danglingSuperseded = Guid.NewGuid();
        int legacyRowId;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var row = await db.EpisodeVersions.SingleAsync(value => value.SubjectId   == 42   &&
                                                                    value.Episode     == 12   &&
                                                                    value.SourceScope == "byrbt");
            Assert.Equal(oldFileId, row.IncumbentVideoFileId);
            Assert.Equal(newFileId, row.PendingVideoFileId);
            legacyRowId               = row.Id;
            row.SourceScope           = SourceScopes.Legacy;
            row.SupersededVideoFileId = danglingSuperseded;
            row.UpdatedAtUtc          = harness.Timestamp;
            await db.SaveChangesAsync();
        }

        var report = await CreateBackfill(harness).RunWithReportAsync();
        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.DanglingPointers);

        var migrated = await FindSourceEpisodeVersionAsync(harness, 12, "byrbt");
        Assert.NotNull(migrated);
        Assert.Equal(oldFileId, migrated.IncumbentVideoFileId);
        Assert.Equal(newFileId, migrated.PendingVideoFileId);
        Assert.Null(migrated.SupersededVideoFileId);
        var retainedLegacy = await FindSourceEpisodeVersionAsync(harness, 12, SourceScopes.Legacy);
        Assert.NotNull(retainedLegacy);
        Assert.Equal(legacyRowId, retainedLegacy.Id);
        Assert.Equal(danglingSuperseded, retainedLegacy.SupersededVideoFileId);
        Assert.Null(retainedLegacy.IncumbentVideoFileId);
        Assert.Null(retainedLegacy.PendingVideoFileId);

        // 回填后照常提交、准备、完成：完成链应命中已迁移的 byrbt 行。
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        CompleteTorrent(harness, newTask.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(newTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.ArchiveReview);
        var final = await FindSourceEpisodeVersionAsync(harness, 12, "byrbt");
        Assert.NotNull(final);
        Assert.Equal(newFileId, final.IncumbentVideoFileId);
        Assert.Equal(oldFileId, final.SupersededVideoFileId);
        Assert.Null(final.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(oldTaskId)).Status);
        Assert.Contains(harness.Store.RenameFileCalls,
                        call => call.Hash == oldTask.InfoHash &&
                                call.NewPath.Contains(".replaced/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadLegacyHandoffTests_CrossSourceSplit_DoesNotCompeteOrCleanOtherSource()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveNamingTemplateAsync("{title} E{episode}");
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");

        // byrbt 第 12 集先完成并归档；它只承担自己的来源行，不能被 u2 完成链顶替或清理。
        var byrbtTaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("简日双语"), "https://tracker.byr.pt/announce.php"));
        var byrbtTask = await harness.GetTaskAsync(byrbtTaskId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, byrbtTask.InfoHash);
        await harness.ReconcileAsync();
        var byrbtFileId = await FindAnimeFileIdAsync(harness, byrbtTaskId);
        Assert.NotNull(byrbtFileId);

        // u2 第 13 集是另一个来源的新任务；仅登记，不完成。
        var u2TaskId = await harness.CreateUploadAsync(TestTorrentBytes.Create(
            NekoTitle("繁日双语", episode : 13), "https://daydream.dmhy.best/announce"));
        var u2Task = await harness.GetTaskAsync(u2TaskId);
        var u2FileId = await FindAnimeFileIdAsync(harness, u2TaskId);
        Assert.NotNull(u2FileId);

        // 迁移前的跨来源单行：同一集数登记里既有 byrbt 现任，又有 u2 待完成优胜。
        await using (var db = await harness.CreateDbContextAsync())
        {
            db.EpisodeVersions.Add(new EpisodeVersion
            {
                SubjectId             = 42,
                Episode               = 13,
                MediaType             = "SingleEpisode",
                SourceScope           = SourceScopes.Legacy,
                IncumbentVideoFileId  = byrbtFileId,
                PendingVideoFileId    = u2FileId,
                UpdatedAtUtc          = harness.Timestamp
            });
            await db.SaveChangesAsync();
        }

        var report = await CreateBackfill(harness).RunWithReportAsync();
        Assert.Equal(2, report.CreatedRows);
        Assert.Equal(2, report.SplitRows);

        var byrbtRow = await FindSourceEpisodeVersionAsync(harness, 13, "byrbt");
        Assert.NotNull(byrbtRow);
        Assert.Equal(byrbtFileId, byrbtRow.IncumbentVideoFileId);
        Assert.Null(byrbtRow.PendingVideoFileId);
        Assert.Null(byrbtRow.SupersededVideoFileId);
        var u2Row = await FindSourceEpisodeVersionAsync(harness, 13, "u2");
        Assert.NotNull(u2Row);
        Assert.Null(u2Row.IncumbentVideoFileId);
        Assert.Equal(u2FileId, u2Row.PendingVideoFileId);
        Assert.Null(u2Row.SupersededVideoFileId);

        await harness.ReconcileAsync();
        await harness.ReconcileAsync();
        CompleteTorrent(harness, u2Task.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(u2TaskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.ArchiveReview);
        var u2Final = await FindSourceEpisodeVersionAsync(harness, 13, "u2");
        Assert.NotNull(u2Final);
        Assert.Equal(u2FileId, u2Final.IncumbentVideoFileId);
        Assert.Null(u2Final.SupersededVideoFileId);
        Assert.Null(u2Final.PendingVideoFileId);

        // 跨来源不竞争：byrbt 行与旧任务、旧文件都保持原状，不因 u2 完成产生 Superseded 或清理。
        var byrbtFinal = await FindSourceEpisodeVersionAsync(harness, 13, "byrbt");
        Assert.NotNull(byrbtFinal);
        Assert.Equal(byrbtFileId, byrbtFinal.IncumbentVideoFileId);
        Assert.Null(byrbtFinal.SupersededVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(byrbtTaskId)).Status);
        Assert.DoesNotContain(harness.Store.RenameFileCalls,
                              call => call.Hash == byrbtTask.InfoHash &&
                                      call.NewPath.Contains(".replaced/", StringComparison.Ordinal));
    }

    private static SourceScopeBackfill CreateBackfill(TestHarness harness) =>
        new(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            harness.Provider.GetRequiredService<IDataProtectionProvider>(),
            NullLogger<SourceScopeBackfill>.Instance);
}
