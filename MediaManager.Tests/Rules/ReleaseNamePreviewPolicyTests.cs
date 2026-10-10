using MediaManager.Core.Rules;

namespace MediaManager.Tests.Rules;

public sealed class ReleaseNamePreviewPolicyTests
{
    [Fact]
    public void Create_DefaultTemplate_RendersTitleEpisodeAndSource()
    {
        var result = new ReleaseNamePreviewPolicy().Create("动画: 第一季 / 特别篇", 7.5m, 2, "Sc", "mikan", null);

        Assert.Equal("[Anime]动画 第一季 特别篇 E07.5 mikan", result);
    }

    [Fact]
    public void Create_LegacyOverload_UsesDefaultTemplate()
    {
        var result = new ReleaseNamePreviewPolicy().Create("测试作品", 1m, 1, "Sc");

        Assert.Equal("[Anime]测试作品 E01", result);
    }

    [Fact]
    public void Create_CustomTemplate_SupportsAllTokens()
    {
        const string template = "{title} - S{episode} - {version} - {group} - {source}";

        var firstVersion = new ReleaseNamePreviewPolicy().Create("动画", 12m, 1, "Subs", "byrbt", template);
        var rerun        = new ReleaseNamePreviewPolicy().Create("动画", 12m, 3, "Subs", "byrbt", template);

        // 渲染后收缩空白：空令牌两侧的占位连字符会自然合并。
        Assert.Equal("动画 - S12 - - Subs - byrbt", firstVersion);
        Assert.Equal("动画 - S12 - v3 - Subs - byrbt", rerun);
    }

    [Fact]
    public void Create_EmptySource_CollapsesTrailingGap()
    {
        var result = new ReleaseNamePreviewPolicy().Create("动画", 1m, 1, "Sc", null, null);

        Assert.Equal("[Anime]动画 E01", result);
    }

    [Fact]
    public void Create_TemplateOutputIsSanitizedOfIllegalCharacters()
    {
        var result = new ReleaseNamePreviewPolicy().Create("动画", 1m, 1, "Sc", "a/b?c", "{source}/{title}");

        Assert.Equal("a b c 动画", result);
    }
}
