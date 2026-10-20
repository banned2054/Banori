using Banned.Qbittorrent.Models.Enums;
using Banori.Integrations.Qbittorrent;

namespace Banori.Tests.Downloads;

/// <summary>qBittorrent 状态到业务类别的映射回归，决定对账如何推进本地状态。</summary>
public sealed class QbittorrentTorrentStateMapperTests
{
    [Theory]
    [InlineData(EnumTorrentState.Downloading, QbittorrentTorrentCategory.Downloading)]
    [InlineData(EnumTorrentState.StalledDownload, QbittorrentTorrentCategory.Downloading)]
    [InlineData(EnumTorrentState.QueuedDownload, QbittorrentTorrentCategory.Downloading)]
    [InlineData(EnumTorrentState.MetaDownload, QbittorrentTorrentCategory.Downloading)]
    [InlineData(EnumTorrentState.CheckingResumeData, QbittorrentTorrentCategory.Checking)]
    [InlineData(EnumTorrentState.CheckingDownload, QbittorrentTorrentCategory.Checking)]
    [InlineData(EnumTorrentState.CheckingUpload, QbittorrentTorrentCategory.Checking)]
    [InlineData(EnumTorrentState.StoppedDownload, QbittorrentTorrentCategory.Stopped)]
    [InlineData(EnumTorrentState.Uploading, QbittorrentTorrentCategory.Seeding)]
    [InlineData(EnumTorrentState.StalledUpload, QbittorrentTorrentCategory.Seeding)]
    [InlineData(EnumTorrentState.StoppedUpload, QbittorrentTorrentCategory.Stopped)]
    [InlineData(EnumTorrentState.Error, QbittorrentTorrentCategory.Error)]
    [InlineData(EnumTorrentState.MissingFiles, QbittorrentTorrentCategory.Error)]
    [InlineData(EnumTorrentState.Unknown, QbittorrentTorrentCategory.Unknown)]
    public void Map_CoversAllRelevantStates(EnumTorrentState state, QbittorrentTorrentCategory expected)
    {
        Assert.Equal(expected, QbittorrentTorrentStateMapper.Map(state));
    }
}
