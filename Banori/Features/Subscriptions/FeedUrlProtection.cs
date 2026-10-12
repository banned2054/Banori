using Microsoft.AspNetCore.DataProtection;

namespace Banori.Features.Subscriptions;

/// <summary>
///     RSS 地址的 Data Protection 用途字符串。既有数据库中的密文按此解密，修改会导致历史订阅无法读取。
/// </summary>
internal static class FeedUrlProtection
{
    public const string Purpose = "MediaManager.MediaSubscriptions.FeedUrl.v1";

    public static IDataProtector CreateProtector(IDataProtectionProvider provider) => provider.CreateProtector(Purpose);
}
