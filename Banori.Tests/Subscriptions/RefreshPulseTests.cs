using Banori.Features.Subscriptions;

namespace Banori.Tests.Subscriptions;

public sealed class RefreshPulseTests
{
    [Fact]
    public async Task PulseBeforeWait_WaitReturnsImmediately()
    {
        var pulse = new RefreshPulse();
        pulse.Pulse();

        var wait      = pulse.WaitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(wait, completed);
        await wait;
    }

    [Fact]
    public async Task PulseDuringWait_ReleasesWaiter()
    {
        var pulse  = new RefreshPulse();
        var wait   = pulse.WaitAsync(CancellationToken.None);
        var before = wait.IsCompleted;

        pulse.Pulse();
        await wait;

        Assert.False(before);
        Assert.True(wait.IsCompleted);
    }

    [Fact]
    public async Task WaitAgainAfterPulseDuringWait_BlocksOnceStickySignalConsumed()
    {
        // 回归：脉冲唤醒等待者后，已完成的等待者不得被后续等待复用，否则刷新工作线程
        // 每轮立即返回，形成忙循环。脉冲同时置位的粘性标记允许多消费一次（等待者可能
        // 已被 WhenAny 抛弃，宁可多重发不可丢失），之后必须重新阻塞等下一个脉冲。
        var pulse = new RefreshPulse();
        var first = pulse.WaitAsync(CancellationToken.None);
        pulse.Pulse();
        await first;

        await pulse.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task NoPulse_WaitStaysPending()
    {
        var       pulse = new RefreshPulse();
        using var cts   = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task WaitAfterConsumedSignal_WaitsForNextPulse()
    {
        var pulse = new RefreshPulse();
        pulse.Pulse();
        await pulse.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pulse.WaitAsync(cts.Token));

        pulse.Pulse();
        await pulse.WaitAsync(CancellationToken.None);
    }
}
