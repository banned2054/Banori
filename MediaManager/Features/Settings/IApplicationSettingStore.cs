namespace MediaManager.Features.Settings;

/// <summary>应用设置的持久化边界：读取时透明解密，写入时按需加密。测试可用内存替身实现。</summary>
public interface IApplicationSettingStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task SetProtectedAsync(string key, string value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
