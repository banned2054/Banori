using System.Runtime.InteropServices;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Banori.E2E.Tests;

/// <summary>
///     冒烟：E2E_SMOKE=1 才执行（dotnet test --filter 或 runbook 的环境变量机制）。
///     链路：Docker qB 4.6.4 容器（外部由 qb_container.py 启动）+ AcceptanceHost 真机 host
///     + HTTP 登录 + qB 设置写入 + 干跑上传（真实 piece 哈希 torrent，announce=tracker.byr.pt）
///     到「qB 中出现该种子(stopped)」即通过。随后完整重置（host 停止 + DB 删除）。
/// </summary>
public sealed class SeedPipelineSmokeTests : IAsyncLifetime
{
    private static string? _smokeRoot;

    private AcceptanceHost? _host;
    private QbApiClient?    _qb;

    public static bool SmokeEnabled =>
        Environment.GetEnvironmentVariable("E2E_SMOKE") == "1";

    /// <summary>惰性求值：只有测试实际执行时才访问文件系统。</summary>
    private static string SmokeRoot => _smokeRoot ??= FindSmokeRoot();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // 干跑后完整重置：停 host（含对账 worker）释放 SQLite 连接并删库；qB 容器留给外部清理。
        if (_host is not null)
        {
            await _host.ResetAsync();
            await _host.DisposeAsync();
        }
        _qb?.Dispose();
    }

    [SkippableFact]
    public async Task 搭建环境_冒烟_上传种子到qB可见()
    {
        Skip.IfNot(SmokeEnabled && RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // 前置：外部 qb_container.py up 已起 4.6.4 容器（E2E_QB_* 环境变量指向它）。
        _qb = new QbApiClient(
            Environment.GetEnvironmentVariable("E2E_QB_BASE_URL") ?? "http://127.0.0.1:18081",
            Environment.GetEnvironmentVariable("E2E_QB_USERNAME") ?? "admin",
            Environment.GetEnvironmentVariable("E2E_QB_PASSWORD") ?? throw new InvalidOperationException(
                "需要 E2E_QB_PASSWORD（qb_container.py up 传入的密码）。"));
        await _qb.LoginAsync();
        var qbVersion = await _qb.GetVersionAsync();

        _host = new AcceptanceHost(SmokeRoot);
        _host.Initialize();
        Assert.True(string.IsNullOrEmpty(_host.EnvironmentFallback), $"环境回退：{_host.EnvironmentFallback}");

        // 真机确认：三个 BackgroundService 实际运行（DownloadReconcileWorker 等）。
        var hosted = _host.ListHostedServices();
        Assert.Contains(hosted, service => service.Contains("DownloadReconcileWorker"));

        var evidence = new
        {
            qb_version        = qbVersion,
            hosted_services   = hosted,
            environment       = "Production",
            subject_binding   = "title-mapping",
            started_utc       = DateTime.UtcNow
        };
        await LoginDriver.WriteJsonAsync(Path.Combine(SmokeRoot, "smoke-summary.json"), evidence);

        // HTTP 登录（静态 SSR 表单 + 防伪令牌）。
        var login = new LoginDriver(_host);
        Assert.True(await login.LoginAsync(), $"登录失败，HTTP {login.LoginLastStatus}（限流 5/min，注意重试间隔）。");

        // qB 设置写入（设置页同一入口）；SavePath 必须是容器内视角 /downloads。
        await _host.WriteQbSettingsAsync();

        // 预种 Subject 与标题映射（离线环境不调 bgm.tv）。
        const string title   = "泣く大人は嫌い";
        const string nameCn  = "讨厌哭泣的大人";
        await _host.SeedSubjectAsync(114514, title, nameCn);

        // 真实 torrent：文件名可被 AniParser 解析出标题（标题映射命中）。
        var contentDir = Path.Combine(SmokeRoot, "content");
        if (Directory.Exists(contentDir)) Directory.Delete(contentDir, true);
        var torrentPath = Path.Combine(SmokeRoot, "smoke.torrent");
        if (File.Exists(torrentPath)) File.Delete(torrentPath);
        var manifest = await ScenarioPrep.MakeTorrentAsync(
            contentDir, torrentPath, "https://tracker.byr.pt/announce.php", title,
            new Dictionary<string, byte[]>
            {
                ["【喵萌奶茶屋】[泣く大人は嫌い][01][1080p][简日双语].mkv"] =
                    MakeMkv(1_400_000)
            });
        await LoginDriver.WriteJsonAsync(Path.Combine(SmokeRoot, "torrent-manifest.json"), manifest);

        // 干跑上传：到 Submitting + qB 出现该种子（stopped 添加）即通过。
        var driver    = new UploadDriver(_host);
        var evidencePath = Path.Combine(SmokeRoot, "evidence.jsonl");
        if (File.Exists(evidencePath)) File.Delete(evidencePath);
        var dryRun = await driver.DryRunAsync(await File.ReadAllBytesAsync(torrentPath), _qb, evidencePath);

        Assert.NotEmpty(dryRun.Targets);
        var task = await new EvidenceObserver(_host, _qb, Path.Combine(SmokeRoot, "final.jsonl"))
                   .LoadTaskAsync();
        Assert.NotNull(task);
        Assert.Equal(manifest.InfoHash, task!.InfoHash);
        Assert.Equal("byrbt", task.SiteSource); // TorrentSiteSource 按 announce 主机+路径判定
        Assert.Equal(114514, task.SubjectId);

        await LoginDriver.WriteJsonAsync(Path.Combine(SmokeRoot, "dry-run.json"), new
        {
            dry_run           = true,
            task_id           = dryRun.TaskId,
            archive_targets   = dryRun.Targets.Select(target => new { target.FileIndex, target.TargetPath }),
            naming_snapshot   = task.NamingSnapshot,
            save_path         = task.SavePath,
            media_directory   = task.MediaDirectory,
            seed_directory    = task.SeedDirectory
        });
        // 完整重置在 DisposeAsync（干跑后重置）。
    }

    private static byte[] MakeMkv(int size)
    {
        // 伪视频数据：确定性内容（种子 info_hash 可复现），piece SHA1 是真实的。
        var data = new byte[size];
        var seed = 0x5A5A_5A5A;
        for (var index = 0; index < data.Length; index++)
        {
            seed = seed * 1_664_525 + 1_013_904_223 & 0x7FFF_FFFF;
            data[index] = (byte)(seed >> 24);
        }
        return data;
    }

    private static string FindSmokeRoot()
    {
        var smokeRoot = Path.Combine(ScenarioSupport.FindE2eRoot(), "smoke");
        Directory.CreateDirectory(smokeRoot);
        return smokeRoot;
    }
}
