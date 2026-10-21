using Banori.Data;
using Banori.Features.Proxies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Proxies;

/// <summary>代理服务 CRUD 回归：校验、密码留空沿用与密文落库、重名拒绝、删除生效。</summary>
public sealed class ProxyServiceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsAndProtectsPassword()
    {
        using var harness = new Harness();

        await harness.Service.SaveAsync(new SaveProxyRequest(null, "家里的代理", ProxyProtocol.Socks5,
                                                             "192.168.1.10", 7890, "alice"), "p@ss:word");

        var proxy = Assert.Single(await harness.Service.ListAsync());
        Assert.Equal("家里的代理", proxy.Name);
        Assert.Equal(ProxyProtocol.Socks5, proxy.Protocol);
        Assert.Equal("192.168.1.10", proxy.Host);
        Assert.Equal(7890, proxy.Port);
        Assert.Equal("alice", proxy.Username);
        Assert.True(proxy.HasPassword);

        // 密码密文落库且可解回原值（含冒号等特殊字符）。
        Assert.NotEqual("p@ss:word", harness.LoadProtectedPassword(proxy.Id));
        Assert.Equal("p@ss:word", harness.UnprotectPassword(proxy.Id));
        var resolved = await harness.Service.ResolveProxyUrlAsync(proxy.Id);
        Assert.Equal("socks5://alice:p%40ss%3Aword@192.168.1.10:7890/", resolved);
    }

    [Fact]
    public async Task Save_WithBlankPassword_KeepsStoredValue()
    {
        using var harness = new Harness();
        await harness.Service.SaveAsync(new SaveProxyRequest(null, "代理A", ProxyProtocol.Http, "10.0.0.1", 8080,
                                                             null), "old-pass");
        var proxy = Assert.Single(await harness.Service.ListAsync());

        await harness.Service.SaveAsync(new SaveProxyRequest(proxy.Id, "代理A", ProxyProtocol.Http, "10.0.0.1", 8080,
                                                             "bob"));

        Assert.Equal("bob", (await harness.Service.FindAsync(proxy.Id))!.Username);
        Assert.True((await harness.Service.FindAsync(proxy.Id))!.HasPassword);
        // URL 中仍解出旧密码：留空表示不修改。
        Assert.Contains(":old-pass@", await harness.Service.ResolveProxyUrlAsync(proxy.Id));
    }

    [Theory]
    [InlineData("", "host", 8080)]
    [InlineData("代理", "", 8080)]
    [InlineData("代理", "host", 0)]
    [InlineData("代理", "host", 70000)]
    public async Task Save_WithInvalidFields_Throws(string name, string host, int port)
    {
        using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        harness.Service.SaveAsync(new SaveProxyRequest(null, name,
                                                                          ProxyProtocol.Http, host, port, null)));
    }

    [Fact]
    public async Task Save_WithDuplicateName_Throws()
    {
        using var harness = new Harness();
        await harness.Service.SaveAsync(new SaveProxyRequest(null, "重名", ProxyProtocol.Http, "10.0.0.1", 8080, null));

        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        harness.Service.SaveAsync(new SaveProxyRequest(null, "重名",
                                                                          ProxyProtocol.Socks5, "10.0.0.2", 1080,
                                                                          null)));
    }

    [Fact]
    public async Task Resolve_WithMissingOrDeletedProxy_ReturnsNull()
    {
        using var harness = new Harness();
        await harness.Service.SaveAsync(new SaveProxyRequest(null, "待删除", ProxyProtocol.Http, "10.0.0.1", 8080,
                                                             null));
        var proxy = Assert.Single(await harness.Service.ListAsync());

        Assert.Null(await harness.Service.ResolveWebProxyAsync(Guid.NewGuid()));

        await harness.Service.DeleteAsync(proxy.Id);
        Assert.Null(await harness.Service.ResolveProxyUrlAsync(proxy.Id));
        Assert.Empty(await harness.Service.ListAsync());
    }

    /// <summary>独立临时目录 + 真实 SQLite 与 Data Protection 的完整环境。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly DirectoryInfo           _directory = Directory.CreateTempSubdirectory("Banori.Proxy.Tests.");
        private readonly IDataProtectionProvider _protection;
        private readonly ServiceProvider         _provider;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory.FullName, "keys")));
            services.AddSingleton(TimeProvider.System);
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                                   options
                                                                      .UseSqlite($"Data Source={Path.Combine(_directory.FullName, "proxy.db")};Pooling=False"));
            services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                          IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            _provider   = services.BuildServiceProvider();
            _protection = _provider.GetRequiredService<IDataProtectionProvider>();
            Service = new ProxyService(_provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                                       _protection, _provider.GetRequiredService<TimeProvider>());
            // 与应用一致的 Identity 模型配置后建库，避免模型缓存顺序依赖。
            using var db = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
            db.Database.EnsureCreatedAsync().GetAwaiter().GetResult();
        }

        public ProxyService Service { get; }

        public void Dispose()
        {
            _provider.Dispose();
            _directory.Delete(true);
        }

        /// <summary>读取落库的密码密文原样（未解密）。</summary>
        public string LoadProtectedPassword(Guid id)
        {
            using var db = CreateDb();
            return db.ProxyServers.AsNoTracking().Single(item => item.Id == id).PasswordProtected!;
        }

        public string UnprotectPassword(Guid id) => _protection.CreateProtector(ProxyService.PasswordPurpose)
                                                               .Unprotect(LoadProtectedPassword(id));

        private ApplicationDbContext CreateDb() =>
            _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
    }
}
