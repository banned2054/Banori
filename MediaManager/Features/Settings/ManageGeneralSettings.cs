namespace MediaManager.Features.Settings;

public sealed class ManageGeneralSettings(IApplicationSettingStore settingStore)
{
    private const string InstanceNameKey = "General.InstanceName";

    public async Task<GeneralSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var instanceName = await settingStore.GetAsync(InstanceNameKey, cancellationToken);
        return new GeneralSettings(string.IsNullOrWhiteSpace(instanceName)
                                       ? GeneralSettings.DefaultInstanceName
                                       : instanceName);
    }

    public Task SaveAsync(GeneralSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.InstanceName);

        var instanceName = settings.InstanceName.Trim();
        if (instanceName.Length > 80) throw new ArgumentException("实例名称长度必须为 1 到 80 个字符。", nameof(settings));

        return settingStore.SetAsync(InstanceNameKey, instanceName, cancellationToken);
    }
}
