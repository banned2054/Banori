using Banned.AniParser;
using Banori.Components;
using Banori.Components.Account;
using Banori.Configuration;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Subjects;
using Banori.Data;
using Banori.Data.Migrations;
using Banori.Features.Anime;
using Banori.Features.Dashboard;
using Banori.Features.Downloads;
using Banori.Features.FeedItems;
using Banori.Features.Notifications;
using Banori.Features.Proxies;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Initialization;
using Banori.Integrations;
using Banori.Integrations.Bangumi;
using Banori.Integrations.Mikan;
using Banori.Integrations.Notifications;
using Banori.Integrations.Qbittorrent;
using Banori.Workers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Banori;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        InitBuilder(builder);

        var app = builder.Build();
        InitApplication(app);
        await ApplicationInitializer.InitializeAsync(app);
        await app.RunAsync();
    }

    private static void InitBuilder(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        builder.Services.AddRazorComponents()
               .AddInteractiveServerComponents();

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        builder.Services.AddAuthentication(options =>
                {
                    options.DefaultScheme       = IdentityConstants.ApplicationScheme;
                    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
                })
               .AddIdentityCookies();

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ??
                               throw new InvalidOperationException("未配置连接字符串 DefaultConnection。");

        var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ??
                              new DatabaseOptions();
        var databaseProvider = DatabaseOptions.ParseProvider(databaseOptions.Provider);

        var dataDirectory =
            ApplicationStoragePaths.ResolveConfigDirectory(builder.Environment.ContentRootPath, databaseProvider,
                                                           connectionString);
        var keyDirectory = Path.Combine(dataDirectory, "keys");
        EnsureDataStorageWritable(dataDirectory, keyDirectory);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

        builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
        {
            options.ReplaceService<IMigrationsAssembly, ProviderAwareMigrationsAssembly>();
            switch (databaseProvider)
            {
                case DatabaseProvider.MySql :
                    options.UseMySql(connectionString,
                                     DatabaseOptions.ResolveMySqlServerVersion(databaseOptions.MySqlServerVersion,
                                                                               connectionString));
                    break;
                case DatabaseProvider.Sqlite :
                    var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);
                    if (!string.IsNullOrEmpty(sqliteConnection.DataSource)   &&
                        sqliteConnection.DataSource != ":memory:"            &&
                        sqliteConnection.Mode       != SqliteOpenMode.Memory &&
                        !sqliteConnection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        sqliteConnection.DataSource =
                            Path.GetFullPath(sqliteConnection.DataSource, builder.Environment.ContentRootPath);

                    options.UseSqlite(sqliteConnection.ConnectionString);
                    break;
                default :
                    throw new InvalidOperationException($"未支持的数据库 provider：{databaseProvider}。");
            }
        });
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.SignIn.RequireConfirmedAccount = true;
                    options.Stores.SchemaVersion           = IdentitySchemaVersions.Version3;
                })
               .AddEntityFrameworkStores<ApplicationDbContext>()
               .AddSignInManager()
               .AddErrorDescriber<ChineseIdentityErrorDescriber>()
               .AddDefaultTokenProviders();

        builder.Services.Configure<BootstrapAdminOptions>(options =>
        {
            options.Username = builder.Configuration["BANORI_USERNAME"] ?? string.Empty;
            options.Password = builder.Configuration["BANORI_PASSWORD"] ?? string.Empty;
        });
        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RefreshPulse>();
        builder.Services.AddScoped<IApplicationSettingStore, EfApplicationSettingStore>();
        builder.Services.AddScoped<NamingTemplateStore>();
        builder.Services.AddScoped<DownloadDirectorySettingsStore>();
        builder.Services.AddScoped<DownloadSubmissionPathValidator>();
        builder.Services.AddSingleton<DownloadFileClassificationPolicy>();
        builder.Services.AddScoped<ManageQbittorrentSettings>();
        builder.Services.AddScoped<ManageSelectionRuleSettings>();
        builder.Services.AddScoped<ManageTelegramSettings>();
        builder.Services.AddScoped<ManageGotifySettings>();
        builder.Services.AddSingleton<ITelegramSender>(new TelegramSender());
        builder.Services.AddSingleton<IGotifySender, GotifySender>();
        builder.Services.AddScoped<NotificationEnqueuer>();
        builder.Services.AddScoped<NotificationDeliveryService>();
        builder.Services.AddSingleton<AniParser>();
        builder.Services.AddSingleton<CandidateEligibilityPolicy>();
        builder.Services.AddSingleton<CandidateRankingPolicy>();
        builder.Services.AddScoped<ISelectionRuleProvider, SettingsSelectionRuleProvider>();
        builder.Services.AddSingleton<SubjectEpisodeCorrectionPolicy>();
        builder.Services.AddSingleton<ReleaseNamePreviewPolicy>();
        builder.Services.AddSingleton<RefreshEnqueueGate>();
        builder.Services.AddSingleton<SelectionGroupGate>();
        builder.Services.AddScoped<SubscriptionService>();
        builder.Services.AddScoped<IRefreshWorkConsumer, MikanRefreshService>();
        builder.Services.AddScoped<FeedItemEvaluator>();
        builder.Services.AddScoped<CandidateSelectionService>();
        builder.Services.AddScoped<FeedItemQueries>();
        builder.Services.AddScoped<DashboardQueries>();
        builder.Services.AddScoped<AnimeQueries>();
        builder.Services.AddSingleton<DownloadCoordinationGate>();
        builder.Services.AddScoped<DownloadService>();
        builder.Services.AddScoped<DownloadFileService>();
        builder.Services.AddScoped<DownloadIntakeService>();
        builder.Services.AddScoped<DownloadTaskQueries>();
        builder.Services.AddScoped<IQbittorrentSessionFactory, QbittorrentSessionFactory>();
        builder.Services.AddSingleton<ProxyService>();
        builder.Services.AddSingleton<IProxiedHttpClientSource, ProxiedHttpClientSource>();
        builder.Services.AddSingleton<ReconcilePulse>();
        builder.Services.AddScoped<QbittorrentHookService>();
        builder.Services.AddSingleton<ITorrentFileSource, HttpTorrentFileSource>();
        builder.Services.AddSingleton<IFeedSourceReader, RssFeedSourceReader>();
        builder.Services.AddSingleton<ISubjectLinkResolver, MikanSubjectLinkResolver>();
        builder.Services.AddSingleton<ISubjectMetadataProvider, BangumiSubjectMetadataProvider>();
        builder.Services.AddScoped<SubjectMetadataCache>();
        builder.Services.AddHttpClient("FeedSource", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Banori/0.1.1");
        });
        builder.Services.AddHttpClient("Bangumi");
        builder.Services.AddHttpClient("TorrentDownload", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Banori/0.1.1");
        });
        builder.Services.AddHostedService<SubscriptionRefreshWorker>();
        builder.Services.AddHostedService<DownloadReconcileWorker>();
        builder.Services.AddHostedService<NotificationDeliveryWorker>();
    }

    // 容器内非 root 进程无法 chown 宿主机目录，bind mount 属主不匹配时只能把原因和修法报清楚，
    // 避免用户只看到一串 UnauthorizedAccessException 堆栈。
    private static void EnsureDataStorageWritable(string dataDirectory, string keyDirectory)
    {
        try
        {
            Directory.CreateDirectory(keyDirectory);
            var probePath = Path.Combine(dataDirectory, $"write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            Console.Error
                   .WriteLine($"[FATAL] 无法写入数据目录 {dataDirectory}，当前进程身份 {DescribeProcessIdentity()}，原始错误：{ex.Message}");
            Console.Error.WriteLine("最常见原因：bind mount 的宿主机目录属主与容器运行用户不一致（Docker daemon 自动创建的缺失目录属主是 root）。");
            Console.Error.WriteLine("修复方式（任选其一）：");
            Console.Error
                   .WriteLine("  1. 在宿主机手动创建目录并授权：mkdir -p config && chown -R <uid>:<gid> config，uid:gid 用上方进程身份");
            Console.Error
                   .WriteLine("  2. 使用本项目镜像时移除 compose 的 user:，在 environment 中设置 PUID/PGID 为宿主机账号的 uid/gid；入口脚本会递归修改 /app/config 属主，请只挂载 Banori 专用数据目录");
            Console.Error.WriteLine("  3. 或设置非零 user: 为有权读写宿主机 config 目录的 uid:gid；此时入口脚本不会修正属主，PUID/PGID 也不会生效");
            Console.Error
                   .WriteLine("  4. rootless Docker 使用本项目镜像时移除 user:，设置环境变量 PUID=0、PGID=0（容器内 UID 0 映射到宿主机当前用户）；仅设置 user: \"0:0\" 仍会降权到默认 UID 1654");
            Console.Error.WriteLine("  5. 或改用命名卷并在 compose 中声明；已有卷仍需确保数据可写");
            throw;
        }
    }

    private static string DescribeProcessIdentity()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return Environment.UserName;

            var uid = -1;
            var gid = -1;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2)
                    continue;
                if (fields[0] == "Uid:")
                    int.TryParse(fields[1], out uid);
                else if (fields[0] == "Gid:")
                    int.TryParse(fields[1], out gid);
            }

            if (uid < 0)
                return Environment.UserName;
            var gidText = gid >= 0 ? $" gid={gid}" : string.Empty;
            return $"uid={uid}{gidText}";
        }
        catch
        {
            return Environment.UserName;
        }
    }

    private static void InitApplication(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error", true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages : true);
        app.UseHttpsRedirection();
        app.UseAntiforgery();
        app.MapStaticAssets();
        // qBittorrent“运行外部程序”钩子：凭令牌回调，立即唤醒对账轮（15 秒轮询仍为兜底）。
        app.MapGet("/hooks/qbittorrent",
                   async (QbittorrentHookService hook, HttpRequest request, CancellationToken cancellationToken) =>
                   {
                       var result = await hook.HandleAsync(request.Query["token"].FirstOrDefault(),
                                                           request.Query["hash"].FirstOrDefault(),
                                                           request.Query["event"].FirstOrDefault(),
                                                           cancellationToken);
                       return result switch
                       {
                           QbittorrentHookResult.Accepted    => Results.Ok(),
                           QbittorrentHookResult.InvalidHash => Results.BadRequest(),
                           _                                 => Results.StatusCode(StatusCodes.Status403Forbidden)
                       };
                   });
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapAdditionalIdentityEndpoints();
    }
}
