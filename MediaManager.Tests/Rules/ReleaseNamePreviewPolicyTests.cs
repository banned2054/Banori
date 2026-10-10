using Banned.AniParser.Models;
using MediaManager.Core.Rules;
using MediaManager.Core.Torrents;

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

    [Fact]
    public void Create_FromIngredients_FallsBackToDefaultTemplateAndMatchesLegacyShape()
    {
        var ingredients = new TorrentNamingIngredients(null, "动画: 第一季", "07.5", 2, "Sc", "mikan");

        var result = new ReleaseNamePreviewPolicy().Create(ingredients);

        // 模板为空回退默认模板，与旧重载同形。
        Assert.Equal("[Anime]动画 第一季 E07.5 mikan", result);
    }

    [Fact]
    public void FormatEpisodeText_FromParseResult_SupportsSingleAndRange()
    {
        var single = new ParseResult { Episode = 7m };
        Assert.Equal("07", ReleaseNamePreviewPolicy.FormatEpisodeText(single));

        var range = new ParseResult { StartEpisode = 1, EndEpisode = 12 };
        Assert.Equal("01-E12", ReleaseNamePreviewPolicy.FormatEpisodeText(range));

        var rangeOfOne = new ParseResult { StartEpisode = 5, EndEpisode = 5 };
        Assert.Equal("05", ReleaseNamePreviewPolicy.FormatEpisodeText(rangeOfOne));

        var unparseable = new ParseResult();
        Assert.Null(ReleaseNamePreviewPolicy.FormatEpisodeText(unparseable));
    }

    [Fact]
    public void Create_RangeEpisodeText_RendersConsolidatedCollectionName()
    {
        var ingredients = new TorrentNamingIngredients(null, "龙与虎", "01-E12", 1, "Sc", "byrbt");

        Assert.Equal("[Anime]龙与虎 E01-E12 byrbt", new ReleaseNamePreviewPolicy().Create(ingredients));
    }

    [Fact]
    public void Ingredients_JsonRoundTrip_PreservesValuesAndToleratesGarbage()
    {
        var ingredients = new TorrentNamingIngredients("[Anime]{title} E{episode} {source}", "动画", "07", 2,
                                                       "Sc", "mikan");

        var restored = TorrentNamingIngredients.FromJson(ingredients.ToJson());

        Assert.Equal(ingredients, restored);
        Assert.Null(TorrentNamingIngredients.FromJson("not json"));
        Assert.Null(TorrentNamingIngredients.FromJson(null));
    }
}
