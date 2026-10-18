using Banned.AniParser;
using Banori.Core.Subjects;
using Banori.Data;
using Banori.Features.Downloads;

namespace Banori.Tests.Downloads;

public sealed class DownloadEpisodeResolverTests
{
    private const string SingleEpisodePath =
        "Root/【喵萌奶茶屋】[Test Anime][07][1080p][简日双语].mkv";

    private const string RangeEpisodePath =
        "Root/【喵萌奶茶屋】[Test Anime][12-13][1080p][简日双语].mkv";

    private readonly DownloadEpisodeResolver _resolver =
        new(new AniParser(), new SubjectEpisodeCorrectionPolicy());

    [Fact]
    public void ManualEpisodeRange_DoesNotClaimSingleEpisodeIdentity()
    {
        var task = new DownloadTask();
        var file = CreateFile("Manual/range.mkv", EpisodeNumberOverride : 12m,
                              EpisodeEndOverride : 13m);

        Assert.Null(_resolver.EpisodeOf(task, file));
    }

    [Fact]
    public void ManualEpisodeRange_WithEqualBounds_IsSingleEpisode()
    {
        var task = new DownloadTask();
        var file = CreateFile("Manual/single.mkv", EpisodeNumberOverride : 12m,
                              EpisodeEndOverride : 12m);

        Assert.Equal(12m, _resolver.EpisodeOf(task, file));
    }

    [Fact]
    public void ManualSingleEpisodeOverride_KeepsPriorityOverParsedRange()
    {
        var task = new DownloadTask();
        var file = CreateFile(RangeEpisodePath, EpisodeNumberOverride : 12m);

        Assert.Equal(12m, _resolver.EpisodeOf(task, file));
    }

    [Fact]
    public void Seed_DoesNotClaimManualEpisodeIdentity()
    {
        var task = new DownloadTask();
        var file = CreateFile(SingleEpisodePath, DownloadFileKind.Seed,
                              7m);

        Assert.Null(_resolver.EpisodeOf(task, file));
    }

    [Fact]
    public void ParsedEpisodeRange_DoesNotClaimSingleEpisodeIdentity()
    {
        var task = new DownloadTask();
        var file = CreateFile(RangeEpisodePath);

        Assert.Null(_resolver.EpisodeOf(task, file));
    }

    [Fact]
    public void ParsedSingleEpisode_UsesFileNameOnly()
    {
        var task = new DownloadTask();
        var file = CreateFile(SingleEpisodePath);

        Assert.Equal(7m, _resolver.EpisodeOf(task, file));
    }

    private static DownloadTaskFile CreateFile(
        string           path,
        DownloadFileKind Kind                  = DownloadFileKind.Anime,
        decimal?         EpisodeNumberOverride = null,
        decimal?         EpisodeEndOverride    = null) =>
        new()
        {
            OriginalRelativePath  = path,
            CurrentRelativePath   = path,
            Kind                  = Kind,
            EpisodeNumberOverride = EpisodeNumberOverride,
            EpisodeEndOverride    = EpisodeEndOverride
        };
}
