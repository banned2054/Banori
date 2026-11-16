using Banori.Core.Rules;
using Banori.Features.Settings;

namespace Banori.Features.Downloads;

/// <summary>普通种子、合集种子与文件模板分别存取；合集未配置时沿用普通种子模板。</summary>
public sealed class NamingTemplateStore(IApplicationSettingStore settingStore)
{
    public const string Key           = "Downloads.NamingTemplate";
    public const string FileKey       = "Downloads.FileNamingTemplate";
    public const string CollectionKey = "Downloads.CollectionNamingTemplate";
    public const int    MaxLength     = 200;

    public Task<string> GetAsync(CancellationToken cancellationToken = default) =>
        GetAsync(Key, ReleaseNamePreviewPolicy.DefaultTemplate, cancellationToken);

    public Task<string> GetFileTemplateAsync(CancellationToken cancellationToken = default) =>
        GetAsync(FileKey, ReleaseNamePreviewPolicy.DefaultFileTemplate, cancellationToken);

    public async Task<string> GetCollectionTemplateAsync(CancellationToken cancellationToken = default) =>
        await GetAsync(CollectionKey, await GetAsync(cancellationToken), cancellationToken);

    public async Task<string?> GetConfiguredCollectionTemplateAsync(CancellationToken cancellationToken = default) =>
        (await settingStore.GetAsync(CollectionKey, cancellationToken))?.Trim();

    public Task SaveAsync(string? template, CancellationToken cancellationToken = default) =>
        SaveAsync(Key, template, cancellationToken);

    public Task SaveFileTemplateAsync(string? template, CancellationToken cancellationToken = default) =>
        SaveAsync(FileKey, template, cancellationToken);

    public Task SaveCollectionTemplateAsync(string? template, CancellationToken cancellationToken = default) =>
        SaveAsync(CollectionKey, template, cancellationToken);

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
