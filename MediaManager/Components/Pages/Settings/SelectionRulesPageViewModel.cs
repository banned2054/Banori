using Banned.AniParser.Models.Enums;
using MediaManager.Core.Rules.Selection;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using Microsoft.Extensions.Hosting;

namespace MediaManager.Components.Pages.Settings;

/// <summary>择优规则的编辑模型，字段与 SelectionRuleConfig 一一对应，全部可写。</summary>
public sealed class SelectionRuleDraft
{
    public SelectionMode Mode { get; set; }

    public List<string> GroupOrder { get; set; } = [];

    public List<EnumLanguage> LanguageOrder { get; set; } = [];

    public SelectionDimensionOrder DimensionOrder { get; set; }

    public List<CombinedRuleDraftEntry> CombinedEntries { get; set; } = [];

    public LanguageMatchMode LanguageMatch { get; set; }

    public UnmatchedPolicy Unmatched { get; set; }

    public List<EnumResolution> AllowedResolutions { get; set; } = [];

    /// <summary>独立模式：底部输入框待添加的字幕组名称。</summary>
    public string? NewGroupName { get; set; }

    /// <summary>独立模式：底部下拉待添加的语言。</summary>
    public EnumLanguage? NewLanguage { get; set; }

    /// <summary>组合模式：底部输入框待添加条件的字幕组；留空表示通配。</summary>
    public string? NewEntryGroup { get; set; }

    /// <summary>组合模式：底部下拉待添加条件的语言；留空表示通配。</summary>
    public EnumLanguage? NewEntryLanguage { get; set; }
}

/// <summary>组合模式条件行的编辑模型；Group 或 Language 为 null 表示该维度通配。</summary>
public sealed record CombinedRuleDraftEntry(string? Group, EnumLanguage? Language);

/// <summary>
///     择优规则页状态：规则草稿、当前版本、比较预览与保存操作。
///     保存成功且内容有变化时触发同集组批量重选。随页面组件创建和释放。
/// </summary>
public sealed class SelectionRulesPageViewModel(
    ManageSelectionRuleSettings          ruleSettings,
    CandidateSelectionService            selectionService,
    IHostApplicationLifetime             hostLifetime,
    ILogger<SelectionRulesPageViewModel> logger)
{
    public SelectionRuleDraft Draft { get; } = new();

    public bool Loaded        { get; private set; }
    public bool Saving        { get; private set; }
    public bool HasError      { get; private set; }
    public string? StatusMessage { get; private set; }

    public int CurrentVersion { get; private set; }

    public SelectionPreviewResult? Preview { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await ruleSettings.GetAsync(cancellationToken);
            ApplyConfig(config);
            Loaded        = true;
            HasError      = false;
            StatusMessage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "加载择优规则失败。");
            HasError      = true;
            StatusMessage = "加载择优规则失败，请查看服务日志。";
        }
    }

    /// <summary>保存当前草稿；内容有变化时按保存后的新规则重选全部同集组。</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        HasError      = false;
        StatusMessage = null;
        var config = BuildConfig();
        if (config is null) return;

        Saving = true;
        try
        {
            var result = await ruleSettings.SaveAsync(config, cancellationToken);
            ApplyConfig(result.Config);
            if (result.Changed)
            {
                // 重选挂应用停机令牌而非页面令牌：规则已落库，页面离开不应中断重选留下
                // 半新半旧的选择结果；只有应用停机能打断，中断部分由恢复扫描按新版本补齐。
                try
                {
                    var reselected = await selectionService.ReselectAllGroupsAsync(hostLifetime.ApplicationStopping);
                    StatusMessage = $"规则已保存为版本 {result.Config.Version}，已重选 {reselected} 个同集组";
                }
                catch (OperationCanceledException) when (hostLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    StatusMessage =
                        $"规则已保存为版本 {result.Config.Version}，重选因应用停机中断，已处理部分生效，剩余由恢复扫描补齐。";
                }
            }
            else
            {
                StatusMessage = $"规则内容未变化，版本保持 {result.Config.Version}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存择优规则失败。");
            HasError      = true;
            StatusMessage = "保存失败，请查看服务日志。";
        }
        finally
        {
            Saving = false;
        }
    }

    /// <summary>用当前草稿计算比较预览：不落库，只展示优胜将变化的同集组。</summary>
    public async Task RunPreviewAsync(CancellationToken cancellationToken = default)
    {
        HasError      = false;
        StatusMessage = null;
        var config = BuildConfig();
        if (config is null) return;

        try
        {
            Preview = await selectionService.PreviewRuleChangeAsync(config, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "计算择优规则比较预览失败。");
            HasError      = true;
            StatusMessage = "预览失败，请查看服务日志。";
        }
    }

    public void AddGroup()
    {
        var name = Draft.NewGroupName?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        Draft.GroupOrder.Add(name);
        Draft.NewGroupName = null;
    }

    public void RemoveGroup(int index)
    {
        if (IsValidIndex(Draft.GroupOrder, index)) Draft.GroupOrder.RemoveAt(index);
    }

    public void MoveGroup(int index, int offset)
    {
        Move(Draft.GroupOrder, index, offset);
    }

    public void AddLanguage()
    {
        if (Draft.NewLanguage is not { } language) return;

        Draft.LanguageOrder.Add(language);
        Draft.NewLanguage = null;
    }

    public void RemoveLanguage(int index)
    {
        if (IsValidIndex(Draft.LanguageOrder, index)) Draft.LanguageOrder.RemoveAt(index);
    }

    public void MoveLanguage(int index, int offset)
    {
        Move(Draft.LanguageOrder, index, offset);
    }

    public void AddCombinedEntry()
    {
        var group = Draft.NewEntryGroup?.Trim();
        if (string.IsNullOrEmpty(group)) group = null;

        Draft.CombinedEntries.Add(new CombinedRuleDraftEntry(group, Draft.NewEntryLanguage));
        Draft.NewEntryGroup    = null;
        Draft.NewEntryLanguage = null;
    }

    public void RemoveCombinedEntry(int index)
    {
        if (IsValidIndex(Draft.CombinedEntries, index)) Draft.CombinedEntries.RemoveAt(index);
    }

    public void MoveCombinedEntry(int index, int offset)
    {
        Move(Draft.CombinedEntries, index, offset);
    }

    /// <summary>切换分辨率是否参与硬性过滤；已选中则移除，未选中则加入。</summary>
    public void ToggleResolution(EnumResolution resolution)
    {
        if (!Draft.AllowedResolutions.Remove(resolution)) Draft.AllowedResolutions.Add(resolution);
    }

    /// <summary>把已保存配置同步进草稿；保存成功后也用它刷新，保证草稿与落库值一致。</summary>
    private void ApplyConfig(SelectionRuleConfig config)
    {
        Draft.Mode              = config.Mode;
        Draft.GroupOrder        = [.. config.GroupOrder];
        Draft.LanguageOrder     = [.. config.LanguageOrder];
        Draft.DimensionOrder    = config.DimensionOrder;
        Draft.CombinedEntries   = config.CombinedEntries
                                       .Select(entry => new CombinedRuleDraftEntry(entry.Group, entry.Language))
                                       .ToList();
        Draft.LanguageMatch       = config.LanguageMatch;
        Draft.Unmatched           = config.Unmatched;
        Draft.AllowedResolutions  = [.. config.AllowedResolutions];
        Draft.NewGroupName        = null;
        Draft.NewLanguage         = null;
        Draft.NewEntryGroup       = null;
        Draft.NewEntryLanguage    = null;
        CurrentVersion            = config.Version;
        Preview                   = null;
    }

    /// <summary>把草稿转换为规则配置；拒绝空白字幕组名与组合模式空条件，未列出项走未命中策略是合法语义。</summary>
    private SelectionRuleConfig? BuildConfig()
    {
        if (Draft.GroupOrder.Any(string.IsNullOrWhiteSpace))
        {
            HasError      = true;
            StatusMessage = "字幕组名称不能为空白，请删除空白项后重试。";
            return null;
        }

        if (Draft.Mode == SelectionMode.Combined && Draft.CombinedEntries.Count == 0)
        {
            HasError      = true;
            StatusMessage = "组合模式必须保留至少一个条件（可添加通配兜底），否则所有候选都无法择优。";
            return null;
        }

        return new SelectionRuleConfig
        {
            Mode               = Draft.Mode,
            GroupOrder         = Draft.GroupOrder.Select(group => group.Trim()).ToList(),
            LanguageOrder      = [.. Draft.LanguageOrder],
            DimensionOrder     = Draft.DimensionOrder,
            CombinedEntries    = Draft.CombinedEntries
                                     .Select(entry => new CombinedRuleEntry(
                                                 string.IsNullOrWhiteSpace(entry.Group)
                                                     ? null
                                                     : entry.Group.Trim(),
                                                 entry.Language))
                                     .ToList(),
            LanguageMatch      = Draft.LanguageMatch,
            Unmatched          = Draft.Unmatched,
            AllowedResolutions = [.. Draft.AllowedResolutions],
            Version            = CurrentVersion
        };
    }

    private static bool IsValidIndex<T>(List<T> list, int index)
    {
        return index >= 0 && index < list.Count;
    }

    private static void Move<T>(List<T> list, int index, int offset)
    {
        var target = index + offset;
        if (!IsValidIndex(list, index) || !IsValidIndex(list, target)) return;

        (list[index], list[target]) = (list[target], list[index]);
    }
}

/// <summary>择优规则页的枚举中文显示；下拉列表数据源用 Enum.GetValues&lt;T&gt;() 全量枚举。</summary>
public static class SelectionRuleDisplay
{
    private static readonly Dictionary<EnumLanguage, string> LanguageNames = new()
    {
        [EnumLanguage.Jp]      = "日语",
        [EnumLanguage.Sc]      = "简中",
        [EnumLanguage.Tc]      = "繁中",
        [EnumLanguage.Eng]     = "英语",
        [EnumLanguage.JpSc]    = "简日",
        [EnumLanguage.JpTc]    = "繁日",
        [EnumLanguage.ScTc]    = "简繁",
        [EnumLanguage.EngSc]   = "英简",
        [EnumLanguage.EngTc]   = "英繁",
        [EnumLanguage.JpScTc]  = "简繁日",
        [EnumLanguage.EngScTc] = "英简繁"
    };

    private static readonly Dictionary<EnumResolution, string> ResolutionNames = new()
    {
        [EnumResolution.R480P]   = "480P",
        [EnumResolution.R720P]   = "720P",
        [EnumResolution.R1080P]  = "1080P",
        [EnumResolution.R2K]     = "2K",
        [EnumResolution.R4K]     = "4K",
        [EnumResolution.Unknown] = "未知"
    };

    /// <summary>语言下拉数据源；None 不是可配置的语言组合，不作为选项提供。</summary>
    public static readonly EnumLanguage[] LanguageChoices =
        Enum.GetValues<EnumLanguage>().Where(language => language != EnumLanguage.None).ToArray();

    public static string LanguageText(EnumLanguage language)
    {
        return LanguageNames.GetValueOrDefault(language, language.ToString());
    }

    public static string ResolutionText(EnumResolution resolution)
    {
        return ResolutionNames.GetValueOrDefault(resolution, resolution.ToString());
    }
}
