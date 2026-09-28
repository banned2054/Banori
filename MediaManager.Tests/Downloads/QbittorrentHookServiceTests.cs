using MediaManager.Features.Downloads;
using MediaManager.Features.Settings;
using MediaManager.Integrations.Qbittorrent;
using MediaManager.Tests.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaManager.Tests.Downloads;

/// <summary>
///     qBittorrent 钩子处理回归：令牌校验（含未生成状态与轮换失效）、hash 格式校验、
///     有效请求触发对账脉冲、被拒绝的请求不触发。
/// </summary>
public sealed class QbittorrentHookServiceTests
{
    [Fact]
    public async Task Handle_WithoutGeneratedToken_RejectsEverything()
    {
        var (service, pulse, _) = CreateService();
        var waitTask = pulse.WaitAsync(CancellationToken.None);

        Assert.Equal(QbittorrentHookResult.Rejected,
                     await service.HandleAsync("any-token", InfoHash, "completed"));
        Assert.Equal(QbittorrentHookResult.Rejected, await service.HandleAsync(null, InfoHash, "completed"));
        Assert.False(waitTask.IsCompleted, "未配置令牌时任何请求都不得触发对账脉冲。");
    }

    [Fact]
    public async Task Handle_WithValidToken_PulsesAndAcceptsCaseInsensitiveHash()
    {
        var (service, pulse, settings) = CreateService();
        var token = await settings.GenerateHookTokenAsync();
        // 先挂等待者，验证 HandleAsync 真的触发了脉冲唤醒。
        var waitTask = pulse.WaitAsync(CancellationToken.None);

        var result = await service.HandleAsync(token, InfoHash.ToUpperInvariant(), "completed");

        Assert.Equal(QbittorrentHookResult.Accepted, result);
        Assert.True(waitTask.IsCompleted, "有效钩子必须触发对账脉冲。");

        // 下一条仍接受：脉冲是“至少一次”语义，连续请求不互相吞掉。
        Assert.Equal(QbittorrentHookResult.Accepted, await service.HandleAsync(token, InfoHash, "added"));
    }

    [Fact]
    public async Task Handle_WithWrongToken_RejectsWithoutPulsing()
    {
        var (service, pulse, settings) = CreateService();
        await settings.GenerateHookTokenAsync();
        var waitTask = pulse.WaitAsync(CancellationToken.None);

        var result = await service.HandleAsync("wrong-token", InfoHash, "completed");

        Assert.Equal(QbittorrentHookResult.Rejected, result);
        Assert.False(waitTask.IsCompleted, "被拒绝的钩子不得触发对账脉冲。");
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("eb7d7d0cfdc65f4d8dfcb1723f640cac05d060e")]   // 39 位
    [InlineData("zz7d7d0cfdc65f4d8dfcb1723f640cac05d060e5")]  // 非十六进制
    public async Task Handle_WithInvalidHash_ReturnsInvalidHashWithoutPulsing(string hash)
    {
        var (service, pulse, settings) = CreateService();
        var token = await settings.GenerateHookTokenAsync();
        var waitTask = pulse.WaitAsync(CancellationToken.None);

        var result = await service.HandleAsync(token, hash, "completed");

        Assert.Equal(QbittorrentHookResult.InvalidHash, result);
        Assert.False(waitTask.IsCompleted, "hash 非法时不得触发对账脉冲。");
    }

    [Fact]
    public async Task RegenerateToken_OldTokenStopsWorking()
    {
        var (service, _, settings) = CreateService();
        var oldToken = await settings.GenerateHookTokenAsync();
        var newToken = await settings.GenerateHookTokenAsync();
        Assert.NotEqual(oldToken, newToken);

        Assert.Equal(QbittorrentHookResult.Rejected, await service.HandleAsync(oldToken, InfoHash, "completed"));
        Assert.Equal(QbittorrentHookResult.Accepted, await service.HandleAsync(newToken, InfoHash, "completed"));
    }

    [Fact]
    public async Task Handle_WithMissingHash_AcceptsAndPulses()
    {
        var (service, pulse, settings) = CreateService();
        var token = await settings.GenerateHookTokenAsync();
        var waitTask = pulse.WaitAsync(CancellationToken.None);

        Assert.Equal(QbittorrentHookResult.Accepted, await service.HandleAsync(token, null, "completed"));
        Assert.True(waitTask.IsCompleted);
    }

    private const string InfoHash = "eb7d7d0cfdc65f4d8dfcb1723f640cac05d060e5";

    private static (QbittorrentHookService Service, ReconcilePulse Pulse, ManageQbittorrentSettings Settings)
        CreateService()
    {
        var store    = new MemoryApplicationSettingStore();
        var settings = new ManageQbittorrentSettings(store, new StubQbittorrentSessionFactory());
        var pulse    = new ReconcilePulse();
        var service  = new QbittorrentHookService(settings, pulse, NullLogger<QbittorrentHookService>.Instance);
        return (service, pulse, settings);
    }

    private sealed class StubQbittorrentSessionFactory : IQbittorrentSessionFactory
    {
        public Task<IQbittorrentSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("钩子测试不应创建下载器会话。");
        }

        public Task<IQbittorrentSession> CreateAsync(QbittorrentSettings settings, string? password,
                                                     CancellationToken   cancellationToken = default)
        {
            throw new InvalidOperationException("钩子测试不应创建下载器会话。");
        }
    }
}
