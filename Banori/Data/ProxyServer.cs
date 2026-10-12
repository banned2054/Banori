namespace Banori.Data;

public enum ProxyProtocol
{
    Http,
    Socks4,
    Socks5
}

/// <summary>命名代理服务器配置；密码使用 Data Protection 受保护存储，读取路径不回传明文。</summary>
public sealed class ProxyServer
{
    public Guid           Id                { get; set; }
    public string         Name              { get; set; } = string.Empty;
    public ProxyProtocol  Protocol          { get; set; }
    public string         Host              { get; set; } = string.Empty;
    public int            Port              { get; set; }
    public string?        Username          { get; set; }
    public string?        PasswordProtected { get; set; }
    public DateTimeOffset CreatedAtUtc      { get; set; }
    public DateTimeOffset UpdatedAtUtc      { get; set; }
}
