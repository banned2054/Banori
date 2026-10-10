using MediaManager.Features.Settings;
using System.Security.Cryptography;
using System.Text;

namespace MediaManager.Features.Downloads;

public enum QbittorrentHookResult
{
    /// <summary>令牌有效，已触发一次对账脉冲。</summary>
    Accepted,

    /// <summary>令牌缺失或不匹配（含尚未生成令牌）。为避免暴露端点状态，统一拒绝。</summary>
    Rejected,

    /// <summary>hash 参数不是合法的 v1 info-hash（40 位十六进制）。</summary>
    InvalidHash
}

/// <summary>
///     qBittorrent“添加时/完成时运行外部程序”钩子的处理入口：校验令牌、格式化 hash，
///     然后触发一次对账脉冲。实际状态推进完全复用 DownloadReconcileWorker 的对账逻辑，
///     因此钩子幂等且可丢——15 秒轮询始终是兜底。
/// </summary>
public sealed class QbittorrentHookService(
    ManageQbittorrentSettings       settings,
    ReconcilePulse                  reconcilePulse,
    ILogger<QbittorrentHookService> logger)
{
    public async Task<QbittorrentHookResult> HandleAsync(
        string? token, string? hash, string? eventName, CancellationToken cancellationToken = default)
    {
        var expected = await settings.GetHookTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(expected) || !TokenEquals(expected, token)) return QbittorrentHookResult.Rejected;

        if (!string.IsNullOrWhiteSpace(hash) && !IsValidInfoHash(hash))
            return QbittorrentHookResult.InvalidHash;

        logger.LogInformation("收到 qBittorrent 钩子：event={Event} hash={Hash}，立即触发对账。",
                              string.IsNullOrWhiteSpace(eventName) ? "unknown" : eventName.Trim(),
                              string.IsNullOrWhiteSpace(hash) ? "-" : hash.Trim().ToLowerInvariant());
        reconcilePulse.Pulse();
        return QbittorrentHookResult.Accepted;
    }

    /// <summary>常数时间比较，避免令牌逐字节比对的时序侧信道。</summary>
    private static bool TokenEquals(string expected, string? actual)
    {
        if (string.IsNullOrEmpty(actual)) return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes   = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool IsValidInfoHash(string hash)
    {
        var trimmed = hash.Trim();
        return trimmed.Length == 40 && trimmed.All(char.IsAsciiHexDigit);
    }
}
