namespace Banori.Core.Rules;

/// <summary>
///     条目评估阶段的结果类别：Accepted 表示通过硬性过滤的合格候选（可参与同集竞争），
///     不再代表最终下载选择；组内胜负由批量择优产生。
/// </summary>
public enum ReleaseDecisionStatus
{
    Accepted,
    Rejected,
    NeedsReview
}
