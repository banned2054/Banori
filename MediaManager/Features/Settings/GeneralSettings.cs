namespace MediaManager.Features.Settings;

public sealed record GeneralSettings(string InstanceName)
{
    public const string DefaultInstanceName = "MediaManager";
}
