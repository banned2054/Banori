using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Subscriptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     来源作用域启动回填：FeedItem 只按可解密订阅地址迁移；CandidateSelection 目标组键冲突保留；
///     EpisodeVersion 按指针属主逐槽迁移，多个来源拆成每源一行。可确定的关系必须迁移，
///     只有真正无法确定的槽（悬空、孤儿、陈旧、冲突、来源歧义）才留在 legacy。
/// </summary>
public sealed class SourceScopeBackfillTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BackfillFeedItems_UsesDecryptedFeedUrlAndKeepsUndecryptableLegacy()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = await harness.AddSubjectAsync();

        var mikanSubscription = harness.AddSubscription("Mikan 订阅", "https://mikanime.tv/RSS/MyBangumi");
        var unknownSubscription = harness.AddSubscription("未知订阅", "https://example.com/rss.xml");
        // 用其他用途的 protector 加密：解密必然失败，来源不能猜。
        var brokenSubscription = harness.AddSubscription("损坏订阅",
                                                         harness.ProtectWithOtherPurpose("https://mikanani.me/RSS/x"),
                                                         alreadyProtected: true);
        var existingSubscription = harness.AddSubscription("已回填订阅", "https://anibt.net/rss");

        var mikanItem    = harness.AddFeedItem(mikanSubscription.Id, subject.Id, "mikan-item");
        var unknownItem  = harness.AddFeedItem(unknownSubscription.Id, subject.Id, "unknown-item");
        var brokenItem   = harness.AddFeedItem(brokenSubscription.Id, subject.Id, "broken-item");
        var existingItem = harness.AddFeedItem(existingSubscription.Id, subject.Id, "existing-item", "anibt");
        await harness.SaveAsync();

        await harness.RunBackfillAsync();
        await harness.RunBackfillAsync();

        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal("mikan", (await db.FeedItems.AsNoTracking().SingleAsync(item => item.Id == mikanItem.Id)).SourceScope);
        Assert.Equal(SourceScopes.Unknown,
                     (await db.FeedItems.AsNoTracking().SingleAsync(item => item.Id == unknownItem.Id)).SourceScope);
        Assert.Equal(SourceScopes.Legacy,
                     (await db.FeedItems.AsNoTracking().SingleAsync(item => item.Id == brokenItem.Id)).SourceScope);
        Assert.Equal("anibt",
                     (await db.FeedItems.AsNoTracking().SingleAsync(item => item.Id == existingItem.Id)).SourceScope);
    }

    [Fact]
    public async Task BackfillCandidateSelections_UsesWinnerSourceAndKeepsConflictsLegacy()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = await harness.AddSubjectAsync();
        var mikanSubscription  = harness.AddSubscription("Mikan 订阅", "https://mikanani.me/RSS/x");
        var brokenSubscription = harness.AddSubscription("损坏订阅",
                                                         harness.ProtectWithOtherPurpose("https://mikanani.me/RSS/x"),
                                                         alreadyProtected: true);

        var mikanWinner  = harness.AddFeedItem(mikanSubscription.Id, subject.Id, "winner", "mikan");
        var legacyWinner = harness.AddFeedItem(brokenSubscription.Id, subject.Id, "legacy-winner");
        await harness.SaveAsync();

        // 无冲突：优胜条目已归属 mikan，旧预选迁出 legacy。
        var movable = harness.AddCandidateSelection(subject.Id, 1, mikanWinner.Id);
        // 冲突：目标组键已有 mikan 行，旧 legacy 行必须保留且不能被删除。
        var conflicting = harness.AddCandidateSelection(subject.Id, 2, mikanWinner.Id);
        harness.AddCandidateSelection(subject.Id, 2, mikanWinner.Id, "mikan");
        // 优胜条目的订阅无法解密，仍为 legacy：不回填，也不在这里重复解密订阅。
        var winnerLegacy = harness.AddCandidateSelection(subject.Id, 3, legacyWinner.Id);
        await harness.SaveAsync();

        await harness.RunBackfillAsync();
        await harness.RunBackfillAsync();

        await using var db = await harness.CreateDbContextAsync();
        Assert.Equal("mikan", (await db.CandidateSelections.AsNoTracking().SingleAsync(row => row.Id == movable.Id)).SourceScope);
        Assert.Equal(SourceScopes.Legacy,
                     (await db.CandidateSelections.AsNoTracking().SingleAsync(row => row.Id == conflicting.Id)).SourceScope);
        Assert.Equal(SourceScopes.Legacy,
                     (await db.CandidateSelections.AsNoTracking().SingleAsync(row => row.Id == winnerLegacy.Id)).SourceScope);
        // 冲突行未被删除：同组仍同时存在 legacy 与 mikan 两行。
        Assert.Equal(2, await db.CandidateSelections.CountAsync(row => row.SubjectId == subject.Id && row.Episode == 2));
    }

    [Fact]
    public async Task BackfillEpisodeVersions_MovesSingleSourceAndClearsMigratedSlots()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var file1 = harness.AddFile(mikanTask.Id, 0);
        var file2 = harness.AddFile(mikanTask.Id, 1);
        var file3 = harness.AddFile(mikanTask.Id, 2);
        await harness.SaveAsync();

        var legacy = harness.AddEpisodeVersion(subject.Id, 1, incumbent: file1.Id, pending: file2.Id,
                                               superseded: file3.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.LegacyRows);
        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(0, report.SplitRows);
        Assert.Equal(0, report.TargetConflicts);
        Assert.Equal(1, report.RemainingLegacyRows);

        var target = await harness.FindEpisodeVersionAsync(subject.Id, 1, "mikan");
        Assert.NotNull(target);
        Assert.Equal(file1.Id, target.IncumbentVideoFileId);
        Assert.Equal(file2.Id, target.PendingVideoFileId);
        Assert.Equal(file3.Id, target.SupersededVideoFileId);

        // 可确定来源的槽全部迁移：旧行保留 Id 与行本身，只清空已迁移槽。
        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Equal(SourceScopes.Legacy, retained.SourceScope);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);
        Assert.Null(retained.SupersededVideoFileId);
        Assert.Equal(2, await harness.CountEpisodeVersionsAsync());
    }

    [Fact]
    public async Task BackfillEpisodeVersions_SplitsMultiSourceRowIntoPerSourceRows()
    {
        await using var harness = await Harness.CreateAsync();
        var subject    = await harness.AddSubjectAsync();
        var mikanTask  = harness.AddTask("mikan", 'a');
        var anibtTask  = harness.AddTask("anibt", 'b');
        await harness.SaveAsync();

        var mikanFile1 = harness.AddFile(mikanTask.Id, 0);
        var anibtFile  = harness.AddFile(anibtTask.Id, 0);
        var mikanFile2 = harness.AddFile(mikanTask.Id, 1);
        await harness.SaveAsync();

        // 迁移前单行可能混合来源：可确定的 Incumbent/Superseded 归 mikan，Pending 归 anibt。
        // 业务上可确定的关系必须迁移；只有无法确定的关系才留在 legacy，因此这里拆行而不是整行保留。
        var legacy = harness.AddEpisodeVersion(subject.Id, 2, incumbent: mikanFile1.Id, pending: anibtFile.Id,
                                               superseded: mikanFile2.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(2, report.CreatedRows);
        Assert.Equal(2, report.SplitRows);
        Assert.Equal(0, report.TargetConflicts);

        var mikanRow = await harness.FindEpisodeVersionAsync(subject.Id, 2, "mikan");
        Assert.NotNull(mikanRow);
        Assert.Equal(mikanFile1.Id, mikanRow.IncumbentVideoFileId);
        Assert.Null(mikanRow.PendingVideoFileId);
        Assert.Equal(mikanFile2.Id, mikanRow.SupersededVideoFileId);

        var anibtRow = await harness.FindEpisodeVersionAsync(subject.Id, 2, "anibt");
        Assert.NotNull(anibtRow);
        Assert.Null(anibtRow.IncumbentVideoFileId);
        Assert.Equal(anibtFile.Id, anibtRow.PendingVideoFileId);
        Assert.Null(anibtRow.SupersededVideoFileId);

        // 跨来源不建立 Superseded 关系：mikan 的现任承接只留在 mikan 行，anibt 行没有回退链。
        Assert.Null(anibtRow.SupersededVideoFileId);

        // 旧行保留，已迁移槽清空；第二次运行不新建行。
        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Equal(SourceScopes.Legacy, retained.SourceScope);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);
        Assert.Null(retained.SupersededVideoFileId);
        Assert.Equal(3, await harness.CountEpisodeVersionsAsync());

        var second = await harness.RunBackfillAsync();
        Assert.Equal(0, second.CreatedRows);
        Assert.Equal(0, second.SplitRows);
        Assert.Equal(3, await harness.CountEpisodeVersionsAsync());
    }

    [Fact]
    public async Task BackfillEpisodeVersions_KeepsDanglingSupersededInLegacy()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var incumbent = harness.AddFile(mikanTask.Id, 0);
        var pending   = harness.AddFile(mikanTask.Id, 1);
        await harness.SaveAsync();

        var danglingId = Guid.NewGuid();
        var legacy = harness.AddEpisodeVersion(subject.Id, 3, incumbent: incumbent.Id, pending: pending.Id,
                                               superseded: danglingId);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        // 悬空 Superseded 不阻断同源有效的 Incumbent/Pending：它们必须迁移。
        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.DanglingPointers);
        Assert.Equal(0, report.OrphanSuperseded);

        var target = await harness.FindEpisodeVersionAsync(subject.Id, 3, "mikan");
        Assert.NotNull(target);
        Assert.Equal(incumbent.Id, target.IncumbentVideoFileId);
        Assert.Equal(pending.Id, target.PendingVideoFileId);
        Assert.Null(target.SupersededVideoFileId);

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);
        Assert.Equal(danglingId, retained.SupersededVideoFileId);

        var second = await harness.RunBackfillAsync();
        Assert.Equal(0, second.CreatedRows);
        Assert.Equal(1, second.DanglingPointers);
        Assert.Equal(2, await harness.CountEpisodeVersionsAsync());
    }

    [Fact]
    public async Task BackfillEpisodeVersions_DoesNotPromoteSupersededWhenIncumbentDangling()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var superseded = harness.AddFile(mikanTask.Id, 0);
        var pending    = harness.AddFile(mikanTask.Id, 1);
        await harness.SaveAsync();

        var danglingIncumbent = Guid.NewGuid();
        var legacy = harness.AddEpisodeVersion(subject.Id, 4, incumbent: danglingIncumbent,
                                               pending: pending.Id, superseded: superseded.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.DanglingPointers);
        Assert.Equal(1, report.DanglingIncumbentWithValidSuperseded);
        // 同源没有现任，Superseded 是孤儿：不建立回退关系，也不自动提升为现任。
        Assert.Equal(1, report.OrphanSuperseded);

        var target = await harness.FindEpisodeVersionAsync(subject.Id, 4, "mikan");
        Assert.NotNull(target);
        Assert.Null(target.IncumbentVideoFileId);
        Assert.Equal(pending.Id, target.PendingVideoFileId);
        Assert.Null(target.SupersededVideoFileId);

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Equal(danglingIncumbent, retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);
        Assert.Equal(superseded.Id, retained.SupersededVideoFileId);
    }

    [Fact]
    public async Task BackfillEpisodeVersions_DoesNotEstablishOrphanSuperseded()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        var anibtTask = harness.AddTask("anibt", 'b');
        await harness.SaveAsync();

        var incumbent = harness.AddFile(mikanTask.Id, 0);
        var superseded = harness.AddFile(anibtTask.Id, 0);
        await harness.SaveAsync();

        // Superseded 属于另一个来源：跨来源不建立回退关系，只迁 mikan 的现任。
        var legacy = harness.AddEpisodeVersion(subject.Id, 5, incumbent: incumbent.Id, superseded: superseded.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.OrphanSuperseded);
        Assert.Equal(0, report.SplitRows);

        var mikanRow = await harness.FindEpisodeVersionAsync(subject.Id, 5, "mikan");
        Assert.NotNull(mikanRow);
        Assert.Equal(incumbent.Id, mikanRow.IncumbentVideoFileId);
        Assert.Null(mikanRow.SupersededVideoFileId);
        Assert.Null(await harness.FindEpisodeVersionAsync(subject.Id, 5, "anibt"));

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Equal(superseded.Id, retained.SupersededVideoFileId);
    }

    [Fact]
    public async Task BackfillEpisodeVersions_DoesNotOverwriteExistingTargetRow()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var legacyIncumbent = harness.AddFile(mikanTask.Id, 0);
        var legacyPending   = harness.AddFile(mikanTask.Id, 1);
        var existingFile    = harness.AddFile(mikanTask.Id, 2);
        await harness.SaveAsync();

        var legacy = harness.AddEpisodeVersion(subject.Id, 6, incumbent: legacyIncumbent.Id,
                                               pending: legacyPending.Id);
        var existing = harness.AddEpisodeVersion(subject.Id, 6, incumbent: existingFile.Id, sourceScope: "mikan");
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(0, report.CreatedRows);
        Assert.Equal(1, report.TargetConflicts);
        Assert.Equal(0, report.MergedRows);

        var existingRow = await harness.GetEpisodeVersionAsync(existing.Id);
        Assert.Equal("mikan", existingRow.SourceScope);
        Assert.Equal(existingFile.Id, existingRow.IncumbentVideoFileId);
        Assert.Null(existingRow.PendingVideoFileId);
        Assert.Null(existingRow.SupersededVideoFileId);

        // 冲突组不覆盖、不清空：可确定但目标已有不同登记，保留原样供人工核对。
        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Equal(legacyIncumbent.Id, retained.IncumbentVideoFileId);
        Assert.Equal(legacyPending.Id, retained.PendingVideoFileId);
        Assert.Equal(2, await harness.CountEpisodeVersionsAsync());
    }

    [Fact]
    public async Task BackfillEpisodeVersions_MergesMissingIncumbentIntoMatchingPending()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var legacyIncumbent = harness.AddFile(mikanTask.Id, 0);
        var pending         = harness.AddFile(mikanTask.Id, 1);
        await harness.SaveAsync();

        // 目标行由迁移后的登记继续演化：Pending 还在，但旧现任尚未写入。
        var target = harness.AddEpisodeVersion(subject.Id, 7, pending: pending.Id, sourceScope: "mikan");
        var legacy = harness.AddEpisodeVersion(subject.Id, 7, incumbent: legacyIncumbent.Id, pending: pending.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.MergedRows);
        Assert.Equal(0, report.CreatedRows);
        Assert.Equal(0, report.TargetConflicts);

        var merged = await harness.GetEpisodeVersionAsync(target.Id);
        Assert.Equal(legacyIncumbent.Id, merged.IncumbentVideoFileId);
        Assert.Equal(pending.Id, merged.PendingVideoFileId);
        Assert.Null(merged.SupersededVideoFileId);

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);
        Assert.Equal(2, await harness.CountEpisodeVersionsAsync());
    }

    [Fact]
    public async Task BackfillEpisodeVersions_MergesLegacyIncumbentIntoCompletedTarget()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var previousIncumbent = harness.AddFile(mikanTask.Id, 0);
        var currentIncumbent  = harness.AddFile(mikanTask.Id, 1);
        await harness.SaveAsync();

        // 目标行的 Pending 已完成并成为现任；legacy 的旧现任正是应保留的单级回退。
        var target = harness.AddEpisodeVersion(subject.Id, 8, incumbent: currentIncumbent.Id, sourceScope: "mikan");
        var legacy = harness.AddEpisodeVersion(subject.Id, 8, incumbent: previousIncumbent.Id,
                                               pending: currentIncumbent.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.MergedRows);
        Assert.Equal(0, report.CreatedRows);

        var merged = await harness.GetEpisodeVersionAsync(target.Id);
        Assert.Equal(currentIncumbent.Id, merged.IncumbentVideoFileId);
        Assert.Null(merged.PendingVideoFileId);
        Assert.Equal(previousIncumbent.Id, merged.SupersededVideoFileId);

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);
    }

    [Fact]
    public async Task BackfillEpisodeVersions_IdenticalTargetRowClearsLegacyAsIdempotent()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var file1 = harness.AddFile(mikanTask.Id, 0);
        var file2 = harness.AddFile(mikanTask.Id, 1);
        await harness.SaveAsync();

        var target = harness.AddEpisodeVersion(subject.Id, 9, incumbent: file1.Id, pending: file2.Id,
                                               sourceScope: "mikan");
        var legacy = harness.AddEpisodeVersion(subject.Id, 9, incumbent: file1.Id, pending: file2.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(0, report.CreatedRows);
        Assert.Equal(1, report.IdempotentRows);
        Assert.Equal(0, report.TargetConflicts);

        var unchanged = await harness.GetEpisodeVersionAsync(target.Id);
        Assert.Equal(file1.Id, unchanged.IncumbentVideoFileId);
        Assert.Equal(file2.Id, unchanged.PendingVideoFileId);
        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Null(retained.PendingVideoFileId);

        var second = await harness.RunBackfillAsync();
        Assert.Equal(0, second.CreatedRows);
        Assert.Equal(0, second.IdempotentRows);
        Assert.Equal(2, await harness.CountEpisodeVersionsAsync());
    }

    [Fact]
    public async Task BackfillEpisodeVersions_EmptySiteSourceIsMigratedToUnknownWithReport()
    {
        await using var harness = await Harness.CreateAsync();
        var subject     = await harness.AddSubjectAsync();
        var unknownTask = harness.AddTask(null, 'c');
        await harness.SaveAsync();

        var file = harness.AddFile(unknownTask.Id, 0);
        await harness.SaveAsync();

        var legacy = harness.AddEpisodeVersion(subject.Id, 10, incumbent: file.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.AmbiguousSiteSource);
        Assert.Equal(0, report.NonCanonicalSiteSource);

        // 与运行期一致归入 (unknown)，不在回填里猜 other；歧义通过报告暴露。
        var target = await harness.FindEpisodeVersionAsync(subject.Id, 10, SourceScopes.Unknown);
        Assert.NotNull(target);
        Assert.Equal(file.Id, target.IncumbentVideoFileId);
        Assert.Null(await harness.FindEpisodeVersionAsync(subject.Id, 10, SourceScopes.Other));

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
    }

    [Fact]
    public async Task BackfillEpisodeVersions_KeepsStalePendingInLegacy()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var incumbent = harness.AddFile(mikanTask.Id, 0);
        // 已被更优任务明确顶替的旧 Pending：文件取消选择且指向承接方。
        var stalePending = harness.AddFile(mikanTask.Id, 1, isSelectedForDownload: false,
                                           supersededByTaskId: mikanTask.Id);
        await harness.SaveAsync();

        var legacy = harness.AddEpisodeVersion(subject.Id, 11, incumbent: incumbent.Id,
                                               pending: stalePending.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.StalePending);

        var target = await harness.FindEpisodeVersionAsync(subject.Id, 11, "mikan");
        Assert.NotNull(target);
        Assert.Equal(incumbent.Id, target.IncumbentVideoFileId);
        Assert.Null(target.PendingVideoFileId);

        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Null(retained.IncumbentVideoFileId);
        Assert.Equal(stalePending.Id, retained.PendingVideoFileId);
    }

    [Fact]
    public async Task BackfillEpisodeVersions_ReportsNonCanonicalSiteSourceWithoutMerging()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = await harness.AddSubjectAsync();
        var oldTask = harness.AddTask("self", 'd');
        await harness.SaveAsync();

        var file = harness.AddFile(oldTask.Id, 0);
        await harness.SaveAsync();

        var legacy = harness.AddEpisodeVersion(subject.Id, 12, incumbent: file.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.CreatedRows);
        Assert.Equal(1, report.NonCanonicalSiteSource);
        Assert.Equal(0, report.AmbiguousSiteSource);

        // 旧域名首段猜测值只按原标签迁移，不静默归并到 (unknown)/other。
        var target = await harness.FindEpisodeVersionAsync(subject.Id, 12, "self");
        Assert.NotNull(target);
        Assert.Equal(file.Id, target.IncumbentVideoFileId);
        Assert.Null(await harness.FindEpisodeVersionAsync(subject.Id, 12, SourceScopes.Unknown));
        Assert.Null(await harness.FindEpisodeVersionAsync(subject.Id, 12, SourceScopes.Other));
    }

    [Fact]
    public async Task BackfillEpisodeVersions_MediaTypeMismatchIsConflict()
    {
        await using var harness = await Harness.CreateAsync();
        var subject   = await harness.AddSubjectAsync();
        var mikanTask = harness.AddTask("mikan", 'a');
        await harness.SaveAsync();

        var file = harness.AddFile(mikanTask.Id, 0);
        await harness.SaveAsync();

        // 槽位完全一致，但 MediaType 不同：唯一键不含 MediaType，必须自己校验并报告冲突。
        var target = harness.AddEpisodeVersion(subject.Id, 13, incumbent: file.Id, sourceScope: "mikan",
                                               mediaType: "Movie");
        var legacy = harness.AddEpisodeVersion(subject.Id, 13, incumbent: file.Id);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.TargetConflicts);
        Assert.Equal(0, report.IdempotentRows);
        Assert.Equal(0, report.MergedRows);

        var unchanged = await harness.GetEpisodeVersionAsync(target.Id);
        Assert.Equal("Movie", unchanged.MediaType);
        Assert.Equal(file.Id, unchanged.IncumbentVideoFileId);
        var retained = await harness.GetEpisodeVersionAsync(legacy.Id);
        Assert.Equal(file.Id, retained.IncumbentVideoFileId);
    }

    [Fact]
    public async Task BackfillEpisodeVersions_NoPointerRowsStayLegacy()
    {
        await using var harness = await Harness.CreateAsync();
        var subject = await harness.AddSubjectAsync();
        var legacy  = harness.AddEpisodeVersion(subject.Id, 14);
        await harness.SaveAsync();

        var report = await harness.RunBackfillAsync();

        Assert.Equal(1, report.LegacyRows);
        Assert.Equal(1, report.NoPointerRows);
        Assert.Equal(0, report.CreatedRows);
        Assert.Equal(SourceScopes.Legacy, (await harness.GetEpisodeVersionAsync(legacy.Id)).SourceScope);
        Assert.Equal(1, await harness.CountEpisodeVersionsAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly DirectoryInfo        _directory;
        private readonly ServiceProvider      _provider;
        private readonly ApplicationDbContext _db;

        private Harness(DirectoryInfo directory, ServiceProvider provider, IDataProtectionProvider protection,
                        ApplicationDbContext db)
        {
            _directory = directory;
            _provider  = provider;
            _db        = db;
            Protector  = FeedUrlProtection.CreateProtector(protection);
        }

        private IDataProtector Protector { get; }

        public static async Task<Harness> CreateAsync()
        {
            var directory    = Directory.CreateTempSubdirectory("Banori.SourceScopeBackfill.Tests.");
            var keyDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "keys"));
            var services     = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection().PersistKeysToFileSystem(keyDirectory);
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                                   options.UseSqlite(
                                                                       $"Data Source={Path.Combine(directory.FullName, "backfill.db")};Pooling=False"));
            services.AddScoped<SourceScopeBackfill>();
            services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                          IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();
            var db       = await provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                         .CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new Harness(directory, provider, provider.GetRequiredService<IDataProtectionProvider>(), db);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync() =>
            _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();

        public async Task<MediaSubject> AddSubjectAsync()
        {
            var subject = new MediaSubject
            {
                Id = 42, Name = "回填作品", NameCn = "回填作品", Platform = "TV", CachedAtUtc = Timestamp
            };
            _db.MediaSubjects.Add(subject);
            await _db.SaveChangesAsync();
            return subject;
        }
        public MediaSubscription AddSubscription(string name, string feedUrl, bool alreadyProtected = false)
        {
            var subscription = new MediaSubscription
            {
                Id                     = Guid.NewGuid(),
                Name                   = name,
                FeedUrlProtected       = alreadyProtected ? feedUrl : Protector.Protect(feedUrl),
                IsEnabled              = true,
                RefreshIntervalMinutes = 30,
                CreatedAtUtc           = Timestamp,
                UpdatedAtUtc           = Timestamp
            };
            _db.MediaSubscriptions.Add(subscription);
            return subscription;
        }

        public string ProtectWithOtherPurpose(string feedUrl) =>
            _provider.GetRequiredService<IDataProtectionProvider>()
                     .CreateProtector("Banori.Tests.OtherPurpose")
                     .Protect(feedUrl);

        public FeedItem AddFeedItem(Guid subscriptionId, int subjectId, string stableId, string? sourceScope = null)
        {
            var item = new FeedItem
            {
                Id             = Guid.NewGuid(),
                SubscriptionId = subscriptionId,
                StableId       = stableId,
                Title          = stableId,
                FirstSeenAtUtc = Timestamp,
                Status         = FeedItemStatus.Accepted,
                SubjectId      = subjectId,
                Episode        = 1,
                Version        = 1,
                SourceScope    = sourceScope ?? SourceScopes.Legacy
            };
            _db.FeedItems.Add(item);
            return item;
        }

        public CandidateSelection AddCandidateSelection(int subjectId, decimal episode, Guid winnerFeedItemId,
                                                        string? sourceScope = null)
        {
            var selection = new CandidateSelection
            {
                Id               = Guid.NewGuid(),
                SubjectId        = subjectId,
                MediaType        = "SingleEpisode",
                Episode          = episode,
                SourceScope      = sourceScope ?? SourceScopes.Legacy,
                WinnerFeedItemId = winnerFeedItemId,
                RuleVersion      = 1,
                RankingBasis     = "回填测试",
                NamingSnapshot   = "命名快照",
                SelectedAtUtc    = Timestamp
            };
            _db.CandidateSelections.Add(selection);
            return selection;
        }

        public DownloadTask AddTask(string? siteSource, char hashPrefix)
        {
            var task = new DownloadTask
            {
                Id             = Guid.NewGuid(),
                InfoHash       = new string(hashPrefix, 40),
                TorrentFile    = [0x64],
                SavePath       = $"/downloads/{hashPrefix}",
                NamingSnapshot = "回填测试",
                Status         = DownloadTaskStatus.Completed,
                CreatedAtUtc   = Timestamp,
                SiteSource     = siteSource
            };
            _db.DownloadTasks.Add(task);
            return task;
        }

        public DownloadTaskFile AddFile(Guid taskId, int fileIndex, bool isSelectedForDownload = true,
                                        Guid? supersededByTaskId = null)
        {
            var file = new DownloadTaskFile
            {
                Id                    = Guid.NewGuid(),
                DownloadTaskId        = taskId,
                FileIndex             = fileIndex,
                OriginalRelativePath  = $"Video/episode{fileIndex:00}.mkv",
                CurrentRelativePath   = $"Video/episode{fileIndex:00}.mkv",
                Size                  = 1024,
                ClassificationReason  = "正片",
                IsSelectedForDownload = isSelectedForDownload,
                SupersededByTaskId    = supersededByTaskId,
                UpdatedAtUtc          = Timestamp
            };
            _db.DownloadTaskFiles.Add(file);
            return file;
        }

        public EpisodeVersion AddEpisodeVersion(int subjectId, decimal episode, Guid? incumbent = null, Guid? pending = null,
                                                Guid? superseded = null, string? sourceScope = null,
                                                string mediaType = "SingleEpisode")
        {
            var version = new EpisodeVersion
            {
                SubjectId             = subjectId,
                Episode               = episode,
                MediaType             = mediaType,
                SourceScope           = sourceScope ?? SourceScopes.Legacy,
                IncumbentVideoFileId  = incumbent,
                PendingVideoFileId    = pending,
                SupersededVideoFileId = superseded,
                UpdatedAtUtc          = Timestamp
            };
            _db.EpisodeVersions.Add(version);
            return version;
        }

        public Task SaveAsync() => _db.SaveChangesAsync();

        public async Task<SourceScopeBackfillReport> RunBackfillAsync()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<SourceScopeBackfill>().RunWithReportAsync();
        }

        public async Task<EpisodeVersion> GetEpisodeVersionAsync(int id)
        {
            await using var db = await CreateDbContextAsync();
            return await db.EpisodeVersions.AsNoTracking().SingleAsync(row => row.Id == id);
        }

        public async Task<EpisodeVersion?> FindEpisodeVersionAsync(int subjectId, decimal episode, string sourceScope)
        {
            await using var db = await CreateDbContextAsync();
            return await db.EpisodeVersions.AsNoTracking()
                           .SingleOrDefaultAsync(row => row.SubjectId == subjectId &&
                                                        row.Episode == episode &&
                                                        row.SourceScope == sourceScope);
        }

        public async Task<int> CountEpisodeVersionsAsync()
        {
            await using var db = await CreateDbContextAsync();
            return await db.EpisodeVersions.AsNoTracking().CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            _provider.Dispose();
            try
            {
                _directory.Delete(true);
            }
            catch (IOException)
            {
            }
        }
    }
}
