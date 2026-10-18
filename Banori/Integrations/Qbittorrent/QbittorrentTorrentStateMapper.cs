using Banned.Qbittorrent.Models.Enums;

namespace Banori.Integrations.Qbittorrent;

/// <summary>把 qBittorrent 种子状态翻译为业务对账需要的类别，便于独立测试。</summary>
public static class QbittorrentTorrentStateMapper
{
    public static QbittorrentTorrentCategory Map(EnumTorrentState state)
    {
        return state switch
        {
            EnumTorrentState.CheckingDownload
             or EnumTorrentState.CheckingResumeData
             or EnumTorrentState.CheckingUpload
                => QbittorrentTorrentCategory.Checking,

            EnumTorrentState.Downloading
             or EnumTorrentState.MetaDownload
             or EnumTorrentState.StalledDownload
             or EnumTorrentState.QueuedDownload
             or EnumTorrentState.ForcedDownload
             or EnumTorrentState.Allocating
             or EnumTorrentState.Moving
                => QbittorrentTorrentCategory.Downloading,

            EnumTorrentState.StoppedDownload
                => QbittorrentTorrentCategory.Stopped,

            EnumTorrentState.Uploading
             or EnumTorrentState.StalledUpload
             or EnumTorrentState.ForcedUpload
             or EnumTorrentState.QueuedUpload
             or EnumTorrentState.StoppedUpload
                => QbittorrentTorrentCategory.Seeding,

            EnumTorrentState.Error
             or EnumTorrentState.MissingFiles
                => QbittorrentTorrentCategory.Error,

            _ => QbittorrentTorrentCategory.Unknown
        };
    }
}
