using Banned.Qbittorrent.Exceptions;
using Banori.Core.Rules;
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
///     本轮 P1-2 红灯回归：按 hash 在 fake qB 的 Add 入口记录请求，断言竞争任务的 Add 请求数为 0。
///     后一次同 hash Add 的 Fails. + 查无不能否定先前结果未知的 Add，占用必须持续保留；
///     只有真正首次明确拒绝（无更早未决 Add）才释放占用并放行另一 hash。
/// </summary>
public sealed class DownloadPendingAddRaceRegressionTests
{
    private const string SharedBundle   = "Race Shared Bundle - 01";
    private const string SharedName     = "Race Shared - 01.mkv";
    private const string SharedRelative = SharedBundle + "/" + SharedName;
    private const string SharedRoot     = "/downloads/Shared";

    /// <summary>经 fake 的真实 Add 入口投递首条请求，入口记录 hash、根与完整目标路径。</summary>
    private static async Task RetryChallengerAsync(SchedulingHarness h, Guid taskId)
    {
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        entity.Status     = DownloadTaskStatus.Failed;
        entity.FailedStep = "提交";
        await db.SaveChangesAsync();
        await ServiceOf(h).RetryAsync(taskId);
    }

    /// <summary>让被挡住的 B 暂时离开竞争路径，避免它自己的占用阻止 A 的补发。</summary>
    private static async Task MoveChallengerAsideAsync(
        SchedulingHarness h, Guid taskId, bool restore, string competingRoot = SharedRoot)
    {
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        entity.DownloadRootPath = restore ? competingRoot : "/downloads/Blocked";
        entity.SavePath         = entity.DownloadRootPath;
        var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
        file.OriginalRelativePath = restore ? SharedRelative : "Blocked Placeholder - 01.mkv";
        if (!restore)
        {
            file.CurrentRelativePath   = file.OriginalRelativePath;
            file.PublishedRelativePath = null;
            file.PublishedPath         = null;
        }

        await db.SaveChangesAsync();
    }

    private static async Task IssueAcceptedAddAsync(SchedulingHarness h, Guid taskId, string root)
    {
        var             task    = await h.GetTaskAsync(taskId);
        await using var session = await h.Factory.CreateAsync();
        await session.AddTorrentAsync(task.TorrentFile, root, [], task.NamingSnapshot, true);
    }

    private static Task SeedUnknownFirstAddAsync(SchedulingHarness h, Guid taskId, string root) =>
        SeedUnknownAddAsync(h, taskId, root, DownloadTaskStatus.Submitting, DownloadIsolationState.None);

    private static Task SeedUnknownIsolationAddAsync(SchedulingHarness h, Guid taskId, string root) =>
        SeedUnknownAddAsync(h, taskId, root, DownloadTaskStatus.Downloading, DownloadIsolationState.Readding);

    private static async Task SeedUnknownAddAsync(
        SchedulingHarness h, Guid taskId, string root, DownloadTaskStatus status, DownloadIsolationState isolation)
    {
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        entity.Status                 = status;
        entity.IsolationState         = isolation;
        entity.PendingSubmitSavePath  = root;
        entity.NextObservationAtUtc   = null;
        entity.SubmittedAtUtc         = h.Time.GetUtcNow();
        entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow();
        entity.AttemptCount           = 1;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task LateDuplicateFail_KeepsFirstOccupation_SecondHashNeverIssuesAdd()
    {
        using var h     = await SchedulingHarness.CreateAsync();
        // 两条种子信息名不同（hash 不同），但种子清单中的原始相对路径和大小完全相同。
        var       a     = CreateSharedPathTorrent(16384);
        var       b     = CreateSharedPathTorrent(32768);
        var       idA   = await h.CreateUploadAsync(a);
        var       idB   = await h.CreateUploadAsync(b);
        var       taskA = await h.GetTaskAsync(idA);
        var       taskB = await h.GetTaskAsync(idB);
        var       hashA = taskA.InfoHash;
        var       hashB = taskB.InfoHash;
        Assert.NotEqual(hashA, hashB);

        // A 的首次 Add 已被下载器接受但响应丢失：实际种子表里有 A、Find/List 隐藏 A，
        // SQLite 已保存本轮提交根占用。A 的文件行保留归档名，原始清单仍将写入共享完整路径。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == idA);
            entity.Status                 = DownloadTaskStatus.Submitting;
            entity.DownloadRootPath       = "/downloads/Shared";
            entity.SavePath               = "/downloads/OldA";
            entity.PendingSubmitSavePath  = "/downloads/Shared";
            entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow();
            entity.SubmittedAtUtc         = h.Time.GetUtcNow();
            entity.AttemptCount           = 1;
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idA);
            Assert.Equal(SharedRelative, file.OriginalRelativePath);
            file.CurrentRelativePath = "Archived A - 01.mkv";
            file.Size                = 1024;

            var bEntity = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            bEntity.DownloadRootPath = "/downloads/Shared";
            bEntity.SavePath         = "/downloads/Shared";
            bEntity.Status           = DownloadTaskStatus.PendingSubmit;
            var bFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
            Assert.Equal(SharedRelative, bFile.OriginalRelativePath);
            bFile.Size = 1024;
            // 含 Seed 附件时提交根直接用 DownloadRootPath = 共享根。
            bFile.Kind = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(hashA, "A", QbittorrentTorrentCategory.Downloading, 0, "/downloads/Shared",
                           [SharedRelative]);
        h.Store.HiddenTorrents.Add(hashA);

        // B 首次提交即被 A 的待确认占用挡住：B 的 Add 请求数为 0。
        await h.ReconcileNowAsync();
        var bState = await h.GetTaskAsync(idB);
        var aState = await h.GetTaskAsync(idA);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
        Assert.Equal(DownloadTaskStatus.Failed, bState.Status);
        Assert.Equal("提交路径", bState.FailedStep);
        Assert.Equal(DownloadTaskStatus.Submitting, aState.Status);
        Assert.Equal("/downloads/Shared", aState.PendingSubmitSavePath);

        // 让被挡住的 B 暂时不占用 A 的提交路径，以便单独核对 A 的补发次数；
        // 正式重试前会把原始清单与提交根恢复。
        await using (var db = await h.CreateDbContextAsync())
        {
            var bEntity = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            bEntity.DownloadRootPath = "/downloads/Blocked";
            bEntity.SavePath         = "/downloads/Blocked";
            var bFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
            bFile.OriginalRelativePath = "Blocked Placeholder - 01.mkv";
            await db.SaveChangesAsync();
        }

        // 窗口到期后 A 补发同 hash：fake 因 A 已存在返回 Fails.，按 hash 查无（A 仍隐藏）。
        h.Factory.SessionConfigurator = session => session.FailOnDuplicate = true;
        h.Time.Advance(TimeSpan.FromSeconds(120));
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();

        // A 的补发确实发生过（单独核对），但结果是拒绝。
        var aAdds = h.Store.AddEntries.Count(entry => entry.Hash == hashA);
        Assert.True(aAdds >= 1, $"A 的补发次数={aAdds}");
        // 关键红灯：先前结果未知的 Add 不能被这次 Fails.+查无否定，占用保留。
        var afterReject = await h.GetTaskAsync(idA);
        Assert.Equal("/downloads/Shared", afterReject.PendingSubmitSavePath);
        Assert.Equal(DownloadTaskStatus.Submitting, afterReject.Status);

        // 人工重试 B：恢复竞争路径后仍被 A 的占用挡住，B 的 Add 请求数必须为 0。
        await using (var db = await h.CreateDbContextAsync())
        {
            var bEntity = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            bEntity.DownloadRootPath = "/downloads/Shared";
            bEntity.SavePath         = "/downloads/Shared";
            var bFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
            bFile.OriginalRelativePath = SharedRelative;
            await db.SaveChangesAsync();
        }

        await ServiceOf(h).RetryAsync(idB);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task FirstExplicitReject_ReleasesOccupation_SecondHashIssuesExactlyOneAdd()
    {
        using var h     = await SchedulingHarness.CreateAsync();
        var       a     = CreateSharedPathTorrent(16384);
        var       b     = CreateSharedPathTorrent(32768);
        var       idA   = await h.CreateUploadAsync(a);
        var       idB   = await h.CreateUploadAsync(b);
        var       hashB = (await h.GetTaskAsync(idB)).InfoHash;

        // B 在第一阶段保持暂停，确保它自己的 Add 不会混入 A 的首次明确拒绝场景。
        await using (var db = await h.CreateDbContextAsync())
        {
            var bEntity = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            bEntity.DownloadRootPath = "/downloads/Shared";
            bEntity.SavePath         = "/downloads/Shared";
            bEntity.Status           = DownloadTaskStatus.Paused;
            var bFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
            bFile.Kind = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        // A 首次提交（无更早未决占用）遭遇明确拒绝：Add 返回 Fails. 且按 hash 查无。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == idA);
            entity.DownloadRootPath      = "/downloads/Shared";
            entity.SavePath              = "/downloads/Shared";
            entity.Status                = DownloadTaskStatus.PendingSubmit;
            entity.PendingSubmitSavePath = null;
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idA);
            file.CurrentRelativePath = "Rejected A - 01.mkv";
            await db.SaveChangesAsync();
        }

        h.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await h.ReconcileNowAsync();
        var rejected = await h.GetTaskAsync(idA);
        Assert.Equal(DownloadTaskStatus.Failed, rejected.Status);
        Assert.Null(rejected.PendingSubmitSavePath);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 占用释放后 B 可以提交：B 的 Add 恰好发出一次。
        h.Factory.SessionConfigurator = null;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            entity.Status = DownloadTaskStatus.PendingSubmit;
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        Assert.Equal(1, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task IsolationExplicitReject_ReleasesTaskRoot_SecondHashIssuesExactlyOneAdd()
    {
        using var h        = await SchedulingHarness.CreateAsync();
        var       a        = CreateSharedPathTorrent(16384);
        var       b        = CreateSharedPathTorrent(32768);
        var       idA      = await h.CreateUploadAsync(a);
        var       idB      = await h.CreateUploadAsync(b);
        var       hashB    = (await h.GetTaskAsync(idB)).InfoHash;
        var       taskRoot = DownloadTaskPlacement.TaskRoot("/downloads", idA);

        await using (var db = await h.CreateDbContextAsync())
        {
            var owner = await db.DownloadTasks.SingleAsync(value => value.Id == idA);
            owner.Status              = DownloadTaskStatus.Completed;
            owner.CompletedAtUtc      = h.Time.GetUtcNow();
            owner.TorrentMissingAtUtc = h.Time.GetUtcNow();
            owner.DownloadRootPath    = "/downloads";
            owner.VerificationIntent = new DownloadVerificationIntent
            {
                Round = 1, Step = DownloadVerificationStep.Unconfirmed, CreatedAtUtc = h.Time.GetUtcNow(), Files = []
            }.ToJson();

            var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            challenger.DownloadRootPath = taskRoot;
            challenger.SavePath         = taskRoot;
            challenger.Status           = DownloadTaskStatus.Paused;
            var challengerFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
            challengerFile.Kind = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        await ServiceOf(h).RetryAsync(idA);
        await h.ReconcileNowAsync(); // 进入隔离重加状态
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(idA)).IsolationState);

        // 隔离重加是全新请求：明确拒绝直接释放任务专属根占用，不留在 Readding 反复重加。
        h.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        var rejected = await h.GetTaskAsync(idA);
        Assert.Equal(DownloadTaskStatus.Failed, rejected.Status);
        Assert.Null(rejected.PendingSubmitSavePath);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 占用释放后，另一 hash 在同一任务专属根下的 Add 恰好发出一次。
        h.Factory.SessionConfigurator = null;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
            entity.Status = DownloadTaskStatus.PendingSubmit;
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        Assert.Equal(1, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task ResendBadRequest_KeepsFirstAcceptedAddPath_SecondHashNeverIssuesAdd()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       setup       = await CreateCompetitionAsync(h, false);
        var       hashA       = setup.HashA;
        var       hashB       = setup.HashB;
        var       firstTarget = SharedRoot + "/" + SharedRelative;

        // A 首次 Add 已被下载器接受但响应丢失；Find/List 暂时隐藏 A，SQLite 已记录提交根占用。
        await IssueAcceptedAddAsync(h, setup.TaskA, SharedRoot);
        h.Store.HiddenTorrents.Add(hashA);
        await SeedUnknownFirstAddAsync(h, setup.TaskA, SharedRoot);
        await h.ReconcileNowAsync();
        var firstAdds = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Single(firstAdds);
        Assert.Equal(SharedRoot, firstAdds[0].SavePath);
        Assert.Contains(firstAdds[0].TargetPaths, target => target.Path == firstTarget);
        Assert.Equal(DownloadTaskStatus.Submitting, (await h.GetTaskAsync(setup.TaskA)).Status);
        Assert.Equal(SharedRoot, (await h.GetTaskAsync(setup.TaskA)).PendingSubmitSavePath);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 人工重试 B 并执行对账：仍被 A 的占用挡住。
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 先让 B 暂时离开竞争路径，单独核对 A 的补发；恢复后再人工重试 B。
        await MoveChallengerAsideAsync(h, setup.TaskB, false);
        // 窗口到期后 A 补发收到 HTTP 400：不能释放首条未决 Add 的实际写入路径。
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        h.Time.Advance(TimeSpan.FromSeconds(120));
        await h.ReconcileNowAsync();
        var resendState = await h.GetTaskAsync(setup.TaskA);
        Assert.True(h.Store.AddEntries.Count(entry => entry.Hash == hashA) == 2,
                    $"A 补发状态={resendState.Status}，错误={resendState.Error}，下次观察={resendState.NextObservationAtUtc}，" +
                    $"尝试={resendState.AttemptCount}，待确认根={resendState.PendingSubmitSavePath}");
        Assert.Equal(SharedRoot, resendState.PendingSubmitSavePath);
        var resent = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Equal(SharedRoot, resent[1].SavePath);
        Assert.Contains(resent[1].TargetPaths, target => target.Path == firstTarget);

        await MoveChallengerAsideAsync(h, setup.TaskB, true);
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        // B Add 入口计数必须为 0。
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task IsolationResendFails_KeepsFirstAcceptedAddPath_SecondHashNeverIssuesAdd()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       setup       = await CreateIsolationCompetitionAsync(h);
        var       hashA       = setup.HashA;
        var       hashB       = setup.HashB;
        var       taskRoot    = DownloadTaskPlacement.TaskRoot("/downloads", setup.TaskA);
        var       firstTarget = taskRoot + "/" + SharedRelative;

        // 首次隔离 Add 已被接受但响应丢失，A 仍不可见。
        await IssueAcceptedAddAsync(h, setup.TaskA, taskRoot);
        h.Store.HiddenTorrents.Add(hashA);
        await SeedUnknownIsolationAddAsync(h, setup.TaskA, taskRoot);
        await h.ReconcileNowAsync();
        var firstAdds = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Single(firstAdds);
        Assert.Equal(taskRoot, firstAdds[0].SavePath);
        Assert.Contains(firstAdds[0].TargetPaths, target => target.Path == firstTarget);
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(setup.TaskA)).IsolationState);
        Assert.Equal(taskRoot, (await h.GetTaskAsync(setup.TaskA)).PendingSubmitSavePath);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 先让 B 暂时离开竞争路径，单独核对 A 的隔离补发；恢复后再人工重试 B。
        await MoveChallengerAsideAsync(h, setup.TaskB, false, taskRoot);
        // 窗口到期后 fake 因 A 已存在返回 Fails.：这只是补发被拒，不能清掉第一次的占用。
        h.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        h.Time.Advance(TimeSpan.FromSeconds(120));
        await h.ReconcileNowAsync();
        var resendState = await h.GetTaskAsync(setup.TaskA);
        Assert.True(h.Store.AddEntries.Count(entry => entry.Hash == hashA) == 2,
                    $"隔离补发状态={resendState.Status}，隔离={resendState.IsolationState}，错误={resendState.Error}，" +
                    $"下次观察={resendState.NextObservationAtUtc}，待确认根={resendState.PendingSubmitSavePath}");
        Assert.Equal(taskRoot, resendState.PendingSubmitSavePath);
        var resent = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Equal(taskRoot, resent[1].SavePath);
        Assert.Contains(resent[1].TargetPaths, target => target.Path == firstTarget);

        await MoveChallengerAsideAsync(h, setup.TaskB, true, taskRoot);
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        // B Add 入口计数必须为 0。
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task ResendReplansNewRoot_KeepsFirstRootOccupied_SecondHashNeverIssuesAdd()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       setup       = await CreateCompetitionAsync(h, true);
        var       hashA       = setup.HashA;
        var       hashB       = setup.HashB;
        var       taskRoot    = DownloadTaskPlacement.TaskRoot("/downloads", setup.TaskA);
        var       firstTarget = taskRoot + "/" + SharedRelative;

        // A 首次 Add 指向任务专属暂存根，响应丢失且仍不可见。
        await IssueAcceptedAddAsync(h, setup.TaskA, taskRoot);
        h.Store.HiddenTorrents.Add(hashA);
        await SeedUnknownFirstAddAsync(h, setup.TaskA, taskRoot);
        await h.ReconcileNowAsync();
        var firstAdds = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Single(firstAdds);
        Assert.Equal(taskRoot, firstAdds[0].SavePath);
        Assert.Contains(firstAdds[0].TargetPaths, target => target.Path == firstTarget);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 窗口期间新增同路径同大小的复用候选，使补发规划改用正式根。
        var fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var target      = (await fileService.ComputeArchivalTargetsAsync(setup.TaskA)).Single().TargetPath;
        await using (var db = await h.CreateDbContextAsync())
        {
            var candidateId = Guid.NewGuid();
            db.DownloadTasks.Add(new DownloadTask
            {
                Id               = candidateId,
                InfoHash         = new string('c', 40),
                TorrentFile      = TestTorrentBytes.Create("Reuse Candidate"),
                SavePath         = DownloadPathNamePolicy.NormalizePath(target[..target.LastIndexOf('/')]),
                DownloadRootPath = "/downloads",
                Status           = DownloadTaskStatus.Completed,
                CompletedAtUtc   = h.Time.GetUtcNow(),
                NamingSnapshot   = "Reuse Candidate"
            });
            db.DownloadTaskFiles.Add(new DownloadTaskFile
            {
                Id                    = Guid.NewGuid(),
                DownloadTaskId        = candidateId,
                FileIndex             = 0,
                Kind                  = DownloadFileKind.Anime,
                ClassificationReason  = string.Empty,
                OriginalRelativePath  = SharedRelative,
                CurrentRelativePath   = target[(target.LastIndexOf('/') + 1)..],
                PublishedRelativePath = target[(target.LastIndexOf('/') + 1)..],
                PublishedPath         = target,
                Size                  = 1024,
                IsSelectedForDownload = true
            });
            await db.SaveChangesAsync();
        }

        h.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        h.Time.Advance(TimeSpan.FromSeconds(120));
        await h.ReconcileNowAsync();
        var resent = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Equal(2, resent.Count);
        // 确认补发确实走到了新根的规划分支；旧根仍由首条未决 Add 占用。
        Assert.Equal(DownloadPathNamePolicy.NormalizePath("/downloads"), resent[1].SavePath);
        Assert.Equal(taskRoot, (await h.GetTaskAsync(setup.TaskA)).PendingSubmitSavePath);

        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        // B Add 入口计数必须为 0。
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task FifthAddAcceptedButResponseLost_WaitsOwnWindow_AndHistoricalSyncDoesNotShortCircuit()
    {
        using var h     = await SchedulingHarness.CreateAsync();
        var       bytes = TestTorrentBytes.Create("Fifth Add Anime - 01");
        var       id    = await h.CreateUploadAsync(bytes);
        var       hash  = (await h.GetTaskAsync(id)).InfoHash;
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == id);
            entity.DownloadRootPath       = "/downloads";
            entity.Status                 = DownloadTaskStatus.Failed;
            entity.FailedStep             = "提交";
            entity.AttemptCount           = DownloadService.MaxAutoSubmitAttempts - 1;
            entity.LastSyncedAtUtc        = h.Time.GetUtcNow();
            entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
            await db.SaveChangesAsync();
        }

        // 第五次 Add 已被下载器接受，但响应丢失且下载器暂时查不到。
        h.Store.HiddenTorrents.Add(hash);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(id);
        // RetryAsync 只重置为 PendingSubmit；预算由上一条待提交请求带入第五次自动提交。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == id);
            entity.AttemptCount           = DownloadService.MaxAutoSubmitAttempts - 1;
            entity.LastSyncedAtUtc        = h.Time.GetUtcNow();
            entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        var fifth = await h.GetTaskAsync(id);
        Assert.Equal(DownloadTaskStatus.Submitting, fifth.Status);
        Assert.Equal(DownloadService.MaxAutoSubmitAttempts, fifth.AttemptCount);
        Assert.NotNull(fifth.PendingSubmitSavePath);
        Assert.Equal(1, h.Store.AddEntries.Count(entry => entry.Hash == hash));

        // 历史 LastSyncedAtUtc 不得短路本轮窗口：人工失败态重试与第 1 秒对账都只观察、不补发。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == id);
            entity.Status          = DownloadTaskStatus.Failed;
            entity.FailedStep      = "提交";
            entity.LastSyncedAtUtc = h.Time.GetUtcNow();
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOf(h).RetryAsync(id));
        Assert.Contains("可见", error.Message);
        await h.RoundAsync();
        Assert.Equal(DownloadTaskStatus.Failed, (await h.GetTaskAsync(id)).Status);
        Assert.Equal(1, h.Store.AddEntries.Count(entry => entry.Hash == hash));
    }

    [Fact]
    public async Task ChangedRootResendAcceptedButLost_KeepsBothRoots_SecondHashNeverIssuesAdd()
    {
        using var    h             = await SchedulingHarness.CreateAsync();
        var          setup         = await CreateCompetitionAsync(h, true);
        var          hashA         = setup.HashA;
        var          hashB         = setup.HashB;
        var          taskRoot      = DownloadTaskPlacement.TaskRoot("/downloads", setup.TaskA);
        const string candidateRoot = "/downloads";
        var          firstTarget   = taskRoot + "/" + SharedRelative;

        // B 暂离竞争路径，单独让 A 的首条 Add 走进任务专属根。
        await MoveChallengerAsideAsync(h, setup.TaskB, false, candidateRoot);

        // A 首条 Add 已被接受但响应丢失，按 hash 查询仍不可见。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == setup.TaskA);
            entity.Status                 = DownloadTaskStatus.PendingSubmit;
            entity.NextObservationAtUtc   = null;
            entity.LastSubmitAttemptAtUtc = null;
            entity.SubmittedAtUtc         = null;
            entity.AttemptCount           = 0;
            var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == setup.TaskB);
            challenger.Status = DownloadTaskStatus.Paused;
            await db.SaveChangesAsync();
        }

        h.Store.HiddenTorrents.Add(hashA);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await h.ReconcileNowAsync();
        var firstAdds = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Single(firstAdds);
        Assert.Equal(taskRoot, firstAdds[0].SavePath);
        Assert.Contains(firstAdds[0].TargetPaths, target => target.Path == firstTarget);

        // 窗口内出现可复用候选，A 的补发规划改用候选根。
        var fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var target      = Assert.Single(await fileService.ComputeArchivalTargetsAsync(setup.TaskA)).TargetPath;
        await using (var db = await h.CreateDbContextAsync())
        {
            var candidateId = Guid.NewGuid();
            db.DownloadTasks.Add(new DownloadTask
            {
                Id               = candidateId,
                InfoHash         = new string('c', 40),
                TorrentFile      = TestTorrentBytes.Create("Reuse Candidate"),
                SavePath         = DownloadPathNamePolicy.NormalizePath(target[..target.LastIndexOf('/')]),
                DownloadRootPath = candidateRoot,
                Status           = DownloadTaskStatus.Completed,
                CompletedAtUtc   = h.Time.GetUtcNow(),
                NamingSnapshot   = "Reuse Candidate"
            });
            db.DownloadTaskFiles.Add(new DownloadTaskFile
            {
                Id                    = Guid.NewGuid(),
                DownloadTaskId        = candidateId,
                FileIndex             = 0,
                Kind                  = DownloadFileKind.Anime,
                ClassificationReason  = string.Empty,
                OriginalRelativePath  = SharedRelative,
                CurrentRelativePath   = target[(target.LastIndexOf('/') + 1)..],
                PublishedRelativePath = target[(target.LastIndexOf('/') + 1)..],
                PublishedPath         = target,
                Size                  = 1024,
                IsSelectedForDownload = true
            });
            await db.SaveChangesAsync();
        }

        h.Time.Advance(TimeSpan.FromSeconds(120));
        await h.ReconcileNowAsync();
        var resent = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Equal(2, resent.Count);
        Assert.Equal(DownloadPathNamePolicy.NormalizePath(candidateRoot), resent[1].SavePath);
        Assert.Contains(resent[1].TargetPaths,
                        entry => entry.Path == DownloadPathNamePolicy.NormalizePath(candidateRoot + "/" +
                                 SharedRelative));
        var aState = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(DownloadTaskStatus.Submitting, aState.Status);
        Assert.NotNull(aState.VerificationIntent);
        Assert.Equal(taskRoot, aState.PendingSubmitSavePath);
        // 补发改根但响应再次丢失：原根与新根都必须持久化占用。
        var expectedRoots = new[] { DownloadPathNamePolicy.NormalizePath(candidateRoot), taskRoot };
        Assert.Equal(expectedRoots,
                     DownloadRoundLayout.PendingAddRoots(aState)
                                        .OrderBy(root => root, StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedRoots,
                     (await ReloadPendingAddRootsAsync(h, setup.TaskA))
                    .OrderBy(root => root, StringComparer.Ordinal).ToArray());

        // 候选行本身也会挡住 B；移除它，只留下 A 的未决占用，再让 B 对同一候选文件人工重试。
        await using (var db = await h.CreateDbContextAsync())
        {
            var candidate = await db.DownloadTasks.SingleAsync(value => value.InfoHash == new string('c', 40));
            db.DownloadTasks.Remove(candidate);
            await db.SaveChangesAsync();
        }

        await MoveChallengerAsideAsync(h, setup.TaskB, true, candidateRoot);
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
        var bState = await h.GetTaskAsync(setup.TaskB);
        Assert.Equal(DownloadTaskStatus.Failed, bState.Status);
        Assert.Equal("提交路径", bState.FailedStep);
    }

    [Fact]
    public async Task VerificationAddHidden_IsolationReset_KeepsCandidateRoot_SecondHashNeverIssuesAdd()
    {
        using var h             = await SchedulingHarness.CreateAsync();
        var       setup         = await CreateCompetitionAsync(h, false);
        var       hashA         = setup.HashA;
        var       hashB         = setup.HashB;
        var       candidateRoot = SharedRoot;
        var       firstTarget   = candidateRoot + "/" + SharedRelative;

        await MoveChallengerAsideAsync(h, setup.TaskB, false, candidateRoot);
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == setup.TaskA);
            entity.Status                 = DownloadTaskStatus.PendingSubmit;
            entity.NextObservationAtUtc   = null;
            entity.LastSubmitAttemptAtUtc = null;
            entity.SubmittedAtUtc         = null;
            entity.AttemptCount           = 0;
            var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == setup.TaskB);
            challenger.Status = DownloadTaskStatus.Paused;
            await db.SaveChangesAsync();
        }

        // 校验 Add 已被接受但响应丢失，查询中 A 仍不可见。
        h.Store.HiddenTorrents.Add(hashA);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await h.ReconcileNowAsync();
        var firstAdds = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Single(firstAdds);
        Assert.Equal(candidateRoot, firstAdds[0].SavePath);
        Assert.Contains(firstAdds[0].TargetPaths, target => target.Path == firstTarget);

        // 可见性窗口到期后对账进入隔离：此时 fake 从未收到 Delete，A 仍在种子表里。
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == setup.TaskA);
            entity.Status                 = DownloadTaskStatus.Downloading;
            entity.IsSystemPaused         = true;
            entity.NextObservationAtUtc   = null;
            entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
            entity.SubmittedAtUtc         = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
            entity.VerificationIntent = new DownloadVerificationIntent
            {
                Round         = 1,
                Step          = DownloadVerificationStep.Mapping,
                CreatedAtUtc  = h.Time.GetUtcNow(),
                CandidateRoot = candidateRoot,
                Files =
                [
                    new DownloadVerificationMapping(0, firstTarget, 1024, true, firstTarget, true)
                ]
            }.ToJson();
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        var deleting = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(DownloadIsolationState.Deleting, deleting.IsolationState);
        Assert.Null(deleting.VerificationIntent);

        await h.ReconcileNowAsync();
        var readding = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(DownloadIsolationState.Readding, readding.IsolationState);
        Assert.DoesNotContain(hashA, h.Store.DeleteCalls.Select(call => call.Hash));
        Assert.Contains(hashA, h.Store.Torrents.Keys);
        Assert.Contains(candidateRoot, DownloadRoundLayout.PendingAddRoots(readding));
        Assert.Contains(candidateRoot, await ReloadPendingAddRootsAsync(h, setup.TaskA));

        await MoveChallengerAsideAsync(h, setup.TaskB, true, candidateRoot);
        await RetryChallengerAsync(h, setup.TaskB);
        var afterRetry = await h.GetTaskAsync(setup.TaskB);
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        var readdRoot = DownloadTaskPlacement.TaskRoot(candidateRoot, setup.TaskA);
        Assert.Contains(h.Store.AddEntries.Where(entry => entry.Hash == hashA),
                        entry => entry.SavePath == readdRoot);
        var bState = await h.GetTaskAsync(setup.TaskB);
        Assert.True(h.Store.AddEntries.Count(entry => entry.Hash == hashB) == 0,
                    $"B Add={string.Join(";", h.Store.AddEntries.Where(entry => entry.Hash == hashB)
                                               .Select(entry => entry.SavePath))}，" +
                    $"重试后状态={afterRetry.Status}，B 状态={bState.Status}，暂停={bState.IsUserPaused}/{bState.IsSystemPaused}，" +
                    $"步骤={bState.FailedStep}，错误={bState.Error}，" +
                    $"A 根={string.Join(",", DownloadRoundLayout.PendingAddRoots(await h.GetTaskAsync(setup.TaskA)))}");
        Assert.Equal(DownloadTaskStatus.Failed, bState.Status);
        Assert.Equal("提交路径", bState.FailedStep);
    }

    [Fact]
    public async Task IsolationReaddAcceptedButHidden_KeepsNewAndOldRoots_SecondHashNeverIssuesAdd()
    {
        using var h         = await SchedulingHarness.CreateAsync();
        var       setup     = await CreateCompetitionAsync(h, false);
        var       hashA     = setup.HashA;
        var       hashB     = setup.HashB;
        var       oldRoot   = SharedRoot;
        var       taskRoot  = DownloadTaskPlacement.TaskRoot(SharedRoot, setup.TaskA);
        var       newTarget = taskRoot + "/" + SharedRelative;

        // B 暂离竞争根并暂停，让 A 的隔离重加预检与 Add 独立完成。
        await MoveChallengerAsideAsync(h, setup.TaskB, false, oldRoot);
        await using (var db = await h.CreateDbContextAsync())
        {
            var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == setup.TaskB);
            challenger.Status = DownloadTaskStatus.Paused;
            await db.SaveChangesAsync();
        }

        // A 在旧候选根的 Add 已被接受但响应丢失：记录实际写入根，且 Find/List 仍查不到。
        await IssueAcceptedAddAsync(h, setup.TaskA, oldRoot);
        h.Store.HiddenTorrents.Add(hashA);
        await SeedUnknownIsolationAddRootAsync(h, setup.TaskA, oldRoot);

        var rootsAtRequests = new List<IReadOnlyList<string>>();
        h.Factory.SessionConfigurator = session => session.BeforeAddTorrentAsync = async () =>
            rootsAtRequests.Add(await ReloadPendingAddRootsAsync(h, setup.TaskA));
        await h.ReconcileNowAsync();

        // A 确实走到隔离重加分支：qB 接受新根请求并返回 Ok.，但种子仍不可见。
        var readds = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Equal(2, readds.Count);
        Assert.Equal(oldRoot, readds[0].SavePath);
        Assert.Equal(taskRoot, readds[1].SavePath);
        Assert.Contains(readds[1].TargetPaths, target => target.Path == newTarget);
        Assert.Contains(rootsAtRequests,
                        roots => roots.Contains(oldRoot) && roots.Contains(taskRoot));
        var aState = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(DownloadIsolationState.Readding, aState.IsolationState);
        Assert.Equal(taskRoot, aState.PendingSubmitSavePath);
        var expectedRoots = new[] { oldRoot, taskRoot }.OrderBy(root => root, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedRoots,
                     DownloadRoundLayout.PendingAddRoots(aState)
                                        .OrderBy(root => root, StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedRoots,
                     (await ReloadPendingAddRootsAsync(h, setup.TaskA))
                    .OrderBy(root => root, StringComparer.Ordinal).ToArray());

        // B 移到 A 的新根竞争同一文件，人工重试后真实对账：新根必须持续占用。
        await MoveChallengerAsideAsync(h, setup.TaskB, true, taskRoot);
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        var bAdds = h.Store.AddEntries.Where(entry => entry.Hash == hashB).ToList();
        Assert.True(bAdds.Count == 0,
                    $"B Add={string.Join(";", bAdds.Select(entry => entry.SavePath))}");
        var bState = await h.GetTaskAsync(setup.TaskB);
        Assert.Equal(DownloadTaskStatus.Failed, bState.Status);
        Assert.Equal("提交路径", bState.FailedStep);
        Assert.Equal(2, h.Store.AddEntries.Count(entry => entry.Hash == hashA));
    }

    [Fact]
    public async Task IsolationReaddPrecheckBlocked_KeepsPriorAddOccupation_SecondHashNeverIssuesAdd()
    {
        using var h        = await SchedulingHarness.CreateAsync();
        var       setup    = await CreateCompetitionAsync(h, false);
        var       hashA    = setup.HashA;
        var       hashB    = setup.HashB;
        var       oldRoot  = SharedRoot;
        var       taskRoot = DownloadTaskPlacement.TaskRoot(SharedRoot, setup.TaskA);

        // A 在旧候选根的 Add 已被接受但响应丢失，旧根仍留未决占用。
        await IssueAcceptedAddAsync(h, setup.TaskA, oldRoot);
        h.Store.HiddenTorrents.Add(hashA);
        await SeedUnknownIsolationAddRootAsync(h, setup.TaskA, oldRoot);

        // 第三个任务 C 占据 A 的隔离重加目标，预检必须挡住这次尚未发出的重加。
        var idC   = await h.CreateUploadAsync(CreateSharedPathTorrent(49152));
        var hashC = (await h.GetTaskAsync(idC)).InfoHash;
        h.Store.AddTorrent(hashC, "Occupier", QbittorrentTorrentCategory.Downloading, 0, taskRoot,
                           [SharedRelative]);
        await using (var db = await h.CreateDbContextAsync())
        {
            var occupier = await db.DownloadTasks.SingleAsync(value => value.Id == idC);
            occupier.DownloadRootPath       = taskRoot;
            occupier.SavePath               = taskRoot;
            occupier.Status                 = DownloadTaskStatus.Paused;
            occupier.SubmittedAtUtc         = h.Time.GetUtcNow();
            occupier.LastSubmitAttemptAtUtc = h.Time.GetUtcNow();
            occupier.NextObservationAtUtc   = null;
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idC);
            file.OriginalRelativePath  = SharedRelative;
            file.CurrentRelativePath   = SharedRelative;
            file.PublishedRelativePath = SharedRelative;
            file.PublishedPath         = taskRoot + "/" + SharedRelative;
            file.Size                  = 1024;
            file.Kind                  = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        await h.ReconcileNowAsync();
        var aState = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(DownloadTaskStatus.Failed, aState.Status);
        Assert.Equal("提交路径", aState.FailedStep);
        // 整段没有 A 的 Delete 或隔离重加请求，只有旧根那一次 Add。
        Assert.Single(h.Store.AddEntries, entry => entry.Hash == hashA);
        Assert.DoesNotContain(h.Store.AddEntries.Where(entry => entry.Hash == hashA),
                              entry => entry.SavePath == taskRoot);
        Assert.DoesNotContain(hashA, h.Store.DeleteCalls.Select(call => call.Hash));
        Assert.Contains(oldRoot, DownloadRoundLayout.PendingAddRoots(aState));
        Assert.Contains(oldRoot, await ReloadPendingAddRootsAsync(h, setup.TaskA));

        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    [Fact]
    public async Task IsolationFirstBadRequest_ReleasesTaskRoot_SecondHashIssuesExactlyOneAdd()
    {
        using var h        = await SchedulingHarness.CreateAsync();
        var       setup    = await CreateIsolationCompetitionAsync(h);
        var       hashA    = setup.HashA;
        var       hashB    = setup.HashB;
        var       taskRoot = DownloadTaskPlacement.TaskRoot("/downloads", setup.TaskA);
        var       target   = taskRoot + "/" + SharedRelative;

        await MoveChallengerAsideAsync(h, setup.TaskB, false, taskRoot);
        await ServiceOf(h).RetryAsync(setup.TaskA);
        await h.ReconcileNowAsync();
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(setup.TaskA)).IsolationState);

        // 首次隔离 Add 收到 HTTP 400：无更早未决请求，明确拒绝需收尾释放本次占用。
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        await h.ReconcileNowAsync();
        var rejected = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(DownloadTaskStatus.Failed, rejected.Status);
        Assert.Null(rejected.PendingSubmitSavePath);
        Assert.Empty(await ReloadPendingAddRootsAsync(h, setup.TaskA));
        var addA = h.Store.AddEntries.Where(entry => entry.Hash == hashA).ToList();
        Assert.Single(addA);
        Assert.Equal(taskRoot, addA[0].SavePath);
        Assert.Contains(addA[0].TargetPaths, entry => entry.Path == target);
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));

        // 占用释放后 B 在同一任务专属根下恰好发出一次 Add。
        h.Factory.SessionConfigurator = null;
        await MoveChallengerAsideAsync(h, setup.TaskB, true, taskRoot);
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        var addB = h.Store.AddEntries.Where(entry => entry.Hash == hashB).ToList();
        Assert.Single(addB);
        Assert.Equal(taskRoot, addB[0].SavePath);
        Assert.Contains(addB[0].TargetPaths, entry => entry.Path == target);
    }

    [Fact]
    public async Task IsolationResendBadRequest_KeepsFirstAcceptedAdd_SecondHashNeverIssuesAdd()
    {
        using var h        = await SchedulingHarness.CreateAsync();
        var       setup    = await CreateIsolationCompetitionAsync(h);
        var       hashA    = setup.HashA;
        var       hashB    = setup.HashB;
        var       taskRoot = DownloadTaskPlacement.TaskRoot("/downloads", setup.TaskA);

        await MoveChallengerAsideAsync(h, setup.TaskB, false, taskRoot);
        // 首次隔离 Add 已被接受但响应丢失，仍不可见。
        await IssueAcceptedAddAsync(h, setup.TaskA, taskRoot);
        h.Store.HiddenTorrents.Add(hashA);
        await SeedUnknownIsolationAddAsync(h, setup.TaskA, taskRoot);
        await h.ReconcileNowAsync();
        Assert.Single(h.Store.AddEntries, entry => entry.Hash == hashA);

        // 窗口到期后补发收到 HTTP 400：不能否定首条结果未知的 Add。
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        h.Time.Advance(TimeSpan.FromSeconds(120));
        await h.ReconcileNowAsync();
        var aState = await h.GetTaskAsync(setup.TaskA);
        Assert.Equal(2, h.Store.AddEntries.Count(entry => entry.Hash == hashA));
        Assert.Equal(DownloadIsolationState.Readding, aState.IsolationState);
        Assert.Equal(taskRoot, aState.PendingSubmitSavePath);
        Assert.Contains(taskRoot, await ReloadPendingAddRootsAsync(h, setup.TaskA));

        h.Factory.SessionConfigurator = null;
        await MoveChallengerAsideAsync(h, setup.TaskB, true, taskRoot);
        await RetryChallengerAsync(h, setup.TaskB);
        await h.ReconcileNowAsync();
        Assert.Equal(0, h.Store.AddEntries.Count(entry => entry.Hash == hashB));
    }

    /// <summary>A 处于隔离重加、旧根仍为未决占用，但没有更早的同 hash Add 请求台账。</summary>
    private static async Task SeedUnknownIsolationAddRootAsync(
        SchedulingHarness h, Guid taskId, string root)
    {
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        entity.Status                 = DownloadTaskStatus.Downloading;
        entity.IsolationState         = DownloadIsolationState.Readding;
        entity.IsSystemPaused         = true;
        entity.PendingSubmitSavePath  = root;
        entity.LastSubmitAttemptAtUtc = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
        entity.SubmittedAtUtc         = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
        DownloadRoundLayout.RegisterPendingAdd(entity, root);
        await db.SaveChangesAsync();
    }

    /// <summary>独立 DbContext 从磁盘重读台账，模拟重启后占用仍然持久化。</summary>
    private static async Task<IReadOnlyList<string>> ReloadPendingAddRootsAsync(
        SchedulingHarness h, Guid taskId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                     .UseSqlite($"Data Source={h.DatabasePath};Pooling=False").Options;
        var             factory = new PooledDbContextFactory<ApplicationDbContext>(options);
        await using var db      = await factory.CreateDbContextAsync();
        var             task    = await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        return DownloadRoundLayout.PendingAddRoots(task);
    }

    /// <summary>共同前置条件：不同 hash、相同原始相对路径与大小，A 的首条 Add 尚未发出。</summary>
    private static async Task<Competition> CreateCompetitionAsync(SchedulingHarness h, bool stageFirstRoot)
    {
        var a     = CreateSharedPathTorrent(16384);
        var b     = CreateSharedPathTorrent(32768);
        var idA   = await h.CreateUploadAsync(a);
        var idB   = await h.CreateUploadAsync(b);
        var hashA = (await h.GetTaskAsync(idA)).InfoHash;
        var hashB = (await h.GetTaskAsync(idB)).InfoHash;
        Assert.NotEqual(hashA, hashB);
        var root = stageFirstRoot ? DownloadTaskPlacement.TaskRoot("/downloads", idA) : SharedRoot;

        await using var db = await h.CreateDbContextAsync();
        var owner = await db.DownloadTasks.SingleAsync(value => value.Id == idA);
        owner.DownloadRootPath = stageFirstRoot ? "/downloads" : SharedRoot;
        owner.SavePath = "/downloads/OldA";
        owner.Status = DownloadTaskStatus.Submitting;
        owner.PendingSubmitSavePath = null;
        owner.NextObservationAtUtc = null;
        owner.LastSubmitAttemptAtUtc = null;
        owner.SubmittedAtUtc = null;
        owner.AttemptCount = 0;
        // 无实际旧任务的替换关系只用于把首条 Add 规划到任务专属根，尚无复用候选。
        if (stageFirstRoot) owner.ReplacesTaskId = Guid.NewGuid();
        var ownerFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idA);
        ownerFile.OriginalRelativePath = SharedRelative;
        ownerFile.CurrentRelativePath  = "Archived A - 01.mkv";
        ownerFile.Size                 = 1024;
        ownerFile.Kind                 = DownloadFileKind.Seed;

        var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
        challenger.DownloadRootPath     = root;
        challenger.SavePath             = root;
        challenger.Status               = DownloadTaskStatus.PendingSubmit;
        challenger.NextObservationAtUtc = null;
        var challengerFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
        challengerFile.OriginalRelativePath = SharedRelative;
        challengerFile.Size                 = 1024;
        challengerFile.Kind                 = DownloadFileKind.Seed;
        await db.SaveChangesAsync();
        return new Competition(idA, idB, hashA, hashB);
    }

    /// <summary>隔离重加场景：A 的旧种子已确认消失，B 在 A 的任务专属根下等待提交。</summary>
    private static async Task<Competition> CreateIsolationCompetitionAsync(SchedulingHarness h)
    {
        var a     = CreateSharedPathTorrent(16384);
        var b     = CreateSharedPathTorrent(32768);
        var idA   = await h.CreateUploadAsync(a);
        var idB   = await h.CreateUploadAsync(b);
        var hashA = (await h.GetTaskAsync(idA)).InfoHash;
        var hashB = (await h.GetTaskAsync(idB)).InfoHash;
        Assert.NotEqual(hashA, hashB);
        var taskRoot = DownloadTaskPlacement.TaskRoot("/downloads", idA);

        await using var db    = await h.CreateDbContextAsync();
        var             owner = await db.DownloadTasks.SingleAsync(value => value.Id == idA);
        owner.Status              = DownloadTaskStatus.Completed;
        owner.CompletedAtUtc      = h.Time.GetUtcNow();
        owner.TorrentMissingAtUtc = h.Time.GetUtcNow();
        owner.DownloadRootPath    = "/downloads";
        owner.VerificationIntent = new DownloadVerificationIntent
        {
            Round = 1, Step = DownloadVerificationStep.Unconfirmed, CreatedAtUtc = h.Time.GetUtcNow(), Files = []
        }.ToJson();

        var challenger = await db.DownloadTasks.SingleAsync(value => value.Id == idB);
        challenger.DownloadRootPath = taskRoot;
        challenger.SavePath         = taskRoot;
        challenger.Status           = DownloadTaskStatus.Paused;
        var challengerFile = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == idB);
        challengerFile.OriginalRelativePath = SharedRelative;
        challengerFile.Size                 = 1024;
        challengerFile.Kind                 = DownloadFileKind.Seed;
        await db.SaveChangesAsync();
        return new Competition(idA, idB, hashA, hashB);
    }

    /// <summary>
    ///     两条不同 hash 的 multi-file 种子：bundle 名与文件清单路径完全相同，
    ///     仅 piece length 不同，因此原始相对路径与大小一致而 InfoHash 不同。
    /// </summary>
    private static byte[] CreateSharedPathTorrent(int pieceLength)
    {
        const int length = 1024;
        var       pieces = new string('0', 20);
        var       files  = "d6:lengthi" + length + "e4:pathl" + B(SharedName) + "ee";
        return Encoding.UTF8.GetBytes("d4:infod5:filesl" + files       + "e4:name"   + B(SharedBundle) +
                                      "12:piece lengthi" + pieceLength + "e6:pieces" + B(pieces)       + "ee");

        static string B(string value) => Encoding.UTF8.GetByteCount(value) + ":" + value;
    }

    private static DownloadService ServiceOf(SchedulingHarness h) =>
        h.Provider.GetRequiredService<DownloadService>();

    private sealed record Competition(Guid TaskA, Guid TaskB, string HashA, string HashB);
}
