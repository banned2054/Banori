using Banori.Features.Settings;

namespace Banori.Integrations.Qbittorrent;

/// <summary>下载器任务的领域归类，由会话实现把 qBittorrent 状态翻译成业务可判断的类别。</summary>
public enum QbittorrentTorrentCategory
{
    Downloading,
    Stopped,
    Seeding,
    Error,

    /// <summary>正在校验（下载校验、恢复数据校验或做种校验）：进度可能临时波动，不推进冲突计划。</summary>
    Checking,

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
    IReadOnlyList<string>      Tags,
    bool                       IsMoving = false);

/// <summary>qB 文件身份；Index 是协议返回的稳定 index，不是列表顺序。</summary>
/// <param name="Progress">文件级下载进度（0-1）；校验期间可能临时波动。</param>
/// <param name="IsSelectedForDownload">该文件当前是否被选为下载（优先级非“不下载”）。</param>
public sealed record TorrentFileSnapshot(
    int    Index,
    string Path,
    long   Size,
    double Progress              = 0,
    bool   IsSelectedForDownload = true);

/// <summary>
///     一个已登录的 qBittorrent 会话。实现负责协议细节（临时文件上传、Hash 规范化、异常翻译）；
///     调用方按业务流程使用，不感知 HTTP。随使用方创建和释放，不跨请求共享。
/// </summary>
public interface IQbittorrentSession : IAsyncDisposable
{
    string ServerVersion { get; }

    /// <summary>
    ///     提交种子文件字节，同时设置隔离保存路径、来源 tag 与种子名（rename）。
    ///     startStopped 为 true 时以停止状态添加（qB 5.x stopped / qB 4.x paused 由客户端适配），保证准备完成前不产生网络下载。
    ///     返回下载器原始响应文本（"Ok." / "Fails."），由调用方解释；协议错误抛 QbittorrentException。
    /// </summary>
    Task<string> AddTorrentAsync(byte[]                torrentBytes, string  savePath,
                                 IReadOnlyList<string> tags,         string? rename,
                                 bool                  startStopped,
                                 CancellationToken     cancellationToken = default);

    /// <summary>按规范化 Hash 批量查询任务；不存在的 Hash 不出现在结果中。</summary>
    Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken = default);

    /// <summary>按 tag 过滤列出种子；tag 为 null 时返回全部。</summary>
    Task<IReadOnlyList<TorrentSnapshot>> ListTorrentsAsync(
        string? tag, CancellationToken cancellationToken = default);

    /// <summary>为已有种子追加 tag（接管外部预先添加的种子时使用）。</summary>
    Task AddTagAsync(string infoHash, string tag, CancellationToken cancellationToken = default);

    /// <summary>列出种子内文件名，用于完成改名时保留原扩展名。</summary>
    Task<IReadOnlyList<string>> ListFileNamesAsync(string infoHash, CancellationToken cancellationToken = default);

    /// <summary>读取可靠的文件 index/path/size；旧会话必须显式支持，禁止用列表位置伪造身份。</summary>
    Task<IReadOnlyList<TorrentFileSnapshot>> ListFilesAsync(
        string infoHash, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("会话不支持文件身份列表。");

    /// <summary>按 qB index 批量设置文件的下载选择；业务层只表达下载或跳过，不区分 qB 的高/最高优先级。请求成功不代表已生效，应随后读取文件快照确认。</summary>
    Task SetTorrentFileSelectionAsync(string infoHash, IReadOnlyCollection<int> fileIndices,
                                      bool   download, CancellationToken        cancellationToken = default) =>
        throw new NotSupportedException("会话不支持设置文件下载选择。");

    /// <summary>请求重新校验种子已有数据（recheck）。校验是异步阶段，结果以快照的校验状态与进度为准。</summary>
    Task RecheckTorrentAsync(string infoHash, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("会话不支持重新校验。");

    /// <summary>请求移动整个种子的保存目录；调用成功不代表移动完成，应随后确认种子快照。</summary>
    Task SetLocationAsync(string infoHash, string savePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("会话不支持移动保存目录。");

    Task PauseAsync(string infoHash, CancellationToken cancellationToken = default);

    Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default);

    Task RenameTorrentAsync(string infoHash, string newName, CancellationToken cancellationToken = default);

    /// <summary>按相对路径重命名种子内单个文件（qBittorrent renameFile）。</summary>
    Task RenameTorrentFileAsync(
        string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>按相对路径重命名种子内文件夹（qBittorrent renameFolder）。</summary>
    Task RenameTorrentFolderAsync(
        string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>从下载器删除种子；deleteFiles 为 true 时连同已下载的文件一起删除。</summary>
    Task DeleteTorrentAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken = default);

    /// <summary>从下载器批量删除种子；deleteFiles 为 true 时连同已下载的文件一起删除。</summary>
    Task DeleteTorrentsAsync(
        IReadOnlyCollection<string> infoHashes, bool deleteFiles, CancellationToken cancellationToken = default);
}

/// <summary>会话工厂：每次创建独立登录的会话。未配置或禁用时 CreateAsync 抛 InvalidOperationException。</summary>
public interface IQbittorrentSessionFactory
{
    /// <summary>读取已保存设置（含受保护密码）并登录。</summary>
    Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>按传入设置登录，供设置页在保存前测试连接。</summary>
    Task<IQbittorrentSession> CreateAsync(
        QbittorrentSettings settings, string? password, CancellationToken cancellationToken = default);
}
