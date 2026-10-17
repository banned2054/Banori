using Banori.Data;
using Microsoft.AspNetCore.Identity;

namespace Banori.Security;

/// <summary>
///     密码策略：最低 8 位 + 拒绝常见泄露密码，取代大小写/数字/符号组合规则（NIST 800-63B 的推荐形态）。
/// </summary>
internal sealed class CommonPasswordValidator(CommonPasswordBlocklist blocklist) : IPasswordValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        if (!string.IsNullOrEmpty(password) && blocklist.IsBlocked(password))
            return Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code        = "CommonPassword",
                Description = "密码过于常见（命中常见泄露密码清单），请换一个。"
            }));

        return Task.FromResult(IdentityResult.Success);
    }
}
