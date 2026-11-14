using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Subscriptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>
///     启动时把来源隔离前的旧行从 legacy 隔离域迁入真实来源。只回填能够证明来源的数据：
///     订阅 RSS 地址可解密才回填 FeedItem；WinnerFeedItem 已归属来源才回填预选；
///     EpisodeVersion 按三个文件指针各自所属任务的来源逐槽迁移，多个来源拆成每源一行。
///     悬空、孤儿、陈旧 Pending、目标冲突与来源歧义保留在旧行并写入结构化报告；
///     迁移成功的槽从旧行清空，旧行本身（包括 Id）始终保留，因此重复执行幂等。
/// </summary>
public sealed class SourceScopeBackfill(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IDataProtectionProvider                 dataProtectionProvider,
    ILogger<SourceScopeBackfill>            logger)
{
    /// <summary>已知的规范 SiteSource 标签；其余非空值来自迁移前的历史猜测，只报告不归并。</summary>
    private static readonly HashSet<string> KnownSiteSources = new(StringComparer.Ordinal)
    {
        "mikan", "anibt", "u2", "byrbt", "tjupt", TorrentSiteSource.FallbackLabel, SourceScopes.Unknown
    };

    private readonly IDataProtector _urlProtector = FeedUrlProtection.CreateProtector(dataProtectionProvider);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var report = await RunWithReportAsync(cancellationToken);
        LogEpisodeVersionReport(report);
    }

    /// <summary>执行全部回填并返回 EpisodeVersion 的结构化结果；<see cref="RunAsync" /> 负责记录同一份报告。</summary>
    public async Task<SourceScopeBackfillReport> RunWithReportAsync(CancellationToken cancellationToken = default)
    {
        await BackfillFeedItemsAsync(cancellationToken);
        await BackfillCandidateSelectionsAsync(cancellationToken);
        var report = new SourceScopeBackfillReport();
        await BackfillEpisodeVersionsAsync(report, cancellationToken);
        return report;
    }

    /// <summary>按订阅解密出的 RSS 地址回填 FeedItem；解密失败不猜测，保留 legacy。</summary>
    private async Task BackfillFeedItemsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var legacyCounts = await db.FeedItems.AsNoTracking()
                                     .Where(item => item.SourceScope == SourceScopes.Legacy)
                                     .GroupBy(item => item.SubscriptionId)
                                     .Select(group => new { SubscriptionId = group.Key, Count = group.Count() })
                                     .ToListAsync(cancellationToken);
        if (legacyCounts.Count == 0) return;

        var subscriptionIds = legacyCounts.Select(item => item.SubscriptionId).ToList();
        var subscriptions = await db.MediaSubscriptions.AsNoTracking()
                                      .Where(item => subscriptionIds.Contains(item.Id))
                                      .Select(item => new { item.Id, item.FeedUrlProtected })
                                      .ToListAsync(cancellationToken);
        var protectedUrls = subscriptions.ToDictionary(item => item.Id, item => item.FeedUrlProtected);

        var updated       = 0;
        var undecryptable = 0;
        foreach (var group in legacyCounts)
        {
            if (!protectedUrls.TryGetValue(group.SubscriptionId, out var protectedUrl))
            {
                undecryptable += group.Count;
                continue;
            }

            var feedUrl = FeedUrlProtection.TryUnprotectUrl(_urlProtector, protectedUrl);
            if (feedUrl is null)
            {
                undecryptable += group.Count;
                continue;
            }

            var sourceScope = SourceScopes.FromFeedUrl(feedUrl);
            updated += await db.FeedItems
                               .Where(item => item.SubscriptionId == group.SubscriptionId &&
                                              item.SourceScope     == SourceScopes.Legacy)
                               .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SourceScope, sourceScope),
                                                   cancellationToken);
        }

        if (updated > 0)
            logger.LogInformation("来源作用域回填：{Updated} 条 FeedItem 已按订阅地址归入来源。", updated);
        if (undecryptable > 0)
            logger.LogWarning("来源作用域回填：{Count} 条 FeedItem 的订阅地址缺失或无法解密，保留 {Legacy} 隔离域。",
                              undecryptable, SourceScopes.Legacy);
    }

    /// <summary>按 WinnerFeedItem 已回填的来源更新预选；目标组键已被占用时保留 legacy 且不删除旧行。</summary>
    private async Task BackfillCandidateSelectionsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var selections = await db.CandidateSelections.AsNoTracking()
                                   .Where(selection => selection.SourceScope == SourceScopes.Legacy)
                                   .Select(selection => new
                                   {
                                       selection.Id,
                                       selection.SubjectId,
                                       selection.MediaType,
                                       selection.Episode,
                                       WinnerSourceScope = selection.WinnerFeedItem.SourceScope
                                   })
                                   .ToListAsync(cancellationToken);
        if (selections.Count == 0) return;

        var existingKeys = await db.CandidateSelections.AsNoTracking()
                                     .Select(selection => new
                                     {
                                         selection.Id,
                                         selection.SubjectId,
                                         selection.MediaType,
                                         selection.Episode,
                                         selection.SourceScope
                                     })
                                     .ToListAsync(cancellationToken);

        var updated      = 0;
        var winnerLegacy = 0;
        var conflicts    = 0;
        foreach (var selection in selections)
        {
            var targetScope = SourceScopes.Normalize(selection.WinnerSourceScope);
            if (targetScope == SourceScopes.Legacy)
            {
                // FeedItem 回填已处理其优胜条目；这里不再重复解密订阅，避免两处来源判断不一致。
                winnerLegacy++;
                continue;
            }

            var conflicted = existingKeys.Any(key => key.Id          != selection.Id             &&
                                                     key.SubjectId   == selection.SubjectId       &&
                                                     key.Episode     == selection.Episode         &&
                                                     key.SourceScope == targetScope               &&
                                                     string.Equals(key.MediaType, selection.MediaType,
                                                                   StringComparison.Ordinal));
            if (conflicted)
            {
                conflicts++;
                logger.LogWarning(
                    "来源作用域回填：预选 {SelectionId} 的目标来源 {SourceScope} 已有同组选择，保留 {Legacy} 且不删除。",
                    selection.Id, targetScope, SourceScopes.Legacy);
                continue;
            }

            updated += await db.CandidateSelections
                               .Where(row => row.Id == selection.Id && row.SourceScope == SourceScopes.Legacy)
                               .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SourceScope, targetScope),
                                                   cancellationToken);
        }

        if (updated > 0)
            logger.LogInformation("来源作用域回填：{Updated} 条 CandidateSelection 已按优胜条目归入来源。", updated);
        if (winnerLegacy > 0)
            logger.LogInformation("来源作用域回填：{Count} 条 CandidateSelection 的优胜条目仍为 {Legacy}，保留隔离域。",
                                  winnerLegacy, SourceScopes.Legacy);
        if (conflicts > 0)
            logger.LogWarning(
                "来源作用域回填：{Count} 条 CandidateSelection 因目标组键已占用而保留 {Legacy}，需人工核对。", conflicts,
                SourceScopes.Legacy);
    }

    /// <summary>
    ///     按三个文件指针各自所属任务的 SiteSource 逐槽回填逐集登记：可确定来源的槽迁入或拆成
    ///     对应来源行，悬空/孤儿/陈旧 Pending/冲突槽原样留在 legacy；只有迁移成功的槽才清空。
    /// </summary>
    private async Task BackfillEpisodeVersionsAsync(SourceScopeBackfillReport report,
                                                    CancellationToken          cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var versions = await db.EpisodeVersions.AsNoTracking()
                                 .Where(version => version.SourceScope == SourceScopes.Legacy)
                                 .Select(version => new LegacyEpisodeVersion(
                                             version.Id,
                                             version.SubjectId,
                                             version.Episode,
                                             version.MediaType,
                                             version.IncumbentVideoFileId,
                                             version.PendingVideoFileId,
                                             version.SupersededVideoFileId))
                                 .ToListAsync(cancellationToken);
        report.LegacyRows = versions.Count;
        if (versions.Count == 0)
        {
            report.RemainingLegacyRows = 0;
            return;
        }

        var fileIds = versions
                      .SelectMany(version => new[]
                       {
                           version.IncumbentVideoFileId, version.PendingVideoFileId, version.SupersededVideoFileId
                       })
                      .Where(id => id is not null)
                      .Select(id => id!.Value)
                      .Distinct()
                      .ToList();
        var files = new Dictionary<Guid, EpisodeVersionFile>();
        if (fileIds.Count > 0)
        {
            var fileRows = await db.DownloadTaskFiles.AsNoTracking()
                                    .Where(file => fileIds.Contains(file.Id))
                                    .Select(file => new EpisodeVersionFile(file.Id, file.DownloadTaskId,
                                                                           file.IsSelectedForDownload,
                                                                           file.SupersededByTaskId))
                                    .ToListAsync(cancellationToken);
            files = fileRows.ToDictionary(file => file.Id);
        }

        var taskIds = files.Values.Select(file => file.TaskId).Distinct().ToList();
        var taskSiteSources = new Dictionary<Guid, string?>();
        if (taskIds.Count > 0)
            taskSiteSources = (await db.DownloadTasks.AsNoTracking()
                                        .Where(task => taskIds.Contains(task.Id))
                                        .Select(task => new { task.Id, task.SiteSource })
                                        .ToListAsync(cancellationToken))
                              .ToDictionary(task => task.Id, task => task.SiteSource);

        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (version is { IncumbentVideoFileId: null, PendingVideoFileId: null, SupersededVideoFileId: null })
            {
                report.NoPointerRows++;
                continue;
            }

            var groups  = ResolveEpisodeVersionGroups(version, files, taskSiteSources, report);
            var isSplit = groups.Count > 1;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await MigrateEpisodeVersionGroupAsync(version.Id, group, isSplit, report, cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // 唯一索引兜底：目标行已被并发实例建立时按冲突处理，绝不覆盖。
                    report.TargetConflicts++;
                    report.AddIssue(CreateGroupIssue(version.Id, version.SubjectId, version.Episode, group,
                                                     SourceScopeBackfillIssueReason.TargetConflict));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    report.Failures++;
                    logger.LogError(exception,
                                    "来源作用域回填：EpisodeVersion {VersionId} 的来源 {SourceScope} 组迁移失败，保留 {Legacy}。",
                                    version.Id, group.SourceScope, SourceScopes.Legacy);
                }
            }
        }

        report.RemainingLegacyRows = await db.EpisodeVersions.AsNoTracking()
                                           .CountAsync(row => row.SourceScope == SourceScopes.Legacy,
                                                       cancellationToken);
    }

    /// <summary>逐槽解析来源并分组；悬空、陈旧 Pending 与孤儿 Superseded 在这里排除并计入报告。</summary>
    private static List<ResolvedGroup> ResolveEpisodeVersionGroups(
        LegacyEpisodeVersion version, IReadOnlyDictionary<Guid, EpisodeVersionFile> files,
        IReadOnlyDictionary<Guid, string?> taskSiteSources, SourceScopeBackfillReport report)
    {
        var resolved           = new List<(SourceScopeBackfillSlot Slot, Guid FileId, string SourceScope)>();
        var danglingIncumbent  = false;
        var hasValidSuperseded = false;

        ResolveSlot(SourceScopeBackfillSlot.Incumbent, version.IncumbentVideoFileId);
        ResolveSlot(SourceScopeBackfillSlot.Pending,   version.PendingVideoFileId);
        ResolveSlot(SourceScopeBackfillSlot.Superseded, version.SupersededVideoFileId);

        if (danglingIncumbent && hasValidSuperseded)
        {
            // 现任悬空时 Superseded 仍可能是有效的回退证据；只报告，不自动提升为现任。
            report.DanglingIncumbentWithValidSuperseded++;
            report.AddIssue(new SourceScopeBackfillIssue(
                                version.Id, version.SubjectId, version.Episode, null,
                                SourceScopeBackfillSlot.Incumbent,
                                SourceScopeBackfillIssueReason.DanglingIncumbentWithValidSuperseded,
                                version.IncumbentVideoFileId));
        }

        var groups = new List<ResolvedGroup>();
        foreach (var group in resolved.GroupBy(item => item.SourceScope, StringComparer.Ordinal)
                                      .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var incumbent  = FirstFile(group, SourceScopeBackfillSlot.Incumbent);
            var pending    = FirstFile(group, SourceScopeBackfillSlot.Pending);
            var superseded = FirstFile(group, SourceScopeBackfillSlot.Superseded);
            if (superseded is { } supersededFile && incumbent is null)
            {
                // 同源没有现任时，Superseded 没有可承接的对象；跨来源更不能建立回退关系。
                report.OrphanSuperseded++;
                report.AddIssue(new SourceScopeBackfillIssue(
                                    version.Id, version.SubjectId, version.Episode, group.Key,
                                    SourceScopeBackfillSlot.Superseded,
                                    SourceScopeBackfillIssueReason.OrphanSuperseded, supersededFile));
                superseded = null;
            }

            if (incumbent is null && pending is null && superseded is null) continue;
            groups.Add(new ResolvedGroup(group.Key, incumbent, pending, superseded));
        }

        return groups;

        void ResolveSlot(SourceScopeBackfillSlot slot, Guid? fileId)
        {
            if (fileId is not { } id) return;
            if (!files.TryGetValue(id, out var file) ||
                !taskSiteSources.TryGetValue(file.TaskId, out var siteSource))
            {
                report.DanglingPointers++;
                report.AddIssue(new SourceScopeBackfillIssue(
                                    version.Id, version.SubjectId, version.Episode, null, slot,
                                    SourceScopeBackfillIssueReason.DanglingPointer, id));
                if (slot == SourceScopeBackfillSlot.Incumbent) danglingIncumbent = true;
                return;
            }

            if (slot == SourceScopeBackfillSlot.Pending &&
                file is { IsSelectedForDownload: false, SupersededByTaskId: not null })
            {
                // 已被更优任务明确顶替的旧待完成优胜：不再迁移成新的 Pending。
                report.StalePending++;
                report.AddIssue(new SourceScopeBackfillIssue(
                                    version.Id, version.SubjectId, version.Episode, null, slot,
                                    SourceScopeBackfillIssueReason.StalePending, id));
                return;
            }

            var sourceScope = SourceScopes.FromSiteSource(siteSource);
            if (string.IsNullOrWhiteSpace(siteSource))
            {
                // 运行期同样把空 SiteSource 归入 (unknown)；这里只报告哨兵歧义，不猜 other。
                report.AmbiguousSiteSource++;
                report.AddIssue(new SourceScopeBackfillIssue(
                                    version.Id, version.SubjectId, version.Episode, sourceScope, slot,
                                    SourceScopeBackfillIssueReason.AmbiguousSiteSource, id));
            }
            else if (!KnownSiteSources.Contains(sourceScope))
            {
                // 迁移前的域名首段猜测值等历史标签：只按原标签迁移，不静默归并。
                report.NonCanonicalSiteSource++;
                report.AddIssue(new SourceScopeBackfillIssue(
                                    version.Id, version.SubjectId, version.Episode, sourceScope, slot,
                                    SourceScopeBackfillIssueReason.NonCanonicalSiteSource, id));
            }

            if (slot == SourceScopeBackfillSlot.Superseded) hasValidSuperseded = true;
            resolved.Add((slot, id, sourceScope));
        }

        static Guid? FirstFile(IEnumerable<(SourceScopeBackfillSlot Slot, Guid FileId, string SourceScope)> group,
                               SourceScopeBackfillSlot slot) =>
            group.Where(item => item.Slot == slot).Select(item => (Guid?)item.FileId).FirstOrDefault();
    }

    /// <summary>迁移一个来源组：新建/幂等/受控合并成功才清空 legacy 对应槽，冲突一律不覆盖。</summary>
    private async Task MigrateEpisodeVersionGroupAsync(
        int legacyId, ResolvedGroup group, bool isSplit, SourceScopeBackfillReport report,
        CancellationToken cancellationToken)
    {
        await using var db          = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var row = await db.EpisodeVersions.SingleOrDefaultAsync(
            version => version.Id == legacyId && version.SourceScope == SourceScopes.Legacy, cancellationToken);
        if (row is null || !GroupStillPresent(row, group))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var target = await db.EpisodeVersions.SingleOrDefaultAsync(
            version => version.SubjectId == row.SubjectId &&
                       version.Episode == row.Episode &&
                       version.SourceScope == group.SourceScope, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (target is null)
        {
            db.EpisodeVersions.Add(new EpisodeVersion
            {
                SubjectId             = row.SubjectId,
                Episode               = row.Episode,
                MediaType             = row.MediaType,
                SourceScope           = group.SourceScope,
                IncumbentVideoFileId  = group.Incumbent,
                PendingVideoFileId    = group.Pending,
                SupersededVideoFileId = group.Superseded,
                UpdatedAtUtc          = now
            });
            ClearMigratedSlots(row, group);
            row.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            report.CreatedRows++;
            if (isSplit) report.SplitRows++;
            return;
        }

        var sameMediaType = string.Equals(target.MediaType, row.MediaType, StringComparison.Ordinal);
        if (sameMediaType && SlotsEqual(target, group))
        {
            ClearMigratedSlots(row, group);
            row.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            report.IdempotentRows++;
            return;
        }

        if (sameMediaType && TryMergeControlled(target, group))
        {
            target.UpdatedAtUtc = now;
            ClearMigratedSlots(row, group);
            row.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            report.MergedRows++;
            return;
        }

        await transaction.RollbackAsync(cancellationToken);
        report.TargetConflicts++;
        report.AddIssue(CreateGroupIssue(row.Id, row.SubjectId, row.Episode, group,
                                         SourceScopeBackfillIssueReason.TargetConflict));
    }

    /// <summary>目标行已存在时只允许两种受控合并；只填 null 槽、不覆盖任何现有值，其余按冲突报告。</summary>
    private static bool TryMergeControlled(EpisodeVersion target, ResolvedGroup group)
    {
        if (target.SupersededVideoFileId is not null || group.Superseded is not null) return false;

        // A) 目标行保留了同一个 Pending，但旧现任尚未写入：补 null 的 Incumbent。
        if (target.PendingVideoFileId is { } pending &&
            target.IncumbentVideoFileId is null &&
            group.Pending == pending &&
            group.Incumbent is { } incumbent)
        {
            target.IncumbentVideoFileId = incumbent;
            return true;
        }

        // B) 目标行的 Pending 已完成并成为现任，旧 legacy 的现任正是应保留的回退。
        if (target.PendingVideoFileId is null &&
            target.IncumbentVideoFileId is { } current &&
            group.Pending == current &&
            group.Incumbent is { } superseded)
        {
            target.SupersededVideoFileId = superseded;
            return true;
        }

        return false;
    }

    /// <summary>组内重读校验：另一来源组已修改同一行时不再迁移本组，避免覆盖。</summary>
    private static bool GroupStillPresent(EpisodeVersion row, ResolvedGroup group) =>
        (group.Incumbent is not { } incumbent || row.IncumbentVideoFileId == incumbent) &&
        (group.Pending is not { } pending || row.PendingVideoFileId == pending) &&
        (group.Superseded is not { } superseded || row.SupersededVideoFileId == superseded);

    private static bool SlotsEqual(EpisodeVersion target, ResolvedGroup group) =>
        target.IncumbentVideoFileId == group.Incumbent &&
        target.PendingVideoFileId == group.Pending &&
        target.SupersededVideoFileId == group.Superseded;

    /// <summary>只清本组确实迁移过的槽，未迁移的悬空/孤儿/冲突槽原样保留。</summary>
    private static void ClearMigratedSlots(EpisodeVersion row, ResolvedGroup group)
    {
        if (group.Incumbent is { } incumbent && row.IncumbentVideoFileId == incumbent)
            row.IncumbentVideoFileId = null;
        if (group.Pending is { } pending && row.PendingVideoFileId == pending)
            row.PendingVideoFileId = null;
        if (group.Superseded is { } superseded && row.SupersededVideoFileId == superseded)
            row.SupersededVideoFileId = null;
    }

    private static SourceScopeBackfillIssue CreateGroupIssue(
        int legacyId, int subjectId, decimal episode, ResolvedGroup group, SourceScopeBackfillIssueReason reason)
    {
        var slot = group.Incumbent is not null
            ? SourceScopeBackfillSlot.Incumbent
            : group.Pending is not null
                ? SourceScopeBackfillSlot.Pending
                : group.Superseded is not null
                    ? SourceScopeBackfillSlot.Superseded
                    : (SourceScopeBackfillSlot?)null;
        var sampleFileId = group.Incumbent ?? group.Pending ?? group.Superseded;
        return new SourceScopeBackfillIssue(legacyId, subjectId, episode, group.SourceScope, slot, reason,
                                            sampleFileId);
    }

    /// <summary>汇总日志：每个非零未解决类别一条；样本只打印前 5 条，不输出 URL、passkey 或绝对路径。</summary>
    private void LogEpisodeVersionReport(SourceScopeBackfillReport report)
    {
        if (report.LegacyRows == 0) return;

        logger.LogInformation(
            "来源作用域回填：扫描 {LegacyRows} 条 EpisodeVersion {Legacy} 行；新建 {Created}、拆行 {Split}、合并 {Merged}、幂等 {Idempotent}，回填后仍为 {Legacy} 的 {Remaining} 条。",
            report.LegacyRows, SourceScopes.Legacy, report.CreatedRows, report.SplitRows, report.MergedRows,
            report.IdempotentRows, SourceScopes.Legacy, report.RemainingLegacyRows);
        if (report.DanglingPointers > 0)
            logger.LogWarning("来源作用域回填：{Count} 个 EpisodeVersion 文件指针悬空，已排除并保留 {Legacy}。",
                              report.DanglingPointers, SourceScopes.Legacy);
        if (report.DanglingIncumbentWithValidSuperseded > 0)
            logger.LogWarning(
                "来源作用域回填：{Count} 条 EpisodeVersion 的现任悬空但 Superseded 有效，未自动提升，需人工核对。",
                report.DanglingIncumbentWithValidSuperseded);
        if (report.OrphanSuperseded > 0)
            logger.LogWarning("来源作用域回填：{Count} 个 Superseded 指针没有同源现任，按孤儿保留在 {Legacy}。",
                              report.OrphanSuperseded, SourceScopes.Legacy);
        if (report.StalePending > 0)
            logger.LogInformation(
                "来源作用域回填：{Count} 个 Pending 已被取消选择且由其他任务顶替，按陈旧待完成保留在 {Legacy}。",
                report.StalePending, SourceScopes.Legacy);
        if (report.TargetConflicts > 0)
            logger.LogWarning("来源作用域回填：{Count} 个 EpisodeVersion 来源组的目标行已有不同登记，未覆盖并保留 {Legacy}。",
                              report.TargetConflicts, SourceScopes.Legacy);
        if (report.NoPointerRows > 0)
            logger.LogInformation("来源作用域回填：{Count} 条 EpisodeVersion 没有文件指针。", report.NoPointerRows);
        if (report.AmbiguousSiteSource > 0)
            logger.LogWarning(
                "来源作用域回填：{Count} 个文件指针所属任务缺少 SiteSource，按运行期语义归入 {Unknown}，需业务确认。",
                report.AmbiguousSiteSource, SourceScopes.Unknown);
        if (report.NonCanonicalSiteSource > 0)
            logger.LogWarning(
                "来源作用域回填：{Count} 个文件指针所属任务是迁移前的非规范来源标签，只按原标签迁移，未归并。",
                report.NonCanonicalSiteSource);
        if (report.Failures > 0)
            logger.LogWarning("来源作用域回填：{Count} 个 EpisodeVersion 来源组迁移失败，已保留 {Legacy} 等待下次启动重试。",
                              report.Failures, SourceScopes.Legacy);

        foreach (var issue in report.Issues.Take(5))
            logger.LogInformation(
                "来源作用域回填样本：EpisodeVersion {VersionId}（Subject {SubjectId}, Episode {Episode}, 槽 {Slot}, 来源 {SourceScope}）原因 {Reason}。",
                issue.LegacyId, issue.SubjectId, issue.Episode, issue.Slot, issue.SourceScope, issue.Reason);
    }

    private sealed record LegacyEpisodeVersion(
        int Id, int SubjectId, decimal Episode, string MediaType,
        Guid? IncumbentVideoFileId, Guid? PendingVideoFileId, Guid? SupersededVideoFileId);

    private sealed record EpisodeVersionFile(
        Guid Id, Guid TaskId, bool IsSelectedForDownload, Guid? SupersededByTaskId);

    private sealed record ResolvedGroup(
        string SourceScope, Guid? Incumbent, Guid? Pending, Guid? Superseded);
}
