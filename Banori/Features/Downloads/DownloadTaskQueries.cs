using Banned.AniParser.Models.Enums;
using Banori.Core.Rules.Selection;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Settings;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Banori.Features.Downloads;

/// <summary>下载任务列表的只读投影查询：筛选与计数在 SQL 端完成后分页，展示不依赖登记编排与下载执行的客户端。</summary>
public sealed class DownloadTaskQueries(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ManageQbittorrentSettings?              qbittorrentSettings   = null,
    ManageSelectionRuleSettings?            selectionRuleSettings = null)
{
    /// <summary>LIKE 转义符：用户输入中的 %、_ 与反斜杠按字面匹配，不当作通配符。</summary>
    private const string LikeEscape = "\\";

    /// <summary>
    ///     校验未确认意图的 JSON 片段（Web 默认序列化的 step 值）：未确认是暂停状态但展示通常归
    ///     「准备与校验中」，校验意图存 JSON 无法直接查询，用该片段做最小口径的 SQL 归属判断；
    ///     保留人工暂停的未确认任务例外，按暂停桶归属。
    /// </summary>
    private const string UnconfirmedIntentPattern = "%\"step\":\"Unconfirmed\"%";

    /// <summary>要素 JSON 中语言键的前缀模式；整串不被匹配即视为无语言任务。</summary>
    private const string LanguageKeyPrefixPattern = "%\"language\":\"%";

    private static readonly MethodInfo LikeMethod =
        typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
                                                [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    public async Task<DownloadTaskListPage> ListAsync(
        DownloadTaskListFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db         = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             pageSize   = Math.Clamp(filter.PageSize, 1, 200);
        var             query      = ApplyFilters(db.DownloadTasks.AsNoTracking(), filter);
        var             totalCount = await query.CountAsync(cancellationToken);
        var             pageCount  = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var             page       = Math.Clamp(filter.Page, 1, pageCount);
        var             languages  = await ListLanguagesAsync(db, cancellationToken);
        if (totalCount == 0) return new DownloadTaskListPage([], page, pageSize, 0, languages);

        // 下载器就绪只读取一次：等待提交的解释需要同一快照，不能每个任务重复读设置。
        var settings = qbittorrentSettings is null ? null : await qbittorrentSettings.GetAsync(cancellationToken);
        var downloaderReady = settings is { Enabled: true, IsConfigured: true };

        // SQLite 不支持 DateTimeOffset 的 ORDER BY 与比较，且默认排序需结合择优规则，
        // 取匹配行的轻量键集在内存中按多列优先级稳定排序后切出当页，再按 Id 取回整行。筛选与计数始终在 SQL 端完成。
        var selectionRules = selectionRuleSettings is null
            ? null
            : await selectionRuleSettings.GetAsync(cancellationToken);
        var rawKeys = await query.Select(task => new
                                  {
                                      task.Id,
                                      SubjectName = task.Subject == null
                                          ? null
                                          : string.IsNullOrWhiteSpace(task.Subject.NameCn)
                                              ? task.Subject.Name
                                              : task.Subject.NameCn,
                                      task.NamingSnapshot,
                                      FeedTitle = task.FeedItem == null ? null : task.FeedItem.Title,
                                      task.Episode,
                                      task.NamingIngredients,
                                      task.Version,
                                      task.CreatedAtUtc
                                  })
                                  .ToListAsync(cancellationToken);
        var keys = rawKeys.Select(row => BuildSortKey(row.Id,
                                                      row.SubjectName,
                                                      row.NamingSnapshot,
                                                      row.FeedTitle,
                                                      row.Episode,
                                                      row.NamingIngredients,
                                                      row.Version,
                                                      row.CreatedAtUtc,
                                                      selectionRules))
                          .ToList();
        var comparer = new SortKeyComparer(filter.SortColumn, filter.SortDescending, selectionRules);
        keys.Sort(comparer);
        var pageIds = keys.Skip((page - 1) * pageSize)
                          .Take(pageSize)
                          .Select(key => key.Id)
                          .ToArray();
        var rows = await db.DownloadTasks.AsNoTracking()
                           .Include(task => task.FeedItem)
                           .Include(task => task.Subject)
                           .Include(task => task.Files)
                           .Where(task => pageIds.Contains(task.Id))
                           .ToListAsync(cancellationToken);
        var rowsById  = rows.ToDictionary(task => task.Id);
        var pageTasks = pageIds.Where(rowsById.ContainsKey).Select(id => rowsById[id]).ToList();

        var relatedNames = await RelatedNamesAsync(db, pageTasks, cancellationToken);
        var items        = pageTasks.Select(task => ToView(task, relatedNames, downloaderReady)).ToList();
        return new DownloadTaskListPage(items, page, pageSize, totalCount, languages);
    }

    private static IQueryable<DownloadTask>
        ApplyFilters(IQueryable<DownloadTask> query, DownloadTaskListFilter filter) =>
        ApplyLanguages(ApplyStatusGroups(ApplySearch(query, filter.SearchText), filter.StatusGroups), filter.Languages);

    /// <summary>
    ///     搜索：条目名（中文名缺失或空白时回退原名，与列表显示标题同一口径）、种子原始名、命名快照
    ///     与字幕组（要素 JSON 的 "group" 值），四者 OR；LIKE 通配符已转义为字面匹配，
    ///     SQLite 对 ASCII 大小写不敏感，MySQL 取决于列排序规则。
    /// </summary>
    private static IQueryable<DownloadTask> ApplySearch(IQueryable<DownloadTask> query, string searchText)
    {
        var keyword = searchText.Trim();
        if (keyword.Length == 0) return query;

        var pattern      = ContainsPattern(keyword);
        var groupPattern = GroupContainsPattern(keyword);
        return query.Where(task =>
                               EF.Functions.Like(task.Subject == null
                                                     ? null
                                                     : string.IsNullOrWhiteSpace(task.Subject.NameCn)
                                                         ? task.Subject.Name
                                                         : task.Subject.NameCn,
                                                 pattern, LikeEscape) ||
                               EF.Functions.Like(task.FeedItem == null ? null : task.FeedItem.Title, pattern,
                                                 LikeEscape)                               ||
                               EF.Functions.Like(task.NamingSnapshot, pattern, LikeEscape) ||
                               EF.Functions.Like(task.NamingIngredients, groupPattern, LikeEscape));
    }

    /// <summary>
    ///     状态分组过滤：与 DownloadStatusFilter.GroupOf 同一口径的 SQL 形状（等价性测试锁定两边一致）。
    ///     集合就是要显示的分组：空集合（页面全不勾选）时所有分支都不启用，谓词恒不成立；
    ///     全部勾选与不限制状态等价，直接省略状态谓词。
    ///     显示阶段的细分（隔离/校验意图/准备阶段）都落在「准备与校验中」桶；校验意图存 JSON 无法直接查询，
    ///     仅「暂停/运行任务被下载器端 recheck 观察为校验中」这一处影响归属，用空意图近似该判断；
    ///     「校验未确认」同样是 Paused 但归准备与校验桶，按意图 JSON 的 Unconfirmed 片段精确归属；
    ///     人工暂停优先：保留 IsUserPaused 的未确认任务按暂停桶归属，不落入准备与校验桶。
    /// </summary>
    private static IQueryable<DownloadTask> ApplyStatusGroups(
        IQueryable<DownloadTask> query, IReadOnlySet<DownloadStatusFilterGroup> groups)
    {
        if (DownloadStatusFilter.Choices.All(groups.Contains)) return query;

        (DownloadStatusFilterGroup Group, DownloadTaskStatus Status)[] directStatusGroups =
        [
            (DownloadStatusFilterGroup.WaitingSubmit, DownloadTaskStatus.PendingSubmit),
            (DownloadStatusFilterGroup.WaitingAddConfirm, DownloadTaskStatus.Submitting),
            (DownloadStatusFilterGroup.Completed, DownloadTaskStatus.Completed),
            (DownloadStatusFilterGroup.Failed, DownloadTaskStatus.Failed),
            (DownloadStatusFilterGroup.Lost, DownloadTaskStatus.Lost),
            (DownloadStatusFilterGroup.Replaced, DownloadTaskStatus.Replaced)
        ];
        var selectedStatuses = directStatusGroups.Where(pair => groups.Contains(pair.Group))
                                                 .Select(pair => pair.Status).ToArray();
        var wantDownloading       = groups.Contains(DownloadStatusFilterGroup.Downloading);
        var wantPreparingChecking = groups.Contains(DownloadStatusFilterGroup.PreparingChecking);
        var wantPaused            = groups.Contains(DownloadStatusFilterGroup.Paused);
        var wantLost              = groups.Contains(DownloadStatusFilterGroup.Lost);

        return from task in query
               let isChecking = task.LastObservedCategory == QbittorrentTorrentCategory.Checking
               let isPreparingDownload = task.IsSystemPaused || isChecking
               let hasUnconfirmedVerification = task.VerificationIntent != null &&
                                                EF.Functions.Like(task.VerificationIntent, UnconfirmedIntentPattern)
               let isPausedForPreparation = (isChecking         && string.IsNullOrEmpty(task.VerificationIntent)) ||
                                            (!task.IsUserPaused && hasUnconfirmedVerification)
               where selectedStatuses.Contains(task.Status) ||
                     (task.Status == DownloadTaskStatus.Downloading &&
                      (isPreparingDownload ? wantPreparingChecking : wantDownloading)) ||
                     (task.Status == DownloadTaskStatus.Paused &&
                      (isPausedForPreparation ? wantPreparingChecking : wantPaused)) ||
                     // 确认缺种的已完成任务同时归入「已完成」和「丢失」。
                     (wantLost && task.Status == DownloadTaskStatus.Completed && task.TorrentMissingAtUtc != null)
               select task;
    }

    /// <summary>语言筛选：字幕语言存于要素 JSON 的 "language" 值，多选 OR；空字符串哨兵匹配键缺失或为空的无语言任务。</summary>
    private static IQueryable<DownloadTask> ApplyLanguages(
        IQueryable<DownloadTask> query, IReadOnlySet<string> languages)
    {
        if (languages.Count == 0) return query;

        var valuePatterns = languages.Where(language => language.Length > 0)
                                     .Select(LanguageEqualsPattern)
                                     .ToList();
        var includeNoLanguage = languages.Contains(string.Empty);
        if (valuePatterns.Count == 0 && !includeNoLanguage) return query;

        return query.Where(LanguagePredicate(valuePatterns, includeNoLanguage));
    }

    /// <summary>语言多选的 OR 链模式数量可变，写不成固定 lambda，手工组合表达式树；Like 统一带转义符。</summary>
    private static Expression<Func<DownloadTask, bool>> LanguagePredicate(
        IReadOnlyList<string> valuePatterns, bool includeNoLanguage)
    {
        var         task = Expression.Parameter(typeof(DownloadTask), "task");
        var         ingredients = Expression.Property(task, nameof(DownloadTask.NamingIngredients));
        Expression? body = null;
        foreach (var pattern in valuePatterns) body = OrElse(body, Like(ingredients, pattern));
        if (includeNoLanguage) body = OrElse(body, Expression.Not(Like(ingredients, LanguageKeyPrefixPattern)));
        return Expression.Lambda<Func<DownloadTask, bool>>(body ?? Expression.Constant(false), task);

        static Expression OrElse(Expression? left, Expression right) =>
            left is null ? right : Expression.OrElse(left, right);
    }

    private static Expression Like(Expression matchExpression, string pattern) =>
        Expression.Call(LikeMethod,
                        Expression.Property(null, typeof(EF).GetProperty(nameof(EF.Functions))!),
                        matchExpression,
                        Expression.Constant(pattern),
                        Expression.Constant(LikeEscape));

    /// <summary>包含匹配的 LIKE 模式：先转义用户输入，通配符按字面处理。</summary>
    private static string ContainsPattern(string keyword) =>
        $"%{EscapeLikePattern(keyword)}%";

    /// <summary>
    ///     字幕组包含匹配：要素 JSON 键序固定（group 紧跟 source），以「"group":" 开头、"，"source": 收尾」
    ///     圈定组名值，关键词必须落在该值内部——尾部只写引号会让通配符扫进后续字段（语言等）造成误命中。
    /// </summary>
    private static string GroupContainsPattern(string keyword) =>
        $"%\"group\":\"%{EscapeLikePattern(JsonStringLiteral(keyword))}%\",\"source\":%";

    /// <summary>字幕语言精确匹配：命中要素 JSON 中 "language" 键的完整值。</summary>
    private static string LanguageEqualsPattern(string language) =>
        $"%\"language\":\"{EscapeLikePattern(JsonStringLiteral(language))}\"%";

    private static string EscapeLikePattern(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    ///     按 System.Text.Json 默认编码把值渲染成 JSON 字符串字面量内容：非 ASCII、控制字符与
    ///     HTML 敏感字符转义为 \uXXXX（UTF-16 代理项逐码元转义），与要素 JSON 的存储一致；
    ///     其余可打印 ASCII 保持原样。语言与字幕组的匹配模式都以此对齐真实存储。
    /// </summary>
    private static string JsonStringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
            if (character < 0x20 || character > 0x7E ||
                character is '<' or '>' or '&' or '\'' or '+' or '"' or '\\')
                builder.Append("\\u").Append(((int)character).ToString("X4"));
            else
                builder.Append(character);

        return builder.ToString();
    }

    /// <summary>
    ///     可选语言覆盖全部历史任务：语言在要素 JSON 内，跨 provider 的 JSON 提取不可移植，
    ///     取整列轻量投影后内存去重（每任务一个短字符串）；存在无语言任务时追加空字符串哨兵。
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListLanguagesAsync(
        ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var ingredients = await db.DownloadTasks.AsNoTracking()
                                  .Select(task => task.NamingIngredients)
                                  .ToListAsync(cancellationToken);
        var parsed = ingredients.Select(json => TorrentNamingIngredients.FromJson(json)?.Language)
                                .ToList();
        var languages = parsed.Where(language => !string.IsNullOrWhiteSpace(language))
                              .Select(language => language!)
                              .GroupBy(language => language, StringComparer.Ordinal)
                              .OrderByDescending(group => group.Count())
                              .ThenBy(group => group.Key, StringComparer.Ordinal)
                              .Select(group => group.Key)
                              .ToList();
        if (parsed.Any(string.IsNullOrWhiteSpace)) languages.Add(string.Empty);
        return languages;
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> RelatedNamesAsync(
        ApplicationDbContext db, IReadOnlyList<DownloadTask> tasks, CancellationToken cancellationToken)
    {
        var relatedIds = tasks.SelectMany(task =>
                               {
                                   var decision = DownloadPathDecision.FromJson(task.PathDecision);
                                   return new[] { task.ReplacesTaskId, task.ReplacedByTaskId, decision?.RelatedTaskId };
                               })
                              .Where(value => value is not null)
                              .Select(value => value!.Value)
                              .Distinct()
                              .ToList();
        if (relatedIds.Count == 0) return new Dictionary<Guid, string>();
        return await db.DownloadTasks.AsNoTracking()
                       .Where(task => relatedIds.Contains(task.Id))
                       .ToDictionaryAsync(task => task.Id, task => task.NamingSnapshot, cancellationToken);
    }

    private static DownloadTaskView ToView(
        DownloadTask task, IReadOnlyDictionary<Guid, string> relatedNames, bool downloaderReady)
    {
        var ingredients   = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        var archiveReview = DownloadArchiveReview.FromJson(task.ArchiveReview);
        var intent        = DownloadVerificationIntent.FromJson(task.VerificationIntent);
        var decision      = DownloadPathDecision.FromJson(task.PathDecision);
        var runRecovery   = DownloadRunRecovery.FromJson(task.RunRecovery);
        return new DownloadTaskView
        {
            Id          = task.Id,
            FeedItemId  = task.FeedItemId,
            InfoHash    = task.InfoHash,
            TorrentName = task.FeedItem?.Title,
            SubjectId   = task.SubjectId,
            SubjectName = task.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(task.Subject.NameCn) ? task.Subject.Name : task.Subject.NameCn,
            Episode          = task.Episode,
            EpisodeText      = ingredients?.EpisodeText,
            Version          = task.Version,
            SubtitleGroup    = ingredients?.Group,
            SubtitleLanguage = ingredients?.Language,
            Status           = task.Status,
            IsUserPaused     = task.IsUserPaused,
            IsSystemPaused   = task.IsSystemPaused,
            Progress         = task.Progress,
            TotalSize        = task.TotalSize,
            NamingSnapshot   = task.NamingSnapshot,
            SiteSource       = task.SiteSource,
            FailedStep       = task.FailedStep,
            Error            = task.Error,
            CreatedAtUtc     = task.CreatedAtUtc,
            SubmittedAtUtc   = task.SubmittedAtUtc,
            CompletedAtUtc   = task.CompletedAtUtc,
            ReplacesTaskId   = task.ReplacesTaskId,
            ReplacesNaming = task.ReplacesTaskId is { } replacedId
                ? relatedNames.GetValueOrDefault(replacedId)
                : null,
            ReplacedByTaskId = task.ReplacedByTaskId,
            ReplacedByNaming = task.ReplacedByTaskId is { } replacedById
                ? relatedNames.GetValueOrDefault(replacedById)
                : null,
            PreparationStage         = task.PreparationStage,
            ArchiveReviewReason      = archiveReview?.Reason,
            IsolationState           = task.IsolationState,
            VerificationStep         = intent?.Step,
            VerificationRequestState = intent?.RequestState,
            VerificationRound        = intent?.Round            ?? 0,
            ObservedChecking         = intent?.ObservedChecking ?? false,
            CompletionEvidence       = intent?.CompletionEvidenceKind,
            StopConfirmPending = intent?.TimeoutStopRequestedAtUtc is not null ||
                                 intent?.PauseRequestedAtUtc is not null,
            ResultStabilizing         = intent?.StabilityObservedAtUtc is not null,
            ExternalStopObservedAtUtc = task.ExternalStopObservedAtUtc,
            RunPurpose                = runRecovery?.Purpose,
            RunState                  = runRecovery?.State,
            RunReason                 = runRecovery?.Reason,
            RunObservedAtUtc          = runRecovery?.LastObservedAtUtc,
            LastSyncedAtUtc           = task.LastSyncedAtUtc,
            LastObservedCategory      = task.LastObservedCategory,
            NextObservationAtUtc      = task.NextObservationAtUtc,
            TorrentMissingAtUtc       = task.TorrentMissingAtUtc,
            AttemptCount              = task.AttemptCount,
            DownloaderReady           = downloaderReady,
            PathDecisionKind          = decision?.Kind,
            PathDecisionReason        = decision?.Reason,
            PathDecisionRelatedTaskId = decision?.RelatedTaskId,
            PathDecisionRelatedNaming = decision?.RelatedTaskId is { } decisionRelatedId
                ? relatedNames.GetValueOrDefault(decisionRelatedId)
                : null,
            HasPendingFileMoves = task.PendingSavePath is not null ||
                                  task.Files.Any(file => file.PendingRenameTo is not null)
        };
    }

    public static bool DefaultSortDescending(DownloadTaskSortColumn column) => column switch
    {
        DownloadTaskSortColumn.Version   => true,
        DownloadTaskSortColumn.CreatedAt => true,
        _                                => false
    };

    private static SortKey BuildSortKey(
        Guid                 id,
        string?              subjectName,
        string?              namingSnapshot,
        string?              feedTitle,
        decimal?             episode,
        string?              namingIngredients,
        int                  version,
        DateTimeOffset       createdAtUtc,
        SelectionRuleConfig? rules)
    {
        var animeName = !string.IsNullOrWhiteSpace(subjectName)    ? subjectName :
                        !string.IsNullOrWhiteSpace(namingSnapshot) ? namingSnapshot :
                        !string.IsNullOrWhiteSpace(feedTitle)      ? feedTitle : string.Empty;

        var ingredients = TorrentNamingIngredients.FromJson(namingIngredients);
        var rawGroup    = ingredients?.Group;
        var rawLanguage = ingredients?.Language;
        var group = string.IsNullOrWhiteSpace(rawGroup) ||
                    string.Equals(rawGroup, rawLanguage, StringComparison.OrdinalIgnoreCase)
            ? null
            : rawGroup;
        var language = string.IsNullOrWhiteSpace(rawLanguage) ? null : rawLanguage;

        var groupRank    = group is null ? int.MaxValue : FindGroupRank(group, rules);
        var languageRank = language is null ? int.MaxValue : FindLanguageRank(language, rules);
        return new SortKey(id, animeName, episode, group, groupRank, language, languageRank, version, createdAtUtc);
    }

    private static int FindGroupRank(string group, SelectionRuleConfig? rules)
    {
        if (rules is null) return int.MaxValue;

        if (rules.Mode == SelectionMode.Independent)
        {
            for (var index = 0; index < rules.GroupOrder.Count; index++)
                if (string.Equals(rules.GroupOrder[index], group, StringComparison.OrdinalIgnoreCase))
                    return index;

            return int.MaxValue;
        }

        var rank = 0;
        foreach (var entry in rules.CombinedEntries)
        {
            if (entry.Group is null) continue;
            if (string.Equals(entry.Group, group, StringComparison.OrdinalIgnoreCase)) return rank;
            rank++;
        }

        return int.MaxValue;
    }

    private static int FindLanguageRank(string languageText, SelectionRuleConfig? rules)
    {
        if (rules is null || !Enum.TryParse<EnumLanguage>(languageText, ignoreCase: true, out var language))
            return int.MaxValue;

        if (rules.Mode == SelectionMode.Independent)
        {
            for (var index = 0; index < rules.LanguageOrder.Count; index++)
                if (LanguageComposition.Matches(rules.LanguageOrder[index], language, rules.LanguageMatch))
                    return index;

            return int.MaxValue;
        }

        var rank = 0;
        foreach (var entry in rules.CombinedEntries)
        {
            if (entry.Language is null) continue;
            if (LanguageComposition.Matches(entry.Language.Value, language, rules.LanguageMatch)) return rank;
            rank++;
        }

        return int.MaxValue;
    }

    private sealed record SortKey(
        Guid           Id,
        string         AnimeName,
        decimal?       Episode,
        string?        Group,
        int            GroupRank,
        string?        Language,
        int            LanguageRank,
        int            Version,
        DateTimeOffset CreatedAtUtc);

    private sealed class SortKeyComparer : IComparer<SortKey>
    {
        private static readonly DownloadTaskSortColumn[] DefaultOrder =
        [
            DownloadTaskSortColumn.AnimeName,
            DownloadTaskSortColumn.Episode,
            DownloadTaskSortColumn.SubtitleGroup,
            DownloadTaskSortColumn.Language,
            DownloadTaskSortColumn.Version,
            DownloadTaskSortColumn.CreatedAt
        ];

        private readonly (DownloadTaskSortColumn Column, bool Descending)[] _steps;

        public SortKeyComparer(
            DownloadTaskSortColumn primaryColumn, bool? primaryDescending, SelectionRuleConfig? _)
        {
            var steps = new List<(DownloadTaskSortColumn, bool)>(DefaultOrder.Length)
            {
                (primaryColumn, primaryDescending ?? DefaultSortDescending(primaryColumn))
            };
            foreach (var column in DefaultOrder)
                if (column != primaryColumn)
                    steps.Add((column, DefaultSortDescending(column)));

            _steps = [.. steps];
        }

        public int Compare(SortKey? left, SortKey? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            foreach (var (column, descending) in _steps)
            {
                var cmp = CompareColumn(left, right, column, descending);
                if (cmp != 0) return cmp;
            }

            return right.Id.CompareTo(left.Id);
        }

        private static int CompareColumn(
            SortKey left, SortKey right, DownloadTaskSortColumn column, bool descending)
        {
            var cmp = column switch
            {
                DownloadTaskSortColumn.AnimeName => CompareNullableString(left.AnimeName, right.AnimeName, descending),
                DownloadTaskSortColumn.Episode   => CompareNullableDecimal(left.Episode, right.Episode, descending),
                DownloadTaskSortColumn.SubtitleGroup =>
                    CompareRankedText(left.Group, left.GroupRank, right.Group, right.GroupRank, descending),
                DownloadTaskSortColumn.Language =>
                    CompareRankedText(left.Language, left.LanguageRank, right.Language, right.LanguageRank, descending),
                DownloadTaskSortColumn.Version   => Flip(left.Version.CompareTo(right.Version), descending),
                DownloadTaskSortColumn.CreatedAt => Flip(left.CreatedAtUtc.CompareTo(right.CreatedAtUtc), descending),
                _                                => 0
            };
            return cmp;
        }

        private static int CompareNullableString(string left, string right, bool descending)
        {
            if (left.Length == 0 && right.Length == 0) return 0;
            if (left.Length == 0) return 1;
            if (right.Length == 0) return -1;
            return Flip(StringComparer.OrdinalIgnoreCase.Compare(left, right), descending);
        }

        private static int CompareNullableDecimal(decimal? left, decimal? right, bool descending)
        {
            if (left is null && right is null) return 0;
            if (left is null) return 1;
            if (right is null) return -1;
            return Flip(left.Value.CompareTo(right.Value), descending);
        }

        private static int CompareRankedText(
            string? leftText, int leftRank, string? rightText, int rightRank, bool descending)
        {
            if (leftText is null && rightText is null) return 0;
            if (leftText is null) return 1;
            if (rightText is null) return -1;

            var rankCmp = leftRank.CompareTo(rightRank);
            var cmp     = rankCmp != 0 ? rankCmp : StringComparer.OrdinalIgnoreCase.Compare(leftText, rightText);
            return Flip(cmp, descending);
        }

        private static int Flip(int cmp, bool descending) => descending ? -cmp : cmp;
    }
}
