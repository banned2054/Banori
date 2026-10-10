using MediaManager.Features.Settings;

namespace MediaManager.Integrations.Qbittorrent;

/// <summary>下载器任务的领域归类，由会话实现把 qBittorrent 状态翻译成业务可判断的四类。</summary>
public enum QbittorrentTorrentCategory
{
    Downloading,
    Stopped,
    Seeding,
    Error,
    Unknown
}

/// <summary>对账所需的种子快照，与 qBittorrent 模型解耦。</summary>
public sealed record TorrentSnapshot(
    string                     Hash,
    string                     Name,
    QbittorrentTorrentCategory Category,
    double                     Progress,
    long                       TotalSize,
    string                     SavePath,
    IReadOnlyList<string>      Tags);

/// <summary>
///     一个已登录的 qBittorrent 会话。实现负责协议细节（临时文件上传、Hash 规范化、异常翻译）；
///     调用方按业务流程使用，不感知 HTTP。随使用方创建和释放，不跨请求共享。
/// </summary>
public interface IQbittorrentSession : IAsyncDisposable
{
    string ServerVersion { get; }

    /// <summary>提交种子文件字节，同时设置隔离保存路径、来源 tag 与种子名（rename）。返回下载器原始响应文本（"Ok." / "Fails."），由调用方解释；协议错误抛 QbittorrentException。</summary>
    Task<string> AddTorrentAsync(byte[]                torrentBytes, string  savePath,
                                 IReadOnlyList<string> tags,         string? rename,
                                 CancellationToken     cancellationToken = default);

    /// <summary>按规范化 Hash 批量查询任务；不存在的 Hash 不出现在结果中。</summary>
    Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(IReadOnlyCollection<string> infoHashes,
                                                           CancellationToken           cancellationToken = default);

    /// <summary>按 tag 过滤列出种子；tag 为 null 时返回全部。</summary>
    Task<IReadOnlyList<TorrentSnapshot>> ListTorrentsAsync(string?           tag,
                                                           CancellationToken cancellationToken = default);

    /// <summary>为已有种子追加 tag（接管外部预先添加的种子时使用）。</summary>
    Task AddTagAsync(string infoHash, string tag, CancellationToken cancellationToken = default);

    /// <summary>列出种子内文件名，用于完成改名时保留原扩展名。</summary>
    Task<IReadOnlyList<string>> ListFileNamesAsync(string infoHash, CancellationToken cancellationToken = default);

    Task PauseAsync(string infoHash, CancellationToken cancellationToken = default);

    Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default);

    Task RenameTorrentAsync(string infoHash, string newName, CancellationToken cancellationToken = default);

    /// <summary>按相对路径重命名种子内单个文件（qBittorrent renameFile）。</summary>
    Task RenameTorrentFileAsync(string            infoHash, string oldPath, string newPath,
                                CancellationToken cancellationToken = default);

    /// <summary>按相对路径重命名种子内文件夹（qBittorrent renameFolder）。</summary>
    Task RenameTorrentFolderAsync(string            infoHash, string oldPath, string newPath,
                                  CancellationToken cancellationToken = default);

    /// <summary>从下载器删除种子；deleteFiles 为 true 时连同已下载的文件一起删除。</summary>
    Task DeleteTorrentAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default);

    /// <summary>从下载器批量删除种子；deleteFiles 为 true 时连同已下载的文件一起删除。</summary>
    Task DeleteTorrentsAsync(IReadOnlyCollection<string> infoHashes, bool deleteFiles,
                             CancellationToken           cancellationToken = default);
}

/// <summary>会话工厂：每次创建独立登录的会话。未配置或禁用时 CreateAsync 抛 InvalidOperationException。</summary>
public interface IQbittorrentSessionFactory
{
    /// <summary>读取已保存设置（含受保护密码）并登录。</summary>
    Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>按传入设置登录，供设置页在保存前测试连接。</summary>
    Task<IQbittorrentSession> CreateAsync(QbittorrentSettings settings, string? password,
                                          CancellationToken   cancellationToken = default);
}
