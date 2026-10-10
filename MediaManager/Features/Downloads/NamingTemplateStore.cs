using MediaManager.Core.Rules;
using MediaManager.Features.Settings;

namespace MediaManager.Features.Downloads;

/// <summary>种子名与种子内文件名模板分别存取；空白时各自回退默认模板。</summary>
public sealed class NamingTemplateStore(IApplicationSettingStore settingStore)
{
    public const string Key = "Downloads.NamingTemplate";
    public const string FileKey = "Downloads.FileNamingTemplate";
    public const int MaxLength = 200;

    public Task<string> GetAsync(CancellationToken cancellationToken = default) =>
        GetAsync(Key, ReleaseNamePreviewPolicy.DefaultTemplate, cancellationToken);

    public Task<string> GetFileTemplateAsync(CancellationToken cancellationToken = default) =>
        GetAsync(FileKey, ReleaseNamePreviewPolicy.DefaultFileTemplate, cancellationToken);

    public Task SaveAsync(string? template, CancellationToken cancellationToken = default) =>
        SaveAsync(Key, template, cancellationToken);

    public Task SaveFileTemplateAsync(string? template, CancellationToken cancellationToken = default) =>
        SaveAsync(FileKey, template, cancellationToken);

    private async Task<string> GetAsync(string key, string defaultTemplate, CancellationToken cancellationToken)
    {
        var value = (await settingStore.GetAsync(key, cancellationToken))?.Trim();
        return string.IsNullOrEmpty(value) ? defaultTemplate : value;
    }

    private async Task SaveAsync(string key, string? template, CancellationToken cancellationToken)
    {
        var trimmed = template?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            await settingStore.RemoveAsync(key, cancellationToken);
            return;
        }

        if (trimmed.Length > MaxLength)
            throw new ArgumentException($"命名模板不能超过 {MaxLength} 个字符。", nameof(template));

        await settingStore.SetAsync(key, trimmed, cancellationToken);
    }
}
