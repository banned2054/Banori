namespace Banori.Features.Downloads;

/// <summary>
///     下载推进的统一时间策略：采样间隔、确认窗口与有界补发预算。窗口到期前只观察不补发，
///     请求预算按实际发出次数扣减（查询轮次与 pulse 不消耗预算）；数据库存续的请求时间戳
///     在重启后仍然生效。数值是内部策略起点，不是用户设置。
/// </summary>
public sealed class DownloadTimingPolicy
{
    /// <summary>
    ///     稳定窗口与间隔为零、稳定样本数为 1 的策略：结果观察始终单样本即可下结论，供既有测试沿用旧的
    ///     “每轮可推进”语义。未显式注册策略的测试构造路径以它兜底；生产必须在 Program.cs 注册 <see cref="Default" />。
    ///     截止类窗口（校验启动、无进展、单轮总上限）保留默认值——旧测试依赖固定时钟下窗口不过期，
    ///     由测试显式改写意图时间戳模拟超时。
    /// </summary>
    public static DownloadTimingPolicy Immediate { get; } = new()
    {
        PreparationSampleInterval   = TimeSpan.Zero,
        VerificationSampleInterval  = TimeSpan.Zero,
        MinReconcileRoundInterval   = TimeSpan.Zero,
        AddVisibilityWindow         = TimeSpan.Zero,
        LayoutConfirmationWindow    = TimeSpan.Zero,
        ControlConfirmationWindow   = TimeSpan.Zero,
        SelectionConfirmationWindow = TimeSpan.Zero,
        ResultStabilityWindow       = TimeSpan.Zero,
        ResultStabilitySamples      = 1,
        ResultStabilityMaxWindow    = TimeSpan.Zero
    };

    /// <summary>生产默认策略：按异步生命周期计划的建议初始参数，真实 qB 验收后再校准。</summary>
    public static DownloadTimingPolicy Default { get; } = new();

    /// <summary>准备与短确认阶段的采样间隔：到期任务批量读取，等待窗口期间按此节奏观察。</summary>
    public TimeSpan PreparationSampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>校验中与等待校验结束的采样间隔；比准备阶段更疏，与下载器刷新周期协调。</summary>
    public TimeSpan VerificationSampleInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>空闲对账间隔：没有任何任务更早到期时的兜底轮询周期。</summary>
    public TimeSpan IdleReconcileInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>两轮对账之间的最小间隔：重复钩子脉冲不得形成忙循环。</summary>
    public TimeSpan MinReconcileRoundInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>添加可见性窗口：请求已被接收（或响应结果未知）但下载器暂时查不到该种子的最长等待。</summary>
    public TimeSpan AddVisibilityWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>布局请求（改名、移动）的确认窗口：窗口内不补发同一请求，窗口过期才允许有界补发。</summary>
    public TimeSpan LayoutConfirmationWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>控制请求（暂停、隔离期停止确认）的确认窗口：窗口内不重复发出、不计失败。</summary>
    public TimeSpan ControlConfirmationWindow { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>文件选择变更的确认窗口：发出后等待下载器反映，不因查询轮次重复下发。</summary>
    public TimeSpan SelectionConfirmationWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单个布局计划允许的请求轮数（初次加补发）；耗尽仍未见效即受阻待人工核对。</summary>
    public int MaxLayoutRequestIssues { get; init; } = 3;

    /// <summary>文件选择变更允许的请求轮数（初次加补发）；耗尽仍未生效按文件整理受阻处理。</summary>
    public int MaxSelectionRequestIssues { get; init; } = 3;

    /// <summary>等待校验开始的窗口：recheck 发出后仍未观察到 Checking 时持续快速观察，窗口过期才允许有界补发。</summary>
    public TimeSpan CheckingStartWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>结果稳定窗口：退出校验后文件结果至少跨越此时长且样本一致才下结论。</summary>
    public TimeSpan ResultStabilityWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>结果稳定所需的最少连续一致样本数（含首个样本）。</summary>
    public int ResultStabilitySamples { get; init; } = 3;

    /// <summary>结果稳定观察的最长期限：到期仍不一致按「结果无法确认」受阻，不判定缺失。</summary>
    public TimeSpan ResultStabilityMaxWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>校验中无有效进展的期限：进度持续不变超过该期限先停止确认再退出自动推进。</summary>
    public TimeSpan VerificationNoProgressWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>单轮校验总上限：防进度抖动无限续期；到期先停止确认再退出自动推进。</summary>
    public TimeSpan VerificationRoundLimit { get; init; } = TimeSpan.FromHours(2);

    /// <summary>校验期无法解释运行状态时可发出的暂停请求总次数（初次加有界补发）。</summary>
    public int MaxVerificationPauseAttempts { get; init; } = 3;

    /// <summary>recheck 请求总次数（初次加一次有界补发）；跨轮次累计。</summary>
    public int MaxRecheckIssues { get; init; } = 2;
}
