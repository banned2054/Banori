using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations.Qbittorrent;
using Banori.Workers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Banori.E2E.Tests;

/// <summary>
///     真实 qB 4.6.4 的订阅规则升级与存量重试验收。只准备条目和离线内容，真实 HTTP 获取种子、
///     真实择优登记、真实 qB API 改名/搬迁/校验与真实 SQLite；显式驱动对账以精确构造旧版缺失关系。
///     内容由卷预置并经真实 piece 校验完成，不宣称验证了 BT 网络传输。E2E_SELECTION=1 启用。
/// </summary>
public sealed class SelectionReplacementScenarioTests
{
    private const int SubjectId = 246810;
    private const string Title = "学生会也有洞！";
    private const string OldName = "[ANi] 学生会也有洞！ - 07 [1080P][Baha][WEB-DL][AAC AVC][CHT].mp4";
    private const string NewName = "【喵萌奶茶屋】★10月新番★[学生会也有洞！][07][1080p][简日双语].mp4";
    private const string TargetRelative = "Media/Anime/学生会也有洞！ E07.mp4";
    private const string Password = "selection-e2e-only-password";

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 同源跨字幕组与语言择优_真实归档与存量已提交重试(bool retrySubmitted)
    {
        Skip.IfNot(ScenarioSupport.Enabled("E2E_SELECTION"), "设置 E2E_SELECTION=1，使用隔离 Docker qB 4.6.4。");
        await ScenarioSupport.SerialGate.WaitAsync();
        try
        {
            await RunAsync(retrySubmitted);
        }
        finally
        {
            ScenarioSupport.SerialGate.Release();
        }
    }

    private static async Task RunAsync(bool retrySubmitted)
    {
        Assert.Equal(7m, new AniParser().Parse(NewName)?.Episode);
        var runId = $"selection-{(retrySubmitted ? "retry" : "new")}-{Guid.NewGuid():N}";
        var sceneRoot = ScenarioSupport.FindSceneRoot(Path.Combine("smoke-tmp", runId));
        var statePath = Path.Combine(sceneRoot, "qb-state.json");
        var evidencePath = Path.Combine(sceneRoot, "evidence.jsonl");
        var keys = new[]
        {
            "E2E_QB_BASE_URL", "E2E_QB_USERNAME", "E2E_QB_PASSWORD", "ASPNETCORE_ENVIRONMENT",
            "ConnectionStrings__DefaultConnection", "Database__Provider", "BANORI_USERNAME", "BANORI_PASSWORD"
        };
        var environment = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        AcceptanceHost? host = null;
        QbApiClient? qb = null;
        try
        {
            await ScenarioSupport.RunToolAsync("qb_container.py",
                $"up --name banori-{runId} --scene-dir \"{sceneRoot}\" --image-tag 4.6.4 " +
                $"--webui-port 18088 --bt-port 16888 --username admin --password {Password} --state \"{statePath}\"");
            Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", "http://127.0.0.1:18088");
            Environment.SetEnvironmentVariable("E2E_QB_USERNAME", "admin");
            Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", Password);
            qb = new QbApiClient("http://127.0.0.1:18088", "admin", Password);
            await qb.LoginAsync();
            var version = await qb.GetVersionAsync();
            Assert.Equal("v4.6.4", version);

            host = new AcceptanceHost(sceneRoot);
            host.Initialize();
            // 调度器停下后手动调用同一生产 ReconcileAsync；构造旧数据期间不允许后台提前补推进。
            await host.Services.GetServices<IHostedService>().OfType<DownloadReconcileWorker>().Single()
                      .StopAsync(CancellationToken.None);
            await host.SeedSubjectAsync(SubjectId, Title, Title);
            await host.WriteQbSettingsAsync();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ManageSelectionRuleSettings>()
                           .SaveAsync(SelectionRuleConfig.CreateDefault() with
                           {
                               GroupOrder = ["喵萌奶茶屋", "ANi"],
                               LanguageOrder = [EnumLanguage.JpSc, EnumLanguage.Tc]
                           });
            }

            var oldData = new byte[800_000];
            var newData = new byte[1_200_000];
            new Random(46407).NextBytes(oldData);
            new Random(46408).NextBytes(newData);
            var oldManifest = await ScenarioPrep.MakeTorrentAsync(Path.Combine(sceneRoot, "content-old"),
                Path.Combine(sceneRoot, "old.torrent"), "http://tracker.invalid/announce", "old",
                new Dictionary<string, byte[]> { [OldName] = oldData });
            var newManifest = await ScenarioPrep.MakeTorrentAsync(Path.Combine(sceneRoot, "content-new"),
                Path.Combine(sceneRoot, "new.torrent"), "http://tracker.invalid/announce", "new",
                new Dictionary<string, byte[]> { [NewName] = newData });
            await using var torrentServer = new TorrentServer(new Dictionary<string, byte[]>
            {
                ["/old.torrent"] = await File.ReadAllBytesAsync(oldManifest.TorrentPath),
                ["/new.torrent"] = await File.ReadAllBytesAsync(newManifest.TorrentPath)
            });

            Directory.CreateDirectory(host.DownloadRoot);
            await File.WriteAllBytesAsync(Path.Combine(host.DownloadRoot, OldName), oldData);
            var oldItemId = await SeedItemAsync(host, OldName, torrentServer.Url("old"));
            await SelectAsync(host);
            await UntilAsync(host, qb, evidencePath, "old-submitted",
                             async () => await qb.FindTorrentAsync(oldManifest.InfoHash) is not null);
            await qb.ResumeAsync(oldManifest.InfoHash);
            await UntilAsync(host, qb, evidencePath, "old-completed",
                             async () => (await LoadTaskAsync(host, oldManifest.InfoHash)).Status == DownloadTaskStatus.Completed);
            var oldTask = await LoadTaskAsync(host, oldManifest.InfoHash);
            var oldFileId = Assert.Single(oldTask.Files).Id;
            Assert.Equal(TargetRelative, oldTask.Files.Single().CurrentRelativePath);
            AssertHash(Path.Combine(host.DownloadRoot, TargetRelative), oldManifest.Files.Single().Sha256);

            var newItemId = await SeedItemAsync(host, NewName, torrentServer.Url("new"));
            await SelectAsync(host);
            var newTask = await LoadTaskAsync(host, newManifest.InfoHash);
            var newFileId = Assert.Single(newTask.Files).Id;
            Assert.Equal(newItemId, newTask.FeedItemId);
            var row = await EpisodeAsync(host);
            Assert.Equal(oldFileId, row.IncumbentVideoFileId);
            Assert.Equal(newFileId, row.PendingVideoFileId);
            Assert.Equal(DownloadTaskStatus.Completed, (await LoadTaskAsync(host, oldManifest.InfoHash)).Status);
            AssertHash(Path.Combine(host.DownloadRoot, TargetRelative), oldManifest.Files.Single().Sha256);

            if (retrySubmitted)
            {
                // 只抹关系来模拟 0.3.0 旧数据，失败必须由真实提交和文件整理链自行产生。
                await using (var scope = host.Services.CreateAsyncScope())
                await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                                 .CreateDbContextAsync())
                {
                    var stored = await db.EpisodeVersions.SingleAsync(value => value.SubjectId == SubjectId);
                    stored.PendingVideoFileId = null;
                    await db.SaveChangesAsync();
                }
                await UntilAsync(host, qb, evidencePath, "original-failure",
                                 async () => (await LoadTaskAsync(host, newManifest.InfoHash)).Status == DownloadTaskStatus.Failed,
                                 allowFailure: true);
                var failed = await LoadTaskAsync(host, newManifest.InfoHash);
                Assert.Equal("文件整理", failed.FailedStep);
                Assert.Contains("占用", failed.Error);
                Assert.NotNull(failed.SubmittedAtUtc);
                Assert.Equal("/downloads", failed.SavePath);
                var failedTorrent = await qb.FindTorrentAsync(newManifest.InfoHash);
                Assert.NotNull(failedTorrent);
                Assert.Equal("/downloads", failedTorrent.SavePath);
                Assert.Equal("pausedDL", failedTorrent.State);
                await using var retryScope = host.Services.CreateAsyncScope();
                await retryScope.ServiceProvider.GetRequiredService<DownloadService>().RetryAsync(failed.Id);
            }

            var taskRoot = DownloadTaskPlacement.TaskRoot("/downloads", newTask.Id);
            await UntilAsync(host, qb, evidencePath, "replacement-staging", async () =>
            {
                var current = await LoadTaskAsync(host, newManifest.InfoHash);
                var torrent = await qb.FindTorrentAsync(newManifest.InfoHash);
                return current.SavePath == taskRoot && current.PendingSavePath is null && torrent?.SavePath?.TrimEnd('/') == taskRoot;
            });
            AssertHash(Path.Combine(host.DownloadRoot, TargetRelative), oldManifest.Files.Single().Sha256);
            Assert.Equal(DownloadTaskStatus.Completed, (await LoadTaskAsync(host, oldManifest.InfoHash)).Status);

            if (retrySubmitted)
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DownloadService>().ResumeAsync(newTask.Id);
            }
            // 等准备期在暂存目录内完成真实改名，再放入离线内容，真实 recheck 证明种子内容完整。
            await UntilAsync(host, qb, evidencePath, "staging-named", async () =>
            {
                var current = await LoadTaskAsync(host, newManifest.InfoHash);
                var files = await qb.GetTorrentFilesAsync(newManifest.InfoHash);
                return current.Files.Single().CurrentRelativePath == TargetRelative &&
                       files.Single().Name == TargetRelative && current.PendingSavePath is null;
            });
            await using (var scope = host.Services.CreateAsyncScope())
            await using (var session = await scope.ServiceProvider.GetRequiredService<IQbittorrentSessionFactory>().CreateAsync())
                await session.PauseAsync(newManifest.InfoHash);
            await WaitQbAsync(qb, newManifest.InfoHash, torrent => torrent.State == "pausedDL");
            var stagedContent = Path.Combine(host.DownloadRoot, ".tasks", newTask.Id.ToString("N"), TargetRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedContent)!);
            await File.WriteAllBytesAsync(stagedContent, newData);
            await qb.RecheckAsync(newManifest.InfoHash);
            await WaitQbAsync(qb, newManifest.InfoHash, torrent => torrent.Progress == 1 && torrent.AmountLeft == 0);
            await qb.ResumeAsync(newManifest.InfoHash);
            await WaitQbAsync(qb, newManifest.InfoHash, torrent => torrent.State is "stalledUP" or "uploading" or "forcedUP");

            await UntilAsync(host, qb, evidencePath, "replacement-completed", async () =>
            {
                var current = await LoadTaskAsync(host, newManifest.InfoHash);
                return current.Status == DownloadTaskStatus.Completed && current.SavePath == "/downloads" &&
                       current.PendingSavePath is null && current.ArchiveReview is null;
            });
            await UntilAsync(host, qb, evidencePath, "old-retired",
                             async () => (await LoadTaskAsync(host, oldManifest.InfoHash)).Status == DownloadTaskStatus.Replaced);
            var finalOld = await LoadTaskAsync(host, oldManifest.InfoHash);
            var finalNew = await LoadTaskAsync(host, newManifest.InfoHash);
            var finalRow = await EpisodeAsync(host);
            Assert.Equal(newFileId, finalRow.IncumbentVideoFileId);
            Assert.Equal(oldFileId, finalRow.SupersededVideoFileId);
            Assert.Null(finalRow.PendingVideoFileId);
            Assert.Equal(newTask.Id, finalOld.ReplacedByTaskId);
            Assert.StartsWith(".replaced/", finalOld.Files.Single().CurrentRelativePath);
            AssertHash(Path.Combine(host.DownloadRoot, finalOld.Files.Single().CurrentRelativePath), oldManifest.Files.Single().Sha256);
            AssertHash(Path.Combine(host.DownloadRoot, TargetRelative), newManifest.Files.Single().Sha256);
            var finalQb = await qb.FindTorrentAsync(newManifest.InfoHash);
            Assert.NotNull(finalQb);
            Assert.Equal("/downloads", finalQb.SavePath?.TrimEnd('/'));
            Assert.Equal(TargetRelative, (await qb.GetTorrentFilesAsync(newManifest.InfoHash)).Single().Name);
            Assert.Equal(1, finalQb.Progress);
            await File.WriteAllTextAsync(Path.Combine(sceneRoot, "summary.json"), JsonSerializer.Serialize(new
            {
                passed = true, qb_version = version, image = "linuxserver/qbittorrent:4.6.4", retrySubmitted,
                oldItemId, newItemId, old_hash = oldManifest.InfoHash, new_hash = newManifest.InfoHash,
                old_task = finalOld.Id, new_task = finalNew.Id, old_file = oldFileId, new_file = newFileId,
                old_backup = finalOld.Files.Single().CurrentRelativePath, new_path = TargetRelative,
                old_sha256 = oldManifest.Files.Single().Sha256, new_sha256 = newManifest.Files.Single().Sha256,
                incumbent = finalRow.IncumbentVideoFileId, pending = finalRow.PendingVideoFileId,
                superseded = finalRow.SupersededVideoFileId, torrent_http_requests = torrentServer.RequestCount,
                content_delivery = "isolated volume fixture + real qB piece recheck; no BT peer download",
                scheduler = "DownloadReconcileWorker stopped; production ReconcileAsync invoked explicitly"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try
            {
                if (host is not null) await host.DisposeAsync();
            }
            finally
            {
                qb?.Dispose();
                try
                {
                    if (File.Exists(statePath))
                        await ScenarioSupport.RunToolAsync("qb_container.py", $"down --state \"{statePath}\" --remove");
                }
                finally
                {
                    foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
    }

    private static async Task<Guid> SeedItemAsync(AcceptanceHost host, string name, string torrentUrl)
    {
        var parsed = new AniParser().Parse(name) ?? throw new InvalidOperationException($"无法解析：{name}");
        Assert.Equal(7m, parsed.Episode);
        await using var scope = host.Services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                             .CreateProtector("Banori.MediaSubscriptions.FeedUrl.v1");
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                       .CreateDbContextAsync();
        var subscription = new MediaSubscription
        {
            Id = Guid.NewGuid(), Name = "隔离择优验收", IsEnabled = false, RefreshIntervalMinutes = 30,
            FeedUrlProtected = protector.Protect("https://mikanani.me/RSS/Bangumi?bangumiId=e2e")
        };
        db.MediaSubscriptions.Add(subscription);
        var id = Guid.NewGuid();
        var ingredients = new TorrentNamingIngredients("{title} E{episode}", Title, "07", 1, parsed.Group,
                                                       "mikan", parsed.Language.ToString(), "R1080P",
                                                       FileTemplate: "{title} E{episode}");
        db.FeedItems.Add(new FeedItem
        {
            Id = id, SubscriptionId = subscription.Id, StableId = id.ToString("N"), Title = name,
            TorrentUrl = torrentUrl, FirstSeenAtUtc = DateTimeOffset.UtcNow,
            Status = FeedItemStatus.Accepted, DecisionStatus = ReleaseDecisionStatus.Accepted,
            SubjectId = SubjectId, ParsedTitle = parsed.Title, MediaType = "SingleEpisode", Episode = 7,
            Version = 1, Group = parsed.Group, Preference = parsed.Group, Language = parsed.Language.ToString(),
            Resolution = "R1080P", SourceScope = "mikan", NamingPreview = $"{Title} E07",
            NamingIngredients = ingredients.ToJson(), EvaluatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task SelectAsync(AcceptanceHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CandidateSelectionService>()
                   .SelectAndRegisterAsync([new SelectionGroupKey(SubjectId, "SingleEpisode", 7, "mikan")]);
    }

    private static async Task<DownloadTask> LoadTaskAsync(AcceptanceHost host, string hash) =>
        await ScenarioSupport.LoadTaskByHashAsync(host, hash) ?? throw new InvalidOperationException($"任务不存在：{hash}");

    private static async Task<EpisodeVersion> EpisodeAsync(AcceptanceHost host) =>
        Assert.Single(await ScenarioSupport.LoadEpisodeVersionsAsync(host, SubjectId),
                      row => row.Episode == 7 && row.SourceScope == "mikan");

    private static void AssertHash(string path, string expected) =>
        Assert.Equal(expected.ToLowerInvariant(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));

    private static async Task UntilAsync(AcceptanceHost host, QbApiClient qb, string evidencePath, string phase,
                                        Func<Task<bool>> predicate, bool allowFailure = false)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(100);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = host.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<DownloadService>().ReconcileAsync();
            await using var evidenceScope = host.Services.CreateAsyncScope();
            await using var db = await evidenceScope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                                   .CreateDbContextAsync();
            var tasks = await db.DownloadTasks.AsNoTracking().Include(task => task.Files).ToListAsync();
            var snapshot = new
            {
                at = DateTimeOffset.UtcNow, phase,
                tasks = tasks.Select(task => new
                {
                    task.Id, task.InfoHash, task.Status, task.SavePath, task.PendingSavePath, task.SubmittedAtUtc,
                    task.FailedStep, task.Error, task.ArchiveReview, task.PathDecision, task.IsUserPaused,
                    files = task.Files.Select(file => new { file.Id, file.CurrentRelativePath, file.Size })
                }),
                qb = await qb.ListTorrentsAsync()
            };
            await File.AppendAllTextAsync(evidencePath, JsonSerializer.Serialize(snapshot) + "\n");
            if (await predicate()) return;
            if (!allowFailure && tasks.Any(task => task.Status == DownloadTaskStatus.Failed))
                throw new InvalidOperationException($"{phase} 失败：" + string.Join("; ", tasks.Select(task => task.Error)));
            await Task.Delay(500);
        }
        throw new TimeoutException($"{phase} 超时，证据：{evidencePath}");
    }

    private static async Task WaitQbAsync(QbApiClient qb, string hash, Func<QbTorrentInfo, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await qb.FindTorrentAsync(hash) is { } torrent && predicate(torrent)) return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"等待 qB 状态超时：{hash}");
    }

    /// <summary>只监听回环的种子 HTTP 源，确保实际使用生产 HttpTorrentFileSource，不替换会话或下载器。</summary>
    private sealed class TorrentServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly int _port;

        public TorrentServer(IReadOnlyDictionary<string, byte[]> files)
        {
            _files = files;
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeAsync();
        }

        public int RequestCount { get; private set; }
        public string Url(string name) => $"http://127.0.0.1:{_port}/{name}.torrent";

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_stop.Token);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                    var path = request?.Split(' ').ElementAtOrDefault(1) ?? "";
                    if (!_files.TryGetValue(path, out var data)) throw new InvalidOperationException($"未知种子请求：{path}");
                    RequestCount++;
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/x-bittorrent\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(data, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
