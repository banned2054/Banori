using Banned.Qbittorrent.Exceptions;
using MediaManager.Core.Torrents;
using MediaManager.Features.Settings;
using MediaManager.Features.Subscriptions;
using MediaManager.Integrations;
using MediaManager.Integrations.Qbittorrent;
using System.Text;

namespace MediaManager.Tests.Downloads;

/// <summary>内存应用设置存储，行为与 EfApplicationSettingStore 的读写语义一致（不含加密）。</summary>
public sealed class MemoryApplicationSettingStore : IApplicationSettingStore
{
    public Dictionary<string, (string Value, bool IsProtected)> Values { get; } = new();

    /// <summary>设置后每次读取抛出该异常，模拟设置存储故障。</summary>
    public Exception? ThrowOnGet { get; set; }

    /// <summary>设置后读取以该前缀开头的键时抛出异常，模拟仅部分功能（如通知）的设置故障。</summary>
    public string? ThrowOnGetKeyPrefix { get; set; }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (ThrowOnGet is not null) throw ThrowOnGet;
        if (ThrowOnGetKeyPrefix is not null && key.StartsWith(ThrowOnGetKeyPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("模拟设置存储故障。");
        return Task.FromResult(Values.TryGetValue(key, out var value) ? value.Value : null);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        return SetCore(key, value, false);
    }

    public Task SetProtectedAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        return SetCore(key, value, true);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }

    private Task SetCore(string key, string value, bool isProtected)
    {
        Values[key] = (value, isProtected);
        return Task.CompletedTask;
    }
}

/// <summary>模拟 qBittorrent 服务器内部的种子存储：跨会话共享，天然表达“下载器状态在应用重启后仍在”。</summary>
public sealed class FakeTorrentStore
{
    public Dictionary<string, TorrentSnapshot> Torrents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>>    Files    { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string>                     Paused   { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(byte[] Bytes, string SavePath, IReadOnlyList<string> Tags, string? Rename)> AddCalls { get; } = [];
    public List<(string Hash, string Name)>                    RenameCalls       { get; } = [];
    public List<(string Hash, string OldPath, string NewPath)> RenameFileCalls   { get; } = [];
    public List<(string Hash, string OldPath, string NewPath)> RenameFolderCalls { get; } = [];
    public List<(string Hash, string Tag)>                     AddTagCalls       { get; } = [];
    public List<string>                                        ResumeCalls       { get; } = [];
    public List<string>                                        PauseCalls        { get; } = [];

    public void AddTorrent(string hash,     string name, QbittorrentTorrentCategory category, double progress,
                           string savePath, IReadOnlyList<string>? fileNames = null,
                           IReadOnlyList<string>? tags = null)
    {
        Torrents[hash] = new TorrentSnapshot(hash, name, category, progress, 1024, savePath, tags ?? []);
        if (fileNames is not null) Files[hash] = [.. fileNames];
    }
}

/// <summary>假会话：把提交直接登记进 FakeTorrentStore，用 Core 提取器从字节推导 hash。</summary>
public sealed class FakeQbittorrentSession(FakeTorrentStore store) : IQbittorrentSession
{
    /// <summary>设置后 AddTorrent 抛出该异常，模拟协议错误。</summary>
    public Exception? AddException { get; set; }

    /// <summary>设置后 AddTorrent 返回该文本，模拟服务器拒绝或其他响应。</summary>
    public string? AddResponse { get; set; }

    /// <summary>设置后 AddTorrent 静默丢弃：响应丢失且请求未到达下载器。</summary>
    public bool DropAdd { get; set; }

    /// <summary>设置后 AddTorrent 登记种子但返回 Fails 之前抛出传输异常，模拟“已生效但响应丢失”。</summary>
    public bool RegisterThenThrowTransport { get; set; }

    /// <summary>设置后 AddTorrent 对已存在 hash 返回 "Fails."，模拟 qBittorrent 对重复种子的真实响应。</summary>
    public bool FailOnDuplicate { get; set; }

    /// <summary>AddTorrent 发出前回调，用于断言请求发出时本地已持久化的状态。</summary>
    public Func<Task>? BeforeAddTorrentAsync { get; set; }

    public string ServerVersion => "v5.0.0-fake";

    public async Task<string> AddTorrentAsync(
        byte[] torrentBytes, string savePath, IReadOnlyList<string> tags, string? rename,
        CancellationToken cancellationToken = default)
    {
        if (BeforeAddTorrentAsync is not null) await BeforeAddTorrentAsync();

        if (AddException is not null) throw AddException;

        var hash = TorrentInfoHashExtractor.TryExtractInfoHash(torrentBytes, out var infoHash)
            ? infoHash
            : throw new InvalidOperationException("测试种子字节无效。");

        if (FailOnDuplicate && store.Torrents.ContainsKey(hash)) return "Fails.";

        if (RegisterThenThrowTransport)
        {
            store.AddCalls.Add((torrentBytes, savePath, tags, rename));
            store.Torrents[hash] = new TorrentSnapshot(hash, $"torrent-{hash[..8]}",
                                                       QbittorrentTorrentCategory.Downloading, 0, 1024, savePath, []);
            store.Files.TryAdd(hash, ["原文件名.mkv"]);
            throw new QbittorrentException("模拟响应丢失");
        }

        store.AddCalls.Add((torrentBytes, savePath, tags, rename));
        if (DropAdd) return "Ok.";

        if (AddResponse is not null) return AddResponse;

        store.Torrents[hash] = new TorrentSnapshot(hash, rename ?? $"torrent-{hash[..8]}",
                                                   QbittorrentTorrentCategory.Downloading, 0, 1024, savePath,
                                                   tags.ToList());
        store.Files.TryAdd(hash, ["原文件名.mkv"]);
        return "Ok.";
    }

    public Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TorrentSnapshot>>(infoHashes
                                                              .Where(hash => store.Torrents.ContainsKey(hash))
                                                              .Select(SnapshotFor)
                                                              .ToList());
    }


    public Task<IReadOnlyList<TorrentSnapshot>> ListTorrentsAsync(
        string? tag, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TorrentSnapshot>>(store.Torrents.Values
                                                                    .Where(snapshot => tag is null ||
                                                                          SnapshotFor(snapshot.Hash)
                                                                             .Tags.Contains(tag,
                                                                                 StringComparer
                                                                                    .OrdinalIgnoreCase))
                                                                    .Select(snapshot => SnapshotFor(snapshot.Hash))
                                                                    .ToList());
    }

    public Task AddTagAsync(string infoHash, string tag, CancellationToken cancellationToken = default)
    {
        store.AddTagCalls.Add((infoHash, tag));
        if (store.Torrents.TryGetValue(infoHash, out var snapshot) &&
            !snapshot.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            store.Torrents[infoHash] = snapshot with { Tags = [.. snapshot.Tags, tag] };

        return Task.CompletedTask;
    }


    public Task<IReadOnlyList<string>> ListFileNamesAsync(
        string infoHash, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(store.Files.TryGetValue(infoHash, out var names) ? names : []);
    }


    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        store.PauseCalls.Add(infoHash);
        store.Paused.Add(infoHash);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        store.ResumeCalls.Add(infoHash);
        store.Paused.Remove(infoHash);
        return Task.CompletedTask;
    }

    public Task RenameTorrentAsync(string infoHash, string newName, CancellationToken cancellationToken = default)
    {
        store.RenameCalls.Add((infoHash, newName));
        if (store.Torrents.TryGetValue(infoHash, out var snapshot))
            store.Torrents[infoHash] = snapshot with { Name = newName };

        return Task.CompletedTask;
    }

    public Task RenameTorrentFileAsync(string            infoHash, string oldPath, string newPath,
                                       CancellationToken cancellationToken = default)
    {
        store.RenameFileCalls.Add((infoHash, oldPath, newPath));
        if (store.Files.TryGetValue(infoHash, out var fileNames))
            store.Files[infoHash] = fileNames.Select(name => name == oldPath ? newPath : name).ToList();

        return Task.CompletedTask;
    }

    public Task RenameTorrentFolderAsync(
        string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        store.RenameFolderCalls.Add((infoHash, oldPath, newPath));
        if (store.Files.TryGetValue(infoHash, out var folderFiles))
            store.Files[infoHash] = folderFiles
                                   .Select(name => name.StartsWith(oldPath + "/", StringComparison.Ordinal)
                                               ? newPath + name[oldPath.Length..]
                                               : name)
                                   .ToList();

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private TorrentSnapshot SnapshotFor(string hash)
    {
        var snapshot = store.Torrents[hash];
        var category = store.Paused.Contains(hash) && snapshot.Category == QbittorrentTorrentCategory.Downloading
            ? QbittorrentTorrentCategory.Stopped
            : snapshot.Category;
        return snapshot with { Category = category };
    }
}

/// <summary>每次创建新会话，共享同一个下载器存储；模拟 qBittorrent 独立于应用进程运行。</summary>
public sealed class FakeQbittorrentSessionFactory(FakeTorrentStore store) : IQbittorrentSessionFactory
{
    /// <summary>设置后 Create 抛错，模拟下载器不可达。</summary>
    public bool ThrowOnCreate { get; set; }

    /// <summary>每个新会话创建后先应用该配置，用于注入提交失败、响应丢失等行为。</summary>
    public Action<FakeQbittorrentSession>? SessionConfigurator { get; set; }

    public FakeQbittorrentSession LastSession { get; private set; } = null!;

    public Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        return CreateAsync(new QbittorrentSettings("http://qb.test", "admin", "/downloads", true), "secret",
                           cancellationToken);
    }

    public Task<IQbittorrentSession> CreateAsync(QbittorrentSettings settings, string? password,
                                                 CancellationToken   cancellationToken = default)
    {
        if (ThrowOnCreate) throw new QbittorrentException("模拟下载器不可达");

        LastSession = new FakeQbittorrentSession(store);
        SessionConfigurator?.Invoke(LastSession);
        return Task.FromResult<IQbittorrentSession>(LastSession);
    }
}

/// <summary>假种子源：默认返回固定种子字节；可注入异常模拟源站失败。</summary>
public sealed class FakeTorrentFileSource(Func<Uri, byte[]> resolver) : ITorrentFileSource
{
    public int CallCount { get; private set; }

    /// <summary>最近一次下载请求携带的代理 Id，供传递断言使用。</summary>
    public Guid? LastProxyId { get; private set; }

    /// <summary>按绝对 URI 注入的下载异常，模拟个别源站失败。</summary>
    public Dictionary<string, Exception> ThrowOnUri { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<byte[]> DownloadAsync(Uri               torrentUri, Guid? proxyId = default,
                                      CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastProxyId = proxyId;
        if (ThrowOnUri.TryGetValue(torrentUri.AbsoluteUri, out var exception)) throw exception;
        return Task.FromResult(resolver(torrentUri));
    }
}

/// <summary>假 RSS 源读取器：返回预设条目；可注入异常模拟抓取失败。</summary>
public sealed class FakeFeedSourceReader : IFeedSourceReader
{
    public List<FeedSourceItem> Items { get; } = [];

    /// <summary>设置后读取时抛出该异常，模拟 feed 不可达或解析失败。</summary>
    public Exception? ThrowOnRead { get; set; }

    /// <summary>最近一次读取请求携带的代理 Id，供传递断言使用。</summary>
    public Guid? LastProxyId { get; private set; }

    public Task<IReadOnlyList<FeedSourceItem>> ReadAsync(
        Uri feedUri, Guid? proxyId = default, CancellationToken cancellationToken = default)
    {
        LastProxyId = proxyId;
        if (ThrowOnRead is not null) throw ThrowOnRead;
        return Task.FromResult<IReadOnlyList<FeedSourceItem>>(Items);
    }
}

/// <summary>构造确定性的最小 .torrent 字节；name 不同则 info-hash 不同，announce 可注入 tracker 供站点判定测试。</summary>
public static class TestTorrentBytes
{
    public static byte[] Create(string name = "Test Anime - 01", string? announce = null)
    {
        var announceText = announce ?? "http://tracker.example/announce";
        return Encoding.UTF8.GetBytes(
                                      $"d8:announce{Encoding.UTF8.GetByteCount(announceText)}:{announceText}" +
                                      "4:infod6:lengthi1024e"                         +
                                      $"4:name{Encode(name)}"                         +
                                      "12:piece lengthi16384e"                        +
                                      "ee");
    }

    public static string InfoHashOf(string name)
    {
        return TorrentInfoHashExtractor.TryExtractInfoHash(Create(name), out var hash)
            ? hash
            : throw new InvalidOperationException("测试种子字节无效。");
    }

    private static string Encode(string value)
    {
        return $"{Encoding.UTF8.GetByteCount(value)}:{value}";
    }
}
