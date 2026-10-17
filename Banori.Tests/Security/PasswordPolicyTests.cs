using Banori.Security;

namespace Banori.Tests.Security;

/// <summary>密码策略回归：黑名单资源可加载、命中常见密码被拒、大小写不敏感；验证器只拦黑名单。</summary>
public sealed class PasswordPolicyTests
{
    [Fact]
    public void Blocklist_LoadsEmbeddedList_AndMatchesCaseInsensitively()
    {
        var blocklist = new CommonPasswordBlocklist();

        Assert.True(blocklist.IsBlocked("password"));
        Assert.True(blocklist.IsBlocked("PASSWORD"), "黑名单匹配必须大小写不敏感。");
        Assert.False(blocklist.IsBlocked("zousi-notebook-7341"));
    }

    [Fact]
    public async Task Validator_RejectsCommonPassword_AndPassesUncommon()
    {
        var validator = new CommonPasswordValidator(new CommonPasswordBlocklist());

        var blocked = await validator.ValidateAsync(null!, null!, "password123");
        Assert.False(blocked.Succeeded);
        Assert.Contains(blocked.Errors, error => error.Code == "CommonPassword");

        var allowed = await validator.ValidateAsync(null!, null!, "zousinotebook");
        Assert.True(allowed.Succeeded);
    }
}
