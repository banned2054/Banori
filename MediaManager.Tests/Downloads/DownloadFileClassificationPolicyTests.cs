using Banned.AniParser;
using MediaManager.Core.Downloads;

namespace MediaManager.Tests.Downloads;

public sealed class DownloadFileClassificationPolicyTests
{
    private const    string                           Name    = "【喵萌奶茶屋】[Test Anime][01][1080p][简日双语]";
    private readonly DownloadFileClassificationPolicy _policy = new(new AniParser());

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".MP4")]
    [InlineData(".avi")]
    [InlineData(".ts")]
    [InlineData(".m2ts")]
    [InlineData(".wmv")]
    [InlineData(".mov")]
    [InlineData(".flv")]
    [InlineData(".ass")]
    [InlineData(".ssa")]
    [InlineData(".srt")]
    [InlineData(".sup")]
    public void ParsedEpisodes_KeepAllCurrentExtensions(string extension)
    {
        var result = _policy.Classify("Root\\" + Name + extension);
        Assert.Equal(DownloadFileKind.Content, result.Kind);
        Assert.Equal(1m, result.Parsed!.Episode);
        Assert.StartsWith("Root/", result.Path);
    }

    [Theory]
    [InlineData("[CM01]")]
    [InlineData("[menu02]")]
    [InlineData("[NCED03]")]
    [InlineData("[PV04]")]
    [InlineData("[NCOP05]")]
    public void SpecialFragments_OverrideParseableEpisode(string fragment)
    {
        Assert.Equal(DownloadFileKind.Seed, _policy.Classify(Name + fragment + ".mkv").Kind);
    }

    [Theory]
    [InlineData("Deculture Ver.")]
    [InlineData("sps")]
    [InlineData("Scans")]
    [InlineData("CDs")]
    [InlineData("BONUS")]
    public void FilterWords_ApplyToFullPathIgnoringCase(string folder)
    {
        Assert.Equal(DownloadFileKind.Seed, _policy.Classify(folder + "/" + Name + ".mkv").Kind);
    }

    [Theory]
    [InlineData(".tc.ass")]
    [InlineData(".CHT.ass")]
    [InlineData(".txt")]
    public void FilteredSubtitleAndUnsupportedExtensions_AreSeed(string suffix)
    {
        Assert.Equal(DownloadFileKind.Seed, _policy.Classify(Name + suffix).Kind);
    }

    [Theory]
    [InlineData("original.mkv")]
    [InlineData("subtitle.ass")]
    [InlineData("NCOP.mp4")]
    public void UnparsedFiles_AreSeed(string path)
    {
        Assert.Equal(DownloadFileKind.Seed, _policy.Classify(path).Kind);
    }

    [Fact]
    public void EpisodeRangesAndFractionalEpisodes_UseAniParserValues()
    {
        var range = _policy.Classify("【喵萌奶茶屋】[Test Anime][01-12][1080p][简日双语].mkv");
        Assert.Equal(DownloadFileKind.Content, range.Kind);
        Assert.Equal(1, range.Parsed!.StartEpisode);
        Assert.Equal(12, range.Parsed.EndEpisode);
        var fractional = _policy.Classify("[VCB-Studio] Test Anime [01.5][Ma10p_1080p][x265_10bit].mkv");
        Assert.Equal(DownloadFileKind.Content, fractional.Kind);
        Assert.Equal(1.5m, fractional.Parsed!.Episode);
    }

    [Fact]
    public void UnsafePath_IsNotSilentlyClassified()
    {
        Assert.Throws<ArgumentException>(() => _policy.Classify("../" + Name + ".mkv"));
    }
}
