using MediaManager.Core.Rules.Selection;
using MediaManager.Features.Settings;
using MediaManager.Tests.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Settings;

/// <summary>
///     规则设置读写的内存存储回归：缺省回退默认规则、损坏回退、保存规范化、
///     版本递增与内容未变化时短路（不重写存储）。
/// </summary>
public sealed class ManageSelectionRuleSettingsTests
{
    [Fact]
    public async Task Get_KeyMissing_ReturnsDefaultRule()
    {
        var (service, store) = CreateService();

        var config = await service.GetAsync();

        Assert.Equal(1, config.Version);
        Assert.Equal(["LoliHouse", "ANi"], config.GroupOrder);
        Assert.False(store.Values.ContainsKey(ManageSelectionRuleSettings.SettingKey));
    }

    [Fact]
    public async Task Get_CorruptedContent_ReturnsDefaultRule()
    {
        var (service, store)                                 = CreateService();
        store.Values[ManageSelectionRuleSettings.SettingKey] = ("{ 不是合法的 JSON", false);

        var config = await service.GetAsync();

        Assert.Equal(1, config.Version);
        Assert.Equal(["LoliHouse", "ANi"], config.GroupOrder);
        // 损坏不自动修复写入：键原样保留，等待下次保存覆盖。
        Assert.Equal("{ 不是合法的 JSON", store.Values[ManageSelectionRuleSettings.SettingKey].Value);
    }

    [Fact]
    public async Task Save_FirstChange_PersistsVersion2()
    {
        var (service, store) = CreateService();
        var draft = SelectionRuleConfig.CreateDefault() with { GroupOrder = ["ANi", "LoliHouse"] };

        var result = await service.SaveAsync(draft);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Config.Version);
        Assert.Equal(["ANi", "LoliHouse"], result.Config.GroupOrder);

        var reloaded = await service.GetAsync();
        Assert.Equal(2, reloaded.Version);
        Assert.True(SelectionRuleConfigSerialization.ContentEquals(draft, reloaded));
    }

    [Fact]
    public async Task Save_UnchangedContent_ReturnsFalseAndKeepsStoredJson()
    {
        var (service, store) = CreateService();
        var draft = SelectionRuleConfig.CreateDefault() with { GroupOrder = ["ANi", "LoliHouse"] };
        Assert.True((await service.SaveAsync(draft)).Changed);
        var jsonBefore = StoredJson(store);
        Assert.Equal(2, SelectionRuleConfigSerialization.TryDeserialize(jsonBefore)!.Version);

        // 与已存内容完全相同。
        var same = await service.SaveAsync(draft);
        Assert.False(same.Changed);
        Assert.Equal(2, same.Config.Version);

        // 仅字幕组大小写不同（匹配不区分大小写，语义相同）。
        var caseOnly   = draft with { GroupOrder = ["ani", "lolihouse"] };
        var caseResult = await service.SaveAsync(caseOnly);
        Assert.False(caseResult.Changed);
        Assert.Equal(2, caseResult.Config.Version);

        // 仅 Version 不同（内容比较忽略版本）。
        var versionOnly   = draft with { Version = 9 };
        var versionResult = await service.SaveAsync(versionOnly);
        Assert.False(versionResult.Changed);
        Assert.Equal(2, versionResult.Config.Version);

        // 未变化时不写存储：JSON 未被重写为更高版本。
        Assert.Equal(jsonBefore, StoredJson(store));
    }

    [Fact]
    public async Task Save_DraftGroupWithWhitespace_NormalizedBeforePersist()
    {
        var (service, _) = CreateService();
        var draft = SelectionRuleConfig.CreateDefault() with { GroupOrder = [" ani ", "LoliHouse"] };

        var result = await service.SaveAsync(draft);

        Assert.True(result.Changed);
        Assert.Equal(["ani", "LoliHouse"], result.Config.GroupOrder);
        var reloaded = await service.GetAsync();
        Assert.Equal(["ani", "LoliHouse"], reloaded.GroupOrder);
    }

    private static (ManageSelectionRuleSettings Service, MemoryApplicationSettingStore Store) CreateService()
    {
        var store   = new MemoryApplicationSettingStore();
        var service = new ManageSelectionRuleSettings(store, NullLogger<ManageSelectionRuleSettings>.Instance);
        return (service, store);
    }

    private static string StoredJson(MemoryApplicationSettingStore store) =>
        store.Values[ManageSelectionRuleSettings.SettingKey].Value;
}
