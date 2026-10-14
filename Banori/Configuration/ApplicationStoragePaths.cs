using Microsoft.Data.Sqlite;

namespace Banori.Configuration;

internal static class ApplicationStoragePaths
{
    internal static string ResolveConfigDirectory(
        string contentRoot, DatabaseProvider provider, string connectionString)
    {
        var configDirectory      = Path.Combine(contentRoot, "config");
        var keyDirectory         = Path.Combine(configDirectory, "keys");
        var needsDefaultDatabase = false;
        if (provider == DatabaseProvider.Sqlite)
        {
            var connection = new SqliteConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(connection.DataSource)   &&
                connection.DataSource != ":memory:"            &&
                connection.Mode       != SqliteOpenMode.Memory &&
                !connection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var databasePath = Path.GetFullPath(connection.DataSource, contentRoot);
                var defaultPath  = Path.Combine(configDirectory, "banori.db");
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                needsDefaultDatabase =
                    string.Equals(databasePath, defaultPath, comparison) && !File.Exists(defaultPath);
            }
        }

        foreach (var legacyName in new[] { "data", "App_Data" })
        {
            var legacyDirectory = Path.Combine(contentRoot, legacyName);
            var legacyKeys      = Path.Combine(legacyDirectory, "keys");
            var needsKeys       = HasKeys(legacyKeys) && !HasKeys(keyDirectory);
            if (!needsKeys && !(needsDefaultDatabase && File.Exists(Path.Combine(legacyDirectory, "banori.db"))))
                continue;

            throw new InvalidOperationException($"检测到旧数据目录 {legacyDirectory}，但尚未迁移到 {configDirectory}。请停止旧应用并备份，" +
                                                "将数据库及其配套文件和 keys/ 一起迁移；Docker 请将现有卷或宿主机目录的挂载目标改为 /app/config，"   +
                                                "不要删除数据卷。Windows 下不要整体移动 Data 源码目录。为避免创建空库或丢失解密密钥，已停止启动。");
        }

        return configDirectory;
    }

    private static bool HasKeys(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.xml").Any();
}
