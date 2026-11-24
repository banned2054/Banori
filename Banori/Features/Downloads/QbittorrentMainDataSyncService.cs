using Banori.Features.Settings;
using Banori.Integrations.Qbittorrent;
using System.Security.Cryptography;
using System.Text;

namespace Banori.Features.Downloads;

public sealed record QbittorrentMainDataSnapshot(
    IReadOnlyDictionary<string, QbittorrentMainDataTorrent> Torrents,
    long                                                    CurrentRid,
    long                                                    SyncRequestCount,
    long                                                    LastResponseBytes,
    long                                                    TotalResponseBytes,
    DateTimeOffset?                                         LastSyncedAtUtc,
    string?                                                 ConfigKey)
{
    public static QbittorrentMainDataSnapshot Empty { get; } = new(
        new Dictionary<string, QbittorrentMainDataTorrent>(StringComparer.OrdinalIgnoreCase),
        0, 0, 0, 0, null, null);
}

/// <summary>
///     下载器单实例 <c>sync/maindata</c> 增量同步服务：按订阅按需运行后台串行轮询，
///     多页面共享同一个登录会话与 <c>rid</c> 增量状态；无订阅者时停止轮询并释放会话，
///     配置切换或连接异常时重置 <c>rid = 0</c> 并在下轮触发全量刷新。
/// </summary>
public sealed class QbittorrentMainDataSyncService(
    IServiceScopeFactory                    scopeFactory,
    TimeProvider                            timeProvider,
    ILogger<QbittorrentMainDataSyncService> logger) : IAsyncDisposable
{
    private readonly Lock                                                  _gate     = new();
    private readonly Dictionary<long, Action<QbittorrentMainDataSnapshot>> _handlers = [];
    private readonly SemaphoreSlim                                         _runGate  = new(1, 1);
    private readonly Dictionary<string, QbittorrentMainDataTorrent>        _torrents =
        new(StringComparer.OrdinalIgnoreCase);

    private string?                     _activeConfigKey;
    private QbittorrentMainDataSnapshot _currentSnapshot = QbittorrentMainDataSnapshot.Empty;
    private bool                        _disposed;
    private long                        _lastResponseBytes;
    private DateTimeOffset?             _lastSyncedAtUtc;
    private CancellationTokenSource?    _loopCts;
    private Task?                       _loopTask;
    private long                        _nextId;
    private long                        _rid;
    private IQbittorrentSession?        _session;
    private long                        _syncRequestCount;
    private long                        _totalResponseBytes;

    /// <summary>测试可覆写轮询间隔；生产默认约 2 秒。</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>当前最近一次已应用的运行时快照与可观测计数。</summary>
    public QbittorrentMainDataSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate) return _currentSnapshot;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? ctsToCancel;
        Task?                    loopTask;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed   = true;
            _handlers.Clear();
            ctsToCancel = _loopCts;
            _loopCts    = null;
            loopTask    = _loopTask;
            _loopTask   = null;
        }

        ctsToCancel?.Cancel();
        if (loopTask is not null)
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
            }

        _runGate.Dispose();
    }

    /// <summary>订阅增量同步推送；首个订阅者触发后台轮询，最后一个订阅者释放后停止。</summary>
    public IDisposable Subscribe(Action<QbittorrentMainDataSnapshot> onUpdated)
    {
        ArgumentNullException.ThrowIfNull(onUpdated);
        long                         id;
        QbittorrentMainDataSnapshot? initialSnapshot = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id            = ++_nextId;
            _handlers[id] = onUpdated;
            if (_currentSnapshot.LastSyncedAtUtc is not null) initialSnapshot = _currentSnapshot;
            if (_loopCts is null)
            {
                var cts   = new CancellationTokenSource();
                _loopCts  = cts;
                _loopTask = Task.Run(() => RunLoopAsync(cts), CancellationToken.None);
            }
        }

        if (initialSnapshot is not null) InvokeHandlerSafely(onUpdated, initialSnapshot);
        return new Subscription(this, id);
    }

    private bool HasSubscribers()
    {
        lock (_gate) return !_disposed && _handlers.Count > 0;
    }

    private void Unsubscribe(long id)
    {
        CancellationTokenSource? ctsToCancel = null;
        lock (_gate)
        {
            if (!_handlers.Remove(id)) return;
            if (_handlers.Count == 0 && _loopCts is not null)
            {
                ctsToCancel = _loopCts;
                _loopCts    = null;
            }
        }

        ctsToCancel?.Cancel();
    }

    private async Task RunLoopAsync(CancellationTokenSource loopCts)
    {
        var token = loopCts.Token;
        await _runGate.WaitAsync(CancellationToken.None);
        try
        {
            while (!token.IsCancellationRequested && HasSubscribers())
            {
                try
                {
                    await SyncStepAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "qBittorrent sync/maindata 同步异常，重置会话与 rid=0 准备下轮全量重试。");
                    await ResetSessionAsync(clearTorrents : false, updateMetricsSnapshot : true,
                                            notifySubscribers : false);
                }

                if (token.IsCancellationRequested || !HasSubscribers()) break;
                try
                {
                    await Task.Delay(PollInterval, timeProvider, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            await ResetSessionAsync(clearTorrents : false, updateMetricsSnapshot : true, notifySubscribers : false);
            loopCts.Dispose();
            _runGate.Release();
        }
    }

    private async Task SyncStepAsync(CancellationToken cancellationToken)
    {
        await using var scope           = scopeFactory.CreateAsyncScope();
        var             settingsService = scope.ServiceProvider.GetRequiredService<ManageQbittorrentSettings>();
        var             (settings, password) = await settingsService.GetConnectionAsync(cancellationToken);

        if (!settings.Enabled || !settings.IsConfigured)
        {
            var wasNonEmpty = _activeConfigKey is not null || _torrents.Count > 0 || _session is not null ||
                              _rid != 0;
            _activeConfigKey = null;
            await ResetSessionAsync(clearTorrents : true, updateMetricsSnapshot : true,
                                    notifySubscribers : wasNonEmpty);
            return;
        }

        var configKey = BuildConfigKey(settings, password);
        if (!string.Equals(_activeConfigKey, configKey, StringComparison.Ordinal))
        {
            // 下载器地址/凭据/代理变化时，必须销毁旧会话并清空内存态，避免把旧实例的 rid/进度混给新实例。
            await ResetSessionAsync(clearTorrents : true, updateMetricsSnapshot : true,
                                    notifySubscribers : _torrents.Count > 0);
            _activeConfigKey = configKey;
        }

        if (_session is null)
        {
            var sessionFactory = scope.ServiceProvider.GetRequiredService<IQbittorrentSessionFactory>();
            _session = await sessionFactory.CreateAsync(settings, password, cancellationToken);
            _rid     = 0;
        }

        _syncRequestCount++;
        var delta = await _session.SyncMainDataAsync(_rid, cancellationToken);
        _lastResponseBytes  =  delta.ResponseBytes;
        _totalResponseBytes += delta.ResponseBytes;

        if (delta.Rid < 0)
            throw new InvalidDataException($"sync/maindata 返回无效 rid: {delta.Rid}。");

        if (_rid == 0 || delta.FullUpdate)
        {
            ApplyFullUpdate(delta);
            return;
        }

        if (!TryApplyIncrementalDelta(delta, out var hasTorrentChanges))
        {
            // 增量流无法衔接（例如遇到未知 Hash）：不推进 rid，重置 rid=0 触发下轮全量拉取。
            _rid = 0;
            PublishCurrentSnapshot(notifySubscribers : false);
            return;
        }

        _rid             = delta.Rid;
        _lastSyncedAtUtc = timeProvider.GetUtcNow();
        PublishCurrentSnapshot(notifySubscribers : hasTorrentChanges);
    }

    private void ApplyFullUpdate(QbittorrentMainDataDelta delta)
    {
        _torrents.Clear();
        foreach (var (rawHash, itemDelta) in delta.Torrents)
        {
            var hash = NormalizeHash(rawHash);
            if (hash.Length == 0) continue;
            _torrents[hash] = MergeTorrent(null, hash, itemDelta);
        }

        foreach (var rawHash in delta.TorrentsRemoved)
        {
            var hash = NormalizeHash(rawHash);
            if (hash.Length > 0) _torrents.Remove(hash);
        }

        _rid             = delta.Rid;
        _lastSyncedAtUtc = timeProvider.GetUtcNow();
        PublishCurrentSnapshot(notifySubscribers : true);
    }

    private bool TryApplyIncrementalDelta(QbittorrentMainDataDelta delta, out bool hasTorrentChanges)
    {
        hasTorrentChanges = false;
        var staged = new Dictionary<string, QbittorrentMainDataTorrent>(_torrents.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (rawHash, itemDelta) in delta.Torrents)
        {
            var hash = NormalizeHash(rawHash);
            if (hash.Length == 0) continue;
            if (!_torrents.TryGetValue(hash, out var existing)) return false;
            staged[hash] = MergeTorrent(existing, hash, itemDelta);
        }

        foreach (var (hash, updated) in staged)
        {
            _torrents[hash]   = updated;
            hasTorrentChanges = true;
        }

        foreach (var rawHash in delta.TorrentsRemoved)
        {
            var hash = NormalizeHash(rawHash);
            if (hash.Length > 0 && _torrents.Remove(hash)) hasTorrentChanges = true;
        }

        return true;
    }

    private async Task ResetSessionAsync(bool clearTorrents, bool updateMetricsSnapshot, bool notifySubscribers)
    {
        if (_session is { } existingSession)
        {
            _session = null;
            try
            {
                await existingSession.DisposeAsync();
            }
            catch
            {
                // 清理失效会话时忽略关闭异常，保证后续重建。
            }
        }

        _rid = 0;
        if (clearTorrents)
        {
            _torrents.Clear();
            _lastSyncedAtUtc = null;
        }

        if (updateMetricsSnapshot) PublishCurrentSnapshot(notifySubscribers);
    }

    private void PublishCurrentSnapshot(bool notifySubscribers)
    {
        var copy = new Dictionary<string, QbittorrentMainDataTorrent>(_torrents, StringComparer.OrdinalIgnoreCase);
        var snapshot = new QbittorrentMainDataSnapshot(copy, _rid, _syncRequestCount, _lastResponseBytes,
                                                       _totalResponseBytes, _lastSyncedAtUtc, _activeConfigKey);
        Action<QbittorrentMainDataSnapshot>[]? targets = null;
        lock (_gate)
        {
            _currentSnapshot = snapshot;
            if (notifySubscribers && _handlers.Count > 0) targets = [.. _handlers.Values];
        }

        if (targets is null) return;
        foreach (var handler in targets) InvokeHandlerSafely(handler, snapshot);
    }

    private static QbittorrentMainDataTorrent MergeTorrent(
        QbittorrentMainDataTorrent? existing, string hash, QbittorrentTorrentDelta delta) =>
        new(hash,
            delta.Name      ?? existing?.Name      ?? string.Empty,
            delta.Category  ?? existing?.Category  ?? QbittorrentTorrentCategory.Unknown,
            delta.RawState  ?? existing?.RawState,
            delta.IsMoving  ?? existing?.IsMoving  ?? false,
            delta.Progress  ?? existing?.Progress  ?? 0,
            delta.TotalSize ?? existing?.TotalSize ?? 0,
            delta.SavePath  ?? existing?.SavePath  ?? string.Empty);

    private static void InvokeHandlerSafely(
        Action<QbittorrentMainDataSnapshot> handler, QbittorrentMainDataSnapshot snapshot)
    {
        try
        {
            handler(snapshot);
        }
        catch
        {
            // 单个订阅者异常不得影响同步循环或其他页面订阅。
        }
    }

    private static string BuildConfigKey(QbittorrentSettings settings, string? password)
    {
        var material = string.Join('\u001f',
                                   settings.Enabled ? "1" : "0",
                                   settings.BaseUrl.Trim().TrimEnd('/'),
                                   settings.Username,
                                   settings.SavePath.Trim(),
                                   settings.ProxyId?.ToString() ?? string.Empty,
                                   password ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string NormalizeHash(string hash) => hash.Trim().ToLowerInvariant();

    private sealed class Subscription(QbittorrentMainDataSyncService owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(id);
        }
    }
}
