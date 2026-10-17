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
using Banori.Security;
using Banori.Workers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Threading.RateLimiting;

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

        builder.Services.ConfigureOptions<TrustedProxyOptionsSetup>();

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
               .AddDefaultTokenProviders()
               .AddPasswordValidator<CommonPasswordValidator>();

        // 基础被动防线：最低 8 位 + 常见泄露密码黑名单，取代大小写/数字/符号组合规则（存量部署不受影响）；
        // stamp 间隔决定改密后旧会话的最迟失效时间。
        builder.Services.Configure<IdentityOptions>(options =>
        {
            options.Password.RequiredLength         = 8;
            options.Password.RequireUppercase       = false;
            options.Password.RequireLowercase       = false;
            options.Password.RequireDigit           = false;
            options.Password.RequireNonAlphanumeric = false;
        });
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval =
                                                                      TimeSpan.FromMinutes(10));
        builder.Services.AddSingleton<CommonPasswordBlocklist>();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)BaselineRateLimiting.Window.TotalSeconds).ToString();
                await context.HttpContext.Response.WriteAsync("请求过于频繁，请稍后再试。", cancellationToken);
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (BaselineRateLimiting.IsAuthenticationSubmit(context.Request.Path, context.Request.Method))
                    return RateLimitPartition.GetFixedWindowLimiter(BaselineRateLimiting.AuthPartitionKey(context), _ =>
                                                                        new FixedWindowRateLimiterOptions
                                                                        {
                                                                            PermitLimit = BaselineRateLimiting
                                                                               .AuthPermitLimit,
                                                                            Window     = BaselineRateLimiting.Window,
                                                                            QueueLimit = 0
                                                                        });
                if (BaselineRateLimiting.IsHookCallback(context.Request.Path))
                    return RateLimitPartition.GetFixedWindowLimiter(BaselineRateLimiting.HookPartitionKey(context), _ =>
                                                                        new FixedWindowRateLimiterOptions
                                                                        {
                                                                            PermitLimit = BaselineRateLimiting
                                                                               .HookPermitLimit,
                                                                            Window     = BaselineRateLimiting.Window,
                                                                            QueueLimit = 0
                                                                        });
                return RateLimitPartition.GetNoLimiter("default");
            });
        });

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
        var userAgent = string.IsNullOrWhiteSpace(builder.Configuration["UserAgent"])
            ? "Banori"
            : builder.Configuration["UserAgent"]!;
        builder.Services.AddHttpClient("FeedSource", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        builder.Services.AddHttpClient("Bangumi");
        builder.Services.AddHttpClient("TorrentDownload", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
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
        // 在 HSTS、HTTPS 重定向和 IP 限流之前恢复可信代理转发的信息。
        app.UseForwardedHeaders();

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
        app.UseRateLimiter();
        app.UseAntiforgery();
        app.MapStaticAssets();
        // qBittorrent“运行外部程序”钩子：凭令牌回调，立即唤醒对账轮（15 秒轮询仍为兜底）。
        // POST 用 X-Hook-Token 头承载令牌以免落入反向代理访问日志；GET 保留 query 兼容既有命令模板。
        app.MapGet("/hooks/qbittorrent",
                   async (QbittorrentHookService hook, HttpRequest request, CancellationToken cancellationToken) =>
                       await HandleQbittorrentHookAsync(hook, request, false, cancellationToken));
        // 钩子自带令牌鉴权，qB 侧 curl 回调无法携带防伪令牌，必须显式豁免。
        app.MapPost("/hooks/qbittorrent",
                    async (QbittorrentHookService hook, HttpRequest request, CancellationToken cancellationToken) =>
                        await HandleQbittorrentHookAsync(hook, request, true, cancellationToken))
           .DisableAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapAdditionalIdentityEndpoints();
    }

    private static async Task<IResult> HandleQbittorrentHookAsync(
        QbittorrentHookService hook, HttpRequest request, bool preferHeaderToken, CancellationToken cancellationToken)
    {
        var token = preferHeaderToken
            ? request.Headers["X-Hook-Token"].FirstOrDefault() ?? request.Query["token"].FirstOrDefault()
            : request.Query["token"].FirstOrDefault();

        var result = await hook.HandleAsync(token,
                                            request.Query["hash"].FirstOrDefault(),
                                            request.Query["event"].FirstOrDefault(),
                                            cancellationToken);
        // 错误响应必须带响应体：空主体的 4xx/5xx 会被状态码页拿去以原方法重执行 /not-found，
        // POST 会撞上组件页的防伪校验，403 被替换成 400。
        return result switch
        {
            QbittorrentHookResult.Accepted    => Results.Ok(),
            QbittorrentHookResult.InvalidHash => Results.BadRequest("hash 参数不是合法的 v1 info-hash。"),
            _                                 => Results.Text("令牌无效。", statusCode : StatusCodes.Status403Forbidden)
        };
    }
}
