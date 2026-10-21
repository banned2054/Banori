using Microsoft.EntityFrameworkCore;

namespace Banori.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Sqlite（默认）或 MySql，大小写不敏感。</summary>
    public string Provider { get; init; } = "Sqlite";

    /// <summary>
    ///     MySQL 的服务器版本（如 "8.0.36" 或 "10.11.4-MariaDB"）。
    ///     留空时启动时自动探测（需要数据库可达）；EF 设计时工具使用占位版本，不要求连库。
    /// </summary>
    public string MySqlServerVersion { get; init; } = string.Empty;

    public static DatabaseProvider ParseProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "sqlite" => DatabaseProvider.Sqlite,
        "mysql"                => DatabaseProvider.MySql,
        _ => throw new InvalidOperationException($"未知的 Database:Provider 配置值“{value}”，可选 Sqlite 或 MySql。")
    };

    public static ServerVersion ResolveMySqlServerVersion(string configuredVersion, string connectionString)
    {
        if (!string.IsNullOrWhiteSpace(configuredVersion)) return ServerVersion.Parse(configuredVersion);
        // 设计时（dotnet ef migrations add）不要求 MySQL 在线；占位版本只影响设计期服务，运行时按配置或探测为准。
        if (EF.IsDesignTime) return ServerVersion.Parse("8.0.36");
        return ServerVersion.AutoDetect(connectionString);
    }
}

public enum DatabaseProvider
{
    Sqlite,
    MySql
}
