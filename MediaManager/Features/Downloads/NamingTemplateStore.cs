using MediaManager.Core.Rules;
using MediaManager.Features.Settings;

namespace MediaManager.Features.Downloads;

/// <summary>
///     命名模板的应用设置存取：空白时回退默认模板。
///     模板令牌见 NamingTokens；变更只影响之后新生成的命名预览，已落库的快照不变。
/// </summary>
public sealed class NamingTemplateStore(IApplicationSettingStore settingStore)
{
    public const string Key       = "Downloads.NamingTemplate";
    public const int    MaxLength = 200;

    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        var value = (await settingStore.GetAsync(Key, cancellationToken))?.Trim();
        return string.IsNullOrEmpty(value) ? ReleaseNamePreviewPolicy.DefaultTemplate : value;
    }

    /// <summary>保存模板；空白表示恢复默认（清除存储值）。超长抛 ArgumentException。</summary>
    public async Task SaveAsync(string? template, CancellationToken cancellationToken = default)
    {
        var trimmed = template?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            await settingStore.RemoveAsync(Key, cancellationToken);
            return;
        }

        if (trimmed.Length > MaxLength)
            throw new ArgumentException($"命名模板不能超过 {MaxLength} 个字符。", nameof(template));

        await settingStore.SetAsync(Key, trimmed, cancellationToken);
    }
}
