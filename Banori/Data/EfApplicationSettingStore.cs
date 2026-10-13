using Banori.Features.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Banori.Data;

public sealed class EfApplicationSettingStore(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IDataProtectionProvider                 dataProtectionProvider,
    TimeProvider                            timeProvider) : IApplicationSettingStore
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("Banori.ApplicationSettings.v1");

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await dbContext.ApplicationSettings
                                     .AsNoTracking()
                                     .SingleOrDefaultAsync(item => item.Key == key, cancellationToken);

        if (setting is null) return null;

        return setting.IsProtected ? _protector.Unprotect(setting.Value) : setting.Value;
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        SetCoreAsync(key, value, false, cancellationToken);

    public Task SetProtectedAsync(string key, string value, CancellationToken cancellationToken = default) =>
        SetCoreAsync(key, value, true, cancellationToken);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting =
            await dbContext.ApplicationSettings.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (setting is null) return;

        dbContext.ApplicationSettings.Remove(setting);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SetCoreAsync(string key, string value, bool isSensitive, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await dbContext.ApplicationSettings.SingleOrDefaultAsync(item => item.Key == key,
                                                                               cancellationToken);
        var storedValue = isSensitive ? _protector.Protect(value) : value;

        if (setting is null)
        {
            setting = new ApplicationSetting { Key = key };
            dbContext.ApplicationSettings.Add(setting);
        }

        setting.Value        = storedValue;
        setting.IsProtected  = isSensitive;
        setting.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > ApplicationSetting.KeyMaxLength)
            throw new ArgumentException($"设置键不能超过 {ApplicationSetting.KeyMaxLength} 个字符。", nameof(key));
    }
}
