using Banori.Security;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace Banori.Tests.Security;

/// <summary>基础限流路径判定回归：认证入口只认 POST 表单端点，钩子按精确路径匹配。</summary>
public sealed class BaselineRateLimitingTests
{
    [Theory]
    [InlineData("/Account/Login", "POST", true)]
    [InlineData("/Account/LoginWith2fa", "POST", true)]
    [InlineData("/Account/LoginWithRecoveryCode", "POST", true)]
    [InlineData("/Account/PasskeyCreationOptions", "POST", true)]
    [InlineData("/Account/PasskeyRequestOptions", "POST", true)]
    [InlineData("/Account/Login/", "POST", true)]
    [InlineData("/account/login/", "POST", true)]
    [InlineData("/Account/LoginWith2fa/", "POST", true)]
    [InlineData("/Account/LoginWithRecoveryCode/", "POST", true)]
    [InlineData("/Account/PasskeyCreationOptions/", "POST", true)]
    [InlineData("/Account/PasskeyRequestOptions/", "POST", true)]
    [InlineData("/Account/Login/extra", "POST", false)]
    [InlineData("/Account/Login", "GET", false)]
    [InlineData("/Account/Logout", "POST", false)]
    [InlineData("/", "POST", false)]
    public void IsAuthenticationSubmit_OnlyMatchesLoginFormPosts(string path, string method, bool expected)
    {
        Assert.Equal(expected, BaselineRateLimiting.IsAuthenticationSubmit(path, method));
    }

    [Theory]
    [InlineData("/hooks/qbittorrent", true)]
    [InlineData("/hooks/qbittorrent/", true)]
    [InlineData("/HOOKS/QBITTORRENT/", true)]
    [InlineData("/hooks/qbittorrent/extra", false)]
    [InlineData("/Account/Login", false)]
    public void IsHookCallback_MatchesExactPath(string path, bool expected)
    {
        Assert.Equal(expected, BaselineRateLimiting.IsHookCallback(path));
    }

    [Fact]
    public void PartitionKeys_AreIsolatedPerBranchForSameClient()
    {
        var context = new DefaultHttpContext
        {
            Connection =
            {
                RemoteIpAddress = IPAddress.Parse("1.2.3.4")
            }
        };

        // PartitionedRateLimiter 按分区键缓存限流器实例，两个分支同 IP 必须得到不同键，
        // 否则 NoLimiter 分支会顶掉限流分支的缓存（此 bug 曾让限流完全失效）。
        Assert.NotEqual(BaselineRateLimiting.AuthPartitionKey(context), BaselineRateLimiting.HookPartitionKey(context));
        Assert.EndsWith("1.2.3.4", BaselineRateLimiting.AuthPartitionKey(context));
    }
}
