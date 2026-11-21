using Banned.Qbittorrent.Exceptions;
using Banori.Core.Rules;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>
///     B 域聚焦验收：人工重下载的入口核对、受控重试两条路径、重复与同 hash 防护，
///     以及预览按 hash 即时核对与 Replaced 清理确认标记。全部使用 fake qB 与可控时钟。
/// </summary>
public sealed class DownloadMissingTorrentRetryTests
{
    [Fact]
    public async Task RetryAsync_SeedReappears_ClearsMissingMarkersAndKeepsStatus()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await h.CreateUploadAsync(TestTorrentBytes.Create("Reappeared Anime - 01"));
        var       hash   = (await h.GetTaskAsync(taskId)).InfoHash;
        h.Store.AddTorrent(hash, "Reappeared", QbittorrentTorrentCategory.Seeding, 1, "/downloads/Media/Anime");
        var at = h.Time.GetUtcNow();
        await SetMissingCompletedAsync(h, taskId, at);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOf(h).RetryAsync(taskId));

        Assert.Contains("已存在该种子", error.Message);
        var task = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal(at, task.CompletedAtUtc);
        Assert.Null(task.TorrentMissingAtUtc);
        Assert.Empty(h.Store.AddCalls);
    }

    [Fact]
    public async Task RetryAsync_QueryUnknown_ThrowsWithoutStateChange()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await h.CreateUploadAsync(TestTorrentBytes.Create("Unknown Query Anime - 01"));
        var       at     = h.Time.GetUtcNow();
        await SetMissingCompletedAsync(h, taskId, at);
        h.Factory.SessionConfigurator =
            session => session.BeforeFindTorrentsAsync = _ => throw new QbittorrentException("模拟查询失败");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOf(h).RetryAsync(taskId));

        Assert.Contains("查询下载器中的种子失败", error.Message);
        var task = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal(at, task.TorrentMissingAtUtc);
        Assert.Empty(h.Store.AddCalls);
    }

    [Fact]
    public async Task RetryAsync_SeedGone_RequeuesWithStaleEvidenceCleared()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await h.CreateUploadAsync(TestTorrentBytes.Create("Gone Anime - 01"));
        var       at     = h.Time.GetUtcNow();
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(task => task.Id == taskId);
            entity.Status                    = DownloadTaskStatus.Failed;
            entity.PendingSavePath           = "/downloads/stale";
            entity.PendingLayoutRequestCount = 8;
            entity.SelectionRequestCount     = 8;
            entity.Error                     = "旧错误";
            await db.SaveChangesAsync();
        }

        await ServiceOf(h).RetryAsync(taskId);

        var retried = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, retried.Status);
        Assert.Null(retried.PendingSavePath);
        Assert.Null(retried.VerificationIntent);
        Assert.Equal(0, retried.PendingLayoutRequestCount);
        Assert.Equal(0, retried.SelectionRequestCount);
        Assert.Null(retried.Error);
        Assert.Null(retried.TorrentMissingAtUtc);
    }

    [Fact]
    public async Task RetryAsync_CompletedMissingWithReusablePieces_RebuildsVerificationMapping()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Reusable Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        var       hash        = task.InfoHash;
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        var size     = torrent.Files[0].Length;

        // 本任务种子在 qB、身份完整，先初始化文件清单并算归档目标。
        h.Store.AddTorrent(hash, "Reusable", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, relative, size)];
        await fileService.InitializeAsync(taskId);
        var target     = Assert.Single(await fileService.ComputeArchivalTargetsAsync(taskId));
        var split      = target.TargetPath.LastIndexOf('/');
        var targetDir  = target.TargetPath[..split];
        var targetFile = target.TargetPath[(split + 1)..];

        // 外部种子在归档目标上保留完整成片：构成可复用候选。
        const string externalName = "External Reused Pack";
        var          externalHash = TestTorrentBytes.InfoHashOf(externalName);
        h.Store.AddTorrent(externalHash, externalName, QbittorrentTorrentCategory.Seeding, 1,
                           targetDir, [targetFile]);
        h.Store.FileIdentities[externalHash] = [new TorrentFileSnapshot(0, targetFile, size)];

        var at = h.Time.GetUtcNow();
        h.Store.Torrents.Remove(hash);
        await SetMissingCompletedAsync(h, taskId, at);

        await ServiceOf(h).RetryAsync(taskId);
        await h.ReconcileNowAsync(); // SubmitAsync → TryPlanVerificationAddAsync

        var submitted = await h.GetTaskAsync(taskId);
        Assert.NotNull(submitted.VerificationIntent);
        var intent = DownloadVerificationIntent.FromJson(submitted.VerificationIntent);
        Assert.NotNull(intent);
        Assert.Contains(intent.Files, mapping => mapping.IsCandidate);
        Assert.Equal(at, submitted.CompletedAtUtc);
        Assert.NotEmpty(h.Store.AddCalls);
    }

    [Fact]
    public async Task RetryAsync_StaleVerificationIntent_EntersIsolationAndReaddsToTaskRoot()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       bytes  = TestTorrentBytes.Create("Isolated Anime - 01");
        var       taskId = await h.CreateUploadAsync(bytes);
        var       task   = await h.GetTaskAsync(taskId);
        var       hash   = task.InfoHash;
        var       at     = h.Time.GetUtcNow();
        h.Store.AddTorrent(hash, "Isolated", QbittorrentTorrentCategory.Downloading, 0.5, task.SavePath);

        var stale = new DownloadVerificationIntent
        {
            Round = 1, Step = DownloadVerificationStep.Unconfirmed, CreatedAtUtc = at, Files = []
        };
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status              = DownloadTaskStatus.Failed;
            entity.VerificationIntent  = stale.ToJson();
            entity.TorrentMissingAtUtc = at;
            await db.SaveChangesAsync();
        }

        h.Store.Torrents.Remove(hash);
        await ServiceOf(h).RetryAsync(taskId);

        var retried = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, retried.Status);
        Assert.NotEqual(DownloadIsolationState.None, retried.IsolationState);
        Assert.Null(retried.VerificationIntent);

        // 隔离状态机：SubmitAsync 转入受控跟踪，Deleting 确认种子不存在后前进到 Readding。
        await h.ReconcileNowAsync();
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(taskId)).IsolationState);

        // 越过添加可见性窗口后以停止状态在任务专属根重加。
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        var add = Assert.Single(h.Store.AddCalls);
        Assert.Equal(DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, taskId), add.SavePath);
    }

    [Fact]
    public async Task RetryAsync_ActiveSameHashTask_Rejected()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       bytes  = TestTorrentBytes.Create("Duplicate Hash Anime - 01");
        var       taskId = await h.CreateUploadAsync(bytes);
        var       task   = await h.GetTaskAsync(taskId);
        var       at     = h.Time.GetUtcNow();
        await SetMissingCompletedAsync(h, taskId, at);
        await using (var db = await h.CreateDbContextAsync())
        {
            db.DownloadTasks.Add(new DownloadTask
            {
                Id               = Guid.NewGuid(), InfoHash                 = task.InfoHash, TorrentFile = bytes,
                Status           = DownloadTaskStatus.Downloading, SavePath = task.SavePath,
                DownloadRootPath = task.DownloadRootPath, NamingSnapshot    = "other", CreatedAtUtc = at
            });
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOf(h).RetryAsync(taskId));

        Assert.Contains("其他任务正在下载中", error.Message);
        Assert.Equal(at, (await h.GetTaskAsync(taskId)).TorrentMissingAtUtc);
        Assert.Empty(h.Store.AddCalls);
    }

    [Fact]
    public async Task RetryAsync_SecondCallWhilePending_Rejected()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await h.CreateUploadAsync(TestTorrentBytes.Create("Double Click Anime - 01"));
        await SetMissingCompletedAsync(h, taskId, h.Time.GetUtcNow());

        await ServiceOf(h).RetryAsync(taskId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceOf(h).RetryAsync(taskId));

        Assert.Contains("只有失败、丢失", error.Message);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, (await h.GetTaskAsync(taskId)).Status);
    }

    [Fact]
    public async Task Preview_SeedGone_ThrowsDedicatedExceptionWithoutWrites()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       taskId      = await h.CreateUploadAsync(TestTorrentBytes.Create("Preview Missing Anime - 01"));
        // 不添加种子到 fake 存储：FindTorrentsAsync 查无。

        var error = await Assert.ThrowsAsync<TorrentMissingException>(() =>
                                                                          fileService.PreviewAsync(taskId, 0,
                                                                              new
                                                                                  DownloadFileEditRequest(DownloadFileKind
                                                                                     .Anime)));

        Assert.Equal("qBittorrent 中已找不到该种子", error.Message);
        Assert.Empty(h.Store.AddCalls);
        Assert.Empty(h.Store.RenameFileCalls);
    }

    [Fact]
    public async Task Preview_QueryUnknown_KeepsOriginalFlow()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("【喵萌奶茶屋】[Preview Anime][01][1080p][简日双语].mkv");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Preview", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);
        h.Factory.SessionConfigurator =
            session => session.BeforeFindTorrentsAsync = _ => throw new QbittorrentException("模拟查询失败");

        // 查询未知时沿用原流程（后续身份/占用保护可能抛出自身原因），但不得误报缺种。
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                                                               fileService.PreviewAsync(taskId, 0,
                                                                   new DownloadFileEditRequest(DownloadFileKind
                                                                      .Anime)));

        Assert.IsNotType<TorrentMissingException>(error);
    }

    /// <summary>查询时种子还在、读取清单前被删：清单读取失败后按 hash 补查确认缺种，给专用提示。</summary>
    [Fact]
    public async Task Preview_SeedDeletedBetweenQueryAndRead_ThrowsDedicatedException()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Preview Race Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Preview", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);

        // 预检时种子可见；第一次读取清单时 qB 删种：隐藏种子并清空文件身份，让清单读取抛身份错误。
        var deleted = false;
        h.Factory.SessionConfigurator = session => session.BeforeListFilesAsync = _ =>
        {
            if (deleted) return Task.CompletedTask;
            deleted = true;
            h.Store.HiddenTorrents.Add(task.InfoHash);
            h.Store.FileIdentities.Remove(task.InfoHash);
            h.Store.Files.Remove(task.InfoHash);
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<TorrentMissingException>(() =>
                                                                          fileService.PreviewAsync(taskId, 0,
                                                                              new
                                                                                  DownloadFileEditRequest(DownloadFileKind
                                                                                     .Anime)));
        Assert.Equal("qBittorrent 中已找不到该种子", error.Message);
    }

    /// <summary>清单读取失败但种子仍在：结果应保留原身份错误，不得误报缺种。</summary>
    [Fact]
    public async Task Preview_ReadFailsButSeedPresent_KeepsIdentityError()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Preview Read Fail Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Preview", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);

        // 只清空清单让读取失败，种子仍在：补查能看到种子，必须沿用原错误。
        h.Factory.SessionConfigurator = session => session.BeforeListFilesAsync = _ =>
        {
            h.Store.FileIdentities.Remove(task.InfoHash);
            h.Store.Files.Remove(task.InfoHash);
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                                                               fileService.PreviewAsync(taskId, 0,
                                                                   new DownloadFileEditRequest(DownloadFileKind
                                                                      .Anime)));
        Assert.IsNotType<TorrentMissingException>(error);
        Assert.IsType<DownloadFileIdentityException>(error);
    }

    [Fact]
    public async Task Preview_CancelledDuringRead_PropagatesCancellation()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Preview Cancel Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Preview", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);

        using var cts = new CancellationTokenSource();
        h.Factory.SessionConfigurator = session => session.BeforeListFilesAsync = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    fileService.PreviewAsync(taskId, 0,
                                                                        new
                                                                            DownloadFileEditRequest(DownloadFileKind
                                                                               .Anime), cts.Token));
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_WritesMarkerAndDeletesWithoutFiles()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await SeedReplacedTombstoneAsync(h, "Replaced Cleanup Anime - 01");
        var       task   = await h.GetTaskAsync(taskId);
        var       hash   = task.InfoHash;
        h.Store.AddTorrent(hash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);

        await h.ReconcileNowAsync();

        Assert.Contains(h.Store.DeleteCalls, call => call.Hash == hash && !call.DeleteFiles);
        Assert.NotNull((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);

        // 已确认的墓碑退出清理资格，下一轮不再重复删除。
        var count = h.Store.DeleteCalls.Count;
        await h.ReconcileNowAsync();
        Assert.Equal(count, h.Store.DeleteCalls.Count);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_UsesSameConnectionSnapshotForKeyAndSession()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await SeedReplacedTombstoneAsync(h, "Snapshot Cleanup Anime - 01");
        var       task   = await h.GetTaskAsync(taskId);
        var       hash   = task.InfoHash;
        // 墓碑已成功查无：本轮只落确认标记，不删除。标记的配置指纹必须来自会话实际使用的同一份连接快照。
        await h.ReconcileNowAsync();

        var confirmed = await h.GetTaskAsync(taskId);
        Assert.NotNull(confirmed.MissingCleanupConfirmedAtUtc);
        Assert.NotNull(h.Factory.LastSettings);
        var snapshotKey = DownloadService.BuildMissingCheckConfigKey(h.Factory.LastSettings!);
        Assert.StartsWith(snapshotKey, confirmed.MissingCleanupScopeKey);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_QueryUnknown_KeepsEligibility()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await SeedReplacedTombstoneAsync(h, "Replaced Unknown Anime - 01");
        var       task   = await h.GetTaskAsync(taskId);
        var       hash   = task.InfoHash;
        h.Store.AddTorrent(hash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = hashes =>
            hashes.Contains(hash, StringComparer.OrdinalIgnoreCase)
                ? throw new QbittorrentException("模拟查询失败")
                : Task.CompletedTask;

        await h.ReconcileNowAsync();

        Assert.Empty(h.Store.DeleteCalls);
        Assert.Null((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);

        // 查询恢复后保留资格：同一墓碑在下一轮完成清理并落标记。
        h.Factory.SessionConfigurator = null;
        await h.ReconcileNowAsync();
        Assert.Contains(h.Store.DeleteCalls, call => call.Hash == hash && !call.DeleteFiles);
        Assert.NotNull((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_SelectionLossTombstone_Skipped()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await SeedReplacedTombstoneAsync(h, "Selection Loss Tombstone - 01", "择优");
        var       task   = await h.GetTaskAsync(taskId);
        h.Store.AddTorrent(task.InfoHash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);

        await h.ReconcileNowAsync();

        Assert.Empty(h.Store.DeleteCalls);
        Assert.Null((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);
    }

    [Fact]
    public async Task RetryAsync_OnlyOwnArchivedPieces_RebuildsVerificationMapping()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Own Pieces Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        var       hash        = task.InfoHash;
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        var size     = torrent.Files[0].Length;

        h.Store.AddTorrent(hash, "Own Pieces", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, relative, size)];
        await fileService.InitializeAsync(taskId);
        var target = Assert.Single(await fileService.ComputeArchivalTargetsAsync(taskId));

        // 只有本任务自己的旧成片：没有外部种子，归档路径与大小都与目标一致。
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.VerificationPath    = target.TargetPath;
            file.VerificationOutcome = DownloadFileVerificationOutcomes.ReusedVerified;
            await db.SaveChangesAsync();
        }

        var at = h.Time.GetUtcNow();
        h.Store.Torrents.Remove(hash);
        await SetMissingCompletedAsync(h, taskId, at);
        await ServiceOf(h).RetryAsync(taskId);
        await h.ReconcileNowAsync();

        var submitted = await h.GetTaskAsync(taskId);
        var intent    = DownloadVerificationIntent.FromJson(submitted.VerificationIntent);
        Assert.NotNull(intent);
        var mapping = Assert.Single(intent.Files);
        Assert.True(mapping.IsCandidate);
        Assert.Equal(target.TargetPath, mapping.VerificationPath);
        Assert.Equal("/downloads", Assert.Single(h.Store.AddCalls).SavePath);
    }

    [Fact]
    public async Task OwnReuseCandidate_PrefersConfirmedArchivePathOverStaleVerificationPath()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Confirmed Layout Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);

        h.Store.AddTorrent(task.InfoHash, "Confirmed", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);
        var target = Assert.Single(await fileService.ComputeArchivalTargetsAsync(taskId));

        // 成片已独立下载并成功归档到 target：SavePath + CurrentRelativePath 指向归档目标，
        // 但 VerificationPath 残留此前复用失败时的旧校验路径（ReuseUnverified）。
        var slash            = target.TargetPath.LastIndexOf('/');
        var archivedRoot     = target.TargetPath[..slash];
        var archivedRelative = target.TargetPath[(slash + 1)..];
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id                 == taskId);
            var file   = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            entity.SavePath          = archivedRoot;
            file.CurrentRelativePath = archivedRelative;
            file.VerificationPath    = "/downloads/Elsewhere/old-verify.mkv";
            file.VerificationOutcome = DownloadFileVerificationOutcomes.ReuseUnverified;
            await db.SaveChangesAsync();
        }

        // 归档目标上的成片仍应被识别为可复用候选，不被旧校验路径覆盖。
        var candidates =
            await fileService.EnumerateOwnReuseCandidatesAsync(taskId, [(target.FileIndex, target.TargetPath)]);
        var candidate = Assert.Single(candidates);
        Assert.Equal(target.TargetPath, candidate.AbsolutePath);
        Assert.True(candidate.Managed);
    }

    [Fact]
    public async Task RetryAsync_NoReusableOwnPieces_AddsToTaskStagingRoot()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Staged Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);
        h.Store.AddTorrent(task.InfoHash, "Staged", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);

        // 旧成片不在归档目标上：没有可复用候选，也不能写回旧文件所在目录。
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.VerificationPath = "/downloads/Elsewhere/old.mkv";
            await db.SaveChangesAsync();
        }

        h.Store.Torrents.Remove(task.InfoHash);
        await SetMissingCompletedAsync(h, taskId, h.Time.GetUtcNow());
        await ServiceOf(h).RetryAsync(taskId);
        await h.ReconcileNowAsync();

        Assert.Null((await h.GetTaskAsync(taskId)).VerificationIntent);
        Assert.Equal(DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, taskId),
                     Assert.Single(h.Store.AddCalls).SavePath);
    }

    [Fact]
    public async Task RetryAsync_FailedAdd_KeepsOldPiecesRootUntilConfirmed()
    {
        using var    h       = await SchedulingHarness.CreateAsync();
        var          bytes   = TestTorrentBytes.Create("Preserved Root Anime - 01");
        var          taskId  = await h.CreateUploadAsync(bytes);
        var          task    = await h.GetTaskAsync(taskId);
        const string oldRoot = "/downloads/Media/Anime/Old";
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.SavePath = oldRoot;
            await db.SaveChangesAsync();
        }

        await SetMissingCompletedAsync(h, taskId, h.Time.GetUtcNow());
        await ServiceOf(h).RetryAsync(taskId);
        h.Factory.SessionConfigurator = session => session.AddException = new QbittorrentException("模拟提交失败");
        await h.ReconcileNowAsync();

        var failed  = await h.GetTaskAsync(taskId);
        var planned = DownloadTaskPlacement.TaskRoot(task.DownloadRootPath!, taskId);
        Assert.Equal(oldRoot, failed.SavePath);
        Assert.Equal(planned, failed.PendingSubmitSavePath);
        Assert.Empty(h.Store.AddCalls);

        h.Factory.SessionConfigurator = null;
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.ReconcileNowAsync();
        var confirmed = await h.GetTaskAsync(taskId);
        // 已归档任务重下：Add 成功只确认下载器布局，旧成片根保持有效直到校验/归档成功。
        Assert.Equal(oldRoot, confirmed.SavePath);
        Assert.Equal(planned, confirmed.PendingSubmitSavePath);
        Assert.Single(h.Store.AddCalls);
    }

    [Fact]
    public async Task RetryAsync_ArchivedPieces_PublishNewPathsOnlyAfterArchive()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Renamed Archive Anime - 01");
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

        // 旧成片已改名，不能再作为复用候选；缺种重下必须走任务专属暂存根。
        await SetMissingCompletedAsync(h, taskId, h.Time.GetUtcNow());
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id                 == taskId);
            var file   = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            entity.SavePath          = archivedRoot;
            file.CurrentRelativePath = archivedRelative;
            file.VerificationPath    = null;
            await db.SaveChangesAsync();
        }

        h.Store.Torrents.Remove(task.InfoHash);
        await ServiceOf(h).RetryAsync(taskId);
        await h.ReconcileNowAsync();

        // Add 成功只推进本轮下载器布局：CurrentRelativePath 切到种子原始文件名，
        // 已发布成片布局仍保存旧根与改名后的完整路径，逐集现任不会指错。
        var afterAdd = await h.GetTaskAsync(taskId);
        var staging  = Assert.Single(h.Store.AddCalls).SavePath;
        Assert.Equal(archivedRoot, afterAdd.SavePath);
        Assert.Equal(staging, afterAdd.PendingSubmitSavePath);
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking()
                               .SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(relative, file.CurrentRelativePath);
            Assert.Equal(archivedRelative, file.PublishedRelativePath);
            Assert.Equal(archivedRoot + "/" + archivedRelative, file.PublishedPath);
        }

        // 新种子在暂存根按原始文件名完成，归档成功后才发布最终路径。
        h.Store.StoppedAdds.Remove(task.InfoHash);
        h.Store.AddTorrent(task.InfoHash, "New", QbittorrentTorrentCategory.Seeding, 1, staging);
        h.Store.FileIdentities[task.InfoHash] =
            [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length, 1.0)];
        for (var i = 0; i < 6; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(60));
            await h.ReconcileNowAsync();
        }

        var completed = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.PendingSubmitSavePath);
        Assert.Null(completed.ArchiveReview);
        Assert.NotEqual(archivedRoot, completed.SavePath);
        await using (var db = await h.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking()
                               .SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(target.TargetPath,
                         DownloadPathNamePolicy.JoinSavePath(completed.SavePath, file.CurrentRelativePath));
            Assert.Equal(target.TargetPath, file.PublishedPath);
        }
    }

    [Fact]
    public async Task RetryAsync_TorrentStillPresent_KeepsUnconfirmedLayoutJournal()
    {
        using var h           = await SchedulingHarness.CreateAsync();
        var       fileService = h.Provider.GetRequiredService<DownloadFileService>();
        var       bytes       = TestTorrentBytes.Create("Normal Retry Anime - 01");
        var       taskId      = await h.CreateUploadAsync(bytes);
        var       task        = await h.GetTaskAsync(taskId);
        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        var relative = TorrentDownloadPolicy.GetRelativePath(torrent, torrent.Files[0], isMultiFile);

        h.Store.AddTorrent(task.InfoHash, "Still Present", QbittorrentTorrentCategory.Stopped, 1, task.SavePath);
        h.Store.FileIdentities[task.InfoHash] = [new TorrentFileSnapshot(0, relative, torrent.Files[0].Length)];
        await fileService.InitializeAsync(taskId);
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.Status                    = DownloadTaskStatus.Failed;
            entity.PendingSavePath           = "/downloads/pending";
            entity.PendingLayoutRequestCount = 8;
            entity.SelectionRequestCount     = 8;
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
            file.PendingRenameFrom = relative;
            file.PendingRenameTo   = "renamed.mkv";
            await db.SaveChangesAsync();
        }

        await ServiceOf(h).RetryAsync(taskId);

        var retried = await h.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.PendingSubmit, retried.Status);
        Assert.Equal("/downloads/pending", retried.PendingSavePath);
        Assert.Equal(0, retried.PendingLayoutRequestCount);
        Assert.Equal(0, retried.SelectionRequestCount);
        await using var verify = await h.CreateDbContextAsync();
        var             after  = await verify.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
        Assert.Equal(relative, after.PendingRenameFrom);
        Assert.Equal("renamed.mkv", after.PendingRenameTo);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_DeleteThenStillPresent_DoesNotConfirm()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await SeedReplacedTombstoneAsync(h, "Still Present Tombstone - 01");
        var       task   = await h.GetTaskAsync(taskId);
        var       hash   = task.InfoHash;
        h.Store.AddTorrent(hash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        var snapshot = h.Store.Torrents[hash];
        var lookups  = 0;
        h.Factory.SessionConfigurator = session => session.BeforeFindTorrentsAsync = hashes =>
        {
            if (!hashes.Contains(hash, StringComparer.OrdinalIgnoreCase)) return Task.CompletedTask;
            if (++lookups == 2) h.Store.Torrents[hash] = snapshot; // 删除请求返回后种子又出现
            return Task.CompletedTask;
        };

        await h.ReconcileNowAsync();
        Assert.Single(h.Store.DeleteCalls);
        Assert.Null((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);

        // 下一轮按 hash 复查确实查无才落确认标记。
        h.Factory.SessionConfigurator = null;
        await h.ReconcileNowAsync();
        Assert.Equal(2, h.Store.DeleteCalls.Count);
        Assert.NotNull((await h.GetTaskAsync(taskId)).MissingCleanupConfirmedAtUtc);
    }

    [Fact]
    public async Task Reconcile_ReplacedCleanup_NewReplacement_ReconfirmsCleanup()
    {
        using var h      = await SchedulingHarness.CreateAsync();
        var       taskId = await SeedReplacedTombstoneAsync(h, "New Replacement Tombstone - 01");
        var       task   = await h.GetTaskAsync(taskId);
        var       hash   = task.InfoHash;
        h.Store.AddTorrent(hash, "Old", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);

        await h.ReconcileNowAsync();
        Assert.Single(h.Store.DeleteCalls);
        var confirmed = await h.GetTaskAsync(taskId);
        Assert.NotNull(confirmed.MissingCleanupConfirmedAtUtc);
        var firstScope = confirmed.MissingCleanupScopeKey;

        var nextReplacement = Guid.NewGuid();
        await using (var db = await h.CreateDbContextAsync())
        {
            var entity = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
            entity.ReplacedByTaskId = nextReplacement;
            await db.SaveChangesAsync();
        }

        h.Store.AddTorrent(hash, "Old Again", QbittorrentTorrentCategory.Seeding, 1, task.SavePath);
        await h.ReconcileNowAsync();

        Assert.Equal(2, h.Store.DeleteCalls.Count);
        var latest = await h.GetTaskAsync(taskId);
        Assert.NotEqual(firstScope, latest.MissingCleanupScopeKey);
        Assert.EndsWith(nextReplacement.ToString("D"), latest.MissingCleanupScopeKey!);
    }

    private static DownloadService ServiceOf(SchedulingHarness h) =>
        h.Provider.GetRequiredService<DownloadService>();

    private static async Task SetMissingCompletedAsync(SchedulingHarness h, Guid taskId, DateTimeOffset at)
    {
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(task => task.Id == taskId);
        entity.Status              = DownloadTaskStatus.Completed;
        entity.CompletedAtUtc      = at;
        entity.TorrentMissingAtUtc = at;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedReplacedTombstoneAsync(
        SchedulingHarness h, string name, string? failedStep = null)
    {
        var             taskId = await h.CreateUploadAsync(TestTorrentBytes.Create(name));
        var             at     = h.Time.GetUtcNow();
        await using var db     = await h.CreateDbContextAsync();
        var             entity = await db.DownloadTasks.SingleAsync(task => task.Id == taskId);
        entity.Status           = DownloadTaskStatus.Replaced;
        entity.ReplacedByTaskId = Guid.NewGuid();
        entity.SubmittedAtUtc   = at;
        entity.FailedStep       = failedStep;
        await db.SaveChangesAsync();
        return taskId;
    }
}
