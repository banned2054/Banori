using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Banori.Features.Subscriptions;

/// <summary>
///     RSS 地址的 Data Protection 用途字符串。既有数据库中的密文按此解密，修改会导致历史订阅无法读取。
/// </summary>
internal static class FeedUrlProtection
{
    public const string Purpose = "Banori.MediaSubscriptions.FeedUrl.v1";

    public static IDataProtector CreateProtector(IDataProtectionProvider provider) => provider.CreateProtector(Purpose);

    /// <summary>解密订阅 RSS 地址；空值或无法解密的脏数据返回 null，由调用方按未知来源处理。</summary>
    public static string? TryUnprotectUrl(IDataProtector protector, string? protectedUrl)
    {
        if (string.IsNullOrWhiteSpace(protectedUrl)) return null;
        try
        {
            return protector.Unprotect(protectedUrl);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
