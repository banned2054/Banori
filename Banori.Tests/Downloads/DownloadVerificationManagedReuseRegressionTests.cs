using Banori.Core.Rules;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadVerificationTests
{
    private static readonly string ManagedOwnerHash = new('d', 40);

    private static async Task<Guid> SeedManagedOccupantAsync(
        VerificationHarness harness, string absolutePath, long size, string? relativeOverride = null,
        long? qbSize = null, string? qbRelative = null, string? qbRoot = null)
    {
        var ownerId  = Guid.NewGuid();
        var fileId   = Guid.NewGuid();
        var relative = relativeOverride ?? absolutePath[(DownloadRoot.Length + 1)..];
        await using (var db = await harness.CreateDbContextAsync())
        {
            var owner = new DownloadTask
            {
                Id             = ownerId, InfoHash = ManagedOwnerHash, SavePath = DownloadRoot,
                Status         = DownloadTaskStatus.Completed, TorrentFile = SingleFileTorrent("繁日双语"),
                NamingSnapshot = "Unrelated Managed Owner",
                CompletedAtUtc = DateTimeOffset.Parse("2026-09-01T00:00:00Z")
            };
            owner.Files.Add(new DownloadTaskFile
            {
                Id                   = fileId, DownloadTaskId = ownerId, FileIndex = 0,
                OriginalRelativePath = "old.mkv", CurrentRelativePath = relative, Size = size,
                Kind                 = DownloadFileKind.Anime, IsSelectedForDownload = true
            });
            db.DownloadTasks.Add(owner);
            await db.SaveChangesAsync();
        }

        harness.Store.AddTorrent(ManagedOwnerHash, "Unrelated Managed Owner", QbittorrentTorrentCategory.Seeding,
                                 1.0, qbRoot ?? DownloadRoot);
        harness.Store.FileIdentities[ManagedOwnerHash] =
            [new TorrentFileSnapshot(0, qbRelative ?? relative, qbSize ?? size, 1.0)];
        return fileId;
    }

    [Fact]
    public async Task Verification_ManagedUnrelatedSamePathSameSize_ReusesDirectlyWithoutReplacementLink()
    {
        using var harness     = await VerificationHarness.CreateAsync();
        var       taskId      = await harness.CreateUploadAsync(SingleFileTorrent());
        var       target      = await ArchivalTargetAsync(harness, taskId);
        var       ownerFileId = await SeedManagedOccupantAsync(harness, target, 1024);
        var       task        = await harness.GetTaskAsync(taskId);

        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交：候选根停止添加并落库校验意图。
        Assert.Contains(task.InfoHash, harness.Store.StoppedAdds);
        var staged = await harness.GetTaskAsync(taskId);
        Assert.NotNull(DownloadVerificationIntent.FromJson(staged.VerificationIntent));
        Assert.Equal(DownloadPathDecisionKinds.ReuseVerification, DownloadPathDecision.FromJson(staged.PathDecision)!.Kind);

        await harness.ReconcileAsync(); // 映射 + recheck。
        await harness.ReconcileAsync(); // 观察 Checking。
        harness.Store.FileIdentities[task.InfoHash] = harness.Store.FileIdentities[task.InfoHash]
                                                             .Select(file => file with { Progress = 1.0 }).ToList();
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync();

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Null(completed.VerificationIntent);
        Assert.Null(completed.ArchiveReview);
        Assert.Empty(harness.Store.ResumeCalls);
        Assert.Empty(harness.Store.SetLocationCalls);
        Assert.Empty(harness.Store.DeleteCalls);
        Assert.DoesNotContain(harness.Store.RenameFileCalls, call => call.Hash == ManagedOwnerHash);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
            Assert.Equal(DownloadPathNamePolicy.NormalizePath(target), file.VerificationPath);
            // 发现候选绝不建立或修改替换关系：占用方保持原状态，挑战者不声明替换，登记表不牵连占用方文件。
            var owner = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                                .SingleAsync(value => value.InfoHash == ManagedOwnerHash);
            Assert.Equal(DownloadTaskStatus.Completed, owner.Status);
            Assert.Null(owner.ReplacedByTaskId);
            Assert.Null(owner.ReplacesTaskId);
            Assert.All(owner.Files, value => Assert.Null(value.SupersededByTaskId));
            Assert.Null((await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId)).ReplacesTaskId);
            Assert.Empty(await db.EpisodeVersions.AsNoTracking()
                                 .Where(value => value.IncumbentVideoFileId == ownerFileId).ToListAsync());
        }
    }

    [Fact]
    public async Task Verification_ManagedUnrelatedCandidateConfirmedMissing_IsolatesAndKeepsOwnerIntact()
    {
        using var harness     = await VerificationHarness.CreateAsync();
        var       taskId      = await harness.CreateUploadAsync(SingleFileTorrent());
        var       target      = await ArchivalTargetAsync(harness, taskId);
        var       ownerFileId = await SeedManagedOccupantAsync(harness, target, 1024);
        var       task        = await harness.GetTaskAsync(taskId);

        harness.Store.RecheckBehavior = FakeRecheckBehavior.EnterChecking;
        await harness.ReconcileAsync(); // 提交（候选根）。
        await harness.ReconcileAsync(); // 映射 + recheck。
        await harness.ReconcileAsync(); // 观察 Checking。
        harness.Store.FinishRecheck(task.InfoHash);
        await harness.ReconcileAsync(); // 确认缺失 → 隔离。

        var isolating = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.Deleting, isolating.IsolationState);
        Assert.Null(isolating.VerificationIntent);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var file = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(value => value.DownloadTaskId == taskId);
            Assert.Equal(DownloadFileVerificationOutcomes.ReuseUnverified, file.VerificationOutcome);
        }

        await harness.ReconcileAsync(); // Deleting：确认停止 → 保留文件删除。
        Assert.Contains(harness.Store.DeleteCalls, call => call.Hash == task.InfoHash && !call.DeleteFiles);
        await harness.ReconcileAsync(); // Readding：任务专属根停止重加。
        Assert.Contains(harness.Store.AddCalls, call => call.SavePath == TaskRootOf(harness, taskId));
        await harness.ReconcileAsync(); // Confirming。
        await harness.ReconcileAsync(); // 布局确认 → 回 Planned。

        var restored = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadIsolationState.None, restored.IsolationState);
        Assert.Equal(DownloadPreparationStage.Planned, restored.PreparationStage);
        Assert.Equal(TaskRootOf(harness, taskId), restored.SavePath);

        for (var round = 0; round < 3; round++) await harness.ReconcileAsync();
        CompleteTorrent(harness, task.InfoHash);
        await harness.ReconcileAsync(); // 独立完成后目标仍被占用 → 待核对保留。

        var completed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(TaskRootOf(harness, taskId), completed.SavePath);
        Assert.NotNull(DownloadArchiveReview.FromJson(completed.ArchiveReview));
        await AssertOwnerUntouchedAsync(harness, ownerFileId, target[(DownloadRoot.Length + 1)..]);
    }

    [Fact]
    public async Task EnumerateReuseCandidates_ManagedUnrelatedStableFile_YieldsSingleManagedCandidate()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(SingleFileTorrent());
        var       target  = await ArchivalTargetAsync(harness, taskId);
        await SeedManagedOccupantAsync(harness, target, 1024);

        var             service = harness.Provider.GetRequiredService<DownloadFileService>();
        await using var session = await harness.Factory.CreateAsync();
        var candidates = await service.EnumerateReuseCandidatesAsync(session, taskId,
                                                                     await service.ComputeArchivalTargetsAsync(taskId));
        // 同一文件同时出现在数据库登记与 qB 实际清单中时，只构成一个候选。
        var candidate = Assert.Single(candidates);
        Assert.Equal(0, candidate.FileIndex);
        Assert.Equal(DownloadPathNamePolicy.NormalizePath(target),
                     DownloadPathNamePolicy.NormalizePath(candidate.AbsolutePath));
        Assert.Equal(1024, candidate.Size);
        Assert.True(candidate.Managed);
    }

    [Theory]
    [InlineData("pending-save")]
    [InlineData("pending-rename")]
    [InlineData("verifying")]
    [InlineData("isolating")]
    [InlineData("qb-moving")]
    [InlineData("qb-checking")]
    [InlineData("different-size")]
    [InlineData("parent-overlap")]
    [InlineData("db-qb-size-mismatch")]
    [InlineData("db-qb-root-mismatch")]
    [InlineData("db-qb-path-mismatch")]
    [InlineData("qb-incomplete")]
    [InlineData("qb-stopped-incomplete")]
    public async Task EnumerateReuseCandidates_ManagedUnrelatedUnstableOrMismatched_YieldsNothing(string mode)
    {
        using var harness  = await VerificationHarness.CreateAsync();
        var       taskId   = await harness.CreateUploadAsync(SingleFileTorrent());
        var       target   = await ArchivalTargetAsync(harness, taskId);
        var       relative = target[(DownloadRoot.Length + 1)..];
        await SeedOwnerForModeAsync(harness, target, relative, mode);
        await ApplyOwnerInstabilityAsync(harness, relative, mode);

        var             service = harness.Provider.GetRequiredService<DownloadFileService>();
        await using var session = await harness.Factory.CreateAsync();
        var candidates = await service.EnumerateReuseCandidatesAsync(session, taskId,
                                                                     await service.ComputeArchivalTargetsAsync(taskId));
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("verifying")]
    [InlineData("isolating")]
    [InlineData("qb-moving")]
    [InlineData("qb-checking")]
    [InlineData("different-size")]
    [InlineData("parent-overlap")]
    public async Task Verification_ManagedUnrelatedOwnerNotACandidate_StaysBlockedWithoutTouchingOwner(string mode)
    {
        using var harness     = await VerificationHarness.CreateAsync();
        var       taskId      = await harness.CreateUploadAsync(SingleFileTorrent());
        var       target      = await ArchivalTargetAsync(harness, taskId);
        var       relative    = target[(DownloadRoot.Length + 1)..];
        var       ownerFileId = await SeedOwnerForModeAsync(harness, target, relative, mode);
        await ApplyOwnerInstabilityAsync(harness, relative, mode);

        for (var round = 0; round < 4; round++) await harness.ReconcileAsync();

        var blocked = await harness.GetTaskAsync(taskId);
        Assert.Null(blocked.VerificationIntent);
        Assert.Empty(harness.Store.RecheckCalls);
        Assert.Equal(DownloadTaskStatus.Failed, blocked.Status);
        Assert.Equal("文件整理", blocked.FailedStep);
        await AssertOwnerUntouchedAsync(harness, ownerFileId, mode == "parent-overlap" ? "Media" : relative);
    }

    [Theory]
    [InlineData("qb-moving")]
    [InlineData("pending-save")]
    [InlineData("qb-incomplete")]
    [InlineData("qb-stopped-incomplete")]
    public async Task Verification_OwnerUnstableBetweenSubmitAndMapping_NeverMapsOrRechecks(string instability)
    {
        using var harness     = await VerificationHarness.CreateAsync();
        var       taskId      = await harness.CreateUploadAsync(SingleFileTorrent());
        var       target      = await ArchivalTargetAsync(harness, taskId);
        var       ownerFileId = await SeedManagedOccupantAsync(harness, target, 1024);

        await harness.ReconcileAsync(); // 提交：owner 尚稳定，候选成立。
        Assert.NotNull(DownloadVerificationIntent.FromJson((await harness.GetTaskAsync(taskId)).VerificationIntent));

        if (instability == "pending-save")
        {
            await using var db    = await harness.CreateDbContextAsync();
            var             owner = await db.DownloadTasks.SingleAsync(value => value.InfoHash == ManagedOwnerHash);
            owner.PendingSavePath = "/downloads/.moving";
            await db.SaveChangesAsync();
        }

        // 保持移动中，避免对账恢复链在映射检查前消费待确认日志。
        if (instability == "pending-save")
            harness.Store.Torrents[ManagedOwnerHash] = harness.Store.Torrents[ManagedOwnerHash] with { IsMoving = true };
        else
            await ApplyOwnerInstabilityAsync(harness, target[(DownloadRoot.Length + 1)..], instability);
        await harness.ReconcileAsync();
        await harness.ReconcileAsync();

        var isolated = await harness.GetTaskAsync(taskId);
        Assert.Null(isolated.VerificationIntent);
        Assert.NotEqual(DownloadIsolationState.None, isolated.IsolationState);
        Assert.Empty(harness.Store.RecheckCalls);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.DeleteFiles);
        await AssertOwnerUntouchedAsync(harness, ownerFileId, target[(DownloadRoot.Length + 1)..]);
    }

    [Fact]
    public async Task Verification_PendingJournalNewRootRenameHitsOriginalLayout_RejectsBeforeAdd()
    {
        using var harness = await VerificationHarness.CreateAsync();
        var       taskId  = await harness.CreateUploadAsync(MixedTorrent());
        var targets = await harness.Provider.GetRequiredService<DownloadFileService>()
                           .ComputeArchivalTargetsAsync(taskId);
        // 合法候选（Index 0）驱动提交走校验规划，冲突检查因此带着候选例外执行。
        await SeedManagedOccupantAsync(harness, targets.Single(target => target.FileIndex == 0).TargetPath, 1024);
        var pendingOwnerId = Guid.NewGuid();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var pendingOwner = new DownloadTask
            {
                Id                   = pendingOwnerId, InfoHash = new string('f', 40), SavePath = "/old",
                Status               = DownloadTaskStatus.Completed, TorrentFile = SingleFileTorrent("繁日双语"),
                NamingSnapshot       = "Pending Journal Owner",
                PendingSavePath      = DownloadRoot,
                NextObservationAtUtc = DateTimeOffset.Parse("2026-09-26T00:00:00Z")
            };
            pendingOwner.Files.Add(new DownloadTaskFile
            {
                Id                   = Guid.NewGuid(), DownloadTaskId = pendingOwnerId, FileIndex = 0,
                OriginalRelativePath = "b-old.mkv", CurrentRelativePath = "b-old.mkv", Size = 17,
                PendingRenameFrom    = "b-old.mkv", PendingRenameTo   = "Test Anime/readme.txt"
            });
            db.DownloadTasks.Add(pendingOwner);
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync(); // 提交：新根+新名撞上原始布局 readme.txt → Add 前拒绝。

        var failed = await harness.GetTaskAsync(taskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("提交路径", failed.FailedStep);
        Assert.Null(failed.VerificationIntent);
        Assert.Empty(harness.Store.AddCalls);
        Assert.Empty(harness.Store.RenameFileCalls);
        Assert.Empty(harness.Store.RecheckCalls);
        await using (var db = await harness.CreateDbContextAsync())
        {
            // 日志原样保留：拒绝发生在提交规划，而不是靠消费或恢复占用方日志达成。
            var pendingOwner = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                                .SingleAsync(value => value.Id == pendingOwnerId);
            Assert.Equal(DownloadTaskStatus.Completed, pendingOwner.Status);
            Assert.Equal(DownloadRoot, pendingOwner.PendingSavePath);
            var journal = pendingOwner.Files.Single(value => value.FileIndex == 0);
            Assert.Equal("Test Anime/readme.txt", journal.PendingRenameTo);
            Assert.Equal("b-old.mkv", journal.PendingRenameFrom);
        }
    }

    private static async Task<Guid> SeedOwnerForModeAsync(
        VerificationHarness harness, string target, string relative, string mode) =>
        mode switch
        {
            // DB 与 qB 呈现不一致时不得仅凭一侧授予候选。
            "db-qb-size-mismatch" => await SeedManagedOccupantAsync(harness, target, 2048, qbSize: 1024),
            "db-qb-root-mismatch" => await SeedManagedOccupantAsync(harness, target, 1024, qbRoot: "/elsewhere"),
            "db-qb-path-mismatch" => await SeedManagedOccupantAsync(harness, target, 1024, qbRelative: "moved.mkv"),
            "different-size"      => await SeedManagedOccupantAsync(harness, target, 2048),
            "parent-overlap"      => await SeedManagedOccupantAsync(harness, target, 1024, "Media"),
            _                     => await SeedManagedOccupantAsync(harness, target, 1024)
        };

    private static async Task ApplyOwnerInstabilityAsync(VerificationHarness harness, string relative, string mode)
    {
        switch (mode)
        {
            case "pending-save" or "pending-rename" or "verifying" or "isolating":
            {
                await using var db    = await harness.CreateDbContextAsync();
                var             owner = await db.DownloadTasks.Include(value => value.Files)
                                                .SingleAsync(value => value.InfoHash == ManagedOwnerHash);
                switch (mode)
                {
                    case "pending-save":
                        owner.PendingSavePath = "/downloads/.moving";
                        break;
                    case "pending-rename":
                        var file = owner.Files.Single(value => value.FileIndex == 0);
                        file.PendingRenameFrom = relative;
                        file.PendingRenameTo   = "pending.mkv";
                        break;
                    case "verifying":
                        owner.VerificationIntent = new DownloadVerificationIntent
                        {
                            Round = 1, CreatedAtUtc = DateTimeOffset.Parse("2026-09-25T04:00:00Z")
                        }.ToJson();
                        break;
                    default:
                        owner.IsolationState = DownloadIsolationState.Deleting;
                        break;
                }

                await db.SaveChangesAsync();
                break;
            }
            case "qb-incomplete" or "qb-stopped-incomplete":
                harness.Store.Torrents[ManagedOwnerHash] = harness.Store.Torrents[ManagedOwnerHash] with
                {
                    Category = mode == "qb-incomplete"
                        ? QbittorrentTorrentCategory.Downloading
                        : QbittorrentTorrentCategory.Stopped,
                    Progress = 0.5
                };
                harness.Store.FileIdentities[ManagedOwnerHash] = harness.Store.FileIdentities[ManagedOwnerHash]
                                                                     .Select(file => file with { Progress = 0.5 })
                                                                     .ToList();
                break;
            case "qb-moving":
                harness.Store.Torrents[ManagedOwnerHash] = harness.Store.Torrents[ManagedOwnerHash] with
                {
                    IsMoving = true
                };
                break;
            case "qb-checking":
                harness.Store.Torrents[ManagedOwnerHash] = harness.Store.Torrents[ManagedOwnerHash] with
                {
                    Category = QbittorrentTorrentCategory.Checking
                };
                break;
        }
    }

    private static async Task AssertOwnerUntouchedAsync(VerificationHarness harness, Guid ownerFileId, string relative)
    {
        Assert.DoesNotContain(harness.Store.RenameFileCalls, call => call.Hash == ManagedOwnerHash);
        Assert.DoesNotContain(harness.Store.SetLocationCalls, call => call.Item1 == ManagedOwnerHash);
        Assert.DoesNotContain(harness.Store.DeleteCalls, call => call.Hash == ManagedOwnerHash);
        Assert.Contains(ManagedOwnerHash, harness.Store.Torrents.Keys);
        Assert.Equal(relative, Assert.Single(harness.Store.FileIdentities[ManagedOwnerHash]).Path);
        await using var db    = await harness.CreateDbContextAsync();
        var             owner = await db.DownloadTasks.Include(value => value.Files).AsNoTracking()
                                .SingleAsync(value => value.InfoHash == ManagedOwnerHash);
        Assert.Equal(DownloadTaskStatus.Completed, owner.Status);
        Assert.Null(owner.ReplacedByTaskId);
        Assert.Null(owner.ReplacesTaskId);
        var file = owner.Files.Single(value => value.Id == ownerFileId);
        Assert.Equal(relative, file.CurrentRelativePath);
        Assert.Null(file.SupersededByTaskId);
        Assert.Null(file.VerificationOutcome);
    }
}
