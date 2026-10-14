using Banori.Configuration;
using Banori.Data;
using Banori.Initialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace Banori.Tests.Configuration;

public sealed class BootstrapAdminConfigurationTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("Banori.BootstrapAdmin.Tests.");

    public void Dispose() => _directory.Delete(true);

    [Theory]
    [InlineData("BANORI_USERNAME", "BANORI_PASSWORD")]
    [InlineData("banori_username", "banori_password")]
    public async Task ProgramOptions_ReadNewKeys_AndIgnoreConflictingLegacyKeys(string usernameKey, string passwordKey)
    {
        await using var app = CreateApplication(new Dictionary<string, string?>
        {
            [usernameKey] = "bootstrap@example.test",
            [passwordKey] = "Bootstrap-Test9!",
            ["Username"]  = "legacy@example.test",
            ["Password"]  = "Legacy-Test8!"
        });

        var options = app.Services.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;

        Assert.Equal("bootstrap@example.test", options.Username);
        Assert.Equal("Bootstrap-Test9!", options.Password);
    }

    [Theory]
    [InlineData("Username", "Password")]
    [InlineData("username", "password")]
    public async Task ProgramOptions_DoNotFallBackToLegacyKeys(string usernameKey, string passwordKey)
    {
        await using var app = CreateApplication(new Dictionary<string, string?>
        {
            [usernameKey] = "legacy@example.test",
            [passwordKey] = "Legacy-Test8!"
        });

        var options = app.Services.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;

        Assert.Equal(string.Empty, options.Username);
        Assert.Equal(string.Empty, options.Password);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Bootstrap-Test9!")]
    [InlineData("bootstrap@example.test", null)]
    [InlineData("", "")]
    [InlineData("  ", "Bootstrap-Test9!")]
    [InlineData("bootstrap@example.test", "  ")]
    public async Task Initializer_MissingNewCredentials_ReportsNewEnvironmentVariables(
        string? username, string? password)
    {
        await using var app = CreateApplication(new Dictionary<string, string?>
        {
            ["BANORI_USERNAME"] = username,
            ["BANORI_PASSWORD"] = password,
            ["Username"]        = "legacy@example.test",
            ["Password"]        = "Legacy-Test8!"
        });

        var error =
            await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationInitializer.InitializeAsync(app));

        Assert.Contains("BANORI_USERNAME", error.Message);
        Assert.Contains("BANORI_PASSWORD", error.Message);
        await using var scope     = app.Services.CreateAsyncScope();
        var             dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.Users.AnyAsync());
    }

    [Fact]
    public async Task Initializer_EmptyDatabase_CreatesAdministratorFromProgramOptions()
    {
        await using var app = CreateApplication("bootstrap@example.test", "Bootstrap-Test9!");

        await ApplicationInitializer.InitializeAsync(app);

        await using var scope         = app.Services.CreateAsyncScope();
        var             userManager   = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var             administrator = Assert.Single(await userManager.Users.ToListAsync());
        Assert.Equal("bootstrap@example.test", administrator.UserName);
        Assert.Equal("bootstrap@example.test", administrator.Email);
        Assert.True(administrator.EmailConfirmed);
        Assert.True(await userManager.CheckPasswordAsync(administrator, "Bootstrap-Test9!"));
    }

    [Theory]
    [InlineData("existing@example.test", "Bootstrap-Test9!")]
    [InlineData("replacement@example.test", "Bootstrap-Test9!")]
    [InlineData(null, null)]
    public async Task Initializer_ExistingAccount_IsNotResetOrReplaced(string? username, string? password)
    {
        await using var app = CreateApplication(username, password);
        ApplicationUser original;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await dbContext.Database.MigrateAsync();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            original = new ApplicationUser
            {
                UserName       = "existing@example.test",
                Email          = "existing-email@example.test",
                EmailConfirmed = false
            };
            var result = await userManager.CreateAsync(original, "Existing-Test7!");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        await ApplicationInitializer.InitializeAsync(app);

        // A fresh scope avoids mistaking Identity/EF's tracked user for persisted state.
        await using var verificationScope = app.Services.CreateAsyncScope();
        var verificationManager = verificationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var persisted = Assert.Single(await verificationManager.Users.AsNoTracking().ToListAsync());
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(original.UserName, persisted.UserName);
        Assert.Equal(original.NormalizedUserName, persisted.NormalizedUserName);
        Assert.Equal(original.Email, persisted.Email);
        Assert.Equal(original.EmailConfirmed, persisted.EmailConfirmed);
        Assert.Equal(original.PasswordHash, persisted.PasswordHash);
        Assert.Equal(original.SecurityStamp, persisted.SecurityStamp);
        Assert.Equal(original.ConcurrencyStamp, persisted.ConcurrencyStamp);
        Assert.True(await verificationManager.CheckPasswordAsync(persisted, "Existing-Test7!"));
        Assert.False(await verificationManager.CheckPasswordAsync(persisted, "Bootstrap-Test9!"));
    }

    private WebApplication CreateApplication(string? username, string? password) =>
        CreateApplication(new Dictionary<string, string?>
        {
            ["BANORI_USERNAME"] = username,
            ["BANORI_PASSWORD"] = password
        });

    private WebApplication CreateApplication(IReadOnlyDictionary<string, string?> bootstrapConfiguration)
    {
        // Unlike CreateBuilder, this never loads appsettings, user secrets, or environment credentials.
        // Production also prevents development-only startup behavior, and all writes stay in our temp root.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ApplicationInitializer).Assembly.GetName().Name,
            ContentRootPath = _directory.FullName,
            EnvironmentName = Environments.Production,
            Args            = []
        });
        builder.WebHost.UseKestrel();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"]                   = "Sqlite",
            ["ConnectionStrings:DefaultConnection"] = "Data Source=config/bootstrap.db;Pooling=False"
        });
        builder.Configuration.AddInMemoryCollection(bootstrapConfiguration);

        // Exercise the actual registration rather than duplicating its Configure<BootstrapAdminOptions> lambda.
        var program     = typeof(ApplicationInitializer).Assembly.GetType("Banori.Program", true)!;
        var initBuilder = program.GetMethod("InitBuilder", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(initBuilder);
        initBuilder.Invoke(null, [builder]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Build only: never start the server or any hosted background workers.
        return builder.Build();
    }
}
