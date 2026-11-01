using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;

namespace Banori.Features.Downloads;

/// <summary>下载任务列表的只读投影查询：筛选与计数在 SQL 端完成后分页，展示不依赖登记编排与下载执行的客户端。</summary>
public sealed class DownloadTaskQueries(IDbContextFactory<ApplicationDbContext> dbContextFactory)
{
    /// <summary>LIKE 转义符：用户输入中的 %、_ 与反斜杠按字面匹配，不当作通配符。</summary>
    private const string LikeEscape = "\\";

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

        // SQLite 不支持 DateTimeOffset 的 ORDER BY 与比较，无法纯 SQL 分页：取匹配行的轻量键集
        // （Id + 创建时间）内存稳定排序后切出当页，再按 Id 取回整行。筛选与计数始终在 SQL 端完成。
        var keys = await query.Select(task => new { task.Id, task.CreatedAtUtc })
                              .ToListAsync(cancellationToken);
        var pageIds = keys.OrderByDescending(key => key.CreatedAtUtc)
                          .ThenByDescending(key => key.Id)
                          .Skip((page - 1) * pageSize)
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
        var items        = pageTasks.Select(task => ToView(task, relatedNames)).ToList();
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
    ///     显示阶段的细分（隔离/校验意图/准备阶段）都落在「准备与校验中」桶；校验意图存 JSON 无法直接查询，
    ///     仅「暂停/运行任务被下载器端 recheck 观察为校验中」这一处影响归属，用空意图近似该判断。
    /// </summary>
    private static IQueryable<DownloadTask> ApplyStatusGroups(
        IQueryable<DownloadTask> query, IReadOnlySet<DownloadStatusFilterGroup> groups)
    {
        if (groups.Count == 0) return query;

        var wantWaitingSubmit     = groups.Contains(DownloadStatusFilterGroup.WaitingSubmit);
        var wantWaitingAddConfirm = groups.Contains(DownloadStatusFilterGroup.WaitingAddConfirm);
        var wantPreparingChecking = groups.Contains(DownloadStatusFilterGroup.PreparingChecking);
        var wantDownloading       = groups.Contains(DownloadStatusFilterGroup.Downloading);
        var wantPaused            = groups.Contains(DownloadStatusFilterGroup.Paused);
        var wantCompleted         = groups.Contains(DownloadStatusFilterGroup.Completed);
        var wantFailed            = groups.Contains(DownloadStatusFilterGroup.Failed);
        var wantLost              = groups.Contains(DownloadStatusFilterGroup.Lost);
        var wantReplaced          = groups.Contains(DownloadStatusFilterGroup.Replaced);
        return query.Where(task =>
                               (wantWaitingSubmit     && task.Status == DownloadTaskStatus.PendingSubmit) ||
                               (wantWaitingAddConfirm && task.Status == DownloadTaskStatus.Submitting)    ||
                               // 运行中任务被下载器端 recheck 观察到校验时展示为校验中，归准备与校验桶。
                               (wantDownloading      && task.Status == DownloadTaskStatus.Downloading &&
                                !task.IsSystemPaused &&
                                task.LastObservedCategory != QbittorrentTorrentCategory.Checking) ||
                               (wantPreparingChecking &&
                                ((task.Status == DownloadTaskStatus.Downloading &&
                                  (task.IsSystemPaused ||
                                   task.LastObservedCategory == QbittorrentTorrentCategory.Checking)) ||
                                 (task.Status               == DownloadTaskStatus.Paused           &&
                                  task.LastObservedCategory == QbittorrentTorrentCategory.Checking &&
                                  string.IsNullOrEmpty(task.VerificationIntent)))) ||
                               (wantPaused && task.Status == DownloadTaskStatus.Paused &&
                                !(task.LastObservedCategory == QbittorrentTorrentCategory.Checking &&
                                  string.IsNullOrEmpty(task.VerificationIntent)))             ||
                               (wantCompleted && task.Status == DownloadTaskStatus.Completed) ||
                               (wantFailed    && task.Status == DownloadTaskStatus.Failed)    ||
                               (wantLost      && task.Status == DownloadTaskStatus.Lost)      ||
                               (wantReplaced  && task.Status == DownloadTaskStatus.Replaced));
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
        if (parsed.Any(language => string.IsNullOrWhiteSpace(language))) languages.Add(string.Empty);
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

    private static DownloadTaskView ToView(DownloadTask task, IReadOnlyDictionary<Guid, string> relatedNames)
    {
        var ingredients   = TorrentNamingIngredients.FromJson(task.NamingIngredients);
        var archiveReview = DownloadArchiveReview.FromJson(task.ArchiveReview);
        var intent        = DownloadVerificationIntent.FromJson(task.VerificationIntent);
        var decision      = DownloadPathDecision.FromJson(task.PathDecision);
        return new DownloadTaskView
        {
            Id          = task.Id,
            FeedItemId  = task.FeedItemId,
            TorrentName = task.FeedItem?.Title,
            SubjectId   = task.SubjectId,
            SubjectName = task.Subject is null                 ? null :
                string.IsNullOrWhiteSpace(task.Subject.NameCn) ? task.Subject.Name : task.Subject.NameCn,
            Episode          = task.Episode,
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
            PreparationStage    = task.PreparationStage,
            ArchiveReviewReason = archiveReview?.Reason,
            IsolationState      = task.IsolationState,
            VerificationStep    = intent?.Step,
            VerificationRound   = intent?.Round            ?? 0,
            ObservedChecking    = intent?.ObservedChecking ?? false,
            StopConfirmPending = intent?.TimeoutStopRequestedAtUtc is not null ||
                                 intent?.PauseRequestedAtUtc is not null,
            ResultStabilizing         = intent?.StabilityObservedAtUtc is not null,
            ExternalStopObservedAtUtc = task.ExternalStopObservedAtUtc,
            LastSyncedAtUtc           = task.LastSyncedAtUtc,
            LastObservedCategory      = task.LastObservedCategory,
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
}
