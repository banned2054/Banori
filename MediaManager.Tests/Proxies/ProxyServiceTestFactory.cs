using MediaManager.Data;
using MediaManager.Features.Proxies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Proxies;

/// <summary>测试用 ProxyService 构造辅助。</summary>
public static class ProxyServiceTestFactory
{
    /// <summary>
    ///     构建独立 Data Protection 的 ProxyService，SQLite 用 :memory:（不会读库）。
    ///     仅适用于"代理为 null 或已被删除"的回退路径测试；CRUD 测试请用带真实临时库的完整环境。
    /// </summary>
    public static ProxyService CreateEphemeral()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                               options.UseSqlite("Data Source=:memory:;Pooling=False"));
        using var provider = services.BuildServiceProvider();
        return new ProxyService(provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                                provider.GetRequiredService<IDataProtectionProvider>(),
                                provider.GetRequiredService<TimeProvider>());
    }
}
