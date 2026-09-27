using MediaManager.Configuration;
using MediaManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaManager.Initialization;

public static class ApplicationInitializer
{
    public static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        await using var scope     = app.Services.CreateAsyncScope();
        var             services  = scope.ServiceProvider;
        var             dbContext = services.GetRequiredService<ApplicationDbContext>();

        await dbContext.Database.MigrateAsync(cancellationToken);

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.Users.AnyAsync(cancellationToken)) return;

        var options = services.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
            throw
                new InvalidOperationException("数据库中没有管理员。请设置 BootstrapAdmin__Email 和 BootstrapAdmin__Password 后重新启动。");

        var administrator = new ApplicationUser
        {
            UserName       = options.Email,
            Email          = options.Email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(administrator, options.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"创建首位管理员失败：{errors}");
        }

        app.Logger.LogInformation("已创建首位管理员 {AdministratorEmail}。", options.Email);
    }
}
