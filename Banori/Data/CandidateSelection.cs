namespace Banori.Data;

/// <summary>
///     同集组的当前优胜选择，每组一条（组键唯一）。
///     IntentTaskId 为空表示下载意图尚未登记，是“选择已落库、意图未创建”中断窗口的恢复依据；
///     IntentFailedReason 非空表示登记失败待人工处理，恢复扫描不再自动重试。
/// </summary>
public sealed class CandidateSelection
{
    public Guid Id { get; set; }

    public int SubjectId { get; set; }

    public MediaSubject Subject { get; set; } = null!;

    /// <summary>同集分组键之一；普通集与 SP / OVA 等按解析出的类型分开分组。</summary>
    public string MediaType { get; set; } = string.Empty;

    public decimal Episode { get; set; }

    public Guid WinnerFeedItemId { get; set; }

    public FeedItem WinnerFeedItem { get; set; } = null!;

    /// <summary>选出该优胜时使用的规则版本；规则修改不改变既有记录的比较依据。</summary>
    public int RuleVersion { get; set; }

    /// <summary>优胜依据的可读描述（命中规则、优先级与修订版本）。</summary>
    public string RankingBasis { get; set; } = string.Empty;

    /// <summary>优胜候选选出时的命名快照，登记下载意图时沿用。</summary>
    public string NamingSnapshot { get; set; } = string.Empty;

    public Guid? IntentTaskId { get; set; }

    public string? IntentFailedReason { get; set; }

    public DateTimeOffset SelectedAtUtc { get; set; }
}
