using Banori.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using System.Reflection;

namespace Banori.Tests.Security;

/// <summary>
///     业务页面默认必须认证的回归防线：Routes.razor 的 AuthorizeRouteView 只对带 [Authorize] 的
///     页面生效，漏标的页面会匿名可达。此测试让新增页面忘记标注时直接失败。
/// </summary>
public sealed class PageAuthorizationTests
{
    // 匿名可达是刻意设计：Error / NotFound 分别服务异常处理器与状态码页重执行。
    private static readonly string[] AnonymousByDesign = ["NotFound", "Error"];

    [Fact]
    public void RoutablePages_UnderBusinessNamespace_RequireAuthorization()
    {
        var businessPages = typeof(Home).Assembly.GetTypes()
                                        .Where(t => t.Namespace?.StartsWith("Banori.Components.Pages",
                                                                            StringComparison.Ordinal) == true)
                                        .Where(t => t.GetCustomAttributes<RouteAttribute>(false).Any())
                                        .Where(t => !AnonymousByDesign.Contains(t.Name))
                                        .ToList();

        Assert.NotEmpty(businessPages);
        Assert.All(businessPages,
                   t => Assert.True(t.GetCustomAttributes<AuthorizeAttribute>(false).Any(),
                                    $"{t.Name} 缺少 [Authorize]，业务页面默认必须认证。"));
    }
}
