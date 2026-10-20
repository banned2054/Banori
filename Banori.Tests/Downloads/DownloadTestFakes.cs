using Banned.Qbittorrent.Exceptions;
using Banori.Core.Torrents;
using Banori.Features.Anime;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations;
using Banori.Integrations.Qbittorrent;
using Microsoft.Extensions.Hosting;
using System.Text;

namespace Banori.Tests.Downloads;

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

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        SetCore(key, value, false);

    public Task SetProtectedAsync(string key, string value, CancellationToken cancellationToken = default) =>
        SetCore(key, value, true);

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

    /// <summary>以停止状态添加的种子：在 Resume 前一直报告 Stopped，模拟 qB 的停止添加语义。</summary>
    public HashSet<string> StoppedAdds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<TorrentFileSnapshot>> FileIdentities { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     recheck 行为模型。默认 RecordOnly：只记录调用、不改变任何状态——未显式配置的测试
    ///     绝不能因为 fake 凭空推进校验而变绿。EnterChecking 模拟校验阶段（经 FinishRecheck 推进结束）；
    ///     CompleteImmediately 模拟「快速校验」：请求与完成发生在两次采样之间，永远观察不到 Checking。
    /// </summary>
    public FakeRecheckBehavior RecheckBehavior { get; set; } = FakeRecheckBehavior.RecordOnly;

    /// <summary>处于校验阶段的种子：SnapshotFor 报告 Checking，直到测试调用 FinishRecheck。</summary>
    public HashSet<string> Rechecking { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     改名 onto 已存在路径的策略：Remap 按已验收语义直接改路径映射；Reject 模拟拒绝该操作的
    ///     qB 构建（降级路径测试用）。fake 不建模磁盘覆盖——覆盖行为只能真机验收。
    /// </summary>
    public FakeRenameOntoExistingPolicy RenameOntoExistingPolicy { get; set; } =
        FakeRenameOntoExistingPolicy.Remap;

    public List<(string Hash, string SavePath)> SetLocationCalls { get; } = [];
    public bool                                 DeferLocation    { get; set; }
    public Dictionary<string, string>           PendingLocations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action?                              AfterRenameFile  { get; set; }

    public List<(byte[] Bytes, string SavePath, IReadOnlyList<string> Tags, string? Rename)> AddCalls { get; } = [];
    public List<(string Hash, string Name)> RenameCalls { get; } = [];
    public List<(string Hash, string OldPath, string NewPath)> RenameFileCalls { get; } = [];
    public List<(string Hash, string OldPath, string NewPath)> RenameFolderCalls { get; } = [];
    public List<(string Hash, string Tag)> AddTagCalls { get; } = [];
    public List<string> ResumeCalls { get; } = [];
    public List<string> PauseCalls { get; } = [];
    public List<(string Hash, bool DeleteFiles)> DeleteCalls { get; } = [];
    public List<(string Hash, IReadOnlyCollection<int> Indices, bool Download)> SelectionCalls { get; } = [];
    public List<string> RecheckCalls { get; } = [];

    /// <summary>结束校验阶段：种子回到非 Checking 状态；intoError 时进入 Error（校验后出错的场景）。</summary>
    public void FinishRecheck(string hash, bool intoError = false)
    {
        Rechecking.Remove(hash);
        if (Torrents.TryGetValue(hash, out var snapshot))
            Torrents[hash] = snapshot with
            {
                Category = intoError ? QbittorrentTorrentCategory.Error : snapshot.Category
            };
    }

    public void CompleteMoves()
    {
        foreach (var (hash, path) in PendingLocations)
            Torrents[hash] = Torrents[hash] with { SavePath = path, IsMoving = false };
        PendingLocations.Clear();
    }

    public void AddTorrent(string hash, string name, QbittorrentTorrentCategory category, double progress,
                           string savePath, IReadOnlyList<string>? fileNames = null,
                           IReadOnlyList<string>? tags = null)
    {
        Torrents[hash] = new TorrentSnapshot(hash, name, category, progress, 1024, savePath, tags ?? []);
        if (fileNames is not null) Files[hash] = [.. fileNames];
    }
}

/// <summary>fake 的 recheck 行为模型；详见 FakeTorrentStore.RecheckBehavior。</summary>
public enum FakeRecheckBehavior
{
    /// <summary>只记录调用，不改变状态（默认；未配置的测试不得因 fake 凭空推进校验而通过）。</summary>
    RecordOnly,

    /// <summary>进入 Checking 阶段，由测试调用 FinishRecheck 决定结束与结果。</summary>
    EnterChecking,

    /// <summary>快速校验：请求即完成，两次采样之间永远观察不到 Checking（未确认路径测试）。</summary>
    CompleteImmediately
}

/// <summary>改名 onto 已存在路径的策略；详见 FakeTorrentStore.RenameOntoExistingPolicy。</summary>
public enum FakeRenameOntoExistingPolicy
{
    /// <summary>按已验收语义：直接改本种子的路径映射（A1 行为已验收后的模拟）。</summary>
    Remap,

    /// <summary>拒绝：模拟拒绝改名 onto 已存在路径的 qB 构建（降级路径测试）。</summary>
    Reject
}

/// <summary>假会话：把提交直接登记进 FakeTorrentStore，用 TorrentDownloadPolicy 从字节推导 hash。</summary>
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

    /// <summary>设置后 DeleteTorrent 抛出该异常，模拟清理时下载器暂时不可用。</summary>
    public Exception? DeleteException { get; set; }

    /// <summary>启动请求失败，用于验证已持久化的启动意图可恢复。</summary>
    public Exception? ResumeException { get; set; }

    /// <summary>Resume 已在远端生效后的回调，用于模拟响应丢失或分享限制立即停止。</summary>
    public Func<Task>? AfterResumeAsync { get; set; }

    /// <summary>AddTorrent 完成后回调：可确定性模拟服务端已接收、客户端尚未收到结果。</summary>
    public Func<Task>? AfterAddTorrentAsync { get; set; }

    /// <summary>FindTorrents 前回调：确定性挂起预检/查询，用于测试迟到结果处理。</summary>
    public Func<IReadOnlyCollection<string>, Task>? BeforeFindTorrentsAsync { get; set; }

    public string ServerVersion => "v5.0.0-fake";

    public async Task<string> AddTorrentAsync(byte[] torrentBytes, string savePath, IReadOnlyList<string> tags,
                                              string? rename, bool startStopped,
                                              CancellationToken cancellationToken = default)
    {
        if (BeforeAddTorrentAsync is not null) await BeforeAddTorrentAsync();

        if (AddException is not null) throw AddException;

        if (!TorrentDownloadPolicy.TryLoad(torrentBytes, out var torrent, out var isMultiFile))
            throw new InvalidOperationException("测试种子字节无效。");
        var hash = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();

        if (FailOnDuplicate && store.Torrents.ContainsKey(hash)) return "Fails.";

        void RegisterCore()
        {
            store.Torrents[hash] = new TorrentSnapshot(hash, rename ?? $"torrent-{hash[..8]}",
                                                       QbittorrentTorrentCategory.Downloading, 0, 1024, savePath,
                                                       tags.ToList());
            store.Files.TryAdd(hash, ["原文件名.mkv"]);

            // 同时登记真实文件身份（qB index / 原始相对路径），默认全部选中、零进度；
            // 测试播种 FileIdentities 时以直接赋值覆盖。
            if (!store.FileIdentities.ContainsKey(hash))
            {
                var indexByPath = TorrentDownloadPolicy.GetQbittorrentFileIndices(torrentBytes, torrent, isMultiFile);
                store.FileIdentities[hash] = torrent.Files
                                                    .Select(file =>
                                                     {
                                                         var path = TorrentDownloadPolicy.GetRelativePath(
                                                              torrent, file, isMultiFile);
                                                         return new TorrentFileSnapshot(
                                                              indexByPath.GetValueOrDefault(path, -1), path,
                                                              file.Length);
                                                     })
                                                    .ToList();
            }

            if (startStopped) store.StoppedAdds.Add(hash);
        }

        if (RegisterThenThrowTransport)
        {
            store.AddCalls.Add((torrentBytes, savePath, tags, rename));
            RegisterCore();
            throw new QbittorrentException("模拟响应丢失");
        }

        store.AddCalls.Add((torrentBytes, savePath, tags, rename));
        if (DropAdd) return "Ok.";

        if (AddResponse is not null) return AddResponse;

        RegisterCore();
        if (AfterAddTorrentAsync is not null) await AfterAddTorrentAsync();
        return "Ok.";
    }

    public async Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken = default)
    {
        if (BeforeFindTorrentsAsync is not null) await BeforeFindTorrentsAsync(infoHashes);
        return infoHashes
              .Where(hash => store.Torrents.ContainsKey(hash))
              .Select(SnapshotFor)
              .ToList();
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
        string infoHash, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(store.Files.TryGetValue(infoHash, out var names) ? names : []);


    public Task<IReadOnlyList<TorrentFileSnapshot>> ListFilesAsync(
        string infoHash, CancellationToken cancellationToken = default)
    {
        if (store.FileIdentities.TryGetValue(infoHash, out var files))
            return Task.FromResult<IReadOnlyList<TorrentFileSnapshot>>(files.ToList());
        return Task.FromResult<IReadOnlyList<TorrentFileSnapshot>>(store.Files.TryGetValue(infoHash, out var names)
                                                                       ? names.Select((name, index) =>
                                                                                    new TorrentFileSnapshot(index, name,
                                                                                             1024)).ToList()
                                                                       : []);
    }

    public Task SetTorrentFileSelectionAsync(string                   infoHash,
                                             IReadOnlyCollection<int> fileIndices, bool download,
                                             CancellationToken        cancellationToken = default)
    {
        store.SelectionCalls.Add((infoHash, fileIndices.ToList(), download));
        if (store.FileIdentities.TryGetValue(infoHash, out var files))
            store.FileIdentities[infoHash] = files.Select(file => fileIndices.Contains(file.Index)
                                                              ? file with { IsSelectedForDownload = download }
                                                              : file)
                                                  .ToList();
        return Task.CompletedTask;
    }

    public Task RecheckTorrentAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        store.RecheckCalls.Add(infoHash);
        switch (store.RecheckBehavior)
        {
            case FakeRecheckBehavior.EnterChecking :
                store.Rechecking.Add(infoHash);
                break;
            case FakeRecheckBehavior.CompleteImmediately :
                // 快速校验：校验阶段在两次采样之间开始并结束，快照永远观察不到 Checking。
                break;
        }

        return Task.CompletedTask;
    }

    public Task SetLocationAsync(string infoHash, string savePath, CancellationToken cancellationToken = default)
    {
        store.SetLocationCalls.Add((infoHash, savePath));
        if (store.DeferLocation)
        {
            store.PendingLocations[infoHash] = savePath;
            store.Torrents[infoHash]         = store.Torrents[infoHash] with { IsMoving = true };
        }
        else
        {
            store.Torrents[infoHash] = store.Torrents[infoHash] with { SavePath = savePath, IsMoving = false };
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        store.PauseCalls.Add(infoHash);
        store.Paused.Add(infoHash);
        return Task.CompletedTask;
    }

    public async Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        if (ResumeException is not null) throw ResumeException;
        store.ResumeCalls.Add(infoHash);
        store.Paused.Remove(infoHash);
        store.StoppedAdds.Remove(infoHash);
        if (AfterResumeAsync is not null) await AfterResumeAsync();
    }

    public Task RenameTorrentAsync(string infoHash, string newName, CancellationToken cancellationToken = default)
    {
        store.RenameCalls.Add((infoHash, newName));
        if (store.Torrents.TryGetValue(infoHash, out var snapshot))
            store.Torrents[infoHash] = snapshot with { Name = newName };

        return Task.CompletedTask;
    }

    public Task RenameTorrentFileAsync(
        string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        // 改名 onto 已存在路径：按配置策略模拟已验收语义（直接改映射）或拒绝该操作的构建。
        // fake 不建模磁盘覆盖——真实覆盖行为只能靠独立 qB 实例验收。
        if (store.RenameOntoExistingPolicy == FakeRenameOntoExistingPolicy.Reject)
            foreach (var (otherHash, otherFiles) in store.FileIdentities)
            {
                if (otherHash.Equals(infoHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (otherFiles.Any(file => string.Equals(file.Path, newPath, StringComparison.Ordinal)))
                    throw new InvalidOperationException($"fake：拒绝改名 onto 已存在路径“{newPath}”（其他种子 {otherHash[..8]} 占用）。");
            }

        store.RenameFileCalls.Add((infoHash, oldPath, newPath));
        if (store.Files.TryGetValue(infoHash, out var fileNames))
            store.Files[infoHash] = fileNames.Select(name => name == oldPath ? newPath : name).ToList();
        if (store.FileIdentities.TryGetValue(infoHash, out var files))
            store.FileIdentities[infoHash] =
                files.Select(file => file.Path == oldPath ? file with { Path = newPath } : file).ToList();
        store.AfterRenameFile?.Invoke();
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

    public Task DeleteTorrentAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        if (DeleteException is not null) throw DeleteException;

        store.DeleteCalls.Add((infoHash, deleteFiles));
        store.Torrents.Remove(infoHash);
        store.Files.Remove(infoHash);
        store.FileIdentities.Remove(infoHash);
        store.Rechecking.Remove(infoHash);
        store.Paused.Remove(infoHash);
        store.StoppedAdds.Remove(infoHash);
        return Task.CompletedTask;
    }

    public Task DeleteTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        foreach (var infoHash in infoHashes) DeleteTorrentAsync(infoHash, deleteFiles, cancellationToken);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private TorrentSnapshot SnapshotFor(string hash)
    {
        var snapshot = store.Torrents[hash];
        var category = snapshot.Category;
        // 校验阶段优先报告：进度可能临时波动，不推进冲突计划。
        if (store.Rechecking.Contains(hash)) return snapshot with { Category = QbittorrentTorrentCategory.Checking };
        // 做种被暂停对应 qB 的 stoppedUP：映射后同样是 Stopped（完成程度由进度独立判定）。
        if ((store.StoppedAdds.Contains(hash) && category == QbittorrentTorrentCategory.Downloading) ||
            (store.Paused.Contains(hash) && category is QbittorrentTorrentCategory.Downloading
                                                     or QbittorrentTorrentCategory.Seeding))
            category = QbittorrentTorrentCategory.Stopped;
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

    public Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default) =>
        CreateAsync(new QbittorrentSettings("http://qb.test", "admin", "/downloads", true), "secret",
                    cancellationToken);

    public Task<IQbittorrentSession> CreateAsync(
        QbittorrentSettings settings, string? password, CancellationToken cancellationToken = default)
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

    public Task<byte[]> DownloadAsync(
        Uri torrentUri, Guid? proxyId = default, CancellationToken cancellationToken = default)
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
        const int length       = 1024;
        const int pieceLength  = 16384;
        var       announceText = announce ?? "http://tracker.example/announce";
        var       pieces       = new string('0', (length + pieceLength - 1) / pieceLength * 20);
        return Encoding.UTF8.GetBytes($"d8:announce{Encode(announceText)}"             +
                                      $"4:infod6:lengthi{length}e4:name{Encode(name)}" +
                                      $"12:piece lengthi{pieceLength}e6:pieces{Encode(pieces)}ee");
    }

    /// <summary>多 tracker 种子：首个地址写入 announce，全部地址按 announce-list 逐层写入，供分类/冲突用例。</summary>
    public static byte[] CreateMultiTracker(string name, params string[] announces)
    {
        const int length      = 1024;
        const int pieceLength = 16384;
        var       pieces      = new string('0', (length + pieceLength - 1) / pieceLength * 20);
        var       tiers       = string.Concat(announces.Select(url => $"l{Encode(url)}e"));
        return Encoding.UTF8.GetBytes($"d8:announce{Encode(announces[0])}"             +
                                      $"13:announce-listl{tiers}e"                     +
                                      $"4:infod6:lengthi{length}e4:name{Encode(name)}" +
                                      $"12:piece lengthi{pieceLength}e6:pieces{Encode(pieces)}ee");
    }

    public static string InfoHashOf(string name) =>
        TorrentDownloadPolicy.TryLoad(Create(name), out var torrent, out _)
            ? torrent.InfoHashes.V1!.ToHex().ToLowerInvariant()
            : throw new InvalidOperationException("测试种子字节无效。");

    private static string Encode(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";
}

/// <summary>Subject 绑定解析的空实现替身：不触达绑定链路的测试 harness 只为满足 DownloadService 构造签名。</summary>
public sealed class NullSubjectMetadataProvider : ISubjectMetadataProvider
{
    public static readonly NullSubjectMetadataProvider Instance = new();

    public Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("该测试不应读取 Bangumi 元数据。");
}

public sealed class NullSubjectLinkResolver : ISubjectLinkResolver
{
    public static readonly NullSubjectLinkResolver Instance = new();

    public Task<int?> ResolveSubjectIdAsync(
        Uri episodeUri, Guid? proxyId = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("该测试不应解析条目链接。");
}

/// <summary>测试用应用生命周期：供依赖 IHostApplicationLifetime 的业务服务在测试中构造。</summary>
public sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted  => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped  => CancellationToken.None;

    public void StopApplication()
    {
    }
}
