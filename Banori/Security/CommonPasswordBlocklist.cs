namespace Banori.Security;

/// <summary>
///     常见密码黑名单：内嵌资源 Security/common-passwords.txt（聚合自公开泄露语料，见文件头声明），
///     大小写不敏感匹配。仅用于注册/改密时拒绝过于常见的密码，不参与登录校验。
/// </summary>
internal sealed class CommonPasswordBlocklist
{
    private const string ResourceName = "Banori.Security.common-passwords.txt";

    private readonly HashSet<string> _entries;

    public CommonPasswordBlocklist()
    {
        using var stream = typeof(CommonPasswordBlocklist).Assembly.GetManifestResourceStream(ResourceName)
                        ?? throw new InvalidOperationException($"未找到内嵌资源 {ResourceName}。");
        using var reader = new StreamReader(stream);

        _entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            _entries.Add(line);
        }
    }

    public bool IsBlocked(string password) => _entries.Contains(password);
}
