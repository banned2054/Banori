using MediaManager.Core.Rules.Selection;
using MediaManager.Features.Settings;

namespace MediaManager.Features.Subscriptions;

/// <summary>提供当前生效的规则配置快照；同批择优与同批内的所有比较使用同一份配置与版本。</summary>
public interface ISelectionRuleProvider
{
    ValueTask<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>内置默认规则。测试与规则尚未接入设置存储的场景使用；应用注册已改为设置版提供者。</summary>
public sealed class DefaultSelectionRuleProvider : ISelectionRuleProvider
{
    public ValueTask<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SelectionRuleConfig.CreateDefault());
}

/// <summary>从设置存储读取可编辑规则；未保存过或内容损坏时回退内置默认规则。</summary>
public sealed class SettingsSelectionRuleProvider(ManageSelectionRuleSettings ruleSettings) : ISelectionRuleProvider
{
    public async ValueTask<SelectionRuleConfig> GetAsync(CancellationToken cancellationToken = default) =>
        await ruleSettings.GetAsync(cancellationToken);
}
