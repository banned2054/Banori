using Banned.Qbittorrent.Exceptions;
using Banned.Qbittorrent.Models.Torrent;
using Banori.Integrations.Qbittorrent;

namespace Banori.Tests.Integrations;

/// <summary>
///     生产适配层对 torrents/info 原始列表的判空契约：只有成功返回的有效列表才能证明查无；
///     JSON null（库会把 null 归并成空列表）必须在适配层翻译成异常，按「未知」处理，
///     不得让缺种巡检或清理确认把无效响应记作缺种证据。
/// </summary>
public sealed class QbittorrentSessionInfoListTests
{
    [Fact]
    public async Task FindTorrents_NullResponse_ThrowsInsteadOfReportingNotFound()
    {
        await using var session = new QbittorrentSession(null!)
        {
            InfoListFetcher = (_, _, _) => Task.FromResult<List<TorrentInfo>?>(null)
        };

        await Assert.ThrowsAsync<QbittorrentException>(() =>
            session.FindTorrentsAsync([new string('a', 40)]));
    }

    [Fact]
    public async Task FindTorrents_EmptyArray_IsSuccessfulNotFound()
    {
        await using var session = new QbittorrentSession(null!)
        {
            InfoListFetcher = (_, _, _) => Task.FromResult<List<TorrentInfo>?>([])
        };

        Assert.Empty(await session.FindTorrentsAsync([new string('a', 40)]));
    }

    [Fact]
    public async Task ListTorrents_NullResponse_ThrowsInsteadOfReportingEmpty()
    {
        await using var session = new QbittorrentSession(null!)
        {
            InfoListFetcher = (_, _, _) => Task.FromResult<List<TorrentInfo>?>(null)
        };

        await Assert.ThrowsAsync<QbittorrentException>(() => session.ListTorrentsAsync(null));
    }

    [Fact]
    public async Task ListTorrents_EmptyArray_IsSuccessfulEmpty()
    {
        await using var session = new QbittorrentSession(null!)
        {
            InfoListFetcher = (_, _, _) => Task.FromResult<List<TorrentInfo>?>([])
        };

        Assert.Empty(await session.ListTorrentsAsync(null));
    }
}
