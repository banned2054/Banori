using MediaManager.Features.Subscriptions;

namespace MediaManager.Tests.Subscriptions;

/// <summary>
///     组级门闸回收回归：互斥语义不受引用计数回收影响；空闲组与被取消的等待者
///     不在单例字典中常驻，组键随订阅内容无限增长时不产生长期内存增长。
/// </summary>
public sealed class SelectionGroupGateTests
{
    private static readonly SelectionGroupKey Key = new(1, "SingleEpisode", 12m);

    [Fact]
    public async Task SameGroup_ConcurrentWaiters_SerializeUntilReleased()
    {
        var gate = new SelectionGroupGate();

        await gate.WaitAsync(Key);

        // 直接在测试线程排队第二个等待者：WaitAsync 的同步前缀（登记引用 + 排队信号量）
        // 在返回任务前已完成，首持有者未释放时该任务必然未完成。不能用 Task.Run + 延时
        // 推断已排队——线程池延迟时测试会先释放持有者、等待者事后才进来，条目已被回收，
        // 新信号量上不存在死锁路径，旧缺陷会假绿漏掉。
        var second = gate.WaitAsync(Key);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, gate.TrackedGroupCount);

        gate.Release(Key);
        await second;

        // 第二个等待者被唤醒并取得信号量（信号量未归还时上面的 await 永不完成）；
        // 条目仍被它登记，未被提前回收。
        Assert.Equal(1, gate.TrackedGroupCount);

        gate.Release(Key);
        Assert.Equal(0, gate.TrackedGroupCount);
    }

    [Fact]
    public async Task DifferentGroups_AcquireIndependently()
    {
        var gate  = new SelectionGroupGate();
        var other = Key with { Episode = 13m };

        await gate.WaitAsync(Key);
        await gate.WaitAsync(other);
        Assert.Equal(2, gate.TrackedGroupCount);

        gate.Release(Key);
        gate.Release(other);
        Assert.Equal(0, gate.TrackedGroupCount);
    }

    [Fact]
    public async Task WaitCanceledWhileWaiting_ReclaimsReferenceWithoutLeak()
    {
        var gate = new SelectionGroupGate();

        await gate.WaitAsync(Key);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // 被取消的等待者必须归还引用：计数只剩持有者，不能遗留空计数条目。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(Key, cancellation.Token));

        Assert.Equal(1, gate.TrackedGroupCount);
        gate.Release(Key);
        Assert.Equal(0, gate.TrackedGroupCount);
    }

    [Fact]
    public async Task RecycledGroup_ReacquireStillSerializes()
    {
        var gate = new SelectionGroupGate();

        // 回收后同键重新进入：使用新信号量，互斥语义不变。
        for (var round = 0; round < 3; round++)
        {
            await gate.WaitAsync(Key);
            Assert.Equal(1, gate.TrackedGroupCount);
            gate.Release(Key);
            Assert.Equal(0, gate.TrackedGroupCount);
        }
    }
}
