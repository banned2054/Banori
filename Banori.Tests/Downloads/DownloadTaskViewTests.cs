using Banori.Features.Downloads;

namespace Banori.Tests.Downloads;

public sealed class DownloadTaskViewTests
{
    [Theory]
    [InlineData(DownloadTaskStatus.PendingSubmit, true)]
    [InlineData(DownloadTaskStatus.Submitting, true)]
    [InlineData(DownloadTaskStatus.Downloading, true)]
    [InlineData(DownloadTaskStatus.Paused, true)]
    [InlineData(DownloadTaskStatus.Completed, false)]
    [InlineData(DownloadTaskStatus.Failed, true)]
    [InlineData(DownloadTaskStatus.Lost, true)]
    [InlineData(DownloadTaskStatus.Replaced, false)]
    public void ShowPendingReplacement_HidesCompletedAndReplacedTasksWithoutClearingHistory(
        DownloadTaskStatus status, bool expected)
    {
        var replacedId = Guid.NewGuid();
        var task = CreateTask(status) with
        {
            ReplacesTaskId = replacedId,
            ReplacesNaming = "previous-release",
            Progress = 1
        };

        Assert.Equal(expected, task.ShowPendingReplacement);
        Assert.Equal(replacedId, task.ReplacesTaskId);
        Assert.Equal("previous-release", task.ReplacesNaming);
    }

    [Theory]
    [InlineData(DownloadTaskStatus.PendingSubmit)]
    [InlineData(DownloadTaskStatus.Submitting)]
    [InlineData(DownloadTaskStatus.Downloading)]
    [InlineData(DownloadTaskStatus.Paused)]
    [InlineData(DownloadTaskStatus.Completed)]
    [InlineData(DownloadTaskStatus.Failed)]
    [InlineData(DownloadTaskStatus.Lost)]
    [InlineData(DownloadTaskStatus.Replaced)]
    public void ShowPendingReplacement_WithoutRelatedName_IsHidden(DownloadTaskStatus status)
    {
        Assert.False(CreateTask(status).ShowPendingReplacement);
    }

    private static DownloadTaskView CreateTask(DownloadTaskStatus status) => new()
    {
        Id           = Guid.NewGuid(),
        FeedItemId   = null,
        Status       = status,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };
}
