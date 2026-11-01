using Banori.Features.Downloads;

namespace Banori.Tests.Downloads;

public sealed class DownloadTaskFilterTests
{
    [Fact]
    public void Choices_ListAllGroupsOnceWithChineseText()
    {
        Assert.Equal(Enum.GetValues<DownloadStatusFilterGroup>(), DownloadStatusFilter.Choices);
        Assert.All(DownloadStatusFilter.Choices,
                   group => Assert.False(string.IsNullOrWhiteSpace(DownloadStatusFilter.Text(group))));
    }

    [Theory]
    [InlineData(DownloadDisplayPhase.WaitingSubmit,      DownloadStatusFilterGroup.WaitingSubmit)]
    [InlineData(DownloadDisplayPhase.WaitingAddConfirm,  DownloadStatusFilterGroup.WaitingAddConfirm)]
    [InlineData(DownloadDisplayPhase.Preparing,          DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.PreparingLayout,    DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.WaitingCheckStart,  DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.Checking,           DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.WaitingResultStable, DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.WaitingStopConfirm, DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.WaitingStartConfirm, DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.IsolationMigrating, DownloadStatusFilterGroup.PreparingChecking)]
    [InlineData(DownloadDisplayPhase.Downloading,        DownloadStatusFilterGroup.Downloading)]
    [InlineData(DownloadDisplayPhase.PausedManually,     DownloadStatusFilterGroup.Paused)]
    [InlineData(DownloadDisplayPhase.PausedByDownloader, DownloadStatusFilterGroup.Paused)]
    [InlineData(DownloadDisplayPhase.Paused,             DownloadStatusFilterGroup.Paused)]
    [InlineData(DownloadDisplayPhase.Completed,          DownloadStatusFilterGroup.Completed)]
    [InlineData(DownloadDisplayPhase.Archiving,          DownloadStatusFilterGroup.Completed)]
    [InlineData(DownloadDisplayPhase.Failed,             DownloadStatusFilterGroup.Failed)]
    [InlineData(DownloadDisplayPhase.Lost,               DownloadStatusFilterGroup.Lost)]
    [InlineData(DownloadDisplayPhase.Replaced,           DownloadStatusFilterGroup.Replaced)]
    public void GroupOf_MapsEveryDisplayPhaseToBadgeAlignedBucket(
        DownloadDisplayPhase phase, DownloadStatusFilterGroup expected) =>
        Assert.Equal(expected, DownloadStatusFilter.GroupOf(phase));

    [Fact]
    public void GroupOf_ReplacedFallbackBucketIsExclusiveToReplacedPhase()
    {
        var replacedOnly = Enum.GetValues<DownloadDisplayPhase>()
                               .Where(phase => DownloadStatusFilter.GroupOf(phase) ==
                                               DownloadStatusFilterGroup.Replaced)
                               .ToList();
        Assert.Equal([DownloadDisplayPhase.Replaced], replacedOnly);
    }
}
