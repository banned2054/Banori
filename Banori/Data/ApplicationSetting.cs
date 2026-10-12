namespace Banori.Data;

public sealed class ApplicationSetting
{
    public const int KeyMaxLength = 200;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool IsProtected { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
