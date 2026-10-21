using Banned.AniParser.Models;
using Banori.Core.Rules;
using Banori.Core.Torrents;

namespace Banori.Tests.Rules;

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
    public void Create_NamespacedTokens_KeepAniParserBangumiAndSiteValuesSeparate()
    {
        var ingredients = new TorrentNamingIngredients("{bgm.nameCn} {ani.episode} {ani.source} {site.source}", "中文名",
                                                       "07", 2, "sc", "mikan",
                                                       FileTemplate : "{ani.title} {bgm.name} {ani.resolution}",
                                                       AniTitle : "[Group] Parsed Title",
                                                       AniSource : "BluRay", AniResolution : "1080p", BangumiId : 123,
                                                       BangumiName : "Original Name",
                                                       BangumiNameCn : "中文名");

        var policy = new ReleaseNamePreviewPolicy();

        Assert.Equal("中文名 07 BluRay mikan", policy.Create(ingredients));
        Assert.Equal("[Group] Parsed Title Original Name 1080p", policy.Create(ingredients, true));
    }

    [Fact]
    public void Create_FileTemplate_IsIndependentAndUsesCurrentFileEpisode()
    {
        var ingredients = new TorrentNamingIngredients("Seed {episode}", "作品", "01", 1, null, null,
                                                       FileTemplate : "File E{ani.episode}");

        var fileIngredients = ingredients with { EpisodeText = "12" };

        Assert.Equal("Seed 01", new ReleaseNamePreviewPolicy().Create(ingredients));
        Assert.Equal("File E12", new ReleaseNamePreviewPolicy().Create(fileIngredients, true));
    }

    [Fact]
    public void Create_FileTemplate_TypedSlashesBecomeSubdirectories_ValuesDoNot()
    {
        var ingredients = new TorrentNamingIngredients(
                                                       "[Anime]{title}/{source}", "测试作品", "02", 1, "Sc", "mikan",
                                                       FileTemplate :
                                                       "[{bgm.date:yyyy.MM}]{bgm.nameCn}/{bgm.nameCn} E{ani.episode:ee} {source}",
                                                       BangumiDate : "2026-04-05", BangumiNameCn : "测试/作品");

        var policy = new ReleaseNamePreviewPolicy();

        // 文件名模式：模板书写的 / 是子目录分隔符；令牌取值里的 / 已转空格，不会意外建目录。
        Assert.Equal("[2026.04]测试 作品/测试 作品 E02 mikan", policy.Create(ingredients, true));
        // 种子名模式：展示名必须单层，分隔符沿旧行为折叠成空格。
        Assert.Equal("[Anime]测试作品 mikan", policy.Create(ingredients));
    }

    [Fact]
    public void Create_FileTemplate_EmptyAndConsecutiveSegmentsCollapse()
    {
        var ingredients = new TorrentNamingIngredients("{title}", "作品", "01", 1, null, null,
                                                       FileTemplate : "/{title}//E{episode}\\\\{ani.title}/");

        var result = new ReleaseNamePreviewPolicy().Create(ingredients with { AniTitle = null }, true);

        // 空段与连续分隔符折叠；\ 与 / 同义；反斜杠也可作分隔符。
        Assert.Equal("作品/E01", result);
    }

    [Theory]
    [InlineData("07", "ee", "07")]
    [InlineData("07", "eee", "007")]
    [InlineData("07", "EE", "07")]
    [InlineData("07.5", "eee", "007.5")]
    [InlineData("01-E12", "eee", "001-E012")]
    [InlineData("special", "eee", "special")]
    public void Create_EpisodeFormatSpecifier_FormatsNumericEpisode(string episode, string format, string expected)
    {
        var ingredients = new TorrentNamingIngredients($"{{ani.episode:{format}}}", "作品", episode, 1, null, null);

        Assert.Equal(expected, new ReleaseNamePreviewPolicy().Create(ingredients));
    }

    [Fact]
    public void Create_BangumiDateFormatSpecifier_FormatsDateAndFallsBackWhenInvalid()
    {
        var valid = new TorrentNamingIngredients("{bgm.date:yyyy.MM}", "作品", "01", 1, null, null,
                                                 BangumiDate : "2026-10-11");
        var invalid = valid with { BangumiDate = "not-a-date" };
        var missing = valid with { BangumiDate = null };

        var policy = new ReleaseNamePreviewPolicy();

        Assert.Equal("2026.10", policy.Create(valid));
        Assert.Equal("not-a-date", policy.Create(invalid));
        Assert.Equal(string.Empty, policy.Create(missing));
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
    public void Ingredients_FromLegacyJson_LeavesNewFileTemplateAndSourceFieldsUnset()
    {
        const string legacyJson =
            "{\"template\":\"{title} E{episode}\",\"title\":\"旧作品\",\"episodeText\":\"03\",\"version\":1,\"group\":null,\"source\":null}";

        var restored = TorrentNamingIngredients.FromJson(legacyJson);

        Assert.NotNull(restored);
        Assert.Null(restored.FileTemplate);
        Assert.Null(restored.AniTitle);
        Assert.Null(restored.BangumiId);
        Assert.Equal("旧作品 E03", new ReleaseNamePreviewPolicy().Create(restored));
        Assert.Equal("旧作品 E03", new ReleaseNamePreviewPolicy().Create(restored, true));
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
