namespace MediaManager.Features.Downloads;

/// <summary>
///     单进程下载登记/提交串行门禁。不同 DI scope 共享同一实例；使用者必须在触碰登记、qB 提交或删除
///     相关状态之前进入，并在持久化状态与外部副作用完成后释放。门禁不允许重入。
/// </summary>
public sealed class DownloadCoordinationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Internal observation point keeps concurrency tests deterministic without changing the public DI contract.
    internal Action? WaitAttempted { get; set; }

    public ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        var wait = _semaphore.WaitAsync(cancellationToken);
        if (wait.IsCompletedSuccessfully)
            return ValueTask.FromResult<IDisposable>(new Lease(_semaphore));

        WaitAttempted?.Invoke();
        return AwaitLeaseAsync(wait);
    }

    private async ValueTask<IDisposable> AwaitLeaseAsync(Task wait)
    {
        await wait;
        return new Lease(_semaphore);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
