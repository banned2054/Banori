using Microsoft.AspNetCore.Identity;

namespace MediaManager.Components.Account;

/// <summary>
///     Identity 框架内置校验错误（密码强度、用户名/邮箱占用等）的中文描述。
/// </summary>
public sealed class ChineseIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError()
    {
        return new IdentityError { Code = nameof(DefaultError), Description = "发生未知故障。" };
    }

    public override IdentityError ConcurrencyFailure()
    {
        return new IdentityError { Code = nameof(ConcurrencyFailure), Description = "并发操作失败，请重试。" };
    }

    public override IdentityError PasswordMismatch()
    {
        return new IdentityError { Code = nameof(PasswordMismatch), Description = "密码不正确。" };
    }

    public override IdentityError InvalidToken()
    {
        return new IdentityError { Code = nameof(InvalidToken), Description = "令牌无效。" };
    }

    public override IdentityError LoginAlreadyAssociated()
    {
        return new IdentityError { Code = nameof(LoginAlreadyAssociated), Description = "该用户已绑定此外部登录。" };
    }

    public override IdentityError InvalidUserName(string? userName)
    {
        return new IdentityError { Code = nameof(InvalidUserName), Description = $"用户名“{userName}”无效，只能包含字母或数字。" };
    }

    public override IdentityError InvalidEmail(string? email)
    {
        return new IdentityError { Code = nameof(InvalidEmail), Description = $"邮箱“{email}”无效。" };
    }

    public override IdentityError DuplicateUserName(string userName)
    {
        return new IdentityError { Code = nameof(DuplicateUserName), Description = $"用户名“{userName}”已被占用。" };
    }

    public override IdentityError DuplicateEmail(string email)
    {
        return new IdentityError { Code = nameof(DuplicateEmail), Description = $"邮箱“{email}”已被占用。" };
    }

    public override IdentityError PasswordTooShort(int length)
    {
        return new IdentityError { Code = nameof(PasswordTooShort), Description = $"密码长度必须至少 {length} 个字符。" };
    }

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars)
    {
        return new IdentityError
            { Code = nameof(PasswordRequiresUniqueChars), Description = $"密码必须至少包含 {uniqueChars} 个不同字符。" };
    }

    public override IdentityError PasswordRequiresNonAlphanumeric()
    {
        return new IdentityError { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "密码必须至少包含一个非字母数字字符。" };
    }

    public override IdentityError PasswordRequiresDigit()
    {
        return new IdentityError { Code = nameof(PasswordRequiresDigit), Description = "密码必须至少包含一个数字（'0'-'9'）。" };
    }

    public override IdentityError PasswordRequiresLower()
    {
        return new IdentityError { Code = nameof(PasswordRequiresLower), Description = "密码必须至少包含一个小写字母（'a'-'z'）。" };
    }

    public override IdentityError PasswordRequiresUpper()
    {
        return new IdentityError { Code = nameof(PasswordRequiresUpper), Description = "密码必须至少包含一个大写字母（'A'-'Z'）。" };
    }

    public override IdentityError UserAlreadyHasPassword()
    {
        return new IdentityError { Code = nameof(UserAlreadyHasPassword), Description = "用户已设置密码。" };
    }

    public override IdentityError RecoveryCodeRedemptionFailed()
    {
        return new IdentityError { Code = nameof(RecoveryCodeRedemptionFailed), Description = "恢复代码兑换失败。" };
    }
}
