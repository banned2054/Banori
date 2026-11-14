using Banori.Data;
using Banori.Features.Anime;
using Banori.Features.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Banori.E2E.Tests;

/// <summary>
///     真机验收宿主：以 WebApplicationFactory 承载完整 Banori Program，环境变量注入每场景
///     独立 SQLite（绝对路径）与独立管理员凭据。
///     入口绑定：WAF 只用 typeof(TEntryPoint).Assembly 定位入口程序集与 ContentRoot，
///     Banori 的 Program 虽是 internal，但同一程序集里任何 public 类型都能当泛型参数——
///     这里用 ManageQbittorrentSettings，Banori 源码零改动（无需 public partial Program）。
///     WAF 的延迟入口路径会完整执行 Program.Main（builder.Build 之后的 InitApplication 与
///     ApplicationInitializer.InitializeAsync 都会跑：迁移 + 首管理员自动创建），
///     DownloadReconcileWorker 等三个 BackgroundService 也真实启动（ListHostedServices 可核验）。
/// </summary>
public sealed class AcceptanceHost : WebApplicationFactory<ManageQbittorrentSettings>
{
    private readonly string _databasePath;

    public AcceptanceHost(string scenarioDirectory, string username = "e2e-admin",
                          string password = "E2e-Password-01")
    {
        ScenarioDirectory = scenarioDirectory;
        _databasePath     = Path.Combine(scenarioDirectory, "banori-e2e.db");
        Username          = username;
        Password          = password;
        Directory.CreateDirectory(scenarioDirectory);

        // 环境变量注入（真机同形状，双下划线分层）；构造在 host 惰性创建前完成。
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", $"Data Source={_databasePath}");
        Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
        Environment.SetEnvironmentVariable("BANORI_USERNAME", Username);
        Environment.SetEnvironmentVariable("BANORI_PASSWORD", Password);
    }

    public string ScenarioDirectory { get; }
    public string Username { get; }
    public string Password { get; }

    /// <summary>Production 初始化受阻时的回退记录（如实报告侧行为差异；冒烟断言必须为空）。</summary>
    public string? EnvironmentFallback { get; private set; }

    /// <summary>宿主侧下载根（volume mount 的场景 downloads/ 目录，核验文件内容用）。</summary>
    public string DownloadRoot => Path.Combine(ScenarioDirectory, "downloads");

    /// <summary>qB 容器内视角的下载根——Banori 全程经 qB API 操作文件，SavePath 必须用它。</summary>
    public string QbSavePath => "/downloads";

    /// <summary>qB WebUI 基地址（场景 fixture 经环境变量注入；默认冒烟端口）。</summary>
    public string QbBaseUrl => Environment.GetEnvironmentVariable("E2E_QB_BASE_URL") ?? "http://127.0.0.1:18081";

    /// <summary>隔离 qB WebUI 凭据（与 qb_container.py up 传入的一致）。</summary>
    public string QbUsername => Environment.GetEnvironmentVariable("E2E_QB_USERNAME") ?? "admin";

    public string QbPassword => Environment.GetEnvironmentVariable("E2E_QB_PASSWORD") ?? "e2e-password";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Information));
    }

    /// <summary>首次触达 host（WAF 惰性创建，Main 全量执行：迁移 + 首管理员 + 三个 worker）。</summary>
    public void Initialize() => _ = Services;

    /// <summary>确认三个真实 BackgroundService 已在 host 内运行（真机对账链路存在的前提）。</summary>
    public IReadOnlyList<string> ListHostedServices() =>
        Services.GetServices<IHostedService>()
                .Select(service => service.GetType().Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

    /// <summary>
    ///     重置环境：停 BackgroundService（释放 SQLite 连接）→ 清 SQLite 连接池 → 删库。
    ///     WAF minimal hosting 下 Server.Host（IWebHost）已废弃且直接抛错，无法经它
    ///     StopAsync；改为逆序手动停止全部 IHostedService（与 host.StopAsync 的停止顺序一致），
    ///     host 自身随调用方的 DisposeAsync（WAF Dispose）释放。Microsoft.Data.Sqlite 的连接池
    ///     会持有打开的连接句柄，Windows 上不清池就删不掉库文件。
    /// </summary>
    public async Task ResetAsync()
    {
        var hostedServices = Services.GetServices<IHostedService>().ToList();
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var service in Enumerable.Reverse(hostedServices))
        {
            try
            {
                await service.StopAsync(stopTimeout.Token);
            }
            catch (Exception)
            {
                // 尽力停止：单服务停止失败不阻断删库（测试收尾，库会被整个删除）。
            }
        }

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
        Directory.CreateDirectory(DownloadRoot);
    }

    /// <summary>从 host 根 scope 解析服务（每调用自建 scope，避免与 BackgroundService 抢 scope）。</summary>
    public async Task<T> ResolveAsync<T>() where T : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    // —— 环境准备 API ——

    /// <summary>
    ///     写 qB 设置（与设置页同一入口 ManageQbittorrentSettings.SaveAsync：键名一致、
    ///     密码经 DataProtection 保护存储）。BaseUrl 指向隔离 qB WebUI，SavePath 用
    ///     qB 容器内视角路径 /downloads（Banori 经 qB API 操作文件，宿主路径只在
    ///     volume mount 侧核验内容时使用）。
    /// </summary>
    public async Task WriteQbSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope    = Services.CreateAsyncScope();
        var             manager = scope.ServiceProvider.GetRequiredService<ManageQbittorrentSettings>();
        await manager.SaveAsync(new QbittorrentSettings(QbBaseUrl, QbUsername, QbSavePath, true, null),
                                QbPassword, cancellationToken);
    }

    /// <summary>
    ///     预种 Bangumi Subject 与标题映射，使离线环境的上传登记可经标题映射命中绑定。
    ///     命中规则（DownloadIntakeService.ResolveUploadedSubjectBindingAsync :1081-1129）：
    ///     先按 ByrTorrent 的 ParseBatch 解析 torrent 内全部文件路径提取标题候选，
    ///     再回退 info.name 解析结果；每个候选 Title 做 TitleNormalizer.Normalize
    ///     （Trim+大写+去全部空白）后与 SubjectTitleMappings.NormalizedTitle 精确匹配，
    ///     join MediaSubjects 必须有行。种子命名保证文件名可解析出标题
    ///     （如「[字幕组] 标题 - 01 [1080p].mkv」），映射键 = Normalize("标题")。
    /// </summary>
    public async Task SeedSubjectAsync(int subjectId, string title, string nameCn,
                                       CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<SubjectMetadataCache>();
        await cache.SaveAsync(new(subjectId, title, nameCn, "TV", "2026-10-01", null,
                                  DateTimeOffset.UtcNow), cancellationToken);
        await cache.SaveTitleMappingAsync(NormalizeTitle(title), subjectId, cancellationToken);
    }

    /// <summary>标题归一化（与 Banori.Core.Rules.TitleNormalizer 同算法）：Trim+大写+去空白。</summary>
    internal static string NormalizeTitle(string title) =>
        string.Concat(title.Trim().ToUpperInvariant().Where(character => !char.IsWhiteSpace(character)));
}
