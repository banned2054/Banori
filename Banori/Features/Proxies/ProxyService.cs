using Banori.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace Banori.Features.Proxies;

public sealed record ProxyView(
    Guid          Id,
    string        Name,
    ProxyProtocol Protocol,
    string        Host,
    int           Port,
    string?       Username,
    bool          HasPassword);

public sealed record SaveProxyRequest(
    Guid?         Id,
    string        Name,
    ProxyProtocol Protocol,
    string        Host,
    int           Port,
    string?       Username);

/// <summary>解密后的代理配置，仅供构建 WebProxy / 代理 URL 使用，不进入视图模型。</summary>
public sealed record ResolvedProxy(
    string        Name,
    ProxyProtocol Protocol,
    string        Host,
    int           Port,
    string?       Username,
    string?       Password);

/// <summary>
///     命名代理配置的读取、校验与保存。密码使用受保护存储；保存时不回传明文，
///     password 参数为 null 或空白表示沿用原值。解析入口对不存在的代理回退 null（直连），
///     设置或订阅中残留的失效引用因此不会阻塞请求。
/// </summary>
public sealed class ProxyService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IDataProtectionProvider                 protectionProvider,
    TimeProvider                            timeProvider)
{
    public const string PasswordPurpose = "Banori.ProxyServers.Password.v1";

    private readonly IDataProtector _protector = protectionProvider.CreateProtector(PasswordPurpose);

    public async Task<IReadOnlyList<ProxyView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ProxyServers.AsNoTracking()
                       .OrderBy(item => item.Name)
                       .Select(item => new ProxyView(item.Id, item.Name, item.Protocol, item.Host, item.Port,
                                                     item.Username, item.PasswordProtected != null))
                       .ToListAsync(cancellationToken);
    }

    public async Task<ProxyView?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ProxyServers.AsNoTracking()
                       .Where(item => item.Id == id)
                       .Select(item => new ProxyView(item.Id, item.Name, item.Protocol, item.Host, item.Port,
                                                     item.Username, item.PasswordProtected != null))
                       .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>保存代理；password 为 null 或空白表示不修改已保存密码（新建时表示无密码）。</summary>
    public async Task SaveAsync(SaveProxyRequest  request, string? password = null,
                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name     = request.Name.Trim();
        var host     = request.Host.Trim();
        var username = request.Username?.Trim();

        if (name.Length is < 1 or > 50) throw new ArgumentException("代理名称长度必须为 1 到 50 个字符。", nameof(request));
        if (host.Length is 0 or > 255) throw new ArgumentException("代理主机不能为空，且不能超过 255 个字符。", nameof(request));
        if (request.Port is < 1 or > 65535) throw new ArgumentException("代理端口必须是 1 到 65535 的整数。", nameof(request));
        if (username?.Length > 100) throw new ArgumentException("代理用户名不能超过 100 个字符。", nameof(request));

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = request.Id is { } id
            ? await db.ProxyServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
           ?? throw new KeyNotFoundException("找不到指定代理。")
            : new ProxyServer
            {
                Id           = Guid.NewGuid(),
                CreatedAtUtc = timeProvider.GetUtcNow()
            };

        var nameConflict = await db.ProxyServers.AsNoTracking()
                                   .AnyAsync(item => item.Id   != entity.Id &&
                                                     item.Name == name, cancellationToken);
        if (nameConflict) throw new ArgumentException("已存在同名代理，请更换名称。", nameof(request));

        entity.Name         = name;
        entity.Protocol     = request.Protocol;
        entity.Host         = host;
        entity.Port         = request.Port;
        entity.Username     = string.IsNullOrEmpty(username) ? null : username;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(password))
            entity.PasswordProtected = _protector.Protect(password);

        // 更新路径的实体已由 SingleOrDefault 跟踪，直接保存即可；只有新建才需要 Add。
        if (request.Id is null) db.ProxyServers.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db     = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var             entity = await db.ProxyServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return;

        // 订阅引用由外键 SetNull 自动置空；设置键中的失效引用在解析时回退直连。
        db.ProxyServers.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WebProxy?> ResolveWebProxyAsync(Guid? proxyId, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveAsync(proxyId, cancellationToken);
        return proxy is null ? null : ProxyEndpoint.ToWebProxy(proxy);
    }

    /// <summary>代理 URL（可携带凭据），供 Telegram/Gotify 发送器的既有解析链路使用。</summary>
    public async Task<string?> ResolveProxyUrlAsync(Guid? proxyId, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveAsync(proxyId, cancellationToken);
        return proxy is null ? null : ProxyEndpoint.ToUrl(proxy);
    }

    private async Task<ResolvedProxy?> ResolveAsync(Guid? proxyId, CancellationToken cancellationToken)
    {
        if (proxyId is null) return null;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ProxyServers.AsNoTracking()
                             .SingleOrDefaultAsync(item => item.Id == proxyId, cancellationToken);
        if (entity is null) return null;

        return new ResolvedProxy(entity.Name, entity.Protocol, entity.Host, entity.Port, entity.Username,
                                 entity.PasswordProtected is null
                                     ? null
                                     : _protector.Unprotect(entity.PasswordProtected));
    }
}
