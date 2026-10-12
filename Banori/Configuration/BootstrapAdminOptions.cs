namespace Banori.Configuration;

public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";

    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}
