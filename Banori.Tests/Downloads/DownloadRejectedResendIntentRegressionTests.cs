using Banned.Qbittorrent.Exceptions;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>
///     被拒补发的校验意图回归：普通补发改根并在发送前持久化 VerificationIntent 后，
///     请求被下载器明确拒绝（HTTP 400，或 Fails. 且按 hash 复查查无）时，该意图必须随本轮请求
///     一并失效。否则旧根种子恢复可见时，残留意图把任务引入校验恢复分支，按候选根不匹配转入
///     隔离删除，对账会对恢复可见的旧根种子发出 DeleteTorrentAsync。两条拒绝分支各覆盖一条
///     完整时序；同时沿用占用撤销口径断言新根占用已撤销、旧根未决占用保留。
/// </summary>
public sealed class DownloadRejectedResendIntentRegressionTests(ITestOutputHelper output)
{
    /// <summary>
    ///     HTTP 400 分支：首条普通 Add 在旧根被接受但响应丢失且暂不可见；补发规划到正式根并持久化
    ///     校验意图后被 400 明确拒绝。意图失效后旧根种子恢复可见并对账：不得进入校验恢复分支，
    ///     不发出针对 A 的 Delete，原种子仍登记在同一旧根；新根占用撤销、旧根占用保留。
    /// </summary>
    [Fact]
    public async Task Resend400InvalidatesIntentAndSparesRecoveredOldRootTorrent()
    {
        using var h = await SchedulingHarness.CreateAsync();
        var (id, hash, oldRoot, rejectedRoot) = await SeedRejectedResendAsync(h, "Resend Intent 400 - 01.mkv",
                                                                              session => session.AddException =
                                                                                  new
                                                                                      QbittorrentBadRequestException("模拟 HTTP 400"));
        output.WriteLine($"400: old root={oldRoot}; rejected root={rejectedRoot}; " +
                         $"A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(await h.GetTaskAsync(id)))}");

        // 旧根种子恢复可见：无残留意图时必须按接管恢复，绝不隔离删除。
        h.Factory.SessionConfigurator = null;
        Assert.True(h.Store.HiddenTorrents.Remove(hash));
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        await h.RoundAsync();

        var recovered = await h.GetTaskAsync(id);
        var deletes   = h.Store.DeleteCalls.Count(call => call.Hash == hash);
        output.WriteLine($"400 recovered: A status={recovered.Status}; isolation={recovered.IsolationState}; " +
                         $"intent={(recovered.VerificationIntent is null ? "null" : "set")}; "                 +
                         $"A existing={h.Store.Torrents[hash].SavePath}; delete count={deletes}");
        Assert.Equal(0, deletes);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        Assert.Equal(oldRoot, h.Store.Torrents[hash].SavePath);
        Assert.Equal(DownloadIsolationState.None, recovered.IsolationState);
        Assert.Null(recovered.VerificationIntent);
    }

    /// <summary>
    ///     Fails. 分支：同一时序改用响应 Fails. 且按 hash 复查查无（fake 未登记新根、原种子仍隐藏）。
    ///     被拒意图同样必须失效，旧根种子恢复可见后不被删除、不被重加。
    /// </summary>
    [Fact]
    public async Task ResendFailsMissInvalidatesIntentAndSparesRecoveredOldRootTorrent()
    {
        using var h = await SchedulingHarness.CreateAsync();
        var (id, hash, oldRoot, rejectedRoot) = await SeedRejectedResendAsync(h, "Resend Intent Fails - 01.mkv",
                                                                              session => session.AddResponse =
                                                                                  "Fails.");
        output.WriteLine($"Fails.: old root={oldRoot}; rejected root={rejectedRoot}; " +
                         $"A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(await h.GetTaskAsync(id)))}");

        h.Factory.SessionConfigurator = null;
        Assert.True(h.Store.HiddenTorrents.Remove(hash));
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        await h.RoundAsync();

        var recovered = await h.GetTaskAsync(id);
        var deletes   = h.Store.DeleteCalls.Count(call => call.Hash == hash);
        output.WriteLine($"Fails. recovered: A status={recovered.Status}; isolation={recovered.IsolationState}; " +
                         $"intent={(recovered.VerificationIntent is null ? "null" : "set")}; "                    +
                         $"A existing={h.Store.Torrents[hash].SavePath}; delete count={deletes}");
        Assert.Equal(0, deletes);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        Assert.Equal(oldRoot, h.Store.Torrents[hash].SavePath);
        Assert.Equal(DownloadIsolationState.None, recovered.IsolationState);
        Assert.Null(recovered.VerificationIntent);
    }

    /// <summary>
    ///     两种拒绝共用的前半时序：历史任务重下 → 首条 Add 旧根被接受但响应丢失且隐藏（无校验意图）
    ///     → 播种复用候选 → 补发规划到正式根并持久化意图 → 明确拒绝。断言被拒后意图已失效、
    ///     新根占用已撤销、旧根未决占用保留（内存与磁盘重读各核对一次）。
    /// </summary>
    private static async Task<(Guid Id, string Hash, string OldRoot, string RejectedRoot)> SeedRejectedResendAsync(
        SchedulingHarness h, string name, Action<FakeQbittorrentSession> configureRejection)
    {
        var (id, hash, _, formal) = await HistoricalAsync(h, name);

        // 首条普通 Add 在任务专属根被下载器接受，但响应丢失且按 hash 暂不可见；首条请求没有校验意图。
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(id);
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.Equal(first.SavePath, h.Store.Torrents[hash].SavePath);
        Assert.Null((await h.GetTaskAsync(id)).VerificationIntent);
        var oldRoot = first.SavePath;
        h.Store.HiddenTorrents.Add(hash);

        // 复用候选出现：补发改规划到正式根，发送前持久化指向新根的校验意图，随后被明确拒绝。
        AddDonorCandidate(h, formal);
        h.Factory.SessionConfigurator = configureRejection;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        var sent = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray();
        Assert.Equal(2, sent.Length);
        var rejectedRoot = sent[1].SavePath;
        Assert.NotEqual(oldRoot, rejectedRoot);
        Assert.Equal(oldRoot, h.Store.Torrents[hash].SavePath);

        // 明确拒绝只否定本轮请求：本次持久化的意图失效，新根占用撤销，旧根占用保留。
        var after = await h.GetTaskAsync(id);
        Assert.Null(after.VerificationIntent);
        Assert.Equal([oldRoot], DownloadRoundLayout.PendingAddRoots(after).ToArray());
        Assert.Equal([oldRoot], (await ReloadPendingAddRootsAsync(h, id)).ToArray());
        Assert.Empty(h.Store.DeleteCalls);
        return (id, hash, oldRoot, rejectedRoot);
    }

    private static DownloadService ServiceOf(SchedulingHarness h) =>
        h.Provider.GetRequiredService<DownloadService>();

    /// <summary>独立 DbContext 从磁盘重读台账：撤销或保留都必须已持久化。</summary>
    private static async Task<IReadOnlyList<string>> ReloadPendingAddRootsAsync(
        SchedulingHarness h, Guid taskId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                     .UseSqlite($"Data Source={h.DatabasePath};Pooling=False").Options;
        var             factory = new PooledDbContextFactory<ApplicationDbContext>(options);
        await using var db      = await factory.CreateDbContextAsync();
        var             task    = await db.DownloadTasks.AsNoTracking().SingleAsync(value => value.Id == taskId);
        return DownloadRoundLayout.PendingAddRoots(task);
    }

    /// <summary>历史已归档任务：旧成片在独立根，种子已缺失，等待经 RetryAsync 重下。</summary>
    private static async Task<(Guid Id, string Hash, string OldPath, string Target)> HistoricalAsync(
        SchedulingHarness h, string name)
    {
        var id = await h.CreateUploadAsync(TestTorrentBytes.Create(name));
        var files = h.Provider.GetRequiredService<DownloadFileService>();
        var target = Assert.Single(await files.ComputeArchivalTargetsAsync(id)).TargetPath;
        const string oldRoot = "/downloads/Legacy archive";
        const string oldName = "Historic release.mkv";
        await using var db = await h.CreateDbContextAsync();
        var task = await db.DownloadTasks.Include(value => value.Files).SingleAsync(value => value.Id == id);
        var file = Assert.Single(task.Files);
        task.Status              = DownloadTaskStatus.Completed;
        task.SavePath            = oldRoot;
        task.CompletedAtUtc      = h.Time.GetUtcNow().AddDays(-5);
        task.SubmittedAtUtc      = task.CompletedAtUtc;
        task.LastSyncedAtUtc     = task.CompletedAtUtc;
        task.TorrentMissingAtUtc = h.Time.GetUtcNow();
        file.CurrentRelativePath = oldName;
        file.VerificationPath    = null;
        var row = await db.EpisodeVersions.SingleOrDefaultAsync(value => value.SubjectId == 42 &&
                                                                         value.Episode   == 1);
        if (row is null)
        {
            row = new EpisodeVersion
            {
                SubjectId   = 42, Episode                       = 1, MediaType = "SingleEpisode",
                SourceScope = SourceScopes.Legacy, UpdatedAtUtc = h.Time.GetUtcNow()
            };
            db.EpisodeVersions.Add(row);
        }

        row.PendingVideoFileId   = null;
        row.IncumbentVideoFileId = file.Id;
        await db.SaveChangesAsync();
        return (id, task.InfoHash, oldRoot + "/" + oldName, target);
    }

    /// <summary>在正式归档目录播种同路径同大小的外部做种数据，使 A 的补发规划出复用候选。</summary>
    private static void AddDonorCandidate(SchedulingHarness h, string formalTarget)
    {
        var slash = formalTarget.LastIndexOf('/');
        var donor = new string('f', 40);
        h.Store.AddTorrent(donor, "Reuse Candidate", QbittorrentTorrentCategory.Seeding, 1,
                           formalTarget[..slash]);
        h.Store.FileIdentities[donor] = [new TorrentFileSnapshot(0, formalTarget[(slash + 1)..], 1024, 1)];
    }
}
