using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Utils;

namespace Banori.Integrations.Qbittorrent;

/// <summary>把 qBittorrent 种子状态翻译为业务对账需要的类别，便于独立测试。</summary>
public static class QbittorrentTorrentStateMapper
{
    public static QbittorrentTorrentCategory MapRawState(string? rawState, out bool isMoving)
    {
        if (string.IsNullOrWhiteSpace(rawState))
        {
            isMoving = false;
            return QbittorrentTorrentCategory.Unknown;
        }

        var state = StringUtils.String2TorrentState(rawState.Trim());
        isMoving = state == EnumTorrentState.Moving;
        return Map(state);
    }

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

            // StoppedUpload 是「做种后停止」：仍是停止（含已完成停止），完成程度由进度与文件结果独立判定。
            EnumTorrentState.StoppedDownload
             or EnumTorrentState.StoppedUpload
                => QbittorrentTorrentCategory.Stopped,

            EnumTorrentState.Uploading
             or EnumTorrentState.StalledUpload
             or EnumTorrentState.ForcedUpload
             or EnumTorrentState.QueuedUpload
                => QbittorrentTorrentCategory.Seeding,

            EnumTorrentState.Error
             or EnumTorrentState.MissingFiles
                => QbittorrentTorrentCategory.Error,

            _ => QbittorrentTorrentCategory.Unknown
        };
    }
}
