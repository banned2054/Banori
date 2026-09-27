using Banned.Qbittorrent.Exceptions;
using MediaManager.Core.Rules;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Downloads;

/// <summary>
///     P3 下载闭环验收：响应丢失、提交后重启、重复触发、人工暂停与安全替换。
///     全部使用假下载器会话与真实 SQLite 临时库，不触发真实下载。
/// </summary>
public sealed class DownloadServiceTests
{
    private const string NamingV1 = "测试作品 E01 [Sc]";
    private const string NamingV2 = "测试作品 E01 v2 [Sc]";

    [Fact]
    public async Task Create_PersistsIntentWithHashAndBlocksActiveDuplicates()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");

        var taskId = await harness.CreateDownloadAsync(itemId);

        await using var db   = await harness.CreateDbContextAsync();
        var             task = await db.DownloadTasks.AsNoTracking().SingleAsync();
        Assert.Equal(DownloadTaskStatus.PendingSubmit, task.Status);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Test Anime - 01"), task.InfoHash);
        Assert.Equal("/downloads/测试作品/" + $"{NamingV1}-{task.InfoHash[..8]}", task.SavePath);
        Assert.Equal(NamingV1, task.NamingSnapshot);
        Assert.Equal(1m, task.Episode);

        // 同一条目重复点击被拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));
        // 不同条目指向同一资源（同 hash）同样被拒绝。
        var sameHashItemId = await SeedAcceptedItemAsync(harness, "item-1-dup", "Test Anime - 01");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(sameHashItemId));
        // 非活动任务不阻止同 hash 再次登记：把任务标记为终态后可重新创建。
        var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        entity.Status = DownloadTaskStatus.Replaced;
        await db.SaveChangesAsync();
        var recreatedTaskId = await harness.CreateDownloadAsync(itemId);
        Assert.NotEqual(taskId, recreatedTaskId);
    }

    [Fact]
    public async Task Reconcile_SubmitsPendingTaskWithIsolatedPath()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        await harness.ReconcileAsync();

        var task = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        Assert.Equal(1, task.AttemptCount);
        Assert.NotNull(task.SubmittedAtUtc);
        var add = Assert.Single(harness.Store.AddCalls);
        Assert.Equal(task.SavePath, add.SavePath);
        Assert.Equal("MediaManager", add.Category);
        var snapshot = Assert.Single(harness.Store.Torrents.Values);
        Assert.StartsWith(task.SavePath, snapshot.SavePath);
    }

    [Fact]
    public async Task Reconcile_ResponseLostButRegistered_ConfirmsByHashWithoutResubmitting()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        harness.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;

        await harness.ReconcileAsync();
        var submitted = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Submitting, submitted.Status);
        Assert.Equal(1, submitted.AttemptCount);

        // 下一轮对账发现下载器中实际已有该任务：直接确认，不重复提交。
        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();

        var confirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, confirmed.Status);
        Assert.Single(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Reconcile_ResponseLostAndNotRegistered_ResubmitsThenFailsAfterCap()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        harness.Factory.SessionConfigurator =
            session => session.AddException = new QbittorrentException("模拟响应丢失");

        for (var attempt = 1; attempt <= DownloadService.MaxAutoSubmitAttempts; attempt++)
        {
            await harness.ReconcileAsync();
            var task = await harness.GetTaskAsync(taskId);
            Assert.Equal(DownloadTaskStatus.Submitting, task.Status);
            Assert.Equal(attempt, task.AttemptCount);
        }

        // 达到自动重试上限后如实失败，等待人工处理；下载器中没有留下任何任务。
        await harness.ReconcileAsync();
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("提交", failed.FailedStep);
        Assert.Empty(harness.Store.Torrents);
    }

    [Fact]
    public async Task Reconcile_AfterRestart_ConfirmsSubmittingTaskByHashWithoutResubmitting()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 模拟提交请求已到达下载器但应用在确认前重启：下载器有任务（隔离路径一致），本地停在 Submitting。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var stored = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            stored.Status         = DownloadTaskStatus.Submitting;
            stored.AttemptCount   = 1;
            stored.SubmittedAtUtc = stored.CreatedAtUtc;
            await db.SaveChangesAsync();
            harness.Store.AddTorrent(stored.InfoHash, "旧登记",
                                     QbittorrentTorrentCategory.Downloading, 0.2, stored.SavePath);
        }

        // 新的作用域与服务实例（等同重启后的进程）按 hash 对账确认。
        await harness.ReconcileAsync();

        var confirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, confirmed.Status);
        Assert.Empty(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Reconcile_PersistsSubmittingBeforeRequest_ThenRestartConfirmsWithoutResubmit()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 在请求真正发往下载器的瞬间读取数据库：Submitting 必须已落库，否则崩溃后会重复提交。
        DownloadTaskStatus? statusAtRequest = null;
        harness.Factory.SessionConfigurator = session =>
        {
            session.RegisterThenThrowTransport = true;
            session.BeforeAddTorrentAsync = async () =>
            {
                await using var db = await harness.CreateDbContextAsync();
                statusAtRequest = (await db.DownloadTasks.AsNoTracking()
                                           .SingleAsync(value => value.Id == taskId)).Status;
            };
        };

        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Submitting, statusAtRequest);
        Assert.Equal(DownloadTaskStatus.Submitting, (await harness.GetTaskAsync(taskId)).Status);

        // 应用在确认前重启：新进程按 Hash 对账确认，不重复提交。
        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();

        var confirmed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, confirmed.Status);
        Assert.Single(harness.Store.AddCalls);
    }

    [Fact]
    public async Task Reconcile_SubmittingTaskWithMismatchedSavePath_FailsWithoutTakingOver()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        // 下载器中已有同 hash 任务，但位于其他目录：不得当作本次隔离下载接管。
        harness.Store.AddTorrent(TestTorrentBytes.InfoHashOf("Test Anime - 01"), "其他目录的旧任务",
                                 QbittorrentTorrentCategory.Downloading, 0.2, "/downloads/测试作品/其他");
        await using (var db = await harness.CreateDbContextAsync())
        {
            var stored = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            stored.Status         = DownloadTaskStatus.Submitting;
            stored.AttemptCount   = 1;
            stored.SubmittedAtUtc = stored.CreatedAtUtc;
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("提交", failed.FailedStep);
        Assert.Empty(harness.Store.AddCalls);
        Assert.Empty(harness.Store.RenameCalls);
    }

    [Fact]
    public async Task Reconcile_TrackedTaskWithMismatchedSavePath_FailsWithoutTakingOver()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        // 下载器中同 hash 任务的保存路径不在本任务的隔离目录：不得继续改名或替换。
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with { SavePath = "/downloads/其他目录" };

        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("对账", failed.FailedStep);
    }

    [Fact]
    public async Task Reconcile_TrackedTaskWithCaseDifferentSavePath_FailsWithoutTakingOver()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        // 目录大小写在 Linux 上敏感：/downloads 与 /Downloads 是不同目录，不得接管。
        var stored = await harness.GetTaskAsync(taskId);
        var hash   = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            SavePath = stored.SavePath.Replace("/downloads/", "/Downloads/", StringComparison.Ordinal)
        };

        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("对账", failed.FailedStep);
    }

    [Fact]
    public async Task Reconcile_DuplicateActiveHashRow_BlockedByUniqueIndex()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);

        await using var db       = await harness.CreateDbContextAsync();
        var             existing = await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        db.DownloadTasks.Add(new DownloadTask
        {
            Id          = Guid.NewGuid(), FeedItemId = existing.FeedItemId, InfoHash = existing.InfoHash,
            TorrentFile = existing.TorrentFile, TorrentUrl = existing.TorrentUrl,
            SavePath    = existing.SavePath, NamingSnapshot = existing.NamingSnapshot,
            Status      = DownloadTaskStatus.PendingSubmit, CreatedAtUtc = existing.CreatedAtUtc
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task PauseAndResume_UserPauseSurvivesReconcileAndOutofBandResumeIsDetected()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        await harness.Service.PauseAsync(taskId);
        var paused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, paused.Status);
        Assert.True(paused.IsUserPaused);
        Assert.Single(harness.Store.PauseCalls);

        // 对账发现任务仍是暂停状态：不得自动恢复。
        await harness.ReconcileAsync();
        var stillPaused = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Paused, stillPaused.Status);
        Assert.Empty(harness.Store.ResumeCalls);

        // 用户在下载器内手动恢复：对账应把任务转回下载中。
        harness.Store.Paused.Remove(TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        var resumed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, resumed.Status);
        Assert.False(resumed.IsUserPaused);

        // 页面恢复动作同样可用。
        await harness.Service.PauseAsync(taskId);
        await harness.Service.ResumeAsync(taskId);
        Assert.Contains(TestTorrentBytes.InfoHashOf("Test Anime - 01"), harness.Store.ResumeCalls);
    }

    [Fact]
    public async Task Reconcile_CompletesWithRenameAndMarksOldTaskReplaced()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        harness.Store.Torrents[TestTorrentBytes.InfoHashOf("Test Anime - 01")] =
            harness.Store.Torrents[TestTorrentBytes.InfoHashOf("Test Anime - 01")] with
            {
                Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
            };
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(NamingV1 + ".mkv", rename.Name);
        // 实际文件也被改名，而不仅是种子展示名。
        var renameFile = Assert.Single(harness.Store.RenameFileCalls);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Test Anime - 01"), renameFile.Hash);
        Assert.Equal("原文件名.mkv", renameFile.OldPath);
        Assert.Equal(NamingV1 + ".mkv", renameFile.NewPath);

        // v2 到来：自动记录替换关系，完成后旧任务被标记为被替换，文件与任务记录保留。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var v2Task   = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(taskIdV1, v2Task.ReplacesTaskId);

        // 先提交 v2，再让它在下载器中完成；随后一轮对账完成改名并标记 v1 被替换。
        await harness.ReconcileAsync();
        var v2Hash = TestTorrentBytes.InfoHashOf("Test Anime - 01 v2");
        harness.Store.Torrents[v2Hash] = harness.Store.Torrents[v2Hash] with
        {
            Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
        };
        await harness.ReconcileAsync();

        var replaced = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replaced.Status);
        Assert.Equal(taskIdV2, replaced.ReplacedByTaskId);
        Assert.Equal(2, harness.Store.Torrents.Count);
    }

    [Fact]
    public async Task Reconcile_CompletesMultiFileTorrent_ByRenamingRootFolder()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] = ["作品文件夹/video.mkv", "作品文件夹/subs.ass"];

        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        var folderRename = Assert.Single(harness.Store.RenameFolderCalls);
        Assert.Equal("作品文件夹", folderRename.OldPath);
        Assert.Equal(NamingV1, folderRename.NewPath);
        Assert.Empty(harness.Store.RenameFileCalls);
        // 多文件种子的展示名不带扩展名，直接使用命名快照。
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(NamingV1, rename.Name);
    }

    [Fact]
    public async Task Reconcile_MultiFileTorrentWithoutRootFolder_FailsInsteadOfFakeCompletion()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        CompleteTorrent(harness, hash);
        harness.Store.Files[hash] = ["original.mkv", "subtitle.ass"];

        await harness.ReconcileAsync();

        // 多个顶层散文件无法唯一映射到命名快照：必须如实失败，不得保留原名却标记完成。
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("改名", failed.FailedStep);
        Assert.Contains("顶层文件", failed.Error);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.RenameFolderCalls);
        Assert.Empty(harness.Store.RenameCalls);
    }

    [Fact]
    public async Task Reconcile_SameNamingAcrossIsolationFolders_CompletesWithoutConflict()
    {
        using var harness = await CreateHarnessAsync();
        var       itemIdA = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdA = await harness.CreateDownloadAsync(itemIdA);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskIdA)).Status);

        // 同集不同字幕组：命名预览相同、hash 不同、隔离目录不同，完成时不得按名字误判冲突。
        var itemIdB = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 [GroupB]");
        var taskIdB = await harness.CreateDownloadAsync(itemIdB);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 [GroupB]"));
        await harness.ReconcileAsync();

        var completedB = await harness.GetTaskAsync(taskIdB);
        Assert.Equal(DownloadTaskStatus.Completed, completedB.Status);
        Assert.Null(completedB.FailedStep);
    }

    [Fact]
    public async Task Reconcile_NewReplacementFailure_LeavesOldResourceUntouched()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();

        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        harness.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await harness.ReconcileAsync();

        var failed = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("提交", failed.FailedStep);
        var untouched = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Downloading, untouched.Status);
        // 被拒绝的 v2 未进入下载器，旧资源是唯一在下载器中的任务。
        var snapshot = Assert.Single(harness.Store.Torrents.Values);
        Assert.Equal(TestTorrentBytes.InfoHashOf("Test Anime - 01"), snapshot.Hash);
    }

    [Fact]
    public async Task Create_ReplacementTargetsCompletedVersion_SkipsFailedIntermediate()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        // v2 提交被下载器拒绝：失败任务没有可供替代的旧文件。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        harness.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Failed, (await harness.GetTaskAsync(taskIdV2)).Status);

        // v3 的替换对象应是已完成的 v1，而不是失败的 v2。
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV3)).ReplacesTaskId);

        harness.Factory.SessionConfigurator = null;
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(taskIdV1)).Status);
        var failedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Failed, failedV2.Status);
        Assert.Null(failedV2.ReplacedByTaskId);
    }

    [Fact]
    public async Task Reconcile_LaterVersionCompletingAfterOlder_LinksReplacementAtCompletion()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);

        // 两个任务都登记后才开始下载：创建 v2 时 v1 尚未完成，创建时无法记录替换对象。
        await harness.ReconcileAsync();
        Assert.Null((await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);

        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        var completedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, completedV1.Status);
        Assert.Null(completedV1.ReplacedByTaskId);

        // v2 完成时补记替换关系：v1 被标记为被替换。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();

        var replacedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV1.Status);
        Assert.Equal(taskIdV2, replacedV1.ReplacedByTaskId);
        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Equal(taskIdV1, completedV2.ReplacesTaskId);
    }

    [Fact]
    public async Task Reconcile_OlderVersionCompletingLast_DoesNotReplaceNewerResource()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        await harness.ReconcileAsync();

        // v2（更高版本）先完成、v1 后完成：旧版本不得反向把新版本标记为被替换。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Null(completedV2.ReplacedByTaskId);
        Assert.Null(completedV2.ReplacesTaskId);
        var completedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Completed, completedV1.Status);
        Assert.Null(completedV1.ReplacesTaskId);
    }

    [Fact]
    public async Task Reconcile_StaleRecordedReplacement_ReSelectedAtCompletion()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        // v2、v3 都在 v1 已完成时创建：两者创建时都记录“替换 v1”。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV3)).ReplacesTaskId);

        // v2 先完成并替换 v1，使 v3 的记录过期。
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(taskIdV1)).Status);

        // v3 完成时重新选择：替换对象应是当前有效的 v2，而不是已被替换的 v1。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();

        var completedV3 = await harness.GetTaskAsync(taskIdV3);
        Assert.Equal(DownloadTaskStatus.Completed, completedV3.Status);
        Assert.Equal(taskIdV2, completedV3.ReplacesTaskId);
        var replacedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV2.Status);
        Assert.Equal(taskIdV3, replacedV2.ReplacedByTaskId);
        // v1 的替换记录保持指向 v2，不被 v3 覆盖。
        var replacedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV1.Status);
        Assert.Equal(taskIdV2, replacedV1.ReplacedByTaskId);
    }

    [Fact]
    public async Task Reconcile_NewerVersionCompletedFirst_ClearsStaleDeclarationOnLaterCompletion()
    {
        using var harness  = await CreateHarnessAsync();
        var       itemIdV1 = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskIdV1 = await harness.CreateDownloadAsync(itemIdV1);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();

        // v2、v3 创建时都记录替换 v1。
        var itemIdV2 = await SeedAcceptedItemAsync(harness, "item-2", "Test Anime - 01 v2",
                                                   NamingV2, 2);
        var taskIdV2 = await harness.CreateDownloadAsync(itemIdV2);
        var itemIdV3 = await SeedAcceptedItemAsync(harness, "item-3", "Test Anime - 01 v3",
                                                   "测试作品 E01 v3 [Sc]", 3);
        var taskIdV3 = await harness.CreateDownloadAsync(itemIdV3);
        Assert.Equal(taskIdV1, (await harness.GetTaskAsync(taskIdV2)).ReplacesTaskId);

        // v3 先完成并替换 v1，使 v2 的声明过期。
        await harness.ReconcileAsync();
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v3"));
        await harness.ReconcileAsync();
        Assert.Equal(taskIdV3, (await harness.GetTaskAsync(taskIdV1)).ReplacedByTaskId);

        // v2 后完成：v1 已被 v3 替换、v3 版本更高不可反向替换，过期声明应被清除而非保留。
        CompleteTorrent(harness, TestTorrentBytes.InfoHashOf("Test Anime - 01 v2"));
        await harness.ReconcileAsync();

        var completedV2 = await harness.GetTaskAsync(taskIdV2);
        Assert.Equal(DownloadTaskStatus.Completed, completedV2.Status);
        Assert.Null(completedV2.ReplacesTaskId);
        var replacedV1 = await harness.GetTaskAsync(taskIdV1);
        Assert.Equal(DownloadTaskStatus.Replaced, replacedV1.Status);
        Assert.Equal(taskIdV3, replacedV1.ReplacedByTaskId);
        var completedV3 = await harness.GetTaskAsync(taskIdV3);
        Assert.Equal(DownloadTaskStatus.Completed, completedV3.Status);
        Assert.Equal(taskIdV1, completedV3.ReplacesTaskId);
    }

    [Fact]
    public async Task Reconcile_MissingTorrentMarksLostAndRetryResubmits()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        harness.Store.Torrents.Remove(TestTorrentBytes.InfoHashOf("Test Anime - 01"));
        await harness.ReconcileAsync();
        var lost = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Lost, lost.Status);
        Assert.Equal("对账", lost.FailedStep);

        await harness.Service.RetryAsync(taskId);
        await harness.ReconcileAsync();
        var retried = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Downloading, retried.Status);
        Assert.Equal(2, harness.Store.AddCalls.Count);
    }

    [Fact]
    public async Task Reconcile_UnconfiguredDownloader_SkipsSilently()
    {
        using var harness = await CreateHarnessAsync(false);
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");

        // 未启用下载器时创建意图被拒绝，对账静默跳过，不产生任何任务或提交。
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(itemId));
        await harness.ReconcileAsync();
        Assert.Empty(harness.Store.AddCalls);
        await using var db = await harness.CreateDbContextAsync();
        Assert.Empty(await db.DownloadTasks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Reconcile_ServerErrorOnTrackedTask_MarksFailedWithDownloaderStep()
    {
        using var harness = await CreateHarnessAsync();
        var       itemId  = await SeedAcceptedItemAsync(harness, "item-1", "Test Anime - 01");
        var       taskId  = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();

        var hash = TestTorrentBytes.InfoHashOf("Test Anime - 01");
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Error
        };

        await harness.ReconcileAsync();
        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("下载器", failed.FailedStep);
    }

    private static void CompleteTorrent(TestHarness harness, string hash)
    {
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Seeding, Progress = 1.0
        };
    }

    private static async Task<Guid> SeedAcceptedItemAsync(TestHarness harness, string stableId, string torrentName,
                                                          string      naming  = NamingV1, int version = 1,
                                                          decimal     episode = 1)
    {
        harness.TorrentNames[stableId] = torrentName;
        await using var db             = await harness.CreateDbContextAsync();
        var             subscriptionId = Guid.NewGuid();
        db.MediaSubscriptions.Add(new MediaSubscription
        {
            Id        = subscriptionId, Name          = $"订阅-{stableId}", FeedUrlProtected = "unused",
            IsEnabled = false, RefreshIntervalMinutes = 30
        });
        var itemId = Guid.NewGuid();
        db.FeedItems.Add(new FeedItem
        {
            Id             = itemId, SubscriptionId = subscriptionId, StableId = stableId, Title = torrentName,
            TorrentUrl     = $"https://mikanani.me/Download/{stableId}.torrent",
            FirstSeenAtUtc = harness.Timestamp.AddHours(-1), Status      = FeedItemStatus.Accepted,
            DecisionStatus = ReleaseDecisionStatus.Accepted, ParsedTitle = torrentName,
            MediaType      = "SingleEpisode", Episode                    = episode, Version    = version, Group = "S1",
            Language       = "Sc", Resolution                            = "R1080P", SubjectId = 42, Reason = "测试决策。",
            NamingPreview  = naming, EvaluatedAtUtc                      = harness.Timestamp
        });
        // P4 批量择优：条目必须是其所在同集组的当前优胜候选才能创建下载任务。
        // 组键 (SubjectId, MediaType, Episode) 有唯一索引；同一测试内同组再播种条目时
        // 以 stableId 区分 MediaType，保证每个条目都能作为优胜通过创建前置核验。
        var groupKey = $"42|SingleEpisode|{episode}";
        var mediaType = harness.UsedWinnerGroups.Add(groupKey) ? "SingleEpisode" : $"SingleEpisode#{stableId}";
        db.CandidateSelections.Add(new CandidateSelection
        {
            Id              = Guid.NewGuid(), SubjectId      = 42, MediaType = mediaType, Episode = episode,
            WinnerFeedItemId = itemId, RuleVersion     = 1, RankingBasis   = "测试优胜",
            NamingSnapshot   = naming, IntentTaskId    = null, SelectedAtUtc = harness.Timestamp
        });
        await db.SaveChangesAsync();
        return itemId;
    }

    private static async Task<TestHarness> CreateHarnessAsync(bool configureDownloader = true)
    {
        var directory           = Directory.CreateTempSubdirectory("MediaManager.P3.Tests.");
        var timestamp           = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var harnessTorrentNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var services            = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options
                                                                  .UseSqlite($"Data Source={Path.Combine(directory.FullName, "p3.db")};Pooling=False"));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(timestamp));
        services.AddSingleton<IApplicationSettingStore, MemoryApplicationSettingStore>();
        var store = new FakeTorrentStore();
        services.AddSingleton(store);
        services.AddSingleton<FakeQbittorrentSessionFactory>();
        services.AddSingleton<IQbittorrentSessionFactory>(provider =>
                                                              provider
                                                                 .GetRequiredService<FakeQbittorrentSessionFactory>());
        services.AddSingleton<ITorrentFileSource>(_ =>
                                                      new FakeTorrentFileSource(uri =>
                                                      {
                                                          // 每个条目的种子名由 seeding 注册；未注册的条目按 stableId 生成独立种子。
                                                          var stableId =
                                                              Path.GetFileNameWithoutExtension(uri.Segments[^1]);
                                                          return TestTorrentBytes.Create(harnessTorrentNames
                                                             .GetValueOrDefault(stableId, stableId));
                                                      }));
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddScoped<DownloadService>();
        // 与应用及订阅/设置测试保持一致的 Identity 模型配置：Identity 服务注册影响
        // ApplicationDbContext 模型缓存的行为，缺注册会与先构建模型的测试产生顺序依赖。
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                      IdentitySchemaVersions.Version3)
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var provider = services.BuildServiceProvider();

        var harness = new TestHarness(directory, timestamp, provider, store,
                                      provider.GetRequiredService<FakeQbittorrentSessionFactory>(),
                                      provider.GetRequiredService<DownloadService>(), harnessTorrentNames);
        await using var db = await harness.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.MediaSubjects.Add(new MediaSubject
        {
            Id          = 42, Name = "Test Subject", NameCn = "测试作品", Platform = "TV",
            CachedAtUtc = timestamp
        });
        await db.SaveChangesAsync();

        if (configureDownloader) await harness.SaveDownloaderSettingsAsync();

        return harness;
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }

    /// <summary>条目 stableId 与种子名的注册表；种子字节按种子名确定性生成，同名单同 hash。</summary>
    private sealed class TestHarness(
        DirectoryInfo                 directory,
        DateTimeOffset                timestamp,
        ServiceProvider               provider,
        FakeTorrentStore              store,
        FakeQbittorrentSessionFactory factory,
        DownloadService               service,
        Dictionary<string, string>    torrentNames) : IDisposable
    {
        public DirectoryInfo                 Directory    { get; } = directory;
        public DateTimeOffset                Timestamp    { get; } = timestamp;
        public FakeTorrentStore              Store        { get; } = store;
        public FakeQbittorrentSessionFactory Factory      { get; } = factory;
        public DownloadService               Service      { get; } = service;
        public Dictionary<string, string>    TorrentNames { get; } = torrentNames;
        public HashSet<string>               UsedWinnerGroups { get; } = new(StringComparer.Ordinal);

        public void Dispose()
        {
            provider.Dispose();
            try
            {
                Directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        public Task<Guid> CreateDownloadAsync(Guid itemId)
        {
            return Service.CreateForFeedItemAsync(itemId);
        }

        public Task ReconcileAsync()
        {
            return Service.ReconcileAsync();
        }

        public async Task<DownloadTask> GetTaskAsync(Guid taskId)
        {
            await using var db = await CreateDbContextAsync();
            return await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        }

        public Task SaveDownloaderSettingsAsync()
        {
            var settingsService = new ManageQbittorrentSettings(provider.GetRequiredService<IApplicationSettingStore>(), Factory);
            return settingsService.SaveAsync(new QbittorrentSettings("http://qb.test:8080", "admin", "/downloads", true), "secret");
        }

        public Task<ApplicationDbContext> CreateDbContextAsync()
        {
            var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            return factory.CreateDbContextAsync();
        }
    }
}
