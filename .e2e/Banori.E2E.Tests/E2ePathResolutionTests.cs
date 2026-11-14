using Xunit;

namespace Banori.E2E.Tests;

/// <summary>
///     目录定位（无 gate，始终运行）：在干净源码布局下证明 .e2e 根定位与场景目录自动创建
///     可用——只依赖提交的源码标记（Banori.E2E.Tests.csproj、tools/），不依赖运行产物。
/// </summary>
public sealed class E2ePathResolutionTests
{
    [Fact]
    public void 根定位_从应用基目录向上找到e2e根()
    {
        var root = ScenarioSupport.FindE2eRoot();

        Assert.True(File.Exists(Path.Combine(root, "Banori.E2E.Tests", "Banori.E2E.Tests.csproj")),
                    $"定位到的根 {root} 下缺少 Banori.E2E.Tests/Banori.E2E.Tests.csproj。");
        Assert.True(File.Exists(Path.Combine(root, "tools", "qb_container.py")),
                    $"定位到的根 {root} 下缺少 tools/qb_container.py。");
    }

    [Fact]
    public void 场景根_不存在的leaf自动创建并返回()
    {
        const string leaf    = "tmp-path-check";
        var          expected = Path.Combine(ScenarioSupport.FindE2eRoot(), leaf);
        try
        {
            var sceneRoot = ScenarioSupport.FindSceneRoot(leaf);

            Assert.Equal(expected, sceneRoot);
            Assert.True(Directory.Exists(sceneRoot), $"未自动创建场景目录 {sceneRoot}。");
        }
        finally
        {
            if (Directory.Exists(expected)) Directory.Delete(expected, true);
        }
    }

    [Fact]
    public void 场景根_已存在的leaf幂等返回同一路径()
    {
        var root   = ScenarioSupport.FindE2eRoot();
        var first  = ScenarioSupport.FindSceneRoot("tools");
        var second = ScenarioSupport.FindSceneRoot("tools");

        Assert.Equal(Path.Combine(root, "tools"), first);
        Assert.Equal(first, second);
        Assert.True(Directory.Exists(first), $"已存在目录 {first} 不应缺失。");
    }
}
