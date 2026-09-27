using MediaManager.Core.Rules;

namespace MediaManager.Tests.Rules;

public sealed class ReleaseNamePreviewPolicyTests
{
    [Fact]
    public void Create_FormatsFractionalEpisodeAndRemovesUnsafeCharacters()
    {
        var result = new ReleaseNamePreviewPolicy().Create("动画: 第一季 / 特别篇", 7.5m, 2, "Sc");

        Assert.Equal("动画 第一季 特别篇 E07.5 v2 [Sc]", result);
    }
}
