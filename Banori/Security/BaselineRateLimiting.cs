namespace Banori.Security;

/// <summary>
///     无条件生效的基础限流：认证入口按客户端 IP 限制提交频率，防密码与验证码在线爆破；
///     qB 钩子回调只做削峰。钩子本身幂等可丢（15 秒对账轮询兜底），限流不承诺送达。
///     反向代理之后须正确配置转发头，RemoteIpAddress 才是真实客户端 IP；
///     通过 ReverseProxy:KnownProxies 显式信任代理，否则忽略转发头。
/// </summary>
public static class BaselineRateLimiting
{
    public const int AuthPermitLimit = 5;
    public const int HookPermitLimit = 60;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>密码、2FA、恢复码与通行密钥端点的表单提交；只限 POST，不拦登录页 GET。</summary>
    public static bool IsAuthenticationSubmit(PathString path, string method)
    {
        path = NormalizePath(path);
        return HttpMethods.IsPost(method) && (path == "/Account/Login"                  ||
                                              path == "/Account/LoginWith2fa"           ||
                                              path == "/Account/LoginWithRecoveryCode"  ||
                                              path == "/Account/PasskeyCreationOptions" ||
                                              path == "/Account/PasskeyRequestOptions");
    }

    public static bool IsHookCallback(PathString path) => NormalizePath(path) == "/hooks/qbittorrent";

    // 路由匹配接受尾部斜杠及大小写变体；限流必须把这些请求视为同一入口。
    private static PathString NormalizePath(PathString path) => new(path.Value?.TrimEnd('/'));

    // PartitionedRateLimiter 按分区键缓存限流器实例，各分支必须用互不相同的键，
    // 否则首个请求的 NoLimiter 会以同一键缓存、顶掉后续限流分支。
    public static string AuthPartitionKey(HttpContext context) =>
        $"auth:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    public static string HookPartitionKey(HttpContext context) =>
        $"hook:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
