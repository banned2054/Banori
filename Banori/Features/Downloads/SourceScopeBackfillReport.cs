namespace Banori.Features.Downloads;

/// <summary>EpisodeVersion 的三个文件指针槽位；报告用它指出具体是哪一条关系无法自动迁移。</summary>
public enum SourceScopeBackfillSlot
{
    Incumbent,
    Pending,
    Superseded
}

/// <summary>回填保留 legacy（或只做受控合并）的原因分类；日志与诊断只读结构化字段，不解析文本。</summary>
public enum SourceScopeBackfillIssueReason
{
    DanglingPointer,
    DanglingIncumbentWithValidSuperseded,
    OrphanSuperseded,
    StalePending,
    TargetConflict,
    AmbiguousSiteSource,
    NonCanonicalSiteSource,
    Failure
}

/// <summary>一条无法自动处理的关系样本；不包含 feed URL、passkey 或文件系统绝对路径。</summary>
public sealed record SourceScopeBackfillIssue(
    int LegacyId, int SubjectId, decimal Episode, string? SourceScope,
    SourceScopeBackfillSlot? Slot, SourceScopeBackfillIssueReason Reason, Guid? SampleFileId);

/// <summary>
///     EpisodeVersion 启动回填的结构化结果，不落库：计数供启动日志与测试断言，
///     <see cref="Issues" /> 保留需要人工核对的样本（日志只打印前几条）。
/// </summary>
public sealed class SourceScopeBackfillReport
{
    private readonly List<SourceScopeBackfillIssue> _issues = [];

    /// <summary>扫描到的 legacy EpisodeVersion 行数。</summary>
    public int LegacyRows { get; internal set; }

    /// <summary>新建来源行数。</summary>
    public int CreatedRows { get; internal set; }

    /// <summary>由多来源 legacy 行拆出的新行数。</summary>
    public int SplitRows { get; internal set; }

    /// <summary>受控合并（只填 null 槽）的目标行数。</summary>
    public int MergedRows { get; internal set; }

    /// <summary>目标行已完全一致、本次只清残留 legacy 槽的行数。</summary>
    public int IdempotentRows { get; internal set; }

    /// <summary>文件或所属任务不存在的指针数。</summary>
    public int DanglingPointers { get; internal set; }

    /// <summary>现任指针悬空但 Superseded 有效的行数；不自动提升 Superseded。</summary>
    public int DanglingIncumbentWithValidSuperseded { get; internal set; }

    /// <summary>没有同源 Incumbent 的 Superseded 指针数；不建立关系。</summary>
    public int OrphanSuperseded { get; internal set; }

    /// <summary>已被取消选择且 SupersededByTaskId 非空的 Pending 数；不迁移。</summary>
    public int StalePending { get; internal set; }

    /// <summary>目标键已有不同有效登记、未覆盖的来源组数。</summary>
    public int TargetConflicts { get; internal set; }

    /// <summary>三个指针全空的 legacy 行数。</summary>
    public int NoPointerRows { get; internal set; }

    /// <summary>指针所属任务 SiteSource 为空、按运行期语义归入 (unknown) 的指针数。</summary>
    public int AmbiguousSiteSource { get; internal set; }

    /// <summary>指针所属任务使用迁移前非规范来源标签、只按原标签迁移的指针数。</summary>
    public int NonCanonicalSiteSource { get; internal set; }

    /// <summary>回填后仍为 legacy 的行数。</summary>
    public int RemainingLegacyRows { get; internal set; }

    /// <summary>单行/单组迁移异常数；旧行保留，等待下次启动重试。</summary>
    public int Failures { get; internal set; }

    /// <summary>无法自动处理的样本；数量不封顶，日志只取前 5 条。</summary>
    public IReadOnlyList<SourceScopeBackfillIssue> Issues => _issues;

    internal void AddIssue(SourceScopeBackfillIssue issue) => _issues.Add(issue);
}
