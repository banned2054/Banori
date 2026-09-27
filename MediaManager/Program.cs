using Banned.AniParser;
using MediaManager.Components;
using MediaManager.Components.Account;
using MediaManager.Configuration;
using MediaManager.Core.Rules;
using MediaManager.Core.Rules.Selection;
using MediaManager.Core.Subjects;
using MediaManager.Data;
using MediaManager.Features.Dashboard;
using MediaManager.Features.Downloads;
using MediaManager.Features.FeedItems;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Initialization;
using MediaManager.Integrations;
using MediaManager.Integrations.Bangumi;
using MediaManager.Integrations.Mikan;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Workers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaManager;

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

        var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        var keyDirectory  = Path.Combine(dataDirectory, "keys");
        Directory.CreateDirectory(keyDirectory);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ??
                               throw new InvalidOperationException("未配置连接字符串 DefaultConnection。");

        var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(sqliteConnection.DataSource)   &&
            sqliteConnection.DataSource != ":memory:"            &&
            sqliteConnection.Mode       != SqliteOpenMode.Memory &&
            !sqliteConnection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            sqliteConnection.DataSource =
                Path.GetFullPath(sqliteConnection.DataSource, builder.Environment.ContentRootPath);

        builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
                                                                       options.UseSqlite(sqliteConnection
                                                                                   .ConnectionString));
        builder.Services.AddDatabaseDeveloperPageExceptionFilter();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.SignIn.RequireConfirmedAccount = true;
                    options.Stores.SchemaVersion           = IdentitySchemaVersions.Version3;
                })
               .AddEntityFrameworkStores<ApplicationDbContext>()
               .AddSignInManager()
               .AddDefaultTokenProviders();

        builder.Services.Configure<BootstrapAdminOptions>(builder.Configuration.GetSection(BootstrapAdminOptions
                                                                      .SectionName));
        builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RefreshPulse>();
        builder.Services.AddScoped<IApplicationSettingStore, EfApplicationSettingStore>();
        builder.Services.AddScoped<ManageGeneralSettings>();
        builder.Services.AddScoped<ManageQbittorrentSettings>();
        builder.Services.AddScoped<ManageSelectionRuleSettings>();
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
        builder.Services.AddScoped<DownloadService>();
        builder.Services.AddScoped<IQbittorrentSessionFactory, QbittorrentSessionFactory>();
        builder.Services.AddSingleton<ITorrentFileSource, HttpTorrentFileSource>();
        builder.Services.AddSingleton<IFeedSourceReader, MikanFeedSourceReader>();
        builder.Services.AddSingleton<ISubjectLinkResolver, MikanSubjectLinkResolver>();
        builder.Services.AddSingleton<ISubjectMetadataProvider, BangumiSubjectMetadataProvider>();
        builder.Services.AddHttpClient("Mikan", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaManager/0.2");
        });
        builder.Services.AddHttpClient("Bangumi");
        builder.Services.AddHttpClient("TorrentDownload", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaManager/0.2");
        });
        builder.Services.AddHostedService<SubscriptionRefreshWorker>();
        builder.Services.AddHostedService<DownloadReconcileWorker>();
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
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapAdditionalIdentityEndpoints();
    }
}
