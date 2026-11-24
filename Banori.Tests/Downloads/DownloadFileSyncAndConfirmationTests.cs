using Banned.AniParser;
using Banori.Core.Downloads;
using Banori.Core.Rules;
using Banori.Core.Subjects;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text;
using DownloadFileKind = Banori.Features.Downloads.DownloadFileKind;

namespace Banori.Tests.Downloads;

/// <summary>第一阶段 A 后端同步测试：已完成任务外部换根重绑与 Banori 改名确认/唤醒链路。</summary>
public sealed class DownloadFileSyncAndConfirmationTests
{
    [Fact]
    public async Task Refresh_CompletedStoppedTorrentRootChange_RebindsOutOfOrderManifestByIndexAndNotifies()
    {
        const string first  = "ep01.mkv";
        const string second = "ep02.mkv";
        await using var h = await FileSyncHarness.CreateAsync((first, 1024), (second, 2048));
        await h.Service.InitializeAsync(h.Task.Id);
        h.Notifications.Clear();

        var initialUpdatedAt = h.Time.GetUtcNow();
        await h.UpdateTaskAsync(task =>
        {
            task.Status = DownloadTaskStatus.Completed;
            foreach (var file in task.Files)
            {
                file.PublishedRelativePath = file.CurrentRelativePath;
                file.PublishedPath         = "/downloads/Media/Anime/" + file.CurrentRelativePath;
                file.UpdatedAtUtc          = initialUpdatedAt;
            }
        });

        h.Time.Advance(TimeSpan.FromSeconds(15));
        const string newRoot = "/archive/Season1";
        Assert.True(TorrentDownloadPolicy.TryLoad(h.Task.TorrentFile, out var torrent, out var isMultiFile));
        var indices = TorrentDownloadPolicy.GetQbittorrentFileIndices(h.Task.TorrentFile, torrent, isMultiFile);

        h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Stopped,
            Progress = 1,
            SavePath = newRoot,
            IsMoving = false
        };
        // 清单按相反顺序返回，必须依靠种子元数据 index + size 绑定到正确的文件记录。
        h.Store.FileIdentities[h.Task.InfoHash] =
        [
            new TorrentFileSnapshot(indices["Original/" + second], "Season 1/E02.mkv", 2048, 1),
            new TorrentFileSnapshot(indices["Original/" + first],  "Season 1/E01.mkv", 1024, 1)
        ];

        var view = await h.Service.RefreshAsync(h.Task.Id);

        Assert.Equal(newRoot, view.CurrentRootPath);
        Assert.Equal("Season 1/E01.mkv", view.Files.Single(f => f.OriginalPath == "Original/" + first).CurrentPath);
        Assert.Equal("Season 1/E02.mkv", view.Files.Single(f => f.OriginalPath == "Original/" + second).CurrentPath);

        var persisted = await h.LoadAsync();
        Assert.Equal(newRoot, persisted.SavePath);
        var file1 = persisted.Files.Single(f => f.OriginalRelativePath == "Original/" + first);
        var file2 = persisted.Files.Single(f => f.OriginalRelativePath == "Original/" + second);
        Assert.Equal("Season 1/E01.mkv", file1.CurrentRelativePath);
        Assert.Equal("Season 1/E01.mkv", file1.PublishedRelativePath);
        Assert.Equal($"{newRoot}/Season 1/E01.mkv", file1.PublishedPath);
        Assert.Equal(h.Time.GetUtcNow(), file1.UpdatedAtUtc);
        Assert.Equal("Season 1/E02.mkv", file2.CurrentRelativePath);
        Assert.Equal("Season 1/E02.mkv", file2.PublishedRelativePath);
        Assert.Equal($"{newRoot}/Season 1/E02.mkv", file2.PublishedPath);
        Assert.Equal(h.Time.GetUtcNow(), file2.UpdatedAtUtc);

        var evt = Assert.Single(h.Notifications);
        Assert.Equal([h.Task.Id], evt.TaskIds);
        Assert.False(evt.AffectsListOrderOrMembership);
    }

    [Fact]
    public async Task Refresh_RootChange_SolidifiesIndependentPublishedCopyWithoutDrift()
    {
        await using var h = await FileSyncHarness.CreateAsync(("ep01.mkv", 1024));
        await h.Service.InitializeAsync(h.Task.Id);

        // 模拟存量独立已发布副本：PublishedPath 为空，靠旧 SavePath + PublishedRelativePath 解析。
        await h.UpdateTaskAsync(task =>
        {
            task.Status = DownloadTaskStatus.Completed;
            var file = task.Files.Single();
            file.CurrentRelativePath   = "Original/ep01.mkv";
            file.PublishedRelativePath = "Published/Anime - 01.mkv";
            file.PublishedPath         = null;
        });

        const string newRoot = "/seedbox/completed";
        h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Stopped,
            Progress = 1,
            SavePath = newRoot
        };
        h.Store.FileIdentities[h.Task.InfoHash] =
            [new TorrentFileSnapshot(0, "Renamed/ep01.mkv", 1024, 1)];

        await h.Service.RefreshAsync(h.Task.Id);

        var persisted = await h.LoadAsync();
        var updated   = persisted.Files.Single();
        Assert.Equal(newRoot, persisted.SavePath);
        Assert.Equal("Renamed/ep01.mkv", updated.CurrentRelativePath);
        // 独立副本路径固化为旧根拼接的绝对路径，不能跟随 SavePath 漂移到 /seedbox/completed。
        Assert.Equal("Published/Anime - 01.mkv", updated.PublishedRelativePath);
        Assert.Equal("/downloads/Media/Anime/Published/Anime - 01.mkv", updated.PublishedPath);
    }

    [Theory]
    [InlineData("moving-first")]
    [InlineData("moving-second")]
    [InlineData("checking")]
    [InlineData("error")]
    [InlineData("unknown")]
    [InlineData("incomplete")]
    [InlineData("root-changed-between-snapshots")]
    [InlineData("wrong-size")]
    [InlineData("duplicate-index")]
    public async Task Refresh_CompletedRootRebindInvalidState_RejectsWithoutModifyingDb(string scenario)
    {
        await using var h = await FileSyncHarness.CreateAsync(("ep01.mkv", 1024), ("ep02.mkv", 2048));
        await h.Service.InitializeAsync(h.Task.Id);
        h.Notifications.Clear();
        await h.UpdateTaskAsync(task => task.Status = DownloadTaskStatus.Completed);

        const string newRoot = "/archive/Season1";
        h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Stopped,
            Progress = 1,
            SavePath = newRoot,
            IsMoving = false
        };
        var originalFiles = h.Store.FileIdentities[h.Task.InfoHash].ToList();
        h.Store.FileIdentities[h.Task.InfoHash] =
        [
            originalFiles[0] with { Path = "Season 1/E01.mkv", Progress = 1 },
            originalFiles[1] with { Path = "Season 1/E02.mkv", Progress = 1 }
        ];

        switch (scenario)
        {
            case "moving-first" :
                h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with { IsMoving = true };
                break;
            case "moving-second" :
                h.Sessions.SessionConfigurator = session =>
                    session.BeforeListFilesAsync = hash =>
                    {
                        h.Store.Torrents[hash] = h.Store.Torrents[hash] with { IsMoving = true };
                        return Task.CompletedTask;
                    };
                break;
            case "checking" :
                h.Store.Torrents[h.Task.InfoHash] =
                    h.Store.Torrents[h.Task.InfoHash] with { Category = QbittorrentTorrentCategory.Checking };
                break;
            case "error" :
                h.Store.Torrents[h.Task.InfoHash] =
                    h.Store.Torrents[h.Task.InfoHash] with { Category = QbittorrentTorrentCategory.Error };
                break;
            case "unknown" :
                h.Store.Torrents[h.Task.InfoHash] =
                    h.Store.Torrents[h.Task.InfoHash] with { Category = QbittorrentTorrentCategory.Unknown };
                break;
            case "incomplete" :
                h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with { Progress = 0.99 };
                break;
            case "root-changed-between-snapshots" :
                h.Sessions.SessionConfigurator = session =>
                    session.BeforeListFilesAsync = hash =>
                    {
                        h.Store.Torrents[hash] = h.Store.Torrents[hash] with { SavePath = "/archive/Season2" };
                        return Task.CompletedTask;
                    };
                break;
            case "wrong-size" :
                h.Store.FileIdentities[h.Task.InfoHash][0] =
                    h.Store.FileIdentities[h.Task.InfoHash][0] with { Size = 9999 };
                break;
            case "duplicate-index" :
                h.Store.FileIdentities[h.Task.InfoHash][1] =
                    h.Store.FileIdentities[h.Task.InfoHash][1] with { Index = 0 };
                break;
        }

        var before = await h.LoadAsync();
        await Assert.ThrowsAsync<DownloadFileIdentityException>(() => h.Service.RefreshAsync(h.Task.Id));
        var after = await h.LoadAsync();

        Assert.Equal(before.SavePath, after.SavePath);
        Assert.Equal(before.Files.Select(f => (f.CurrentRelativePath, f.PublishedPath, f.UpdatedAtUtc)),
                     after.Files.Select(f => (f.CurrentRelativePath, f.PublishedPath, f.UpdatedAtUtc)));
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task Refresh_CompletedRootRebind_RejectsWithSpecificReasonWhenTargetOccupiedByExternalTorrent()
    {
        await using var h = await FileSyncHarness.CreateAsync(("ep01.mkv", 1024));
        await h.Service.InitializeAsync(h.Task.Id);
        h.Notifications.Clear();
        await h.UpdateTaskAsync(task => task.Status = DownloadTaskStatus.Completed);

        const string newRoot = "/archive/Season1";
        h.Store.Torrents[h.Task.InfoHash] = h.Store.Torrents[h.Task.InfoHash] with
        {
            Category = QbittorrentTorrentCategory.Stopped,
            Progress = 1,
            SavePath = newRoot
        };
        h.Store.FileIdentities[h.Task.InfoHash] =
            [new TorrentFileSnapshot(0, "E01.mkv", 1024, 1)];

        const string externalHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        h.Store.AddTorrent(externalHash, "External", QbittorrentTorrentCategory.Seeding, 1, newRoot);
        h.Store.FileIdentities[externalHash] = [new TorrentFileSnapshot(0, "E01.mkv", 1024, 1)];

        var ex = await Assert.ThrowsAsync<TargetPathOccupiedException>(() => h.Service.RefreshAsync(h.Task.Id));
        Assert.Contains("被下载器中的其他种子占用", ex.Message);

        var persisted = await h.LoadAsync();
        Assert.Equal("/downloads/Media/Anime", persisted.SavePath);
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task Refresh_WhenPathsUnchanged_DoesNotAdvanceUpdatedAtUtcOrNotify()
    {
        await using var h = await FileSyncHarness.CreateAsync(("ep01.mkv", 1024));
        await h.Service.InitializeAsync(h.Task.Id);
        h.Notifications.Clear();

        var initialUpdatedAt = h.Time.GetUtcNow();
        await h.UpdateTaskAsync(task =>
        {
            task.Status = DownloadTaskStatus.Completed;
            var file = task.Files.Single();
            file.PublishedRelativePath = file.CurrentRelativePath;
            file.PublishedPath         = "/downloads/Media/Anime/" + file.CurrentRelativePath;
            file.UpdatedAtUtc          = initialUpdatedAt;
        });

        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Service.RefreshAsync(h.Task.Id);

        var persisted = await h.LoadAsync();
        Assert.Equal(initialUpdatedAt, persisted.Files.Single().UpdatedAtUtc);
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task ApplyAsync_WhenImmediateConfirmationSucceeds_CommitsAndNotifiesWithoutPulse()
    {
        await using var h = await FileSyncHarness.CreateAsync(("[Group] Anime - 01.mkv", 1024));
        await h.Service.InitializeAsync(h.Task.Id);
        h.Notifications.Clear();

        var view = await h.Service.ApplyAsync(h.Task.Id, 0,
                                              new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 7m,
                                                                          AnimeRelativePath : "Manual/E07.mkv"));

        Assert.False(view.HasPendingFileOperation);
        Assert.Equal("Manual/E07.mkv", Assert.Single(view.Files).CurrentPath);
        // 立即确认完成时不应多余唤醒后台 worker。
        Assert.False(h.Pulse.WaitAsync(CancellationToken.None).IsCompleted);
        var evt = Assert.Single(h.Notifications);
        Assert.Equal([h.Task.Id], evt.TaskIds);
        Assert.False(evt.AffectsListOrderOrMembership);
    }

    [Fact]
    public async Task ApplyAsync_AndObservePendingAsync_PulsesWorkerOnDeferAndConfirmsOnceQbCompletes()
    {
        var policy = DownloadTimingPolicy.Default;
        await using var h = await FileSyncHarness.CreateAsync(("[Group] Anime - 01.mkv", 1024), timingPolicy : policy);
        await h.Service.InitializeAsync(h.Task.Id);
        h.Notifications.Clear();
        h.Store.DeferRenames = true;

        var applied = await h.Service.ApplyAsync(h.Task.Id, 0,
                                                 new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 7m,
                                                                             AnimeRelativePath : "Manual/E07.mkv"));

        Assert.True(applied.HasPendingFileOperation);
        Assert.True(h.Pulse.WaitAsync(CancellationToken.None).IsCompleted);
        Assert.Single(h.Notifications);

        // 确认窗口内（+1 秒）qB 仍未生效：ObservePendingAsync 返回待确认，不重复发改名请求。
        h.Time.Advance(policy.PreparationSampleInterval);
        var stillPending = await h.Service.ObservePendingAsync(h.Task.Id);
        Assert.True(stillPending.HasPendingFileOperation);
        Assert.Single(h.Store.RenameFileCalls);

        // qB 完成改名后，再次 ObservePendingAsync 写库、通知并开放再次编辑。
        h.Store.CompleteRenames();
        var confirmed = await h.Service.ObservePendingAsync(h.Task.Id);
        Assert.False(confirmed.HasPendingFileOperation);
        Assert.Equal("Manual/E07.mkv", Assert.Single(confirmed.Files).CurrentPath);
        Assert.Equal(2, h.Notifications.Count);

        var persisted = await h.LoadAsync();
        Assert.Null(persisted.PendingSavePath);
        Assert.Null(persisted.Files.Single().PendingRenameTo);
    }

    [Fact]
    public async Task ObservePendingAsync_BudgetExhaustedThrowsConfirmationException_RetainsJournalAndRecoversWhenTargetAppears()
    {
        var policy = new DownloadTimingPolicy
        {
            LayoutConfirmationWindow  = TimeSpan.FromSeconds(30),
            MaxLayoutRequestIssues    = 2,
            PreparationSampleInterval = TimeSpan.FromSeconds(1)
        };
        await using var h = await FileSyncHarness.CreateAsync(("[Group] Anime - 01.mkv", 1024), timingPolicy : policy);
        await h.Service.InitializeAsync(h.Task.Id);
        h.Store.DeferRenames = true;

        var applied = await h.Service.ApplyAsync(h.Task.Id, 0,
                                                 new DownloadFileEditRequest(DownloadFileKind.Anime, Episode : 8m,
                                                                             AnimeRelativePath : "Manual/E08.mkv"));
        Assert.True(applied.HasPendingFileOperation);
        Assert.Single(h.Store.RenameFileCalls);

        // 窗口过期，触发第 2 轮（预算上限）补发，仍未生效。
        h.Time.Advance(TimeSpan.FromSeconds(31));
        var secondAttempt = await h.Service.ObservePendingAsync(h.Task.Id);
        Assert.True(secondAttempt.HasPendingFileOperation);
        Assert.Equal(2, h.Store.RenameFileCalls.Count);

        // 第 2 轮窗口再过期，预算耗尽，抛出 DownloadLayoutConfirmationException 但保留操作日志。
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<DownloadLayoutConfirmationException>(() => h.Service.ObservePendingAsync(h.Task.Id));

        var afterExhaust = await h.LoadAsync();
        Assert.NotNull(afterExhaust.PendingSavePath);
        Assert.Equal("Manual/E08.mkv", afterExhaust.Files.Single().PendingRenameTo);

        // 后续 qB 最终达到目标路径：再次观察可确认并写库。
        h.Store.CompleteRenames();
        var recovered = await h.Service.ObservePendingAsync(h.Task.Id);
        Assert.False(recovered.HasPendingFileOperation);
        Assert.Equal("Manual/E08.mkv", Assert.Single(recovered.Files).CurrentPath);

        var afterRecover = await h.LoadAsync();
        Assert.Null(afterRecover.PendingSavePath);
        Assert.Null(afterRecover.Files.Single().PendingRenameTo);
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class FileSyncHarness : IAsyncDisposable
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), "mm-sync-" + Guid.NewGuid().ToString("N") + ".db");

        public IDbContextFactory<ApplicationDbContext> Factory       { get; private set; } = null!;
        public FakeTorrentStore                        Store         { get; }              = new();
        public FakeQbittorrentSessionFactory           Sessions      { get; private set; } = null!;
        public DownloadCoordinationGate                Gate          { get; }              = new();
        public MemoryApplicationSettingStore           Settings      { get; }              = new();
        public DownloadTaskChangeNotifier              Notifier      { get; }              = new();
        public ReconcilePulse                          Pulse         { get; }              = new();
        public MutableTimeProvider                     Time          { get; }              = new(new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
        public List<DownloadTaskChangedEvent>          Notifications { get; }              = [];
        public DownloadFileService                     Service       { get; private set; } = null!;
        public DownloadTask                            Task          { get; private set; } = null!;

        private IDisposable? _subscription;

        public ValueTask DisposeAsync()
        {
            _subscription?.Dispose();
            File.Delete(_dbPath);
            return ValueTask.CompletedTask;
        }

        public static async Task<FileSyncHarness> CreateAsync(
            params (string Path, long Size)[] files) =>
            await CreateAsync(files, null);

        public static async Task<FileSyncHarness> CreateAsync(
            (string Path, long Size) file, DownloadTimingPolicy? timingPolicy = null) =>
            await CreateAsync([file], timingPolicy);

        private static async Task<FileSyncHarness> CreateAsync(
            (string Path, long Size)[] files, DownloadTimingPolicy? timingPolicy)
        {
            var h = new FileSyncHarness();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                         .UseSqlite("Data Source=" + h._dbPath + ";Pooling=False")
                         .Options;
            h.Factory  = new PooledDbContextFactory<ApplicationDbContext>(options);
            h.Sessions = new FakeQbittorrentSessionFactory(h.Store);
            var parser      = new AniParser();
            var directories = new DownloadDirectorySettingsStore(h.Settings);
            var templates   = new NamingTemplateStore(h.Settings);
            await templates.SaveFileTemplateAsync("E{episode}");

            h._subscription = h.Notifier.Subscribe(h.Notifications.Add);
            h.Service = new DownloadFileService(h.Factory, h.Gate, h.Sessions, directories, templates,
                                                new DownloadFileClassificationPolicy(parser), parser,
                                                new ReleaseNamePreviewPolicy(),
                                                new SubjectEpisodeCorrectionPolicy(), h.Time,
                                                timingPolicy, h.Pulse, h.Notifier);

            var bytes = MultiTorrent(files);
            Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out _));
            var infoHash = torrent.InfoHashes.V1!.ToHex().ToLowerInvariant();
            h.Task = new DownloadTask
            {
                Id = Guid.NewGuid(), InfoHash = infoHash, TorrentFile = bytes, SavePath = "/downloads/Media/Anime",
                DownloadRootPath = "/downloads", MediaDirectory = "Media/Anime", SeedDirectory = "Seed",
                NamingSnapshot = "Anime", Episode = 1, Status = DownloadTaskStatus.Downloading,
                NamingIngredients =
                    new TorrentNamingIngredients("{title}", "Anime", "01", 1, null, null, FileTemplate : "E{episode}")
                       .ToJson()
            };
            await using (var db = await h.Factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Add(h.Task);
                await db.SaveChangesAsync();
            }

            h.Store.AddTorrent(infoHash, "Original", QbittorrentTorrentCategory.Seeding, 1, h.Task.SavePath);
            h.Store.FileIdentities[infoHash] =
                files.Select((file, i) => new TorrentFileSnapshot(i, "Original/" + file.Path, file.Size)).ToList();
            return h;
        }

        public async Task<DownloadTask> LoadAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            return await db.DownloadTasks.Include(task => task.Files).AsNoTracking()
                           .SingleAsync(task => task.Id == Task.Id);
        }

        public async Task UpdateTaskAsync(Action<DownloadTask> update)
        {
            await using var db   = await Factory.CreateDbContextAsync();
            var             task = await db.DownloadTasks.Include(value => value.Files).SingleAsync(value => value.Id == Task.Id);
            update(task);
            await db.SaveChangesAsync();
        }

        private static byte[] MultiTorrent((string Path, long Size)[] entries)
        {
            static string B(string text) => Encoding.UTF8.GetByteCount(text) + ":" + text;

            var files = string.Concat(entries.Select(file =>
                                                         "d6:lengthi" + file.Size + "e4:pathl" +
                                                         string.Concat(file.Path.Split('/').Select(B)) + "ee"));
            var pieceCount = (entries.Sum(file => file.Size) + 16383) / 16384;
            var pieces     = new string('0', checked((int)pieceCount * 20));
            return Encoding.UTF8.GetBytes("d4:infod5:filesl"                                + files     +
                                          "e4:name8:Original12:piece lengthi16384e6:pieces" + B(pieces) + "ee");
        }
    }
}
