using MediaManager.Features.Settings;

namespace MediaManager.Tests.Settings;

public sealed class ManageGeneralSettingsTests
{
    [Fact]
    public async Task GetAsync_ReturnsDefaultNameWhenSettingIsMissing()
    {
        var service = new ManageGeneralSettings(new InMemorySettingStore());

        var settings = await service.GetAsync();

        Assert.Equal(GeneralSettings.DefaultInstanceName, settings.InstanceName);
    }

    [Fact]
    public async Task SaveAsync_TrimsAndPersistsInstanceName()
    {
        var store   = new InMemorySettingStore();
        var service = new ManageGeneralSettings(store);

        await service.SaveAsync(new GeneralSettings("  家庭媒体库  "));

        Assert.Equal("家庭媒体库", (await service.GetAsync()).InstanceName);
    }

    private sealed class InMemorySettingStore : IApplicationSettingStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetProtectedAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return SetAsync(key, value, cancellationToken);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
