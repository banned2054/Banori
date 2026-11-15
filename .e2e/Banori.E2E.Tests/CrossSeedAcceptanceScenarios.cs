using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Banori.E2E.Tests;

/// <summary>
///     跨站搬运复用两个验收场景（DESIGN 施工图场景1/2）：每个场景一台隔离 qB 4.6.4 容器
///     （banori-accept-qb-s1/s2，串行绝不并行），Banori 被测流程由真实 worker（AcceptanceHost
///     的 DownloadReconcileWorker）推进；qB API 只用于数据准备与只读采样。
///     gate：E2E_S1=1 跑场景1、E2E_S2=1 跑场景2；未启用显示 [SKIP]。
///     证据落盘 .e2e/s1、.e2e/s2（torrent-manifest-*.json、evidence.jsonl、final.jsonl、summary.json）。
/// </summary>
internal static class CrossSeedScenario
{
    public const int    SubjectId      = 114514;
    public const string SubjectTitle   = "泣く大人は嫌い";
    public const string SubjectNameCn  = "讨厌哭泣的大人";
    public const string RenderedName   = "[Anime]讨厌哭泣的大人 E01 byrbt";
    public const string TargetRelative = "Media/Anime/[Anime]讨厌哭泣的大人 E01 byrbt.mkv";
    public const string TargetAbsolute = "/downloads/Media/Anime/[Anime]讨厌哭泣的大人 E01 byrbt.mkv";
    public const string OriginalNameV1 = "【喵萌奶茶屋】[泣く大人は嫌い][01][1080p][简日双语].mkv";
    public const string OriginalNameV2 = "【喵萌奶茶屋】[泣く大人は嫌い][01v2][1080p][简日双语].mkv";
    public const string ByrbtAnnounce  = "https://tracker.byr.pt/announce.php?passkey=e2e";
    public const string DummyAnnounce  = "http://tracker.invalid/announce";

    /// <summary>视为做种/运行的 qB 状态（4.6.4 命名）。</summary>
    public static readonly string[] SeedingStates = ["stalledUP", "forcedUP", "uploading", "stoppedUP"];

    /// <summary>确定性伪视频内容（与冒烟 MakeMkv 同算法）：A/B(C) 共用同一 bytes。</summary>
    public static byte[] MakeMkv(int size)
    {
        var data = new byte[size];
        var seed = 0x5A5A_5A5A;
        for (var index = 0; index < data.Length; index++)
        {
            seed = seed * 1_664_525 + 1_013_904_223 & 0x7FFF_FFFF;
            data[index] = (byte)(seed >> 24);
        }
        return data;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>轮询直到种子文件清单可用，返回首文件相对路径（rename 语义观察用）。</summary>
    public static async Task<string> WaitForFirstFileAsync(QbApiClient qb, string infoHash, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await qb.FindTorrentAsync(infoHash) is not null)
            {
                var files = await qb.GetTorrentFilesAsync(infoHash);
                if (files.Count > 0) return files[0].Name ?? string.Empty;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"等待种子 {infoHash[..8]} 文件清单超时（{timeout.TotalSeconds:F0}s）。");
    }

    /// <summary>轮询直到种子文件相对路径满足条件（如受控映射改名到目标路径）。
    /// 种子尚未添加时 torrents/files 返回 404：按「尚无文件」继续轮询，不视为失败。</summary>
    public static async Task WaitForFileAsync(QbApiClient qb, string infoHash, Func<string, bool> condition,
                                              TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var files = await qb.GetTorrentFilesAsync(infoHash);
                last = files.Count > 0 ? files[0].Name : null;
            }
            catch (System.Net.Http.HttpRequestException)
            {
                last = null;
            }
            if (last is not null && condition(last)) return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"等待{what}超时（{timeout.TotalSeconds:F0}s），最后文件路径：{last ?? "<无>"}。");
    }
}

/// <summary>场景共用支持：串行门、python 工具调用、证据 JSONL 读取、数据库读取。</summary>
internal static class ScenarioSupport
{
    /// <summary>两个场景绝不并行：共用一把进程内串行门（xunit 可能并行运行不同测试类）。</summary>
    public static readonly SemaphoreSlim SerialGate = new(1, 1);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>观察终止条件：任务到达终态即停（失败路径不留 8 分钟空等）。</summary>
    public static bool TaskReachedFinalState(DownloadTask? task, IReadOnlyList<QbTorrentInfo> torrents,
                                             string infoHash) =>
        task is { Status: DownloadTaskStatus.Completed or DownloadTaskStatus.Failed
                                            or DownloadTaskStatus.Lost or DownloadTaskStatus.Replaced } &&
        string.Equals(task.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>场景 gate 判定：环境变量为 1 且在 Windows 上（docker 工具链按 Windows 实测）。</summary>
    public static bool Enabled(string gateVariable) =>
        Environment.GetEnvironmentVariable(gateVariable) == "1" &&
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    ///     定位 .e2e 根：从应用基目录向上找包含 Banori.E2E.Tests.csproj 的目录（提交的源码，
    ///     不依赖 .git、运行产物或个人机器路径），其父目录即 .e2e。
    /// </summary>
    public static string FindE2eRoot()
    {
        var current = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(current); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Banori.E2E.Tests.csproj"))) continue;
            return directory.Parent?.FullName ??
                   throw new InvalidOperationException(
                       $"在 {directory.FullName} 找到 Banori.E2E.Tests.csproj，但缺少父目录，无法定位 .e2e 根。");
        }

        throw new InvalidOperationException(
            $"未能在 {current} 上方找到包含 Banori.E2E.Tests.csproj 的目录（.e2e 根定位失败）。");
    }

    /// <summary>定位场景目录（.e2e/{leaf}）：不存在则创建（幂等）。须在场景 gate 通过后调用。</summary>
    public static string FindSceneRoot(string leaf)
    {
        var sceneRoot = Path.Combine(FindE2eRoot(), leaf);
        Directory.CreateDirectory(sceneRoot);
        return sceneRoot;
    }

    /// <summary>python -I 运行 .e2e/tools 脚本；失败时把两侧输出一并抛出。</summary>
    public static async Task<string> RunToolAsync(string script, string arguments)
    {
        var python = Environment.GetEnvironmentVariable("E2E_PYTHON")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                     ".local", "bin", "python.exe");
        var info = new ProcessStartInfo(python, $"-I \"{FindTool(script)}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 python。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"python {script} 退出码 {process.ExitCode}：{stderr}{stdout}");
        return stdout;
    }

    private static string FindTool(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory      = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".e2e", "tools", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"未能在 {AppContext.BaseDirectory} 上方找到 .e2e/tools/{name}。");
    }

    /// <summary>按 state 记录尽力停掉旧容器（上次运行残留）；失败不阻断——up 自身会 rm -f 重名容器。</summary>
    public static async Task TryDownAsync(string statePath)
    {
        if (!File.Exists(statePath)) return;
        try
        {
            await RunToolAsync("qb_container.py", $"down --state \"{statePath}\" --remove");
            await Task.Delay(1500);
        }
        catch (InvalidOperationException)
        {
            // 容器可能已不存在：down 对缺失容器报非零也视为清理完成。
        }
    }

    /// <summary>清空场景工作区（本任务专用目录）：config/、downloads/、Banori 数据库与本次证据文件。</summary>
    public static void ClearSceneDirectory(string sceneRoot)
    {
        foreach (var leaf in new[] { "config", "downloads" })
        {
            var path = Path.Combine(sceneRoot, leaf);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.CreateDirectory(path);
        }
        foreach (var file in Directory.GetFiles(sceneRoot))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("banori-e2e.db", StringComparison.Ordinal) ||
                fileName is "evidence.jsonl" or "final.jsonl" or "summary.json")
                File.Delete(file);
        }
    }

    /// <summary>按 info-hash 读取任务（含文件表）；找不到返回 null。</summary>
    public static async Task<DownloadTask?> LoadTaskByHashAsync(AcceptanceHost host, string infoHash)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                             .CreateDbContextAsync();
        var tasks = await db.DownloadTasks.AsNoTracking()
                            .Include(value => value.Files)
                            .ToListAsync();
        return tasks.FirstOrDefault(value =>
                                        string.Equals(value.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     等待完成后的做种恢复确认：Resume 发出（AwaitingConfirm）与观察到运行（ConfirmedRunning）
    ///     分属不同对账轮，CompleteAsync 时可能仍是 AwaitingConfirm——终态断言前先等确认落库。
    /// </summary>
    public static async Task<DownloadTask> WaitForRunConfirmedAsync(AcceptanceHost host, string infoHash,
                                                                    TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        DownloadTask? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await LoadTaskByHashAsync(host, infoHash);
            if (DownloadRunRecovery.FromJson(last?.RunRecovery) is { State: DownloadRunStates.ConfirmedRunning })
                return last!;
            await Task.Delay(1000);
        }
        throw new TimeoutException(
            $"等待做种恢复确认超时（{timeout.TotalSeconds:F0}s），最后 RunRecovery：{last?.RunRecovery ?? "<无>"}。");
    }

    /// <summary>读取 Subject 的逐集登记行（全部来源作用域）。</summary>
    public static async Task<IReadOnlyList<EpisodeVersion>> LoadEpisodeVersionsAsync(AcceptanceHost host,
                                                                                     int subjectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                             .CreateDbContextAsync();
        return await db.EpisodeVersions.AsNoTracking()
                       .Where(row => row.SubjectId == subjectId)
                       .ToListAsync();
    }

    /// <summary>证据样本字段读取（EvidenceObserver 序列化保持 C# 属性名，大小写不敏感查找）。</summary>
    public static JsonElement PropertyOf(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        throw new KeyNotFoundException($"证据样本缺少字段 {name}。");
    }

    /// <summary>从 evidence.jsonl 提取某 hash 的 qB 采样序列（时间序）。</summary>
    public static async Task<IReadOnlyList<QbSample>> ReadTorrentSeriesAsync(string evidencePath, string infoHash)
    {
        var result = new List<QbSample>();
        foreach (var line in await File.ReadAllLinesAsync(evidencePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var torrents = PropertyOf(document.RootElement, "qb_torrents");
            foreach (var torrent in torrents.EnumerateArray())
            {
                if (!string.Equals(PropertyOf(torrent, "Hash").GetString(), infoHash,
                                   StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new QbSample(PropertyOf(document.RootElement, "timestamp_utc").GetDateTimeOffset(),
                                        PropertyOf(torrent, "State").GetString(),
                                        PropertyOf(torrent, "Progress").GetDouble(),
                                        PropertyOf(torrent, "Downloaded").GetInt64(),
                                        PropertyOf(torrent, "AmountLeft").GetInt64()));
            }
        }
        return result;
    }

    /// <summary>
    ///     从 evidence.jsonl 提取某任务的校验意图快照序列（时间序，含重复）：用于断言
    ///     Mapping→Requested→Observing→ReadyToRun 的真实推进轨迹与候选映射内容。
    /// </summary>
    public static async Task<IReadOnlyList<DownloadVerificationIntent>> ReadIntentSamplesAsync(
        string evidencePath, string infoHash)
    {
        var result = new List<DownloadVerificationIntent>();
        foreach (var line in await File.ReadAllLinesAsync(evidencePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var task = document.RootElement.GetProperty("task");
            if (task.ValueKind == JsonValueKind.Null) continue;
            if (!string.Equals(task.GetProperty("InfoHash").GetString(), infoHash,
                               StringComparison.OrdinalIgnoreCase)) continue;
            if (DownloadVerificationIntent.FromJson(task.GetProperty("VerificationIntent").GetString())
                is { } intent) result.Add(intent);
        }
        return result;
    }

    /// <summary>从 evidence.jsonl 提取某任务出现过失败的采样（FailedStep 非空），用于证明无占用异常。</summary>
    public static async Task<IReadOnlyList<string>> ReadFailedStepsAsync(string evidencePath, string infoHash)
    {
        var result = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(evidencePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var task = document.RootElement.GetProperty("task");
            if (task.ValueKind == JsonValueKind.Null) continue;
            if (!string.Equals(task.GetProperty("InfoHash").GetString(), infoHash,
                               StringComparison.OrdinalIgnoreCase)) continue;
            var failedStep = task.GetProperty("FailedStep").GetString();
            if (failedStep is not null) result.Add(failedStep);
        }
        return result;
    }

    public sealed record QbSample(DateTimeOffset TimestampUtc, string? State, double Progress,
                                  long Downloaded, long AmountLeft);

    public static async Task WriteJsonAsync(string path, object payload) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);

    /// <summary>断言种子下载量序列：Downloaded 恒 0；AmountLeft 一旦归零不得回升。</summary>
    public static void AssertNoDownload(IReadOnlyList<QbSample> series, string label, bool amountLeftAlwaysZero)
    {
        Assert.True(series.Count > 0, $"{label} 的证据采样为空。");
        Assert.All(series, sample => Assert.Equal(0, sample.Downloaded));
        if (!amountLeftAlwaysZero) return;
        Assert.All(series, sample => Assert.Equal(0, sample.AmountLeft));
    }

    /// <summary>AmountLeft「归零后不回升、终值为 0」的采样断言（复用复检前的未完成期允许非零）。</summary>
    public static void AssertAmountLeftNeverRegresses(IReadOnlyList<QbSample> series, string label)
    {
        var zeroSeen = false;
        foreach (var sample in series)
        {
            if (sample.AmountLeft == 0) zeroSeen = true;
            else Assert.False(zeroSeen, $"{label} 的 AmountLeft 归零后回升（出现真实下载）。");
        }
        Assert.Equal(0, series[^1].AmountLeft);
    }
}

/// <summary>场景1：目标命名已正确（外部种子 A 占位，非受管候选）——上传 B 全链复用。</summary>
public sealed class CrossSeedScenario1Tests
{
    private const string ContainerName = "banori-accept-qb-s1";
    private const int    WebUiPort     = 18085;
    private const int    BtPort        = 16885;
    private const string QbPassword    = "e2e-s1-password";
    private const string GateVariable  = "E2E_S1";

    [SkippableFact]
    public async Task 场景1_跨站搬运复用_非受管候选全链()
    {
        Skip.IfNot(ScenarioSupport.Enabled(GateVariable),
                   $"设置 {GateVariable}=1 且在 Windows 上才执行（另需 Docker；.e2e/s1 目录自动创建）");
        await ScenarioSupport.SerialGate.WaitAsync();
        try
        {
            await RunAsync();
        }
        finally
        {
            ScenarioSupport.SerialGate.Release();
        }
    }

    private async Task RunAsync()
    {
        var sceneRoot      = ScenarioSupport.FindSceneRoot("s1");
        var containerState = Path.Combine(sceneRoot, "qb-state.json");
        var evidencePath   = Path.Combine(sceneRoot, "evidence.jsonl");
        await ScenarioSupport.TryDownAsync(containerState);
        ScenarioSupport.ClearSceneDirectory(sceneRoot);

        await ScenarioSupport.RunToolAsync("qb_container.py",
                                           $"up --name {ContainerName} --scene-dir \"{sceneRoot}\" " +
                                           $"--webui-port {WebUiPort} --bt-port {BtPort} " +
                                           $"--username admin --password {QbPassword} --state \"{containerState}\"");
        // 进程级环境变量覆盖必须可恢复：SeedPipelineSmokeTests 读取同一组变量但不经过
        // SerialGate，只写不恢复会让同会话后跑的冒烟读到残留值（meta 场景已按此模式验证）。
        // 原值可能不存在（null），恢复时 SetEnvironmentVariable(name, null) 即删除。
        var qbEnvironment = (BaseUrl:  Environment.GetEnvironmentVariable("E2E_QB_BASE_URL"),
                             Username: Environment.GetEnvironmentVariable("E2E_QB_USERNAME"),
                             Password: Environment.GetEnvironmentVariable("E2E_QB_PASSWORD"));
        Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", $"http://127.0.0.1:{WebUiPort}");
        Environment.SetEnvironmentVariable("E2E_QB_USERNAME", "admin");
        Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", QbPassword);

        var qb  = new QbApiClient($"http://127.0.0.1:{WebUiPort}", "admin", QbPassword);
        AcceptanceHost? host = null;
        try
        {
            await qb.LoginAsync();
            var qbVersion = await qb.GetVersionAsync();

            host = new AcceptanceHost(sceneRoot);
            host.Initialize();
            Assert.True(string.IsNullOrEmpty(host.EnvironmentFallback), $"环境回退：{host.EnvironmentFallback}");
            Assert.Contains(host.ListHostedServices(), service => service.Contains("DownloadReconcileWorker"));

            await host.SeedSubjectAsync(CrossSeedScenario.SubjectId, CrossSeedScenario.SubjectTitle,
                                        CrossSeedScenario.SubjectNameCn);
            await host.WriteQbSettingsAsync();

            // 内容：A/B 共用同一 bytes（同 sha256 同 size），piece 长度不同 → info-hash 必不同。
            var bytes       = CrossSeedScenario.MakeMkv(800_000);
            var contentDirA = Path.Combine(sceneRoot, "content-a");
            var contentDirB = Path.Combine(sceneRoot, "content-b");
            var manifestA = await ScenarioPrep.MakeTorrentAsync(
                contentDirA, Path.Combine(sceneRoot, "torrent-a.torrent"), CrossSeedScenario.DummyAnnounce,
                "external-source-a",
                new Dictionary<string, byte[]>
                {
                    [CrossSeedScenario.TargetRelative.Split('/')[^1]] = bytes
                },
                pieceLength : 262144);
            var manifestB = await ScenarioPrep.MakeTorrentAsync(
                contentDirB, Path.Combine(sceneRoot, "torrent-b.torrent"), CrossSeedScenario.ByrbtAnnounce,
                "【喵萌奶茶屋】[泣く大人は嫌い][01][1080p][简日双语]",
                new Dictionary<string, byte[]> { [CrossSeedScenario.OriginalNameV1] = bytes },
                pieceLength : 131072);
            await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "torrent-manifest-a.json"), manifestA);
            await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "torrent-manifest-b.json"), manifestB);
            Assert.NotEqual(manifestA.InfoHash, manifestB.InfoHash);
            Assert.Equal(manifestA.Files[0].Sha256, manifestB.Files[0].Sha256);
            Assert.Equal(manifestA.Files[0].Size, manifestB.Files[0].Size);

            // [准备] A 预置完成做种（外部种子，占住目标路径 /downloads/Media/Anime/…）。
            await ScenarioPrep.SeedCompletedTorrentAsync(qb, manifestA, contentDirA,
                                                         Path.Combine(host.DownloadRoot, "Media", "Anime"),
                                                         "/downloads/Media/Anime");
            var seedBaseline = await qb.FindTorrentAsync(manifestA.InfoHash) ??
                               throw new InvalidOperationException("A 预置后不在 qB 中。");
            Assert.Equal(0, seedBaseline.Downloaded);
            Assert.Equal(0, seedBaseline.AmountLeft);

            // [被测] 上传 B，由真实 worker 推进全链。
            var driver = new UploadDriver(host);
            await driver.UploadAsync(await File.ReadAllBytesAsync(manifestB.TorrentPath),
                                     CrossSeedScenario.SubjectId);

            // 提交后瞬间：qB 内 B 文件仍是元数据原始名（验证 rename 语义假设）。
            var firstSeen = await CrossSeedScenario.WaitForFirstFileAsync(qb, manifestB.InfoHash,
                                                                          TimeSpan.FromSeconds(90));
            Assert.Equal(CrossSeedScenario.OriginalNameV1, firstSeen);

            // 全程观察到 B 终态（2s 采样 → evidence.jsonl）。
            await new EvidenceObserver(host, qb, evidencePath).ObserveUntilAsync(
                (task, torrents) => ScenarioSupport.TaskReachedFinalState(task, torrents, manifestB.InfoHash),
                TimeSpan.FromMinutes(8));

            await AssertChainAsync(host, qb, sceneRoot, evidencePath, qbVersion,
                                   manifestA.InfoHash, manifestB.InfoHash, manifestA.Files[0].Sha256);
        }
        finally
        {
            // 环境变量最先恢复（先于容器清理与外层串行门释放）。
            Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", qbEnvironment.BaseUrl);
            Environment.SetEnvironmentVariable("E2E_QB_USERNAME", qbEnvironment.Username);
            Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", qbEnvironment.Password);
            // 停机尽力而为：NotificationDeliveryWorker 的 PeriodicTimer 循环不捕获
            // OperationCanceledException（Workers/NotificationDeliveryWorker.cs:31，已另行记录），
            // .NET 11 的 Host 会把它从 StopAsync/DisposeAsync 重新抛出——只吞取消异常，
            // 真实清理失败（SQLite 文件锁、DI 释放错误等）向上抛出以便观察。
            // 与 MetadataRecoveryScenarioTests 的停机防护同型。
            try
            {
                if (host is not null) await host.ResetAsync();
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                if (host is not null) await host.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
            qb.Dispose();
            await ScenarioSupport.TryDownAsync(containerState);
        }
    }

    /// <summary>断言链（场景1 施工图逐条）：DB 状态 → qB 终态 → 证据采样 → 逐集登记。</summary>
    private static async Task AssertChainAsync(AcceptanceHost host, QbApiClient qb, string sceneRoot,
                                               string evidencePath, string qbVersion, string hashA,
                                               string hashB, string contentSha256)
    {
        var task = await ScenarioSupport.WaitForRunConfirmedAsync(host, hashB, TimeSpan.FromMinutes(2));
        Assert.True(task is not null, "B 任务不在 Banori 数据库中。");
        Assert.Equal("byrbt", task!.SiteSource);
        Assert.Equal(CrossSeedScenario.SubjectId, task.SubjectId);

        var decision = DownloadPathDecision.FromJson(task.PathDecision);
        Assert.True(decision is not null, "B 缺少路径决策。");
        Assert.Equal(DownloadPathDecisionKinds.ReuseVerification, decision!.Kind);

        var file = task.Files.Single();
        Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        Assert.Equal(CrossSeedScenario.TargetAbsolute, file.VerificationPath);
        Assert.Equal(CrossSeedScenario.TargetRelative, file.CurrentRelativePath);

        var recovery = DownloadRunRecovery.FromJson(task.RunRecovery);
        Assert.True(recovery is not null, "B 缺少启动恢复记录。");
        Assert.Equal(DownloadRunPurposes.Seeding, recovery!.Purpose);
        Assert.Equal(DownloadRunStates.ConfirmedRunning, recovery.State);

        Assert.False(task.IsSystemPaused);
        Assert.Null(task.VerificationIntent);
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal(1.0, task.Progress);
        Assert.Null(task.ArchiveReview);
        Assert.Null(task.FailedStep);

        // 校验步骤轨迹：Mapping→（Requested 请求台账）→Observing。recheck 发出与立即观察在
        // 同一对账轮内完成，Requested 只在轮内瞬时存在，2s 采样抓不到——用意图请求台账
        // （RecheckRequestedAtUtc + RequestState=Sent）证明本轮请求确实经 Requested 路径发出。
        var intents        = (await ScenarioSupport.ReadIntentSamplesAsync(evidencePath, hashB)).ToList();
        var mappingIndex   = intents.FindIndex(intent => intent.Step == DownloadVerificationStep.Mapping);
        var observingIndex = intents.FindIndex(intent => intent.Step == DownloadVerificationStep.Observing);
        Assert.True(mappingIndex >= 0 && observingIndex > mappingIndex,
                    $"校验步骤推进异常：{string.Join("→", intents.Select(intent => intent.Step).Distinct())}");
        Assert.Contains(intents, intent => intent.RecheckRequestedAtUtc is not null &&
                                           intent.RequestState == DownloadVerificationRequestState.Sent &&
                                           intent.IssuedCount >= 1);
        var mapping = intents[mappingIndex];
        Assert.True(mapping.Files.Count == 1 && mapping.Files[0].IsCandidate &&
                    mapping.Files[0].VerificationPath == CrossSeedScenario.TargetAbsolute,
                    $"映射快照不是候选映射：{mapping.ToJson()}");

        // qB 终态：B 改名/做种，A 未受影响。
        var torrentB = await qb.FindTorrentAsync(hashB) ?? throw new InvalidOperationException("B 不在 qB 中。");
        Assert.Equal(CrossSeedScenario.RenderedName, torrentB.Name);
        Assert.Equal("/downloads", torrentB.SavePath);
        Assert.True(CrossSeedScenario.SeedingStates.Contains(torrentB.State),
                    $"B 终态 {torrentB.State} 不是做种类。");
        Assert.Equal(0, torrentB.Downloaded);
        Assert.Equal(0, torrentB.AmountLeft);
        var filesB = await qb.GetTorrentFilesAsync(hashB);
        Assert.Single(filesB);
        Assert.Equal(CrossSeedScenario.TargetRelative, filesB[0].Name);

        var torrentA = await qb.FindTorrentAsync(hashA) ?? throw new InvalidOperationException("A 不在 qB 中。");
        Assert.True(CrossSeedScenario.SeedingStates.Contains(torrentA.State),
                    $"A 终态 {torrentA.State} 不是做种类。");

        // 全样本下载量：Downloaded 恒 0；A 的 AmountLeft 恒 0；B 的 AmountLeft 归零后不回升且终值 0。
        var seriesA = await ScenarioSupport.ReadTorrentSeriesAsync(evidencePath, hashA);
        var seriesB = await ScenarioSupport.ReadTorrentSeriesAsync(evidencePath, hashB);
        ScenarioSupport.AssertNoDownload(seriesA, "A", amountLeftAlwaysZero : true);
        ScenarioSupport.AssertNoDownload(seriesB, "B", amountLeftAlwaysZero : false);
        ScenarioSupport.AssertAmountLeftNeverRegresses(seriesB, "B");

        // A 文件未损（同一物理文件也是 B 的复用目标）。
        var hostFileA = Path.Combine(host.DownloadRoot, "Media", "Anime",
                                     CrossSeedScenario.TargetRelative.Split('/')[^1]);
        Assert.Equal(contentSha256, CrossSeedScenario.Sha256Of(hostFileA));

        // 逐集登记：完成链写入现任行。
        var rows = await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId);
        var row  = rows.FirstOrDefault(value => value.Episode == 1m);
        Assert.True(row is not null, "EpisodeVersions 没有第 1 集行。");
        Assert.Equal("byrbt", row!.SourceScope);
        Assert.Equal(file.Id, row.IncumbentVideoFileId);

        await WriteEvidenceAsync(sceneRoot, evidencePath, qbVersion, task, torrentB, torrentA, filesB,
                                 hostFileA, row, seriesA, seriesB, hashA, hashB);
    }

    /// <summary>终态快照 final.jsonl（单行）与 summary.json（含基线/增量数值）。</summary>
    private static async Task WriteEvidenceAsync(
        string sceneRoot, string evidencePath, string qbVersion, DownloadTask task,
        QbTorrentInfo torrentB, QbTorrentInfo torrentA, IReadOnlyList<QbTorrentFile> filesB,
        string hostFileA, EpisodeVersion row, IReadOnlyList<ScenarioSupport.QbSample> seriesA,
        IReadOnlyList<ScenarioSupport.QbSample> seriesB, string hashA, string hashB)
    {
        var final = new
        {
            timestamp_utc = DateTime.UtcNow,
            task = new
            {
                task.Id, task.InfoHash, task.Status, task.PreparationStage, task.IsSystemPaused,
                task.IsUserPaused, task.SubjectId, task.Episode, task.Version, task.NamingSnapshot,
                task.SiteSource, task.SavePath, task.MediaDirectory, task.SeedDirectory, task.PathDecision,
                task.RunRecovery, task.ArchiveReview, task.Progress, task.TotalSize, task.FailedStep,
                task.Error, task.CompletedAtUtc,
                files = task.Files.Select(value => new
                {
                    value.FileIndex, value.OriginalRelativePath, value.CurrentRelativePath, value.Kind,
                    value.IsSelectedForDownload, value.VerificationOutcome, value.VerificationPath, value.Size
                })
            },
            qb_torrents = new[] { torrentA, torrentB },
            qb_files    = new Dictionary<string, object> { [hashB] = filesB },
            host_files  = new Dictionary<string, string> { [hostFileA] = CrossSeedScenario.Sha256Of(hostFileA) }
        };
        await File.WriteAllTextAsync(Path.Combine(sceneRoot, "final.jsonl"),
                                     JsonSerializer.Serialize(final, ScenarioSupport.JsonOptions) + "\n",
                                     Encoding.UTF8);

        var summary = new
        {
            scenario           = "s1-cross-seed-external-candidate",
            gate               = "E2E_S1",
            qb_version         = qbVersion,
            result             = "passed",
            info_hashes        = new { external_a = hashA, uploaded_b = hashB },
            info_hash_distinct = hashA != hashB,
            download_baseline  = new
            {
                external_a = new { downloaded = seriesA[0].Downloaded, amount_left = seriesA[0].AmountLeft,
                                   at_utc = seriesA[0].TimestampUtc },
                uploaded_b = new { downloaded = seriesB[0].Downloaded, amount_left = seriesB[0].AmountLeft,
                                   at_utc = seriesB[0].TimestampUtc }
            },
            download_final = new
            {
                external_a = new { downloaded = seriesA[^1].Downloaded, amount_left = seriesA[^1].AmountLeft },
                uploaded_b = new { downloaded = seriesB[^1].Downloaded, amount_left = seriesB[^1].AmountLeft }
            },
            downloaded_increment = new
            {
                external_a = seriesA[^1].Downloaded - seriesA[0].Downloaded,
                uploaded_b = seriesB[^1].Downloaded - seriesB[0].Downloaded
            },
            episode_version = new
            {
                row.SubjectId, row.Episode, row.SourceScope, row.IncumbentVideoFileId,
                row.PendingVideoFileId, row.SupersededVideoFileId
            },
            evidence_samples = new { external_a = seriesA.Count, uploaded_b = seriesB.Count },
            evidence_path    = evidencePath
        };
        await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "summary.json"), summary);
    }
}

/// <summary>
///     场景2：映射修正命名 + 目标路径被旧任务占用（受管候选）。C 先走正常完整链占住目标，
///     再上传 v2 逐集胜出 → 受控映射 onto 被占路径 → recheck 复用 → 提前切换 → 完成归档。
/// </summary>
public sealed class CrossSeedScenario2Tests
{
    private const string ContainerName = "banori-accept-qb-s2";
    private const int    WebUiPort     = 18086;
    private const int    BtPort        = 16886;
    private const string QbPassword    = "e2e-s2-password";
    private const string GateVariable  = "E2E_S2";

    [SkippableFact]
    public async Task 场景2_映射修正命名_受管候选占用目标路径()
    {
        Skip.IfNot(ScenarioSupport.Enabled(GateVariable),
                   $"设置 {GateVariable}=1 且在 Windows 上才执行（另需 Docker；.e2e/s2 目录自动创建）");
        await ScenarioSupport.SerialGate.WaitAsync();
        try
        {
            await RunAsync();
        }
        finally
        {
            ScenarioSupport.SerialGate.Release();
        }
    }

    private async Task RunAsync()
    {
        var sceneRoot      = ScenarioSupport.FindSceneRoot("s2");
        var containerState = Path.Combine(sceneRoot, "qb-state.json");
        var evidencePath   = Path.Combine(sceneRoot, "evidence.jsonl");
        await ScenarioSupport.TryDownAsync(containerState);
        ScenarioSupport.ClearSceneDirectory(sceneRoot);

        await ScenarioSupport.RunToolAsync("qb_container.py",
                                           $"up --name {ContainerName} --scene-dir \"{sceneRoot}\" " +
                                           $"--webui-port {WebUiPort} --bt-port {BtPort} " +
                                           $"--username admin --password {QbPassword} --state \"{containerState}\"");
        // 进程级环境变量覆盖必须可恢复：SeedPipelineSmokeTests 读取同一组变量但不经过
        // SerialGate，只写不恢复会让同会话后跑的冒烟读到残留值（meta 场景已按此模式验证）。
        // 原值可能不存在（null），恢复时 SetEnvironmentVariable(name, null) 即删除。
        var qbEnvironment = (BaseUrl:  Environment.GetEnvironmentVariable("E2E_QB_BASE_URL"),
                             Username: Environment.GetEnvironmentVariable("E2E_QB_USERNAME"),
                             Password: Environment.GetEnvironmentVariable("E2E_QB_PASSWORD"));
        Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", $"http://127.0.0.1:{WebUiPort}");
        Environment.SetEnvironmentVariable("E2E_QB_USERNAME", "admin");
        Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", QbPassword);

        var qb  = new QbApiClient($"http://127.0.0.1:{WebUiPort}", "admin", QbPassword);
        AcceptanceHost? host = null;
        try
        {
            await qb.LoginAsync();
            var qbVersion = await qb.GetVersionAsync();

            host = new AcceptanceHost(sceneRoot);
            host.Initialize();
            Assert.True(string.IsNullOrEmpty(host.EnvironmentFallback), $"环境回退：{host.EnvironmentFallback}");
            Assert.Contains(host.ListHostedServices(), service => service.Contains("DownloadReconcileWorker"));

            await host.SeedSubjectAsync(CrossSeedScenario.SubjectId, CrossSeedScenario.SubjectTitle,
                                        CrossSeedScenario.SubjectNameCn);
            await host.WriteQbSettingsAsync();

            var bytes        = CrossSeedScenario.MakeMkv(800_000);
            var contentDirC  = Path.Combine(sceneRoot, "content-c");
            var contentDirB2 = Path.Combine(sceneRoot, "content-b2");
            var manifestC = await ScenarioPrep.MakeTorrentAsync(
                contentDirC, Path.Combine(sceneRoot, "torrent-c.torrent"), CrossSeedScenario.ByrbtAnnounce,
                "【喵萌奶茶屋】[泣く大人は嫌い][01][1080p][简日双语]",
                new Dictionary<string, byte[]> { [CrossSeedScenario.OriginalNameV1] = bytes },
                pieceLength : 262144);

            // [准备] C 数据预放在 /downloads 根平铺（C 走正常完整链时由 Banori 改名进媒体目录）。
            Directory.CreateDirectory(host.DownloadRoot);
            File.Copy(Path.Combine(contentDirC, CrossSeedScenario.OriginalNameV1),
                      Path.Combine(host.DownloadRoot, CrossSeedScenario.OriginalNameV1));

            // [被测] 上传 C：无候选 → FinalRoot：stopped 添加 → 早命名 → Resume → 完成。
            var driver = new UploadDriver(host);
            await driver.UploadAsync(await File.ReadAllBytesAsync(manifestC.TorrentPath),
                                     CrossSeedScenario.SubjectId);
            await new EvidenceObserver(host, qb, evidencePath).ObserveUntilAsync(
                (task, torrents) => ScenarioSupport.TaskReachedFinalState(task, torrents, manifestC.InfoHash),
                TimeSpan.FromMinutes(8));
            var taskC = await ScenarioSupport.LoadTaskByHashAsync(host, manifestC.InfoHash);
            Assert.True(taskC is { Status: DownloadTaskStatus.Completed },
                        $"C 未走完正常链：Status={taskC?.Status}, FailedStep={taskC?.FailedStep}, Error={taskC?.Error}");
            var decisionC = DownloadPathDecision.FromJson(taskC!.PathDecision);
            Assert.True(decisionC is not null, "C 缺少路径决策。");
            Assert.Equal(DownloadPathDecisionKinds.FinalRoot, decisionC!.Kind);
            var fileC = taskC.Files.Single();
            Assert.Equal(CrossSeedScenario.TargetRelative, fileC.CurrentRelativePath);
            var torrentC = await qb.FindTorrentAsync(manifestC.InfoHash) ??
                           throw new InvalidOperationException("C 完成后不在 qB 中。");
            Assert.Equal(CrossSeedScenario.RenderedName, torrentC.Name);
            Assert.Equal("/downloads", torrentC.SavePath);
            Assert.True(CrossSeedScenario.SeedingStates.Contains(torrentC.State),
                        $"C 完成后状态 {torrentC.State} 不是做种类。");
            var rowsAfterC = await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId);
            var rowAfterC  = rowsAfterC.FirstOrDefault(value => value.Episode == 1m);
            Assert.True(rowAfterC is not null, "C 完成后 EpisodeVersions 没有第 1 集行。");
            Assert.Equal("byrbt", rowAfterC!.SourceScope);
            Assert.Equal(fileC.Id, rowAfterC.IncumbentVideoFileId);
            Assert.Equal("byrbt", taskC.SiteSource);

            // [准备] 造 B v2（保证 AniParser 解析出 Version=2）。
            var manifestB2 = await ScenarioPrep.MakeTorrentAsync(
                contentDirB2, Path.Combine(sceneRoot, "torrent-b2.torrent"), CrossSeedScenario.ByrbtAnnounce,
                "【喵萌奶茶屋】[泣く大人は嫌い][01v2][1080p][简日双语]",
                new Dictionary<string, byte[]> { [CrossSeedScenario.OriginalNameV2] = bytes },
                pieceLength : 131072);
            await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "torrent-manifest-c.json"), manifestC);
            await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "torrent-manifest-b.json"), manifestB2);
            Assert.NotEqual(manifestC.InfoHash, manifestB2.InfoHash);
            Assert.Equal(manifestC.Files[0].Sha256, manifestB2.Files[0].Sha256);

            // [被测] 上传 B v2：逐集裁决 B 胜 C → 候选=C 的目标路径文件（受管）→ ReuseVerification。
            await driver.UploadAsync(await File.ReadAllBytesAsync(manifestB2.TorrentPath),
                                     CrossSeedScenario.SubjectId);
            var taskB2 = await ScenarioSupport.LoadTaskByHashAsync(host, manifestB2.InfoHash);
            Assert.True(taskB2 is not null, "B v2 任务不在 Banori 数据库中。");
            Assert.True(taskB2!.Version == 2,
                        $"AniParser 未能从「{CrossSeedScenario.OriginalNameV2}」解析 v2：task.Version={taskB2.Version}");
            var rowsAfterUpload = await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId);
            var rowAfterUpload  = rowsAfterUpload.FirstOrDefault(value => value.Episode == 1m);
            Assert.True(rowAfterUpload is not null, "B v2 登记后 EpisodeVersions 没有第 1 集行。");
            Assert.Equal(fileC.Id, rowAfterUpload!.IncumbentVideoFileId);
            Assert.Equal(taskB2.Files.Single().Id, rowAfterUpload.PendingVideoFileId);

            // 全程观察 B v2 到终态；期间核对受控映射与共享文件完整性。
            var observeTask = new EvidenceObserver(host, qb, evidencePath).ObserveUntilAsync(
                (task, torrents) => ScenarioSupport.TaskReachedFinalState(task, torrents, manifestB2.InfoHash),
                TimeSpan.FromMinutes(8));
            Exception? midFailure = null;
            try
            {
                // 受控映射：B 文件（与目标命名不一致）被改名 onto 被 C 占用的目标路径。
                await CrossSeedScenario.WaitForFileAsync(qb, manifestB2.InfoHash,
                                                         name => string.Equals(name, CrossSeedScenario.TargetRelative,
                                                                               StringComparison.Ordinal),
                                                         TimeSpan.FromMinutes(3), "受控映射改名 onto 占用路径");
                var sharedFile = Path.Combine(host.DownloadRoot, CrossSeedScenario.TargetRelative);
                Assert.Equal(manifestB2.Files[0].Sha256, CrossSeedScenario.Sha256Of(sharedFile));
            }
            catch (Exception exception)
            {
                midFailure = exception;
            }
            if (midFailure is not null) throw midFailure;
            await observeTask;

            await AssertChainAsync(host, qb, sceneRoot, evidencePath, qbVersion,
                                   manifestC.InfoHash, manifestB2.InfoHash, fileC, bytes);
        }
        finally
        {
            // 环境变量最先恢复（先于容器清理与外层串行门释放）。
            Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", qbEnvironment.BaseUrl);
            Environment.SetEnvironmentVariable("E2E_QB_USERNAME", qbEnvironment.Username);
            Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", qbEnvironment.Password);
            // 停机尽力而为：NotificationDeliveryWorker 的 PeriodicTimer 循环不捕获
            // OperationCanceledException（Workers/NotificationDeliveryWorker.cs:31，已另行记录），
            // .NET 11 的 Host 会把它从 StopAsync/DisposeAsync 重新抛出——只吞取消异常，
            // 真实清理失败（SQLite 文件锁、DI 释放错误等）向上抛出以便观察。
            // 与 MetadataRecoveryScenarioTests 的停机防护同型。
            try
            {
                if (host is not null) await host.ResetAsync();
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                if (host is not null) await host.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
            qb.Dispose();
            await ScenarioSupport.TryDownAsync(containerState);
        }
    }

    private static async Task AssertChainAsync(AcceptanceHost host, QbApiClient qb, string sceneRoot,
                                               string evidencePath, string qbVersion, string hashC,
                                               string hashB2, DownloadTaskFile fileC, byte[] bytes)
    {
        var task = await ScenarioSupport.WaitForRunConfirmedAsync(host, hashB2, TimeSpan.FromMinutes(2));
        Assert.True(task is not null, "B v2 任务不在 Banori 数据库中。");
        Assert.Equal(2, task!.Version);
        Assert.Equal("byrbt", task.SiteSource);
        Assert.Equal(CrossSeedScenario.SubjectId, task.SubjectId);

        var decision = DownloadPathDecision.FromJson(task.PathDecision);
        Assert.True(decision is not null, "B v2 缺少路径决策。");
        Assert.Equal(DownloadPathDecisionKinds.ReuseVerification, decision!.Kind);

        var file = task.Files.Single();
        Assert.Equal(DownloadFileVerificationOutcomes.ReusedVerified, file.VerificationOutcome);
        Assert.Equal(CrossSeedScenario.TargetAbsolute, file.VerificationPath);
        Assert.Equal(CrossSeedScenario.TargetRelative, file.CurrentRelativePath);

        var recovery = DownloadRunRecovery.FromJson(task.RunRecovery);
        Assert.True(recovery is not null, "B v2 缺少启动恢复记录。");
        Assert.Equal(DownloadRunPurposes.Seeding, recovery!.Purpose);
        Assert.Equal(DownloadRunStates.ConfirmedRunning, recovery.State);

        // 占用放行与全链：无 TargetPathOccupiedException（终态 Completed、无失败步骤、全程无失败采样）。
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Null(task.FailedStep);
        Assert.Null(task.Error);
        Assert.Null(task.ArchiveReview);
        Assert.Null(task.VerificationIntent);
        Assert.False(task.IsSystemPaused);
        Assert.Equal(1.0, task.Progress);
        Assert.Empty(await ScenarioSupport.ReadFailedStepsAsync(evidencePath, hashB2));

        // 校验步骤轨迹：Mapping→Requested→Observing（ReadyToRun 在单个对账轮内落库并清除，
        // 2s 采样抓不到；其语义由 ReusedVerified、逐集提前切换与 ConfirmedRunning 断言覆盖）。
        var intents = (await ScenarioSupport.ReadIntentSamplesAsync(evidencePath, hashB2)).ToList();
        var mappingIndex   = intents.FindIndex(intent => intent.Step == DownloadVerificationStep.Mapping);
        var requestedIndex = intents.FindIndex(intent => intent.Step == DownloadVerificationStep.Requested);
        var observingIndex = intents.FindIndex(intent => intent.Step == DownloadVerificationStep.Observing);
        Assert.True(mappingIndex >= 0 && requestedIndex > mappingIndex && observingIndex > requestedIndex,
                    $"校验步骤推进异常：{string.Join("→", intents.Select(intent => intent.Step).Distinct())}");
        var mapping = intents[mappingIndex];
        Assert.True(mapping.Files.Count == 1 && mapping.Files[0].IsCandidate &&
                    mapping.Files[0].VerificationPath == CrossSeedScenario.TargetAbsolute,
                    $"映射快照不是候选映射：{mapping.ToJson()}");

        // qB 终态：B v2 改名/做种；目标文件（共享数据）内容与 bytes 一致。
        var torrentB2 = await qb.FindTorrentAsync(hashB2) ?? throw new InvalidOperationException("B v2 不在 qB 中。");
        Assert.Equal(CrossSeedScenario.RenderedName, torrentB2.Name);
        Assert.Equal("/downloads", torrentB2.SavePath);
        Assert.True(CrossSeedScenario.SeedingStates.Contains(torrentB2.State),
                    $"B v2 终态 {torrentB2.State} 不是做种类。");
        Assert.Equal(0, torrentB2.Downloaded);
        Assert.Equal(0, torrentB2.AmountLeft);
        var filesB2 = await qb.GetTorrentFilesAsync(hashB2);
        Assert.Single(filesB2);
        Assert.Equal(CrossSeedScenario.TargetRelative, filesB2[0].Name);
        var sharedFile = Path.Combine(host.DownloadRoot, CrossSeedScenario.TargetRelative);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                     CrossSeedScenario.Sha256Of(sharedFile));

        // C 终态容忍 Completed 或 Replaced（清理环可能已保留内容移除种子）。
        var taskC = await ScenarioSupport.LoadTaskByHashAsync(host, hashC);
        Assert.True(taskC is { Status: DownloadTaskStatus.Completed or DownloadTaskStatus.Replaced },
                    $"C 终态异常：{taskC?.Status}");
        var torrentC = await qb.FindTorrentAsync(hashC);
        if (torrentC is not null)
            Assert.True(CrossSeedScenario.SeedingStates.Contains(torrentC.State),
                        $"C 残留种子状态 {torrentC.State} 异常。");

        // 全样本下载量：B v2 Downloaded 恒 0、AmountLeft 归零后不回升；C Downloaded 恒 0。
        var seriesC  = await ScenarioSupport.ReadTorrentSeriesAsync(evidencePath, hashC);
        var seriesB2 = await ScenarioSupport.ReadTorrentSeriesAsync(evidencePath, hashB2);
        ScenarioSupport.AssertNoDownload(seriesC, "C", amountLeftAlwaysZero : false);
        ScenarioSupport.AssertNoDownload(seriesB2, "B v2", amountLeftAlwaysZero : false);
        ScenarioSupport.AssertAmountLeftNeverRegresses(seriesB2, "B v2");
        Assert.Equal(0, seriesC[^1].AmountLeft);

        // 逐集登记：提前切换后现任为 B v2，上一任 C 入 Superseded，待完成清空。
        var rows = await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId);
        var row  = rows.FirstOrDefault(value => value.Episode == 1m);
        Assert.True(row is not null, "EpisodeVersions 没有第 1 集行。");
        Assert.Equal("byrbt", row!.SourceScope);
        Assert.Null(row.PendingVideoFileId);
        Assert.Equal(file.Id, row.IncumbentVideoFileId);
        Assert.Equal(fileC.Id, row.SupersededVideoFileId);

        await WriteEvidenceAsync(sceneRoot, evidencePath, qbVersion, task, torrentB2, torrentC, taskC!,
                                 filesB2, sharedFile, row, seriesC, seriesB2, hashC, hashB2);
    }

    private static async Task WriteEvidenceAsync(
        string sceneRoot, string evidencePath, string qbVersion, DownloadTask task,
        QbTorrentInfo torrentB2, QbTorrentInfo? torrentC, DownloadTask taskC,
        IReadOnlyList<QbTorrentFile> filesB2, string sharedFile, EpisodeVersion row,
        IReadOnlyList<ScenarioSupport.QbSample> seriesC,
        IReadOnlyList<ScenarioSupport.QbSample> seriesB2, string hashC, string hashB2)
    {
        var final = new
        {
            timestamp_utc = DateTime.UtcNow,
            task = new
            {
                task.Id, task.InfoHash, task.Status, task.PreparationStage, task.IsSystemPaused,
                task.IsUserPaused, task.SubjectId, task.Episode, task.Version, task.NamingSnapshot,
                task.SiteSource, task.SavePath, task.MediaDirectory, task.SeedDirectory, task.PathDecision,
                task.RunRecovery, task.ArchiveReview, task.Progress, task.TotalSize, task.FailedStep,
                task.Error, task.CompletedAtUtc,
                files = task.Files.Select(value => new
                {
                    value.FileIndex, value.OriginalRelativePath, value.CurrentRelativePath, value.Kind,
                    value.IsSelectedForDownload, value.VerificationOutcome, value.VerificationPath, value.Size
                })
            },
            task_c = new { taskC.Id, taskC.Status, taskC.CompletedAtUtc, taskC.ReplacedByTaskId },
            qb_torrents = torrentC is null ? new[] { torrentB2 } : new[] { torrentC, torrentB2 },
            qb_files    = new Dictionary<string, object> { [hashB2] = filesB2 },
            host_files  = new Dictionary<string, string> { [sharedFile] = CrossSeedScenario.Sha256Of(sharedFile) }
        };
        await File.WriteAllTextAsync(Path.Combine(sceneRoot, "final.jsonl"),
                                     JsonSerializer.Serialize(final, ScenarioSupport.JsonOptions) + "\n",
                                     Encoding.UTF8);

        var summary = new
        {
            scenario           = "s2-cross-seed-managed-candidate",
            gate               = "E2E_S2",
            qb_version         = qbVersion,
            result             = "passed",
            info_hashes        = new { incumbent_c = hashC, uploaded_b_v2 = hashB2 },
            info_hash_distinct = hashC != hashB2,
            download_baseline  = new
            {
                incumbent_c   = new { downloaded = seriesC[0].Downloaded, amount_left = seriesC[0].AmountLeft,
                                      at_utc = seriesC[0].TimestampUtc },
                uploaded_b_v2 = new { downloaded = seriesB2[0].Downloaded, amount_left = seriesB2[0].AmountLeft,
                                      at_utc = seriesB2[0].TimestampUtc }
            },
            download_final = new
            {
                incumbent_c   = new { downloaded = seriesC[^1].Downloaded, amount_left = seriesC[^1].AmountLeft },
                uploaded_b_v2 = new { downloaded = seriesB2[^1].Downloaded, amount_left = seriesB2[^1].AmountLeft }
            },
            downloaded_increment = new
            {
                incumbent_c   = seriesC[^1].Downloaded - seriesC[0].Downloaded,
                uploaded_b_v2 = seriesB2[^1].Downloaded - seriesB2[0].Downloaded
            },
            episode_version = new
            {
                row.SubjectId, row.Episode, row.SourceScope, row.IncumbentVideoFileId,
                row.PendingVideoFileId, row.SupersededVideoFileId
            },
            evidence_samples = new { incumbent_c = seriesC.Count, uploaded_b_v2 = seriesB2.Count },
            evidence_path    = evidencePath
        };
        await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "summary.json"), summary);
    }
}
