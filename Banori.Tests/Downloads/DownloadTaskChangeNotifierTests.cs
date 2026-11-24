using Banori.Data;
using Banori.Features.Downloads;
using Banori.Integrations.Qbittorrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>数据库提交后变更通知服务的线程安全、异常隔离、退订与服务提交触发测试。</summary>
public sealed class DownloadTaskChangeNotifierTests
{
    [Fact]
    public void Notify_IsolatesSubscriberExceptions_AndHonorsDispose()
    {
        var notifier = new DownloadTaskChangeNotifier();
        var received = new List<DownloadTaskChangedEvent>();

        using var badSub = notifier.Subscribe(_ => throw new InvalidOperationException("模拟订阅者失败"));
        var       goodSub = notifier.Subscribe(received.Add);
        var       firstId = Guid.NewGuid();

        notifier.Notify(firstId, true);
        Assert.Single(received);
        Assert.Equal([firstId], received[0].TaskIds);
        Assert.True(received[0].AffectsListOrderOrMembership);

        goodSub.Dispose();
        goodSub.Dispose(); // 重复释放幂等

        notifier.Notify(Guid.NewGuid(), false);
        Assert.Single(received);
    }

    [Fact]
    public async Task SubscribeAndNotify_AreThreadSafeUnderConcurrentPublishersAndSubscribers()
    {
        var notifier = new DownloadTaskChangeNotifier();
        var count    = 0;

        using var stable = notifier.Subscribe(_ => Interlocked.Increment(ref count));
        var workers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                using var temp = notifier.Subscribe(_ => { });
                notifier.Notify(Guid.NewGuid(), index % 2 == 0);
            }
        }));

        await Task.WhenAll(workers);
        Assert.Equal(400, count);
    }

    [Fact]
    public async Task Services_EmitNotificationsAfterCreateReconcileAndDeleteCommits()
    {
        using var harness  = await DownloadServiceTests.CreateHarnessForPageTestsAsync();
        var       notifier = new DownloadTaskChangeNotifier();
        var       events   = new List<DownloadTaskChangedEvent>();
        using var sub      = notifier.Subscribe(events.Add);

        var intake = ActivatorUtilities.CreateInstance<DownloadIntakeService>(harness.Provider, notifier);
        var service = ActivatorUtilities.CreateInstance<DownloadService>(harness.Provider, notifier);

        var torrent = TestTorrentBytes.Create("[ANi] Notify Test - 01 [1080P].mkv");
        var taskId  = await intake.CreateFromUploadAsync(torrent, subjectId : 42);
        Assert.Contains(events, evt => evt.TaskIds.Contains(taskId) && evt.AffectsListOrderOrMembership);

        events.Clear();
        await service.ReconcileAsync();
        Assert.Contains(events, evt => evt.TaskIds.Contains(taskId));

        // 模拟运行中进度变化（不改变列表顺序或归属）。
        events.Clear();
        string hash;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.SingleAsync(item => item.Id == taskId);
            task.Status         = DownloadTaskStatus.Downloading;
            task.IsSystemPaused = false;
            hash                = task.InfoHash;
            await db.SaveChangesAsync();
        }

        harness.Store.StoppedAdds.Remove(hash);
        harness.Store.Torrents[hash] = harness.Store.Torrents[hash] with
        {
            Category = QbittorrentTorrentCategory.Downloading,
            Progress = 0.5
        };
        await service.ReconcileAsync();
        Assert.Contains(events, evt => evt.TaskIds.Contains(taskId) && !evt.AffectsListOrderOrMembership);

        events.Clear();
        await service.DeleteAsync(taskId, false, false);
        var deleteEvt = Assert.Single(events);
        Assert.Contains(taskId, deleteEvt.TaskIds);
        Assert.True(deleteEvt.AffectsListOrderOrMembership);
    }
}
