using Banned.Qbittorrent.Exceptions;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Banori.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Linq.Expressions;

namespace Banori.Tests.Downloads;

/// <summary>
///     A 域聚焦验收：缺种巡检的批次上限、存量覆盖、三态判定、取消语义，
///     以及 Replaced 清理确认后的退出轮询与 deleteFiles=false 断言。
/// </summary>
public sealed class DownloadMissingTorrentPatrolTests
{
    [Fact]
    public async Task ReconcileMissingTorrents_BatchLimit100_AndResumeNextRound()
    {
        using var h   = await SchedulingHarness.CreateAsync();
        var       due = h.Time.GetUtcNow().AddMinutes(-1);
        var       ids = new List<Guid>();
        for (var i = 0; i < 105; i++)
            ids.Add(await CreateDueCompletedAsync(h, $"Batch Patrol {i:D3}", due));

        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        Assert.Equal(100, await CountPendingAsync(h));
        Assert.Equal(5, await CountDueUnprocessedAsync(h, due));

        // 不依赖任何进程内状态：第二轮按数据库里剩余的到期记录继续覆盖。
        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        Assert.Equal(105, await CountPendingAsync(h));
        Assert.Equal(0, await CountDueUnprocessedAsync(h, due));
    }

    [Fact]
    public async Task ReconcileMissingTorrents_FirstNotFoundThenReappears_DoesNotMarkMissing()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await CreateDueCompletedAsync(h, "Reappear Patrol Anime - 01");
        var       hash   = (await h.GetTaskAsync(taskId)).InfoHash;

        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        var first = await h.GetTaskAsync(taskId);
        Assert.Null(first.TorrentMissingAtUtc);
        Assert.NotNull(first.MissingCheckPendingAtUtc);
        Assert.NotNull(first.MissingCheckConfigKey);

        h.Store.AddTorrent(hash, "Back Again", QbittorrentTorrentCategory.Seeding, 1, "/downloads");
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        var second = await h.GetTaskAsync(taskId);
        Assert.Null(second.TorrentMissingAtUtc);
        Assert.Null(second.MissingCheckPendingAtUtc);
        Assert.Null(second.MissingCheckConfigKey);
        Assert.True(second.NextMissingCheckAtUtc > h.Time.GetUtcNow() + TimeSpan.FromHours(11));
    }

    [Fact]
    public async Task ReconcileMissingTorrents_LoginFailure_DoesNotWriteAnyField()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await CreateDueCompletedAsync(h, "Login Fail Patrol Anime - 01");
        var       before = await h.GetTaskAsync(taskId);
        h.Factory.SessionConfigurator =
            session => session.BeforeFindTorrentsAsync = _ => throw new QbittorrentException("模拟登录失败");

        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        var after = await h.GetTaskAsync(taskId);
        Assert.Null(after.TorrentMissingAtUtc);
        Assert.Null(after.MissingCheckPendingAtUtc);
        Assert.Null(after.MissingCheckConfigKey);
        Assert.Equal(before.NextMissingCheckAtUtc, after.NextMissingCheckAtUtc);
    }

    [Fact]
    public async Task ReconcileMissingTorrents_NullInfoResponse_DoesNotAdvanceEvidence()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await CreateDueCompletedAsync(h, "Null Info Patrol Anime - 01");
        var       before = await h.GetTaskAsync(taskId);
        // 生产适配层把 HTTP 200 的 JSON null 翻译成 QbittorrentException（查询未知），不是成功查无。
        h.Factory.SessionConfigurator = session =>
            session.BeforeFindTorrentsAsync = _ => throw new QbittorrentException("torrents/info returned null");

        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        var after = await h.GetTaskAsync(taskId);
        Assert.Null(after.TorrentMissingAtUtc);
        Assert.Null(after.MissingCheckPendingAtUtc);
        Assert.Null(after.MissingCheckConfigKey);
        Assert.Equal(before.NextMissingCheckAtUtc, after.NextMissingCheckAtUtc);
    }

    [Fact]
    public async Task ReconcileMissingTorrents_SecondNotFound_ConfirmsMissingAndSpreadsDue()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await CreateDueCompletedAsync(h, "Confirmed Missing Anime - 01");

        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        var task = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status); // 不翻转状态
        Assert.Equal(h.Time.GetUtcNow(), task.TorrentMissingAtUtc);
        Assert.Null(task.MissingCheckPendingAtUtc);
        Assert.Null(task.MissingCheckConfigKey);
        Assert.True(task.NextMissingCheckAtUtc >= h.Time.GetUtcNow() + TimeSpan.FromHours(12));
        Assert.True(task.NextMissingCheckAtUtc <=
                    h.Time.GetUtcNow() + TimeSpan.FromHours(12) + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task ReconcileMissingTorrents_FoundAgain_ClearsMissingEvidence()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await CreateDueCompletedAsync(h, "Cleared Evidence Anime - 01");
        var       hash   = (await h.GetTaskAsync(taskId)).InfoHash;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
            entity.TorrentMissingAtUtc      = h.Time.GetUtcNow().AddHours(-1);
            entity.MissingCheckPendingAtUtc = h.Time.GetUtcNow().AddHours(-1);
            entity.MissingCheckConfigKey    = "old-fingerprint";
            entity.NextMissingCheckAtUtc    = h.Time.GetUtcNow().AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(hash, "Found Again", QbittorrentTorrentCategory.Seeding, 1, "/downloads");

        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        var task = await h.GetTaskAsync(taskId);
        Assert.Null(task.TorrentMissingAtUtc);
        Assert.Null(task.MissingCheckPendingAtUtc);
        Assert.Null(task.MissingCheckConfigKey);
    }

    [Fact]
    public async Task ReconcileMissingTorrents_CancelledToken_ThrowsOut()
    {
        using var h = await SchedulingHarness.CreateAsync();
        await CreateDueCompletedAsync(h, "Cancelled Patrol Anime - 01");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServiceOf(h)
                                                                   .ReconcileMissingTorrentsAsync(cts.Token));
    }

    [Fact]
    public async Task ReconcileMissingTorrents_DoesNotTouchOwnershipEvidence()
    {
        using var h         = await SchedulingHarness.CreateAsync();
        var       taskId    = await CreateDueCompletedAsync(h, "Ownership Patrol Anime - 01");
        var       episodeId = 0;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.Include(t => t.Files).SingleAsync(t => t.Id == taskId);
            var file   = entity.Files.First();
            file.VerificationOutcome = "ReusedVerified";
            file.VerificationPath    = "/downloads/Media/Anime/keep.mkv";
            file.SupersededByTaskId  = Guid.NewGuid();
            var episode = new EpisodeVersion
            {
                SubjectId            = 42,
                Episode              = 1,
                MediaType            = "TV",
                IncumbentVideoFileId = file.Id,
                UpdatedAtUtc         = h.Time.GetUtcNow()
            };
            db.EpisodeVersions.Add(episode);
            await db.SaveChangesAsync();
            episodeId = episode.Id;
        }

        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        await using var verify       = await h.CreateDbContextAsync();
        var             afterFile    = await verify.DownloadTaskFiles.SingleAsync(f => f.DownloadTaskId == taskId);
        var             afterEpisode = await verify.EpisodeVersions.SingleAsync(e => e.Id               == episodeId);
        Assert.Equal("ReusedVerified", afterFile.VerificationOutcome);
        Assert.Equal("/downloads/Media/Anime/keep.mkv", afterFile.VerificationPath);
        Assert.NotNull(afterFile.SupersededByTaskId);
        Assert.Equal(afterFile.Id, afterEpisode.IncumbentVideoFileId);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanupConfirmed_StopsPollingAndKeepsFiles()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       bytes  = TestTorrentBytes.Create("Retired Tombstone Anime - 01");
        var       taskId = await h.CreateUploadAsync(bytes);
        var       at     = h.Time.GetUtcNow();
        string    hash;
        string    savePath;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
            entity.Status                       = DownloadTaskStatus.Replaced;
            entity.ReplacedByTaskId             = Guid.NewGuid();
            entity.SubmittedAtUtc               = at;
            entity.MissingCleanupConfirmedAtUtc = null;
            hash                                = entity.InfoHash;
            savePath                            = entity.SavePath;
            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(hash, "Retired", QbittorrentTorrentCategory.Seeding, 1, savePath);

        await h.ReconcileNowAsync();

        var delete = Assert.Single(h.Store.DeleteCalls);
        Assert.Equal(hash, delete.Hash);
        Assert.False(delete.DeleteFiles);
        var confirmed = await h.GetTaskAsync(taskId);
        Assert.NotNull(confirmed.MissingCleanupConfirmedAtUtc);

        h.Store.DeleteCalls.Clear();
        await h.ReconcileNowAsync();
        Assert.Empty(h.Store.DeleteCalls);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_NullInfoResponse_DoesNotConfirm()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       bytes  = TestTorrentBytes.Create("Null Info Cleanup Anime - 01");
        var       taskId = await h.CreateUploadAsync(bytes);
        string    hash;
        string    savePath;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(task => task.Id == taskId);
            entity.Status                       = DownloadTaskStatus.Replaced;
            entity.ReplacedByTaskId             = Guid.NewGuid();
            entity.SubmittedAtUtc               = h.Time.GetUtcNow();
            entity.MissingCleanupConfirmedAtUtc = null;
            hash                                = entity.InfoHash;
            savePath                            = entity.SavePath;
            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(hash, "Retired", QbittorrentTorrentCategory.Seeding, 1, savePath);
        h.Factory.SessionConfigurator = session =>
            session.BeforeFindTorrentsAsync = hashes => hashes.Count > 0
                ? throw new QbittorrentException("torrents/info returned null")
                : Task.CompletedTask;

        await h.ReconcileNowAsync();

        Assert.Empty(h.Store.DeleteCalls);
        Assert.Null((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);
    }

    [Fact]
    public async Task ReconcileMissingTorrents_NoDueCandidate_SkipsLogin()
    {
        using var h = await SchedulingHarness.CreateAsync();
        var taskId = await CreateDueCompletedAsync(h, "No Login Patrol Anime - 01",
                                                   h.Time.GetUtcNow().AddHours(1));
        var logins = 0;
        h.Factory.SessionConfigurator = _ => logins++;

        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        Assert.Equal(0, logins);
        Assert.NotNull((await h.GetTaskAsync(taskId)).NextMissingCheckAtUtc);

        h.Time.Advance(TimeSpan.FromHours(2));
        await ServiceOf(h).ReconcileMissingTorrentsAsync();
        Assert.True(logins > 0);
        Assert.NotNull((await h.GetTaskAsync(taskId)).MissingCheckPendingAtUtc);
    }

    [Fact]
    public async Task ReconcileMissingTorrents_DueReviewBeforeBacklog_WithinBatch()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       now    = h.Time.GetUtcNow();
        var       dueIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
            dueIds.Add(await CreateDueCompletedAsync(h, $"Due Priority {i:D2}", now.AddMinutes(-1)));
        for (var i = 0; i < 100; i++)
        {
            var             id     = await h.CreateUploadAsync(TestTorrentBytes.Create($"Backlog Patrol {i:D3}"));
            await using var db     = await h.CreateDbContextAsync();
            var             entity = await db.DownloadTasks.SingleAsync(task => task.Id == id);
            entity.Status                = DownloadTaskStatus.Completed;
            entity.CompletedAtUtc        = now;
            entity.NextMissingCheckAtUtc = null;
            await db.SaveChangesAsync();
        }

        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        await using var verify = await h.CreateDbContextAsync();
        var             due    = await verify.DownloadTasks.Where(task => dueIds.Contains(task.Id)).ToListAsync();
        Assert.All(due, task => Assert.NotNull(task.MissingCheckPendingAtUtc));
        Assert.Equal(100, await verify.DownloadTasks.CountAsync(task => task.MissingCheckPendingAtUtc != null));
        Assert.Equal(3, await verify.DownloadTasks.CountAsync(task => task.NextMissingCheckAtUtc      == null));
    }

    [Fact]
    public async Task ReconcileMissingTorrents_ConfigKeyComesFromSessionSettings()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await CreateDueCompletedAsync(h, "Session Config Patrol Anime - 01");

        await ServiceOf(h).ReconcileMissingTorrentsAsync();

        var settings = h.Factory.LastSettings;
        Assert.NotNull(settings);
        Assert.Equal(DownloadService.BuildMissingCheckConfigKey(settings),
                     (await h.GetTaskAsync(taskId)).MissingCheckConfigKey);
    }

    [Fact]
    public void MissingPatrol_HasIndependentTwoMinuteThrottle()
    {
        var now    = DateTimeOffset.Parse("2026-11-20T08:00:00Z");
        var timing = DownloadTimingPolicy.Default;
        Assert.True(DownloadReconcileWorker.ShouldRunMissingPatrol(now, null, timing));
        Assert.False(DownloadReconcileWorker.ShouldRunMissingPatrol(now.AddSeconds(30), now, timing));
        Assert.Equal(now.AddMinutes(2), DownloadReconcileWorker.NextPatrolAt(now.AddSeconds(30), now, timing));
        Assert.True(DownloadReconcileWorker.ShouldRunMissingPatrol(now.AddMinutes(2), now, timing));
    }

    private static DownloadService ServiceOf(SchedulingHarness h) =>
        h.Provider.GetRequiredService<DownloadService>();

    private static async Task<Guid> CreateDueCompletedAsync(
        SchedulingHarness h, string name, DateTimeOffset? due = null)
    {
        var             taskId = await h.CreateUploadAsync(TestTorrentBytes.Create(name));
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(t => t.Id == taskId);
        entity.Status                = DownloadTaskStatus.Completed;
        entity.CompletedAtUtc        = h.Time.GetUtcNow();
        entity.NextMissingCheckAtUtc = due ?? h.Time.GetUtcNow().AddMinutes(-1);
        await db.SaveChangesAsync();
        return taskId;
    }

    private static Task<int> CountPendingAsync(SchedulingHarness h) =>
        CountAsync(h, task => task.MissingCheckPendingAtUtc != null);

    private static Task<int> CountDueUnprocessedAsync(SchedulingHarness h, DateTimeOffset due) =>
        CountAsync(h, task => task.NextMissingCheckAtUtc == due && task.MissingCheckPendingAtUtc == null);

    private static async Task<int> CountAsync(
        SchedulingHarness h, Expression<Func<DownloadTask, bool>> predicate)
    {
        await using var db = await h.CreateDbContextAsync();
        return await db.DownloadTasks.CountAsync(predicate);
    }
}
