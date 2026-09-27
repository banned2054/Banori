using MediaManager.Core.Rules.Selection;

namespace MediaManager.Features.Settings;

/// <summary>规则保存结果：Changed 表示草拟内容相对已保存规则发生变化并已写入存储。</summary>
public sealed record SelectionRuleSaveResult(bool Changed, SelectionRuleConfig Config);

/// <summary>
///     同集择优规则的读取与保存。整份配置序列化为单个设置键持久化；键不存在或内容损坏时
///     回退内置默认规则（损坏不自动修复写入，等待下次保存覆盖）。保存前先规范化草稿，
///     内容未变化时不写存储、版本不变；内容变化时版本在当前值基础上递增。
/// </summary>
public sealed class ManageSelectionRuleSettings(
    IApplicationSettingStore store,
    ILogger<ManageSelectionRuleSettings> logger)
{
    public const string SettingKey = "Selection.RuleConfig";

    /// <summary>读取已保存规则；键不存在或内容损坏时回退内置默认规则。</summary>
    public async Task<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await store.GetAsync(SettingKey, cancellationToken);
        if (json is null) return SelectionRuleConfig.CreateDefault();

        var config = SelectionRuleConfigSerialization.TryDeserialize(json);
        if (config is not null) return config;

        logger.LogWarning("设置键 {Key} 的规则内容损坏，回退内置默认规则。", SettingKey);
        return SelectionRuleConfig.CreateDefault();
    }

    /// <summary>保存草拟规则：规范化后与已保存内容比较，内容不变则短路，变化则版本递增后落库。</summary>
    public async Task<SelectionRuleSaveResult> SaveAsync(SelectionRuleConfig draft,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var draftNormalized = SelectionRuleConfigSerialization.Normalize(draft);
        var current = await GetAsync(cancellationToken);
        if (SelectionRuleConfigSerialization.ContentEquals(draftNormalized, current))
            return new SelectionRuleSaveResult(false, current);

        var newConfig = draftNormalized with { Version = current.Version + 1 };
        await store.SetAsync(SettingKey, SelectionRuleConfigSerialization.Serialize(newConfig),
                             cancellationToken);
        return new SelectionRuleSaveResult(true, newConfig);
    }
}
