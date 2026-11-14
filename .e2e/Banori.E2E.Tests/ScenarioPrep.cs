using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banori.E2E.Tests;

/// <summary>
///     场景环境准备：生成真实 piece 哈希的 torrent（调 tools/make_torrent.py）、
///     预放内容到 qB downloads volume、向隔离 qB 添加种子并 recheck 至完成态。
///     预置顺序（RUNBOOK「qB 数据预置」）：宿主放内容（布局与 torrent name/path 一致）→
///     multipart /api/v2/torrents/add（stopped=true）→ recheck → force-resume；
///     断言 amount_downloaded≈0 基线（数据来自预放文件而非网络下载）。
/// </summary>
public static class ScenarioPrep
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public record TorrentManifest(
        [property: JsonPropertyName("info_hash")] string InfoHash,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("announce")] string Announce,
        [property: JsonPropertyName("total_size")] long TotalSize,
        [property: JsonPropertyName("piece_count")] int PieceCount,
        [property: JsonPropertyName("piece_length")] int PieceLength,
        [property: JsonPropertyName("torrent_path")] string TorrentPath,
        [property: JsonPropertyName("files")] IReadOnlyList<ManifestFile> Files);

    public record ManifestFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("size")] long Size);

    /// <summary>
    ///     生成 torrent：内容文件先写到 <paramref name="contentDirectory" />（调用方给文件名→字节），
    ///     再调 make_torrent.py 产出 .torrent 与清单 JSON。python 固定 -I 运行。
    /// </summary>
    public static async Task<TorrentManifest> MakeTorrentAsync(
        string contentDirectory, string torrentPath, string announce, string torrentName,
        IReadOnlyDictionary<string, byte[]> files, int pieceLength = 262144,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(contentDirectory);
        foreach (var (name, data) in files)
        {
            var fullPath = Path.Combine(contentDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, data, cancellationToken);
        }

        var python = Environment.GetEnvironmentVariable("E2E_PYTHON")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                     ".local", "bin", "python.exe");
        var script = FindTool("make_torrent.py");
        var arguments = new StringBuilder($"-I \"{script}\" --content-dir \"{contentDirectory}\" " +
                                          $"--output \"{torrentPath}\" --announce \"{announce}\" " +
                                          $"--name \"{torrentName}\" --piece-length {pieceLength}");
        var info = new ProcessStartInfo(python, arguments.ToString())
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var process = Process.Start(info)
                           ?? throw new InvalidOperationException("无法启动 python。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"make_torrent.py 退出码 {process.ExitCode}：{stderr}");

        // 最后一行是清单 JSON。
        var jsonLine = stdout.TrimEnd().Split('\n')[^1].Trim();
        return JsonSerializer.Deserialize<TorrentManifest>(jsonLine, JsonOptions)
               ?? throw new InvalidOperationException("make_torrent.py 输出无法解析为清单 JSON。");
    }

    /// <summary>
    ///     预置 qB 完成态种子：把 <paramref name="contentDirectory" />（MakeTorrentAsync 的
    ///     contentDirectory，内容文件所在地）按种子文件布局复制进 downloads volume（宿主侧）→
    ///     停止态添加 → recheck → 等 recheck 结束（进度 1.0）→ force-resume → 等做种态。
    ///     布局（qB 4.6.4 实测）：单文件种子数据文件 = &lt;savePath&gt;/&lt;info.name&gt;（info.name
    ///     = 内容文件名）；多文件种子 = &lt;savePath&gt;/&lt;name&gt;/&lt;相对路径&gt;。
    ///     state 串（4.6.4 实测）：停止态添加 pausedDL、完成停止 pausedUP、做种 stalledUP、
    ///     强制开始 setForceStart → forcedUP（与 5.x 的 stoppedDL/stoppedUP/stoppedUP 命名不同）。
    /// </summary>
    public static async Task SeedCompletedTorrentAsync(QbApiClient qb, TorrentManifest manifest,
                                                       string contentDirectory, string hostDownloadRoot,
                                                       string savePath = "/downloads",
                                                       CancellationToken cancellationToken = default)
    {
        // 内容文件：按种子内布局复制进 downloads。单文件种子 name=文件名且平铺在 savePath 下
        // （无 <name>/ 子目录）；多文件种子 copy 进 downloads/<name>/。
        var targetRoot = manifest.Files.Count == 1 && Path.GetFileName(manifest.Files[0].Path) == manifest.Name
            ? hostDownloadRoot
            : Path.Combine(hostDownloadRoot, manifest.Name);
        await CopyToAsync(contentDirectory, targetRoot, manifest, cancellationToken);

        await qb.LoginAsync(cancellationToken);
        var torrentBytes = await File.ReadAllBytesAsync(manifest.TorrentPath, cancellationToken);
        await qb.AddStoppedAsync(torrentBytes, savePath, cancellationToken);
        await WaitTorrentAsync(qb, manifest.InfoHash, cancellationToken);

        await qb.RecheckAsync(manifest.InfoHash, cancellationToken);
        await WaitForAsync(async () =>
        {
            var torrent = await qb.FindTorrentAsync(manifest.InfoHash, cancellationToken);
            return torrent is { Progress: 1.0 } && torrent.AmountLeft == 0;
        }, TimeSpan.FromSeconds(120), cancellationToken);

        await qb.ForceResumeAsync(manifest.InfoHash, cancellationToken);
        await WaitForAsync(async () =>
        {
            var torrent = await qb.FindTorrentAsync(manifest.InfoHash, cancellationToken);
            return torrent?.State is "pausedUP" or "stalledUP" or "stoppedUP" or "forcedUP" or "uploading";
        }, TimeSpan.FromSeconds(60), cancellationToken);

        var final = await qb.FindTorrentAsync(manifest.InfoHash, cancellationToken);
        if (final is not { AmountLeft: 0 })
            throw new InvalidOperationException(
                $"预置基线异常：amount_left={final?.AmountLeft}（应为 0，数据应全部来自预放文件）。");
    }

    private static async Task CopyToAsync(string contentDirectory, string targetRoot, TorrentManifest manifest,
                                          CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files)
        {
            var source = Path.Combine(contentDirectory, file.Path);
            var target = Path.Combine(targetRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }

        await Task.CompletedTask;
    }

    private static async Task WaitTorrentAsync(QbApiClient qb, string infoHash,
                                               CancellationToken cancellationToken)
    {
        await WaitForAsync(async () => await qb.FindTorrentAsync(infoHash, cancellationToken) is not null,
                           TimeSpan.FromSeconds(60), cancellationToken);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout,
                                           CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"等待条件超时（{timeout.TotalSeconds:F0}s）。");
    }

    private static string FindTool(string name)
    {
        var current = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(current); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".e2e", "tools", name);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException($"未能在 {current} 上方找到工具 {name}。");
    }
}
