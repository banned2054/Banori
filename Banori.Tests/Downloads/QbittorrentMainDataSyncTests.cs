using Banori.Components.Pages.Downloads;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Xunit.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>第二阶段验收测试：sync/maindata 协议解析、单实例共享轮询、断连/配置切换重置、虚拟列表边界与耗时观测。</summary>
public sealed class QbittorrentMainDataSyncTests(ITestOutputHelper output)
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task QbittorrentSession_SyncMainData_DistinguishesFullAndPartialFields()
    {
        const string hashA = "0123456789abcdef0123456789abcdef01234567";
        const string hashB = "fedcba9876543210fedcba9876543210fedcba98";

        var requestedUris = new List<string>();
        var responses = new Queue<string>(
        [
            $$"""
              {
                "rid": 1,
                "full_update": true,
                "torrents": {
                  "{{hashA.ToUpperInvariant()}}": {
                    "name": "Banori.Show.Ep01.mkv",
                    "state": "downloading",
                    "progress": 0.15,
                    "total_size": 1073741824,
                    "save_path": "/downloads/Anime"
                  },
                  "{{hashB}}": {
                    "name": "Banori.Show.Ep02.mkv",
                    "state": "moving",
                    "progress": 1.0,
                    "total_size": 536870912,
                    "save_path": "/downloads/Moving"
                  }
                }
              }
              """,
            $$"""
              {
                "rid": 2,
                "torrents": {
                  "{{hashA.ToUpperInvariant()}}": {
                    "progress": 0.42
                  }
                },
                "torrents_removed": ["{{hashB.ToUpperInvariant()}}"]
              }
              """
        ]);

        using var handler = new DelegateHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri!.ToString());
            var json = responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var session = new QbittorrentSession(null!, null, httpClient, "http://qb.local:8080/");

        var full = await session.SyncMainDataAsync(0);
        Assert.Equal("http://qb.local:8080/api/v2/sync/maindata?rid=0", requestedUris[0]);
        Assert.True(full.FullUpdate);
        Assert.Equal(1, full.Rid);
        Assert.True(full.ResponseBytes > 0);
        Assert.Equal(2, full.Torrents.Count);
        Assert.Equal("Banori.Show.Ep01.mkv", full.Torrents[hashA].Name);
        Assert.Equal(QbittorrentTorrentCategory.Downloading, full.Torrents[hashA].Category);
        Assert.Equal(false, full.Torrents[hashA].IsMoving);
        Assert.Equal(0.15, full.Torrents[hashA].Progress);
        Assert.Equal(1073741824, full.Torrents[hashA].TotalSize);
        Assert.Equal("/downloads/Anime", full.Torrents[hashA].SavePath);
        Assert.Equal(true, full.Torrents[hashB].IsMoving);

        var delta = await session.SyncMainDataAsync(1);
        Assert.Equal("http://qb.local:8080/api/v2/sync/maindata?rid=1", requestedUris[1]);
        Assert.False(delta.FullUpdate);
        Assert.Equal(2, delta.Rid);
        Assert.True(delta.ResponseBytes < full.ResponseBytes);
        var deltaA = Assert.Contains(hashA, delta.Torrents);
        Assert.Equal(0.42, deltaA.Progress);
        // 增量未包含的字段必须严格保持 null，不能把 0/false 误当成显式变更。
        Assert.Null(deltaA.Name);
        Assert.Null(deltaA.Category);
        Assert.Null(deltaA.RawState);
        Assert.Null(deltaA.IsMoving);
        Assert.Null(deltaA.TotalSize);
        Assert.Null(deltaA.SavePath);
        Assert.Equal([hashB], delta.TorrentsRemoved);

        Assert.Throws<InvalidDataException>(() => QbittorrentSession.ParseMainDataDelta(Encoding.UTF8.GetBytes("null")));
        Assert.Throws<InvalidDataException>(() => QbittorrentSession.ParseMainDataDelta(Encoding.UTF8.GetBytes("{}")));

        output.WriteLine($"Protocol bytes: full={full.ResponseBytes} B, delta={delta.ResponseBytes} B");
    }

    [Fact]
    public async Task SyncService_IncrementalMergeRemovalAndUnknownHashReset_TracksMetrics()
    {
        const string hashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string hashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string hashC = "cccccccccccccccccccccccccccccccccccccccc";

        var settingStore = new InMemorySettingStore();
        await SaveSettingsAsync(settingStore, "http://qb.local:8080", "pw-1");

        var sentRids = new ConcurrentQueue<long>();
        var step     = 0;
        var sessionFactory = new ScriptedSessionFactory((settings, _) => new ScriptedSyncSession(rid =>
        {
            sentRids.Enqueue(rid);
            var current = Interlocked.Increment(ref step);
            return current switch
            {
                1 => MakeJsonDelta($$"""
                                      {
                                        "rid": 1,
                                        "full_update": true,
                                        "torrents": {
                                          "{{hashA}}": {
                                            "name": "Show-A",
                                            "state": "downloading",
                                            "progress": 0.12,
                                            "total_size": 2048,
                                            "save_path": "/downloads/A"
                                          },
                                          "{{hashB}}": {
                                            "name": "Show-B",
                                            "state": "stalledDL",
                                            "progress": 0.50,
                                            "total_size": 4096,
                                            "save_path": "/downloads/B"
                                          }
                                        }
                                      }
                                      """),
                2 => MakeJsonDelta($$"""
                                      {
                                        "rid": 2,
                                        "torrents": {
                                          "{{hashA}}": { "progress": 0.64 }
                                        },
                                        "torrents_removed": ["{{hashB}}"]
                                      }
                                      """),
                3 => MakeJsonDelta($$"""
                                      {
                                        "rid": 3,
                                        "torrents": {
                                          "{{hashC}}": { "progress": 0.20 }
                                        }
                                      }
                                      """),
                _ => MakeJsonDelta($$"""
                                      {
                                        "rid": 10,
                                        "full_update": true,
                                        "torrents": {
                                          "{{hashA}}": {
                                            "name": "Show-A",
                                            "state": "downloading",
                                            "progress": 0.80,
                                            "total_size": 2048,
                                            "save_path": "/downloads/A"
                                          },
                                          "{{hashC}}": {
                                            "name": "Show-C",
                                            "state": "downloading",
                                            "progress": 0.25,
                                            "total_size": 1024,
                                            "save_path": "/downloads/C"
                                          }
                                        }
                                      }
                                      """)
            };
        }));

        await using var provider = BuildSyncProvider(settingStore, sessionFactory);
        var             sync     = provider.GetRequiredService<QbittorrentMainDataSyncService>();
        sync.PollInterval = TimeSpan.FromMilliseconds(15);

        var snapshots = new ConcurrentQueue<QbittorrentMainDataSnapshot>();
        using var sub = sync.Subscribe(snapshots.Enqueue);

        await WaitForAsync(() => sync.CurrentSnapshot.CurrentRid == 10);

        var orderedRids = sentRids.ToArray();
        Assert.Equal([0L, 1L, 2L, 0L], orderedRids[..4]);

        var final = sync.CurrentSnapshot;
        Assert.Equal(2, final.Torrents.Count);
        Assert.Equal(0.80, final.Torrents[hashA].Progress);
        Assert.Equal("Show-A", final.Torrents[hashA].Name);
        Assert.Equal(0.25, final.Torrents[hashC].Progress);
        Assert.False(final.Torrents.ContainsKey(hashB));

        var receivedList = snapshots.ToArray();
        var fullBytes    = receivedList[0].LastResponseBytes;
        var deltaBytes   = receivedList[1].LastResponseBytes;
        Assert.True(deltaBytes < fullBytes);
        Assert.Equal(0.64, receivedList[1].Torrents[hashA].Progress);
        Assert.Equal("Show-A", receivedList[1].Torrents[hashA].Name);
        Assert.Equal(2048, receivedList[1].Torrents[hashA].TotalSize);
        Assert.Equal("/downloads/A", receivedList[1].Torrents[hashA].SavePath);

        output.WriteLine($"Sync metrics: requestCount={final.SyncRequestCount}, fullBytes={fullBytes}, deltaBytes={deltaBytes}, totalBytes={final.TotalResponseBytes}");
    }

    [Fact]
    public async Task SyncService_DisconnectAndConfigSwitch_DisposesOldSessionAndReloadsFromRidZero()
    {
        const string hashA = "1111111111111111111111111111111111111111";
        const string hashB = "2222222222222222222222222222222222222222";

        var settingStore = new InMemorySettingStore();
        await SaveSettingsAsync(settingStore, "http://qb-a:8080", "pw-a");

        var failNextOnA = false;
        var createdLog  = new ConcurrentQueue<(string BaseUrl, List<long> Rids, ScriptedSyncSession Session)>();
        var sessionFactory = new ScriptedSessionFactory((settings, _) =>
        {
            var rids = new List<long>();
            ScriptedSyncSession? created = null;
            created = new ScriptedSyncSession(rid =>
            {
                rids.Add(rid);
                if (settings.BaseUrl.Contains("qb-a", StringComparison.OrdinalIgnoreCase))
                {
                    if (failNextOnA)
                    {
                        failNextOnA = false;
                        throw new HttpRequestException("连接断开");
                    }

                    return rid == 0
                        ? MakeJsonDelta($$"""{"rid":5,"full_update":true,"torrents":{"{{hashA}}":{"name":"A","state":"downloading","progress":0.25,"total_size":100,"save_path":"/a"} } }""")
                        : MakeJsonDelta($$"""{"rid":6,"torrents":{"{{hashA}}":{"progress":0.40} } }""");
                }

                return MakeJsonDelta($$"""{"rid":1,"full_update":true,"torrents":{"{{hashB}}":{"name":"B","state":"downloading","progress":0.75,"total_size":200,"save_path":"/b"} } }""");
            });
            createdLog.Enqueue((settings.BaseUrl, rids, created));
            return created;
        });

        await using var provider = BuildSyncProvider(settingStore, sessionFactory);
        var             sync     = provider.GetRequiredService<QbittorrentMainDataSyncService>();
        sync.PollInterval = TimeSpan.FromMilliseconds(15);

        using var sub = sync.Subscribe(_ => { });
        await WaitForAsync(() => sync.CurrentSnapshot.CurrentRid == 6);

        // 模拟运行中断连：必须销毁旧会话并重置 rid=0 重建拉全量。
        failNextOnA = true;
        await WaitForAsync(() => createdLog.Count >= 2 && sync.CurrentSnapshot.CurrentRid == 5);
        var firstSession = createdLog.ElementAt(0).Session;
        Assert.True(firstSession.Disposed);
        Assert.Equal(0L, createdLog.ElementAt(1).Rids[0]);

        // 切换下载器地址：销毁旧会话、清空旧快照并从 rid=0 拉新下载器全量。
        await SaveSettingsAsync(settingStore, "http://qb-b:8080", "pw-b");
        await WaitForAsync(() => sync.CurrentSnapshot.Torrents.ContainsKey(hashB));

        var logEntries = createdLog.ToArray();
        Assert.True(logEntries[1].Session.Disposed);
        Assert.Equal("http://qb-b:8080", logEntries[^1].BaseUrl);
        Assert.Equal(0L, logEntries[^1].Rids[0]);
        Assert.False(sync.CurrentSnapshot.Torrents.ContainsKey(hashA));
        Assert.Equal(0.75, sync.CurrentSnapshot.Torrents[hashB].Progress);

        // 关闭下载器开关后应停止拉取并清空运行时状态。
        await settingStore.SetAsync(ManageQbittorrentSettings.EnabledKey, "false");
        await WaitForAsync(() => sync.CurrentSnapshot.Torrents.Count == 0 && sync.CurrentSnapshot.ConfigKey is null);
        Assert.True(logEntries[^1].Session.Disposed);
    }

    [Fact]
    public async Task SyncService_MultipleSubscribersShareOneSession_StopsPollingAfterAllUnsubscribe()
    {
        const string hash = "3333333333333333333333333333333333333333";

        var settingStore = new InMemorySettingStore();
        await SaveSettingsAsync(settingStore, "http://qb.local:8080", "pw");

        var sessionCount = 0;
        var syncCounter  = 0;
        ScriptedSyncSession? activeSession = null;
        var sessionFactory = new ScriptedSessionFactory((_, _) =>
        {
            Interlocked.Increment(ref sessionCount);
            activeSession = new ScriptedSyncSession(rid =>
            {
                var n = Interlocked.Increment(ref syncCounter);
                return rid == 0
                    ? MakeJsonDelta($$"""{"rid":1,"full_update":true,"torrents":{"{{hash}}":{"name":"Shared","state":"downloading","progress":0.10,"total_size":100,"save_path":"/dl"} } }""")
                    : MakeJsonDelta($$"""{"rid":{{n}},"torrents":{"{{hash}}":{"progress":{{Math.Min(0.99, 0.10 + n * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture)}} } } }""");
            });
            return activeSession;
        });

        await using var provider = BuildSyncProvider(settingStore, sessionFactory);
        var             sync     = provider.GetRequiredService<QbittorrentMainDataSyncService>();
        sync.PollInterval = TimeSpan.FromMilliseconds(20);

        var sub1Updates = 0;
        var sub2Updates = 0;
        var sub1        = sync.Subscribe(_ => Interlocked.Increment(ref sub1Updates));
        await WaitForAsync(() => Volatile.Read(ref sub1Updates) >= 1);

        var sub2 = sync.Subscribe(_ => Interlocked.Increment(ref sub2Updates));
        Assert.True(Volatile.Read(ref sub2Updates) >= 1);
        Assert.Equal(1, Volatile.Read(ref sessionCount));

        await WaitForAsync(() => Volatile.Read(ref sub2Updates) >= 3);
        Assert.Equal(1, Volatile.Read(ref sessionCount));

        // 释放第一个订阅者后第二个仍正常工作，会话不重建。
        sub1.Dispose();
        var afterSub1Dispose = Volatile.Read(ref syncCounter);
        await WaitForAsync(() => Volatile.Read(ref syncCounter) >= afterSub1Dispose + 2);
        Assert.Equal(1, Volatile.Read(ref sessionCount));

        // 最后一个订阅者退出后立即停止轮询并释放会话。
        sub2.Dispose();
        await WaitForAsync(() => activeSession?.Disposed == true);
        var countAtStop = Volatile.Read(ref syncCounter);
        await Task.Delay(100);
        Assert.Equal(countAtStop, Volatile.Read(ref syncCounter));

        // 再次订阅时重建新会话并从 rid=0 重新同步。
        using var sub3 = sync.Subscribe(_ => { });
        await WaitForAsync(() => Volatile.Read(ref sessionCount) == 2 &&
                                 Volatile.Read(ref syncCounter) > countAtStop);
    }

    [Fact]
    public async Task ViewModel_ApplyMainDataSnapshot_UpdatesActiveAndSelectedOnlyAndPreservesAcrossScrollAndRefresh()
    {
        using var harness = await DownloadServiceTests.CreateHarnessForPageTestsAsync();

        var hDownloading  = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var hPaused       = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var hCompleted    = "cccccccccccccccccccccccccccccccccccccccc";
        var hReplaced     = "dddddddddddddddddddddddddddddddddddddddd";
        var hLost         = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var hIsolated     = "ffffffffffffffffffffffffffffffffffffffff";
        var hOutOfView    = "9999999999999999999999999999999999999999";

        var ids = new Dictionary<string, Guid>();
        await using (var db = await harness.CreateDbContextAsync())
        {
            var tDownloading = MakeTask("Active-Downloading", DownloadTaskListTestSeed.At(206),
                                        DownloadTaskStatus.Downloading, progress : 0.10, infoHash : hDownloading);
            var tPaused = MakeTask("Active-Paused", DownloadTaskListTestSeed.At(205),
                                   DownloadTaskStatus.Paused, userPaused : true,
                                   progress : 0.30, infoHash : hPaused);
            var tCompleted = MakeTask("Final-Completed", DownloadTaskListTestSeed.At(204),
                                      DownloadTaskStatus.Completed, progress : 1.0, infoHash : hCompleted);
            var tReplaced = MakeTask("Final-Replaced", DownloadTaskListTestSeed.At(203),
                                     DownloadTaskStatus.Replaced, progress : 0.40, infoHash : hReplaced);
            var tLost = MakeTask("Final-Lost", DownloadTaskListTestSeed.At(202),
                                 DownloadTaskStatus.Lost, progress : 0.25, infoHash : hLost);
            var tIsolated = MakeTask("Guard-Isolated", DownloadTaskListTestSeed.At(201),
                                     DownloadTaskStatus.Downloading, progress : 0.20, infoHash : hIsolated);
            tIsolated.IsolationState = DownloadIsolationState.Deleting;

            var fillerTasks = Enumerable.Range(0, 40)
                                        .Select(i => MakeTask($"Filler-{i:00}",
                                                              DownloadTaskListTestSeed.At(10 + i),
                                                              DownloadTaskStatus.Completed, progress : 1.0))
                                        .ToArray();
            var tOutOfView = MakeTask("OutOfView-Active", DownloadTaskListTestSeed.At(1),
                                      DownloadTaskStatus.Downloading, progress : 0.15, infoHash : hOutOfView);

            db.DownloadTasks.AddRange(tDownloading, tPaused, tCompleted, tReplaced, tLost, tIsolated, tOutOfView);
            db.DownloadTasks.AddRange(fillerTasks);
            await db.SaveChangesAsync();

            ids["downloading"] = tDownloading.Id;
            ids["paused"]      = tPaused.Id;
            ids["completed"]   = tCompleted.Id;
            ids["replaced"]    = tReplaced.Id;
            ids["lost"]        = tLost.Id;
            ids["isolated"]    = tIsolated.Id;
            ids["outofview"]   = tOutOfView.Id;
        }

        var queries = new DownloadTaskQueries(harness.Provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());
        var vm = new DownloadTasksPageViewModel(
            harness.Service, harness.Intake, queries,
            new DownloadTaskFilterStore(harness.Provider.GetRequiredService<IApplicationSettingStore>()),
            NullLogger<DownloadTasksPageViewModel>.Instance);
        vm.OverscanCount = 0;
        await vm.SetViewportAsync(0, 6);
        await vm.LoadAsync();
        await vm.SelectTaskAsync(ids["outofview"]);

        var orderedBefore = vm.OrderedTaskIds.ToArray();
        var resultsEvents = 0;
        vm.ResultsChanged += () => resultsEvents++;

        var sw = Stopwatch.StartNew();
        var snapshot = new QbittorrentMainDataSnapshot(
            new Dictionary<string, QbittorrentMainDataTorrent>(StringComparer.OrdinalIgnoreCase)
            {
                [hDownloading] = MakeTorrent(hDownloading, QbittorrentTorrentCategory.Downloading, 0.73),
                [hPaused]      = MakeTorrent(hPaused, QbittorrentTorrentCategory.Stopped, 0.55),
                [hCompleted]   = MakeTorrent(hCompleted, QbittorrentTorrentCategory.Downloading, 0.22),
                [hReplaced]    = MakeTorrent(hReplaced, QbittorrentTorrentCategory.Downloading, 0.88),
                [hLost]        = MakeTorrent(hLost, QbittorrentTorrentCategory.Downloading, 0.66),
                [hIsolated]    = MakeTorrent(hIsolated, QbittorrentTorrentCategory.Downloading, 0.91),
                [hOutOfView]   = MakeTorrent(hOutOfView, QbittorrentTorrentCategory.Downloading, 0.84)
            },
            1, 1, 512, 512, DateTimeOffset.UtcNow, "cfg");
        vm.ApplyMainDataSnapshot(snapshot);
        sw.Stop();

        Assert.Equal(1, resultsEvents);
        Assert.Equal(orderedBefore, vm.OrderedTaskIds);
        Assert.Equal(0.73, vm.GetTaskView(ids["downloading"])!.Progress);
        Assert.Equal(0.55, vm.GetTaskView(ids["paused"])!.Progress);
        Assert.Equal(1.00, vm.GetTaskView(ids["completed"])!.Progress);
        Assert.Equal(0.40, vm.GetTaskView(ids["replaced"])!.Progress);
        Assert.Equal(0.25, vm.GetTaskView(ids["lost"])!.Progress);
        Assert.Equal(0.20, vm.GetTaskView(ids["isolated"])!.Progress);
        Assert.Equal(0.84, vm.SelectedTask!.Progress);

        // 数据库通知局部刷新或滚动再回来时，不得把实时进度回退为数据库旧值。
        await vm.RefreshChangedTasksAsync([ids["downloading"]], false);
        Assert.Equal(0.73, vm.GetTaskView(ids["downloading"])!.Progress);

        await vm.SetViewportAsync(10, 6);
        await vm.SetViewportAsync(0, 6);
        Assert.Equal(0.73, vm.GetTaskView(ids["downloading"])!.Progress);

        output.WriteLine($"ViewModel live progress overlay latency: {sw.Elapsed.TotalMilliseconds:F3} ms");
    }

    private static QbittorrentMainDataTorrent MakeTorrent(
        string hash, QbittorrentTorrentCategory category, double progress) =>
        new(hash, hash, category, "downloading", false, progress, 1000, "/downloads");

    private static QbittorrentMainDataDelta MakeJsonDelta(string json) =>
        QbittorrentSession.ParseMainDataDelta(Encoding.UTF8.GetBytes(json));

    private static ServiceProvider BuildSyncProvider(
        IApplicationSettingStore settingStore, IQbittorrentSessionFactory sessionFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settingStore);
        services.AddSingleton(sessionFactory);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddScoped<ManageQbittorrentSettings>();
        services.AddSingleton<QbittorrentMainDataSyncService>();
        return services.BuildServiceProvider();
    }

    private static async Task SaveSettingsAsync(
        IApplicationSettingStore store, string baseUrl, string password)
    {
        await store.SetAsync(ManageQbittorrentSettings.EnabledKey, "true");
        await store.SetAsync(ManageQbittorrentSettings.BaseUrlKey, baseUrl);
        await store.SetAsync(ManageQbittorrentSettings.UsernameKey, "admin");
        await store.SetAsync(ManageQbittorrentSettings.PasswordKey, password);
        await store.SetAsync(ManageQbittorrentSettings.SavePathKey, "/downloads");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "等待异步同步条件超时。");
    }

    private static DownloadTask MakeTask(
        string             snapshot,
        DateTimeOffset     createdAtUtc,
        DownloadTaskStatus status,
        bool               userPaused = false,
        double?            progress   = null,
        string?            infoHash   = null)
    {
        var task = DownloadTaskListTestSeed.NewTask(snapshot, createdAtUtc, status, userPaused : userPaused);
        task.Progress = progress;
        if (infoHash is not null) task.InfoHash = infoHash;
        return task;
    }

    private sealed class InMemorySettingStore : IApplicationSettingStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetProtectedAsync(string key, string value, CancellationToken cancellationToken = default) =>
            SetAsync(key, value, cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class ScriptedSessionFactory(
        Func<QbittorrentSettings, string?, IQbittorrentSession> createSession) : IQbittorrentSessionFactory
    {
        public Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IQbittorrentSession> CreateAsync(
            QbittorrentSettings settings, string? password, CancellationToken cancellationToken = default) =>
            Task.FromResult(createSession(settings, password));
    }

    private sealed class ScriptedSyncSession(Func<long, QbittorrentMainDataDelta> sync) : IQbittorrentSession
    {
        public bool Disposed { get; private set; }

        public string ServerVersion => "5.0.0";

        public Task<QbittorrentMainDataDelta> SyncMainDataAsync(
            long rid, CancellationToken cancellationToken = default) =>
            Task.FromResult(sync(rid));

        public Task<string> AddTorrentAsync(byte[] torrentBytes, string savePath, IReadOnlyList<string> tags,
                                            string? rename, bool startStopped,
                                            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TorrentSnapshot>> FindTorrentsAsync(
            IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TorrentSnapshot>> ListTorrentsAsync(
            string? tag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AddTagAsync(string infoHash, string tag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListFileNamesAsync(
            string infoHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PauseAsync(string infoHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResumeAsync(string infoHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RenameTorrentAsync(
            string infoHash, string newName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RenameTorrentFileAsync(
            string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RenameTorrentFolderAsync(
            string infoHash, string oldPath, string newPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteTorrentAsync(
            string infoHash, bool deleteFiles, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteTorrentsAsync(
            IReadOnlyCollection<string> infoHashes, bool deleteFiles, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
