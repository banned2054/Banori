using Banori.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Banori.Components.Account;

// 活跃连接与 Cookie 使用同一安全戳检查周期，避免改密后旧连接继续沿用更长的验证间隔。
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory                          loggerFactory,
    IServiceScopeFactory                    scopeFactory,
    IOptions<IdentityOptions>               options,
    IOptions<SecurityStampValidatorOptions> securityStampOptions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => securityStampOptions.Value.ValidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // Get the user manager from a new scope to ensure it fetches fresh data
        await using var scope       = scopeFactory.CreateAsyncScope();
        var             userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await ValidateSecurityStampAsync(userManager, authenticationState.User);
    }

    private async Task<bool> ValidateSecurityStampAsync(
        UserManager<ApplicationUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null) return false;

        if (!userManager.SupportsUserSecurityStamp) return true;

        var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        var userStamp      = await userManager.GetSecurityStampAsync(user);
        return principalStamp == userStamp;
    }
}
