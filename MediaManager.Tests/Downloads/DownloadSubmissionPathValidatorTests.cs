using MediaManager.Core.Torrents;
using MediaManager.Data;
using MediaManager.Features.Downloads;
using MediaManager.Integrations.Qbittorrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text;

namespace MediaManager.Tests.Downloads;

public sealed class DownloadSubmissionPathValidatorTests
{
    [Fact]
    public async Task DifferentHashesWithSameOriginalFileAreRejected()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("episode.mkv", length : 2048);
        Assert.NotEqual(h.Task.InfoHash, other.InfoHash);
        await h.Add(other);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        Assert.Contains("/downloads/media/episode.mkv", error.Message);
        Assert.Contains(other.InfoHash, error.Message);
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task ForeignDownloaderTorrentsAreNotConsulted()
    {
        await using var h = await Harness.Create();
        // 下载器里未经本应用登记的种子，即使占据完全相同的路径也不阻塞提交，更不会因它访问下载器。
        h.Store.AddTorrent(new string('a', 40), "foreign", QbittorrentTorrentCategory.Seeding,
                           1, h.Task.SavePath, ["episode.mkv"]);
        await h.Validate();
        h.AssertNoSideEffects();
    }

    [Theory]
    [InlineData("/downloads/media", null, "episode.mkv", null, null)]
    [InlineData("/downloads/media", null, "old.mkv", "old.mkv", "episode.mkv")]
    [InlineData("/old", "/downloads/media", "old.mkv", "old.mkv", "episode.mkv")]
    [InlineData("/downloads/media", "/new", "old.mkv", "old.mkv", "episode.mkv")]
    [InlineData("/old", "/downloads/media", "episode.mkv", null, null)]
    [InlineData("/downloads/media", "/new", "old.mkv", "episode.mkv", "new.mkv")]
    public async Task DatabaseCurrentAndEveryPendingTransitionLocationAreReserved(
        string root, string? pendingRoot, string current, string? from, string? to)
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated.mkv", root);
        other.Status          = DownloadTaskStatus.Completed;
        other.PendingSavePath = pendingRoot;
        other.Files.Add(File(other, current, from, to));
        await h.Add(other);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        Assert.Contains("数据库下载任务", error.Message);
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task SameHashDatabaseTaskIsExcludedForAdoption()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("episode.mkv");
        other.InfoHash = h.Task.InfoHash.ToUpperInvariant();
        other.Files.Add(File(other, "episode.mkv"));
        await h.Add(other);
        await h.Validate();
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task UnrelatedFilesAndSiblingDirectoriesSucceedEvenInSharedBase()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("episode.mkv.extra");
        other.Files.Add(File(other, "episode.mkv.extra"));
        await h.Add(other);
        await h.Validate();
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task DisjointRootTaskWithUnparseableRelativePathIsIgnored()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated.mkv", "/elsewhere");
        other.Files.Add(File(other, "../escape.mkv"));
        await h.Add(other);
        await h.Validate();
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task UnsafeStoredRelativePathFailsClosed()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated.mkv");
        other.Files.Add(File(other, "../escape.mkv"));
        await h.Add(other);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        Assert.Contains("不安全", error.Message);
        h.AssertNoSideEffects();
    }

    [Theory]
    [InlineData("episode.mkv", "episode.mkv")]
    [InlineData("episode.mkv", "EPISODE.MKV")]
    [InlineData("folder", "folder/episode.mkv")]
    [InlineData("folder/episode.mkv", "FOLDER")]
    public async Task SameTorrentDuplicateAndParentChildOriginalFilesAreRejected(string first, string second)
    {
        await using var h = await Harness.Create();
        h.Task.TorrentFile = MultiTorrent("Bundle", first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task DatabaseFileDirectoryParentChildConflictIsRejected()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated.mkv");
        other.Files.Add(File(other, "EPISODE.MKV/child.txt"));
        await h.Add(other);
        await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task CompletedLegacyWithoutFilesOrManifestDoesNotReserveEntireBase()
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated", "/downloads");
        other.DownloadRootPath = null;
        other.TorrentFile      = [];
        other.Status           = DownloadTaskStatus.Completed;
        await h.Add(other);
        await h.Validate();
        h.AssertNoSideEffects();
    }

    [Theory]
    [InlineData(DownloadTaskStatus.Completed)]
    [InlineData(DownloadTaskStatus.PendingSubmit)]
    public async Task LegacyManifestReservesSpecificPossibleOriginalNotWholeBase(DownloadTaskStatus status)
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated.mkv");
        other.DownloadRootPath = null;
        other.Status           = status;
        await h.Add(other);
        await h.Validate();
        h.Task.TorrentFile = TestTorrentBytes.Create("unrelated.mkv");
        await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        h.AssertNoSideEffects();
    }

    [Theory]
    [InlineData(DownloadTaskStatus.PendingSubmit)]
    [InlineData(DownloadTaskStatus.Submitting)]
    [InlineData(DownloadTaskStatus.Downloading)]
    public async Task ActiveLegacyWithUnknownFilesConservativelyReservesDirectory(DownloadTaskStatus status)
    {
        await using var h     = await Harness.Create();
        var             other = NewTask("unrelated", "/downloads");
        other.TorrentFile      = [];
        other.DownloadRootPath = null;
        other.Status           = status;
        await h.Add(other);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        Assert.Contains("目录范围", error.Message);
        h.AssertNoSideEffects();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    public async Task InvalidManifestOriginalFailsClosed(string name)
    {
        await using var h = await Harness.Create();
        h.Task.TorrentFile = TestTorrentBytes.Create(name);
        await Assert.ThrowsAsync<InvalidOperationException>(h.Validate);
        h.AssertNoSideEffects();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UnknownLegacySubmissionLayoutIsNotValidated(string? downloadRoot)
    {
        await using var h = await Harness.Create();
        h.Task.DownloadRootPath = downloadRoot;
        h.Task.TorrentFile      = [];
        await h.Validate();
        h.AssertNoSideEffects();
    }

    [Fact]
    public async Task CancelledValidationDoesNotHaveSideEffects()
    {
        await using var h   = await Harness.Create();
        using var       cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Validator.ValidateAsync(h.Task, cts.Token));
        h.AssertNoSideEffects();
    }

    private static DownloadTask NewTask(string name, string root = "/downloads/media", int length = 1024)
    {
        var bytes = TestTorrentBytes.Create(name);
        if (length != 1024)
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("i1024e", "i" + length + "e"));
        Assert.True(TorrentInfoHashExtractor.TryExtractInfoHash(bytes, out var hash));
        return new DownloadTask
        {
            Id               = Guid.NewGuid(), InfoHash = hash, TorrentFile = bytes, SavePath = root,
            DownloadRootPath = "/downloads", Status     = DownloadTaskStatus.PendingSubmit
        };
    }

    private static DownloadTaskFile File(DownloadTask task, string path, string? from = null, string? to = null) =>
        new()
        {
            Id = Guid.NewGuid(), DownloadTaskId = task.Id, FileIndex = 0, OriginalRelativePath = "original.mkv",
            CurrentRelativePath = path, PendingRenameFrom = from, PendingRenameTo = to, Size = 1024
        };

    private static byte[] MultiTorrent(string name, params string[] paths)
    {
        static string B(string value) => Encoding.UTF8.GetByteCount(value) + ":" + value;

        var files = string.Concat(paths.Select(path => "d6:lengthi1024e4:pathl"                 +
                                                       string.Concat(path.Split('/').Select(B)) + "ee"));
        return Encoding.UTF8.GetBytes("d4:infod5:filesl" + files + "e4:name" + B(name) + "12:piece lengthi16384eee");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection                        _connection = new("Data Source=:memory:");
        public           IDbContextFactory<ApplicationDbContext> Factory   { get; private set; } = null!;
        public           DownloadSubmissionPathValidator         Validator { get; private set; } = null!;
        public           DownloadTask                            Task      { get; } = NewTask("episode.mkv");
        public           FakeTorrentStore                        Store     { get; } = new();
        public           FakeQbittorrentSession                  Session   { get; private set; } = null!;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _connection.DisposeAsync();
        }

        public static async Task<Harness> Create()
        {
            var h = new Harness();
            await h._connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(h._connection).Options;
            h.Factory   = new PooledDbContextFactory<ApplicationDbContext>(options);
            h.Validator = new DownloadSubmissionPathValidator(h.Factory);
            h.Session   = new FakeQbittorrentSession(h.Store);
            await using var db = await h.Factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return h;
        }

        public Task Validate() => Validator.ValidateAsync(Task);

        public async Task Add(DownloadTask task)
        {
            await using var db = await Factory.CreateDbContextAsync();
            db.Add(task);
            await db.SaveChangesAsync();
        }

        public void AssertNoSideEffects()
        {
            Assert.Empty(Store.AddCalls);
            Assert.Empty(Store.AddTagCalls);
            Assert.Empty(Store.RenameCalls);
            Assert.Empty(Store.RenameFileCalls);
            Assert.Empty(Store.RenameFolderCalls);
            Assert.Empty(Store.SetLocationCalls);
            Assert.Empty(Store.PauseCalls);
            Assert.Empty(Store.ResumeCalls);
            Assert.Empty(Store.DeleteCalls);
        }
    }
}
