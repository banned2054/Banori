using MediaManager.Features.Notifications;
using MediaManager.Features.Settings;
using MediaManager.Integrations.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.Tests.Notifications;

/// <summary>假 Telegram 发送器：记录调用供断言，可注入异常模拟发送失败。</summary>
public sealed class FakeTelegramSender : ITelegramSender
{
    public List<(string BotToken, string ChatId, string? ProxyUrl, string Text)> Calls { get; } = [];

    /// <summary>设置后每次发送抛出该异常，模拟渠道不可用。</summary>
    public Exception? SendException { get; set; }

    public int CallCount => Calls.Count;

    public Task SendAsync(string            botToken, string chatId, string? proxyUrl, string text,
                          CancellationToken cancellationToken = default)
    {
        if (SendException is not null) throw SendException;
        Calls.Add((botToken, chatId, proxyUrl, text));
        return Task.CompletedTask;
    }
}

/// <summary>假 Gotify 发送器：记录调用供断言，可注入异常模拟发送失败。</summary>
public sealed class FakeGotifySender : IGotifySender
{
    public List<(string BaseUrl, string AppToken, string Title, string Message)> Calls { get; } = [];

    /// <summary>设置后每次发送抛出该异常，模拟渠道不可用。</summary>
    public Exception? SendException { get; set; }

    public int CallCount => Calls.Count;

    public Task SendAsync(string            baseUrl, string appToken, string title, string message,
                          CancellationToken cancellationToken = default)
    {
        if (SendException is not null) throw SendException;
        Calls.Add((baseUrl, appToken, title, message));
        return Task.CompletedTask;
    }
}

/// <summary>通知渠道测试的 DI 注册：替身发送器 + 渠道设置管理 + 入队与投递服务。</summary>
public static class NotificationTestRegistrations
{
    public static IServiceCollection AddNotificationTestFakes(this IServiceCollection services)
    {
        services.AddSingleton<FakeTelegramSender>();
        services.AddSingleton<FakeGotifySender>();
        services.AddSingleton<ITelegramSender>(provider => provider.GetRequiredService<FakeTelegramSender>());
        services.AddSingleton<IGotifySender>(provider => provider.GetRequiredService<FakeGotifySender>());
        services.AddScoped<ManageTelegramSettings>();
        services.AddScoped<ManageGotifySettings>();
        services.AddScoped<NotificationEnqueuer>();
        services.AddScoped<NotificationDeliveryService>();
        return services;
    }
}
