using Banned.Qbittorrent.Exceptions;
using Banori.Core.Rules;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Xunit.Abstractions;

namespace Banori.Tests.Downloads;

/// <summary>
///     复审两项 Add 路径占用的回归。fake qB 在 Add 入口（决定接受/拒绝/抛异常之前）记录 hash、
///     保存根与完整目标路径，按挑战者 hash 计数：
///     P1 补发改根收到 Ok. 不得释放旧根上仍可能生效的请求；P2 首次隔离 Add 被 HTTP 400 明确拒绝时
///     只撤销本次新根的占用，既保留更早的未决根，也不把被拒的新根误留为占用。
/// </summary>
public sealed class DownloadAddRootRevocationRegressionTests(ITestOutputHelper output)
{
    private const string SharedRelative = "Revocation Shared - 01.mkv";

    /// <summary>
    ///     P1：A 的首条 Add 在旧根已被接受但响应丢失、按 hash 暂不可见；B 到旧根同一完整文件路径
    ///     首次被挡住。窗口后 A 补发改到新根并返回 Ok.，此时 A 仍不可见且 fake qB 仍保有旧根登记。
    ///     人工重试 B 并真实对账后，B 向旧路径的 Add 入口计数必须为 0。
    /// </summary>
    [Fact]
    public async Task ResubmitOkAtNewRootKeepsOlderPendingRootProtected()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Changed Root - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);

        // A 的首条 Add 在任务专属根被下载器接受，但响应丢失且暂不可见。
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(id);
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        h.Store.HiddenTorrents.Add(hash);
        var oldRoot = first.SavePath;
        var oldPath = Assert.Single(first.TargetPaths).Path;

        // B 用不同 hash 提交到旧根的同一完整文件路径：第一次被挡住，入口计数为 0。
        var secondId   = await ChallengerAsync(h, name, oldRoot);
        var challenger = await h.GetTaskAsync(secondId);
        Assert.NotEqual(hash, challenger.InfoHash);
        await AssertChallengerPathAsync(h, challenger, oldPath);
        await h.RoundAsync();
        Assert.Equal("提交路径", (await h.GetTaskAsync(secondId)).FailedStep);
        Assert.Equal(0, CountAdds(h, challenger.InfoHash));

        // 窗口内出现可复用候选，A 的补发改规划到新根并返回 Ok.（fake 不登记，保留旧根登记）。
        AddDonorCandidate(h, formal);
        h.Factory.SessionConfigurator = session => session.DropAdd = true;
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        var sent = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray();
        Assert.Equal(2, sent.Length);
        var secondPath = Assert.Single(sent[1].TargetPaths).Path;
        Assert.NotEqual(oldRoot, sent[1].SavePath);
        Assert.NotEqual(oldPath, secondPath);
        Assert.Contains(hash, h.Store.HiddenTorrents);
        Assert.Equal(oldRoot, h.Store.Torrents[hash].SavePath);

        var after = await h.GetTaskAsync(id);
        Assert.Equal(DownloadTaskStatus.Downloading, after.Status);
        // 旧根上仍可能生效的请求必须继续受保护：内存与磁盘重读各核对一次。
        Assert.Contains(oldRoot, DownloadRoundLayout.PendingAddRoots(after));
        Assert.Contains(oldRoot, await ReloadPendingAddRootsAsync(h, id));

        // 人工重试 B 并真实对账：旧根占用仍挡住它，入口计数为 0。
        h.Factory.SessionConfigurator = null;
        await RetryChallengerAsync(h, secondId);
        await h.ReconcileNowAsync();
        var attempts = h.Store.AddEntries.Where(entry => entry.Hash == challenger.InfoHash).ToArray();
        output.WriteLine($"P1: A first root={oldRoot}; A first path={oldPath}; A second path={secondPath}; " +
                         $"A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; "        +
                         $"B count={attempts.Length}; "                                                      +
                         $"B paths={string.Join(",", attempts.SelectMany(entry => entry.TargetPaths).Select(item => item.Path))}");
        Assert.Empty(attempts);
    }

    /// <summary>
    ///     P2：A 在旧候选根的 Add 已被接受但暂不可见；进入 Readding 后首次向任务专属根发出隔离 Add，
    ///     fake qB 抛 HTTP 400。修复后只撤销这次明确被拒、此前无未决请求的新根：
    ///     旧根挑战者 0，新根挑战者 1，且新根的实际完整路径与被拒请求一致。
    /// </summary>
    [Fact]
    public async Task FirstIsolation400RevokesOnlyTheRejectedNewRoot()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Rejected New Root - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);
        AddDonorCandidate(h, formal);

        await ServiceOf(h).RetryAsync(id);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        h.Store.HiddenTorrents.Add(hash);
        // 只推进时钟再显式对账：第一轮转隔离，第二轮由 Deleting 确认查无后进入 Readding，
        // 这一轮不发出隔离 Add，才能把 400 精确落在首次隔离请求上。
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        var prior = await h.GetTaskAsync(id);
        Assert.Equal(DownloadIsolationState.Readding, prior.IsolationState);
        Assert.Equal([first.SavePath], DownloadRoundLayout.PendingAddRoots(prior).ToArray());

        // 首次隔离 Add 到任务专属根被 HTTP 400 明确拒绝：该根此前没有未决请求。
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        await h.RoundAsync();
        var sent = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray();
        Assert.Equal(2, sent.Length);
        var rejected  = sent[1];
        var readdRoot = rejected.SavePath;
        var readdPath = Assert.Single(rejected.TargetPaths).Path;
        Assert.NotEqual(first.SavePath, readdRoot);
        Assert.Equal(first.SavePath, h.Store.Torrents[hash].SavePath);
        Assert.Empty(h.Store.DeleteCalls);

        var after = await h.GetTaskAsync(id);
        output.WriteLine($"P2 after HTTP 400: A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; " +
                         $"old root={first.SavePath}; rejected root={readdRoot}; rejected path={readdPath}");
        // 只撤销被拒的新根；旧候选根上更早的未决请求继续占用（内存与磁盘重读各核对一次）。
        Assert.Equal([first.SavePath], DownloadRoundLayout.PendingAddRoots(after).ToArray());
        Assert.Equal([first.SavePath], (await ReloadPendingAddRootsAsync(h, id)).ToArray());

        // 两个不同于 A、彼此也不同的 hash 分别争用旧根与新根。
        var oldId   = await ChallengerAsync(h, name, first.SavePath);
        var newId   = await ChallengerAsync(h, name, readdRoot, '3');
        var oldTask = await h.GetTaskAsync(oldId);
        var newTask = await h.GetTaskAsync(newId);
        Assert.Equal(3, new[] { hash, oldTask.InfoHash, newTask.InfoHash }.Distinct().Count());
        await AssertChallengerPathAsync(h, oldTask, Assert.Single(first.TargetPaths).Path);
        await AssertChallengerPathAsync(h, newTask, readdPath);

        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var oldAttempts = h.Store.AddEntries.Where(entry => entry.Hash == oldTask.InfoHash).ToArray();
        var newAttempts = h.Store.AddEntries.Where(entry => entry.Hash == newTask.InfoHash).ToArray();
        output.WriteLine($"P2: old-root competitor count={oldAttempts.Length}; " +
                         $"new-root competitor count={newAttempts.Length}; "     +
                         $"new-root actual={string.Join(",", newAttempts.SelectMany(entry => entry.TargetPaths).Select(item => item.Path))}");
        Assert.Empty(oldAttempts);
        Assert.Equal(Assert.Single(rejected.TargetPaths).Path,
                     Assert.Single(Assert.Single(newAttempts).TargetPaths).Path);
    }

    /// <summary>
    ///     P2 同根子场景：该根上更早的 Add 已被接受但暂不可见，同根补发再被 HTTP 400 拒绝时
    ///     只能否定这一次请求，更早的未决占用必须保留，同路径另一 hash 的入口计数为 0。
    /// </summary>
    [Fact]
    public async Task SameRootIsolationResend400KeepsThatRootsEarlierPending()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Same Root - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);
        AddDonorCandidate(h, formal);

        await ServiceOf(h).RetryAsync(id);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await h.ReconcileNowAsync();
        Assert.Equal(hash, Assert.Single(h.Store.AddEntries).Hash);
        h.Store.HiddenTorrents.Add(hash);
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(id)).IsolationState);

        // 首次隔离 Add 到任务专属根返回 Ok. 但暂不可见：该根进入未决占用。
        h.Factory.SessionConfigurator = session => session.AfterAddTorrentAsync = () =>
        {
            h.Store.HiddenTorrents.Add(hash);
            return Task.CompletedTask;
        };
        await h.RoundAsync();
        var accepted  = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray()[1];
        var readdRoot = accepted.SavePath;
        var readdPath = Assert.Single(accepted.TargetPaths).Path;
        Assert.Contains(readdRoot, DownloadRoundLayout.PendingAddRoots(await h.GetTaskAsync(id)));

        // 窗口到期后同根补发收到 HTTP 400：更早的未决请求仍在该根，占用必须保留。
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        Assert.Equal(3, h.Store.AddEntries.Count(entry => entry.Hash == hash));
        var after = await h.GetTaskAsync(id);
        Assert.Contains(readdRoot, DownloadRoundLayout.PendingAddRoots(after));
        Assert.Contains(readdRoot, await ReloadPendingAddRootsAsync(h, id));

        var challengerId = await ChallengerAsync(h, name, readdRoot);
        var challenger   = await h.GetTaskAsync(challengerId);
        Assert.NotEqual(hash, challenger.InfoHash);
        await AssertChallengerPathAsync(h, challenger, readdPath);
        h.Factory.SessionConfigurator = null;
        await h.ReconcileNowAsync();
        var attempts = h.Store.AddEntries.Where(entry => entry.Hash == challenger.InfoHash).ToArray();
        output.WriteLine($"Same-root resend: A readd root={readdRoot}; "                              +
                         $"A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; " +
                         $"B count={attempts.Length}");
        Assert.Empty(attempts);
    }

    /// <summary>
    ///     原场景 1：A 旧候选根未决，隔离重加在任务专属根返回 Ok. 且仍不可见时，
    ///     同路径另一 hash 的 Add 入口计数为 0。
    /// </summary>
    [Fact]
    public async Task IsolationReaddOkBeforeVisibleKeepsNewRootProtected()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Isolation Ok Hidden - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);
        AddDonorCandidate(h, formal);

        await ServiceOf(h).RetryAsync(id);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await h.ReconcileNowAsync();
        Assert.Equal(hash, Assert.Single(h.Store.AddEntries).Hash);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        h.Store.HiddenTorrents.Add(hash);
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        Assert.Equal(DownloadIsolationState.Readding, (await h.GetTaskAsync(id)).IsolationState);

        h.Factory.SessionConfigurator = session => session.AfterAddTorrentAsync = () =>
        {
            h.Store.HiddenTorrents.Add(hash);
            return Task.CompletedTask;
        };
        await h.RoundAsync();
        var readd = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray()[1];
        var root  = readd.SavePath;
        var path  = Assert.Single(readd.TargetPaths).Path;
        Assert.Contains(hash, h.Store.HiddenTorrents);
        var afterOk = await h.GetTaskAsync(id);
        Assert.Equal(DownloadIsolationState.Readding, afterOk.IsolationState);
        Assert.Contains(root, DownloadRoundLayout.PendingAddRoots(afterOk));
        Assert.Contains(root, await ReloadPendingAddRootsAsync(h, id));

        var challengerId = await ChallengerAsync(h, name, root);
        var challenger   = await h.GetTaskAsync(challengerId);
        Assert.NotEqual(hash, challenger.InfoHash);
        await AssertChallengerPathAsync(h, challenger, path);
        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var attempts = h.Store.AddEntries.Where(entry => entry.Hash == challenger.InfoHash).ToArray();
        output.WriteLine($"Scenario 1: A readd root={root}; path={path}; B count={attempts.Length}; " +
                         $"B paths={string.Join(",", attempts.SelectMany(entry => entry.TargetPaths).Select(item => item.Path))}");
        Assert.Empty(attempts);
    }

    /// <summary>
    ///     原场景 2：A 在旧候选根已被接受但暂不可见，C 占据隔离重加目标使预检失败。
    ///     确认 A 的预检失败、未发出 Delete、未发出隔离重加；B 争用旧候选根的同一完整路径为 0。
    /// </summary>
    [Fact]
    public async Task IsolationPrecheckBlockedKeepsOlderPendingRootProtected()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Isolation Preflight - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);
        AddDonorCandidate(h, formal);

        await ServiceOf(h).RetryAsync(id);
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        h.Store.HiddenTorrents.Add(hash);
        // 先让 A 走到 Readding 但尚未发出隔离 Add，再把 C 放到隔离重加目标上。
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.ReconcileNowAsync();
        await h.ReconcileNowAsync();
        var prior = await h.GetTaskAsync(id);
        Assert.Equal(DownloadIsolationState.Readding, prior.IsolationState);
        Assert.Contains(first.SavePath, DownloadRoundLayout.PendingAddRoots(prior));

        // C 以同原始相对路径占据 A 的隔离重加目标（数据库行 + 下载器种子都指向该完整路径）。
        var taskRoot = DownloadTaskPlacement.TaskRoot(prior.DownloadRootPath!, id);
        var blockId  = await CreateChallengerAsync(h, name, taskRoot, name, '2', DownloadTaskStatus.Failed);
        var block    = await h.GetTaskAsync(blockId);
        Assert.NotEqual(hash, block.InfoHash);
        await MarkFileAtAsync(h, blockId, name, taskRoot);
        h.Store.AddTorrent(block.InfoHash, "Occupier", QbittorrentTorrentCategory.Downloading, 0, taskRoot, [name]);

        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var after = await h.GetTaskAsync(id);
        Assert.Equal("提交路径", after.FailedStep);
        // 整段没有隔离重加请求，也没有 Delete；旧候选根的未决占用继续保留。
        Assert.Single(h.Store.AddEntries, entry => entry.Hash == hash);
        Assert.DoesNotContain(h.Store.AddEntries.Where(entry => entry.Hash == hash),
                              entry => entry.SavePath == taskRoot);
        Assert.DoesNotContain(hash, h.Store.DeleteCalls.Select(call => call.Hash));
        Assert.Contains(first.SavePath, DownloadRoundLayout.PendingAddRoots(after));
        Assert.Contains(first.SavePath, await ReloadPendingAddRootsAsync(h, id));

        var challengerId = await ChallengerAsync(h, name, first.SavePath);
        var challenger   = await h.GetTaskAsync(challengerId);
        Assert.NotEqual(hash, challenger.InfoHash);
        await AssertChallengerPathAsync(h, challenger, Assert.Single(first.TargetPaths).Path);
        await h.ReconcileNowAsync();
        var attempts = h.Store.AddEntries.Where(entry => entry.Hash == challenger.InfoHash).ToArray();
        output.WriteLine($"Scenario 2: A old root={first.SavePath}; isolation target={taskRoot}; "    +
                         $"failed step={after.FailedStep}; deletes={h.Store.DeleteCalls.Count}; "     +
                         $"A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; " +
                         $"B count={attempts.Length}");
        Assert.Empty(attempts);
    }

    /// <summary>
    ///     原场景 3：A 的首次隔离 Add 收到 HTTP 400，且没有更早未决 Add。被拒请求的同一完整路径
    ///     对另一 hash 不再占用：入口计数为 1，且两次请求的完整路径一致。
    /// </summary>
    [Fact]
    public async Task FirstIsolation400WithNoEarlierPendingReleasesRootForChallenger()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Isolation First 400 - 01.mkv";
        var (id, hash, _, _) = await HistoricalAsync(h, name);
        await using (var db = await h.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.SingleAsync(value => value.Id == id);
            task.VerificationIntent = new DownloadVerificationIntent
            {
                Round = 1, Step = DownloadVerificationStep.Unconfirmed, CreatedAtUtc = h.Time.GetUtcNow(), Files = []
            }.ToJson();
            await db.SaveChangesAsync();
        }

        // 重试时校验意图直接转隔离；此时既无更早未决 Add，也未向下载器发出任何请求。
        await ServiceOf(h).RetryAsync(id);
        await h.ReconcileNowAsync();
        Assert.Empty(h.Store.AddEntries);
        Assert.Empty(DownloadRoundLayout.PendingAddRoots(await h.GetTaskAsync(id)));

        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        await h.RoundAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.False(h.Store.Torrents.ContainsKey(hash));
        var rejectedPath = Assert.Single(first.TargetPaths).Path;
        var rejected     = await h.GetTaskAsync(id);
        output.WriteLine($"Scenario 3 A: hash={hash}; rejected root={first.SavePath}; rejected path={rejectedPath}; " +
                         $"A status={rejected.Status}; A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(rejected))}");
        Assert.Empty(DownloadRoundLayout.PendingAddRoots(rejected));

        h.Factory.SessionConfigurator = null;
        var challengerId = await ChallengerAsync(h, name, first.SavePath);
        var challenger   = await h.GetTaskAsync(challengerId);
        Assert.NotEqual(hash, challenger.InfoHash);
        await AssertChallengerPathAsync(h, challenger, rejectedPath);
        await h.RoundAsync();
        var actual = Assert.Single(Assert.Single(h.Store.AddEntries, entry => entry.Hash == challenger.InfoHash)
                                         .TargetPaths).Path;
        output.WriteLine($"Scenario 3 B: hash={challenger.InfoHash}; actual path={actual}; " +
                         $"count={CountAdds(h, challenger.InfoHash)}");
        Assert.Equal(rejectedPath, actual);
        Assert.Equal(1, CountAdds(h, challenger.InfoHash));
    }

    /// <summary>
    ///     普通补发改根被 HTTP 400 明确拒绝：该根是本次新登记、此前没有未决 Add，必须撤销；
    ///     旧根上更早的结果未知请求继续占用，旧根挑战者 0、新根挑战者恰好 1 且路径与被拒请求一致。
    /// </summary>
    [Fact]
    public async Task ResubmitBadRequestAtNewRootRevokesOnlyThatRoot()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Resubmit 400 - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);

        // A 的首条普通 Add 在任务专属根被下载器接受，但响应丢失且按 hash 暂不可见。
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(id);
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        h.Store.HiddenTorrents.Add(hash);
        var oldRoot = first.SavePath;
        var oldPath = Assert.Single(first.TargetPaths).Path;

        // 窗口内出现可复用候选：补发改规划到正式根，被 HTTP 400 明确拒绝。
        AddDonorCandidate(h, formal);
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        var sent = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray();
        Assert.Equal(2, sent.Length);
        var rejectedRoot = sent[1].SavePath;
        var rejectedPath = Assert.Single(sent[1].TargetPaths).Path;
        Assert.NotEqual(oldRoot, rejectedRoot);
        Assert.Contains(hash, h.Store.HiddenTorrents);
        Assert.Equal(oldRoot, h.Store.Torrents[hash].SavePath);

        var after = await h.GetTaskAsync(id);
        output.WriteLine($"Resubmit 400: A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; " +
                         $"old root={oldRoot}; rejected root={rejectedRoot}; rejected path={rejectedPath}");
        Assert.Equal(DownloadTaskStatus.Submitting, after.Status);
        // 只撤销本轮新登记的新根；旧根上结果未知的请求继续占用（内存与磁盘重读各核对一次）。
        Assert.Equal([oldRoot], DownloadRoundLayout.PendingAddRoots(after).ToArray());
        Assert.Equal([oldRoot], (await ReloadPendingAddRootsAsync(h, id)).ToArray());

        var oldId   = await ChallengerAsync(h, name, oldRoot);
        var newId   = await ChallengerAsync(h, name, rejectedRoot, '3');
        var oldTask = await h.GetTaskAsync(oldId);
        var newTask = await h.GetTaskAsync(newId);
        Assert.Equal(3, new[] { hash, oldTask.InfoHash, newTask.InfoHash }.Distinct().Count());
        await AssertChallengerPathAsync(h, oldTask, oldPath);
        await AssertChallengerPathAsync(h, newTask, rejectedPath);

        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var oldAttempts = h.Store.AddEntries.Where(entry => entry.Hash == oldTask.InfoHash).ToArray();
        var newAttempts = h.Store.AddEntries.Where(entry => entry.Hash == newTask.InfoHash).ToArray();
        output.WriteLine($"Resubmit 400: old-root competitor count={oldAttempts.Length}; " +
                         $"new-root competitor count={newAttempts.Length}");
        Assert.Empty(oldAttempts);
        Assert.Equal(rejectedPath, Assert.Single(Assert.Single(newAttempts).TargetPaths).Path);
    }

    /// <summary>
    ///     普通补发改根返回 Fails. 且按 hash 查无：与 HTTP 400 同属明确拒绝，只撤销本次新登记的根，
    ///     旧根上更早的结果未知请求继续占用。
    /// </summary>
    [Fact]
    public async Task ResubmitFailsUnseenAtNewRootRevokesOnlyThatRoot()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Resubmit Fails - 01.mkv";
        var (id, hash, _, formal) = await HistoricalAsync(h, name);

        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(id);
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        h.Store.HiddenTorrents.Add(hash);
        var oldRoot = first.SavePath;
        var oldPath = Assert.Single(first.TargetPaths).Path;

        // 补发改规划到正式根，返回 Fails.；按 hash 复查仍查无（fake 未登记，原种子仍在旧根）。
        AddDonorCandidate(h, formal);
        h.Factory.SessionConfigurator = session => session.AddResponse = "Fails.";
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));
        var sent = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray();
        Assert.Equal(2, sent.Length);
        var rejectedRoot = sent[1].SavePath;
        var rejectedPath = Assert.Single(sent[1].TargetPaths).Path;
        Assert.NotEqual(oldRoot, rejectedRoot);
        Assert.Contains(hash, h.Store.HiddenTorrents);
        Assert.Equal(oldRoot, h.Store.Torrents[hash].SavePath);

        var after = await h.GetTaskAsync(id);
        output.WriteLine($"Resubmit Fails.: A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; " +
                         $"old root={oldRoot}; rejected root={rejectedRoot}");
        Assert.Equal(DownloadTaskStatus.Submitting, after.Status);
        Assert.Equal([oldRoot], DownloadRoundLayout.PendingAddRoots(after).ToArray());
        Assert.Equal([oldRoot], (await ReloadPendingAddRootsAsync(h, id)).ToArray());

        var oldId   = await ChallengerAsync(h, name, oldRoot);
        var newId   = await ChallengerAsync(h, name, rejectedRoot, '3');
        var oldTask = await h.GetTaskAsync(oldId);
        var newTask = await h.GetTaskAsync(newId);
        Assert.Equal(3, new[] { hash, oldTask.InfoHash, newTask.InfoHash }.Distinct().Count());
        await AssertChallengerPathAsync(h, oldTask, oldPath);
        await AssertChallengerPathAsync(h, newTask, rejectedPath);

        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var oldAttempts = h.Store.AddEntries.Where(entry => entry.Hash == oldTask.InfoHash).ToArray();
        var newAttempts = h.Store.AddEntries.Where(entry => entry.Hash == newTask.InfoHash).ToArray();
        output.WriteLine($"Resubmit Fails.: old-root competitor count={oldAttempts.Length}; " +
                         $"new-root competitor count={newAttempts.Length}");
        Assert.Empty(oldAttempts);
        Assert.Equal(rejectedPath, Assert.Single(Assert.Single(newAttempts).TargetPaths).Path);
    }

    /// <summary>
    ///     目标根此前已有更早的未决 Add：本轮补发再次规划到该根并被 HTTP 400 拒绝时，
    ///     只能否定本轮请求，该根与另一旧根上的未决占用都必须保留，两个挑战者入口计数均为 0。
    /// </summary>
    [Fact]
    public async Task ResubmitRejectedKeepsTargetRootWithEarlierPendingAdd()
    {
        using var    h          = await SchedulingHarness.CreateAsync();
        const string name       = "Revocation Pending Target - 01.mkv";
        var          id         = await h.CreateUploadAsync(TestTorrentBytes.Create(name));
        var          hash       = (await h.GetTaskAsync(id)).InfoHash;
        const string oldRoot    = "/downloads/Earlier Root";
        const string targetRoot = "/downloads";
        await using (var db = await h.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.SingleAsync(value => value.Id == id);
            task.Status                 = DownloadTaskStatus.Submitting;
            task.DownloadRootPath       = targetRoot;
            task.SavePath               = oldRoot;
            task.PendingSubmitSavePath  = oldRoot;
            task.SubmittedAtUtc         = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
            task.LastSubmitAttemptAtUtc = h.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
            task.AttemptCount           = 2;
            // 更早一次补发已规划到目标根且结果未知：目标根在本次请求之前就有未决占用。
            DownloadRoundLayout.RegisterPendingAdd(task, targetRoot);
            var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == id);
            file.Kind = DownloadFileKind.Seed;
            await db.SaveChangesAsync();
        }

        var seeded = await h.GetTaskAsync(id);
        Assert.Equal([oldRoot, targetRoot], DownloadRoundLayout.PendingAddRoots(seeded).ToArray());

        // 本轮补发再次规划到 targetRoot：本次登记之前该根已有未决 Add，拒绝不得撤销它。
        h.Factory.SessionConfigurator = session => session.AddException =
            new QbittorrentBadRequestException("模拟 HTTP 400");
        await h.ReconcileNowAsync();
        var sent         = h.Store.AddEntries.Where(entry => entry.Hash == hash).ToArray();
        var rejected     = Assert.Single(sent);
        var rejectedPath = Assert.Single(rejected.TargetPaths).Path;
        Assert.Equal(targetRoot, rejected.SavePath);

        var after = await h.GetTaskAsync(id);
        output.WriteLine($"Pending target: A ledger={string.Join(",", DownloadRoundLayout.PendingAddRoots(after))}; " +
                         $"rejected root={rejected.SavePath}; rejected path={rejectedPath}");
        Assert.Equal(DownloadTaskStatus.Submitting, after.Status);
        Assert.Equal([oldRoot, targetRoot], DownloadRoundLayout.PendingAddRoots(after).ToArray());
        Assert.Equal([oldRoot, targetRoot], (await ReloadPendingAddRootsAsync(h, id)).ToArray());

        var targetId   = await ChallengerAsync(h, name, targetRoot, '3');
        var oldId      = await ChallengerAsync(h, name, oldRoot, '4');
        var targetTask = await h.GetTaskAsync(targetId);
        var oldTask    = await h.GetTaskAsync(oldId);
        await AssertChallengerPathAsync(h, targetTask, rejectedPath);

        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var targetAttempts = h.Store.AddEntries.Where(entry => entry.Hash == targetTask.InfoHash).ToArray();
        var oldAttempts    = h.Store.AddEntries.Where(entry => entry.Hash == oldTask.InfoHash).ToArray();
        output.WriteLine($"Pending target: target-root competitor count={targetAttempts.Length}; " +
                         $"old-root competitor count={oldAttempts.Length}");
        Assert.Empty(targetAttempts);
        Assert.Empty(oldAttempts);
    }

    /// <summary>
    ///     Fails. 后按 hash 复查命中：说明先前请求实际已写入，不得按拒绝撤销占用，仍按原逻辑接管；
    ///     接管后实际保存根继续挡住同一路径的另一 hash。
    /// </summary>
    [Fact]
    public async Task ResubmitFailsWithVisibleTorrentAdoptsAndKeepsActualRoot()
    {
        using var    h    = await SchedulingHarness.CreateAsync();
        const string name = "Revocation Fails Visible - 01.mkv";
        var (id, hash, _, _) = await HistoricalAsync(h, name);

        // 首条 Add 在任务专属根被接受但响应丢失，按 hash 暂不可见。
        h.Factory.SessionConfigurator = session => session.RegisterThenThrowTransport = true;
        await ServiceOf(h).RetryAsync(id);
        await h.ReconcileNowAsync();
        var first = Assert.Single(h.Store.AddEntries);
        Assert.Equal(hash, first.Hash);
        Assert.True(h.Store.Torrents.ContainsKey(hash));
        h.Store.HiddenTorrents.Add(hash);

        // 补发返回 Fails.，但复查前种子已可见：实际已写入，必须接管而不是撤销后按拒绝收尾。
        h.Factory.SessionConfigurator = session =>
        {
            session.AddResponse = "Fails.";
            session.BeforeAddTorrentAsync = () =>
            {
                h.Store.HiddenTorrents.Remove(hash);
                return Task.CompletedTask;
            };
        };
        await h.AdvanceAsync(TimeSpan.FromSeconds(31));

        var after   = await h.GetTaskAsync(id);
        var adopted = h.Store.Torrents[hash].SavePath;
        output.WriteLine($"Fails. + visible: A status={after.Status}; A save={after.SavePath}; " +
                         $"A pending={after.PendingSubmitSavePath}; adopted root={adopted}");
        // 复查命中即接管：已归档任务按接管语义把本轮根对齐为实际写入位置，不按拒绝收尾。
        Assert.NotEqual(DownloadTaskStatus.Failed, after.Status);
        Assert.Equal(adopted, after.PendingSubmitSavePath);
        Assert.Null(after.FailedStep);

        // 接管后的实际保存根继续占用：同一路径的另一 hash 入口计数为 0。
        var challengerId = await ChallengerAsync(h, name, adopted);
        var challenger   = await h.GetTaskAsync(challengerId);
        Assert.NotEqual(hash, challenger.InfoHash);
        h.Factory.SessionConfigurator = null;
        await h.RoundAsync();
        var attempts = h.Store.AddEntries.Where(entry => entry.Hash == challenger.InfoHash).ToArray();
        output.WriteLine($"Fails. + visible: competitor count={attempts.Length}; " +
                         $"competitor paths={string.Join(",", attempts.SelectMany(entry => entry.TargetPaths)
                                                                      .Select(item => item.Path))}");
        Assert.Empty(attempts);
    }

    private static int CountAdds(SchedulingHarness h, string hash) =>
        h.Store.AddEntries.Count(entry => string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase));

    private static DownloadService ServiceOf(SchedulingHarness h) =>
        h.Provider.GetRequiredService<DownloadService>();

    /// <summary>人工重试入口：fake 外部已投递过请求的任务需要先回到可重试状态。</summary>
    private static Task RetryChallengerAsync(SchedulingHarness h, Guid taskId) =>
        ServiceOf(h).RetryAsync(taskId);

    /// <summary>计数为 0 时核对：挑战者的计划目标就是 A 对应请求的完整路径。</summary>
    private static async Task AssertChallengerPathAsync(
        SchedulingHarness h, DownloadTask challenger, string expected)
    {
        await using var db = await h.CreateDbContextAsync();
        var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == challenger.Id);
        var target = DownloadPathNamePolicy.JoinSavePath(challenger.DownloadRootPath!, file.OriginalRelativePath);
        Assert.Equal(expected, target);
    }

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

    /// <summary>另一 hash 的挑战者，目标根与 A 的某个请求根相同，保持 PendingSubmit 等对账提交。</summary>
    private static Task<Guid> ChallengerAsync(
        SchedulingHarness h, string name, string root, char marker = '1') =>
        CreateChallengerAsync(h, name, root, SharedRelativeName(name), marker, DownloadTaskStatus.PendingSubmit);

    private static string SharedRelativeName(string name) => name;

    /// <summary>
    ///     播种另一 hash 的任务：与 A 的原始相对路径和大小相同，只有 pieces 不同；
    ///     可指定保存根、文件相对路径与状态。
    /// </summary>
    private static async Task<Guid> CreateChallengerAsync(
        SchedulingHarness h, string name, string root, string relative, char marker, DownloadTaskStatus status)
    {
        var             id   = await h.CreateUploadAsync(TwinTorrent(name, marker));
        await using var db   = await h.CreateDbContextAsync();
        var             task = await db.DownloadTasks.SingleAsync(value => value.Id == id);
        task.Status           = status;
        task.SavePath         = root;
        task.DownloadRootPath = root;
        var file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == id);
        file.OriginalRelativePath = relative;
        file.CurrentRelativePath  = relative;
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>把已有任务的文件行固定到指定完整路径：只用于竞争占用的第三方任务。</summary>
    private static async Task MarkFileAtAsync(SchedulingHarness h, Guid taskId, string relative, string root)
    {
        await using var db   = await h.CreateDbContextAsync();
        var             file = await db.DownloadTaskFiles.SingleAsync(value => value.DownloadTaskId == taskId);
        file.OriginalRelativePath  = relative;
        file.CurrentRelativePath   = relative;
        file.PublishedRelativePath = relative;
        file.PublishedPath         = DownloadPathNamePolicy.JoinSavePath(root, relative);
        file.Size                  = 1024;
        file.Kind                  = DownloadFileKind.Seed;
        var task = await db.DownloadTasks.SingleAsync(value => value.Id == taskId);
        task.SavePath         = root;
        task.DownloadRootPath = root;
        await db.SaveChangesAsync();
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

    /// <summary>与给定种子名、单文件大小相同，仅 pieces 不同，因此 hash 不同。</summary>
    private static byte[] TwinTorrent(string name, char marker)
    {
        const int length      = 1024;
        const int pieceLength = 32768;
        var       pieces      = new string(marker, (length + pieceLength - 1) / pieceLength * 20);
        return Encoding.UTF8.GetBytes($"d8:announce{Encode("http://tracker.example/announce")}" +
                                      $"4:infod6:lengthi{length}e4:name{Encode(name)}"          +
                                      $"12:piece lengthi{pieceLength}e6:pieces{Encode(pieces)}ee");

        static string Encode(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";
    }
}
