using System.Text;
using System.Text.Json;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Banori.E2E.Tests;

/// <summary>
///     元数据恢复验收场景：旧下载任务缺失/无效 TorrentFile 原始种子元数据时，Banori 经 qB
///     /torrents/export 导出并核验补存（RecoverTorrentFileAsync；端点自 qB 4.5.0 可用，4.6.4 实测）。
///     流程：Banori 正式链路（上传登记 → worker 停止态添加 → 数据在位 + qB API resume 自动校验
///     到做种态 → 归档链执行自己的命名改名）构造「已保存文件记录 + 改名后路径」的旧任务 →
///     正式链路提交一次恢复前手工编辑（人工目标路径，构成「恢复前人工值」）→ 宿主 SQLite 直接
///     UPDATE DownloadTasks SET TorrentFile = X'' 模拟旧数据元数据缺失 → 正式服务链路
///     PreviewAsync（补存、恢复前人工值原样保留、由存量 override 推导目标、无编辑草稿落库、
///     qB 零写操作）→ ApplyAsync 提交与存量相同的手工值（目标即当前路径，无远端改名，
///     OriginalRelativePath/任务 Id/文件 Id/人工覆盖/逐集登记不变）。
///     gate：E2E_META=1；证据落盘 .e2e/meta/；容器 banori-accept-qb-meta（18087/16887）。
/// </summary>
public sealed class MetadataRecoveryScenarioTests
{
    private const string ContainerName  = "banori-accept-qb-meta";
    private const int    WebUiPort      = 18087;
    private const int    BtPort         = 16887;
    private const string QbPassword     = "e2e-meta-password";
    private const string GateVariable   = "E2E_META";
    private const string ManualOverride = "Manual/Kept E01.mkv";
    private const string ManualRelative = "Media/Anime/Manual/Kept E01.mkv";
    private const string ManualAbsolute = "/downloads/Media/Anime/Manual/Kept E01.mkv";

    [SkippableFact]
    public async Task 元数据恢复_旧任务缺失种子元数据经下载器导出补存并应用手工路径()
    {
        Skip.IfNot(ScenarioSupport.Enabled(GateVariable),
                   $"设置 {GateVariable}=1 且在 Windows 上才执行（另需 Docker；.e2e/meta 目录自动创建）");
        await ScenarioSupport.SerialGate.WaitAsync();
        try
        {
            await RunAsync();
        }
        finally
        {
            ScenarioSupport.SerialGate.Release();
        }
    }

    private async Task RunAsync()
    {
        var sceneRoot      = ScenarioSupport.FindSceneRoot("meta");
        var containerState = Path.Combine(sceneRoot, "qb-state.json");
        var evidencePath   = Path.Combine(sceneRoot, "evidence.jsonl");
        await ScenarioSupport.TryDownAsync(containerState);
        ScenarioSupport.ClearSceneDirectory(sceneRoot);

        await ScenarioSupport.RunToolAsync("qb_container.py",
                                           $"up --name {ContainerName} --scene-dir \"{sceneRoot}\" " +
                                           $"--webui-port {WebUiPort} --bt-port {BtPort} " +
                                           $"--username admin --password {QbPassword} --state \"{containerState}\"");
        // 进程级环境变量覆盖必须可恢复：SeedPipelineSmokeTests 读取同一组变量但不经过
        // SerialGate（xUnit 可能并行测试类），同会话混跑时不得把冒烟指向本场景的 qB 实例。
        // 原值可能不存在（null），恢复时 SetEnvironmentVariable(name, null) 即删除。
        var qbEnvironment = (BaseUrl:  Environment.GetEnvironmentVariable("E2E_QB_BASE_URL"),
                             Username: Environment.GetEnvironmentVariable("E2E_QB_USERNAME"),
                             Password: Environment.GetEnvironmentVariable("E2E_QB_PASSWORD"));
        Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", $"http://127.0.0.1:{WebUiPort}");
        Environment.SetEnvironmentVariable("E2E_QB_USERNAME", "admin");
        Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", QbPassword);

        var qb  = new QbApiClient($"http://127.0.0.1:{WebUiPort}", "admin", QbPassword);
        AcceptanceHost? host = null;
        try
        {
            await qb.LoginAsync();
            var qbVersion = await qb.GetVersionAsync();

            host = new AcceptanceHost(sceneRoot);
            host.Initialize();
            Assert.True(string.IsNullOrEmpty(host.EnvironmentFallback), $"环境回退：{host.EnvironmentFallback}");
            Assert.Contains(host.ListHostedServices(), service => service.Contains("DownloadReconcileWorker"));

            await host.SeedSubjectAsync(CrossSeedScenario.SubjectId, CrossSeedScenario.SubjectTitle,
                                        CrossSeedScenario.SubjectNameCn);
            await host.WriteQbSettingsAsync();

            // [准备] 单文件种子（真实 piece SHA1、标准 v1：info.name = 内容文件名）。
            var bytes      = CrossSeedScenario.MakeMkv(800_000);
            var contentDir = Path.Combine(sceneRoot, "content-meta");
            var manifest = await ScenarioPrep.MakeTorrentAsync(
                contentDir, Path.Combine(sceneRoot, "torrent-meta.torrent"), CrossSeedScenario.ByrbtAnnounce,
                "【喵萌奶茶屋】[泣く大人は嫌い][01][1080p][简日双语]",
                new Dictionary<string, byte[]> { [CrossSeedScenario.OriginalNameV1] = bytes });
            await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "torrent-manifest.json"), manifest);

            // [准备] 单文件数据平铺在 /downloads 根（4.6.4 实测：<savePath>/<info.name>），先于上传
            // 落位：worker 停止态添加时数据已在容器卷内，resume 即自动校验到做种态（qb-notes 实测）。
            Directory.CreateDirectory(host.DownloadRoot);
            File.Copy(Path.Combine(contentDir, CrossSeedScenario.OriginalNameV1),
                      Path.Combine(host.DownloadRoot, CrossSeedScenario.OriginalNameV1));

            // [被测] Banori 正式链路：上传登记（标题映射命中 Subject）→ worker 停止态添加 →
            // qB API resume 完成下载 → worker 归档链执行自己的命名改名——构成
            // 「已保存文件记录 + 改名后路径」的旧任务。
            var driver = new UploadDriver(host);
            var taskId = await driver.UploadAsync(await File.ReadAllBytesAsync(manifest.TorrentPath),
                                                  CrossSeedScenario.SubjectId);
            await WaitTorrentVisibleAsync(qb, manifest.InfoHash);
            await qb.ResumeAsync(manifest.InfoHash);
            await WaitContentCompleteAsync(qb, manifest.InfoHash);

            await new EvidenceObserver(host, qb, evidencePath).ObserveUntilAsync(
                (task, torrents) => ScenarioSupport.TaskReachedFinalState(task, torrents, manifest.InfoHash),
                TimeSpan.FromMinutes(8));
            // FinalRoot 正常完成链不写做种恢复记录（RunRecovery 只由复用校验路径写入），
            // 这里只等 Completed 终态，不使用 WaitForRunConfirmedAsync。
            var task = await WaitTaskCompletedAsync(host, manifest.InfoHash);
            Assert.True(task is not null, "任务不在 Banori 数据库中。");
            Assert.Equal(taskId, task!.Id);
            Assert.Equal(DownloadTaskStatus.Completed, task.Status);
            Assert.Equal(CrossSeedScenario.SubjectId, task.SubjectId);

            var fileRow        = Assert.Single(task.Files);
            var fileId         = fileRow.Id;
            var originalPath   = fileRow.OriginalRelativePath;
            Assert.Equal(CrossSeedScenario.OriginalNameV1, originalPath);
            Assert.Equal(CrossSeedScenario.TargetRelative, fileRow.CurrentRelativePath);
            var torrentBefore  = await qb.FindTorrentAsync(manifest.InfoHash) ??
                                 throw new InvalidOperationException("完成后种子不在 qB 中。");
            Assert.Equal("/downloads", torrentBefore.SavePath);
            Assert.True(CrossSeedScenario.SeedingStates.Contains(torrentBefore.State),
                        $"完成后状态 {torrentBefore.State} 不是做种类。");
            var filesBefore    = await qb.GetTorrentFilesAsync(manifest.InfoHash);
            Assert.Single(filesBefore);
            Assert.Equal(CrossSeedScenario.TargetRelative, filesBefore[0].Name);
            var hostFile = Path.Combine(host.DownloadRoot, CrossSeedScenario.TargetRelative);
            Assert.Equal(manifest.Files[0].Sha256, CrossSeedScenario.Sha256Of(hostFile));

            // [被测] 恢复前的人工编辑：在抹元数据之前经正式链路提交手工目标路径，构成
            // 「恢复前人工值」。qB 4.6.4 的 renameFile 写后读存在可见延迟：首轮 ApplyAsync
            // 观察到旧路径时按既有规则推迟发布（待执行日志保留、远端改名已生效），
            // 改名可见后再次 Apply 即确认并发布——按最终一致断言。
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                await fileService.ApplyAsync(task.Id, 0,
                                             new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                 AnimeRelativePath : ManualOverride));
            }

            await WaitQbFileAsync(qb, manifest.InfoHash, ManualRelative, TimeSpan.FromSeconds(60));
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                var view = await fileService.ApplyAsync(task.Id, 0,
                                                        new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                            AnimeRelativePath : ManualOverride));
                Assert.Equal(ManualRelative, view.Files.Single().CurrentPath);
            }

            var edited = await ScenarioSupport.LoadTaskByHashAsync(host, manifest.InfoHash);
            Assert.True(edited is not null, "恢复前手工编辑后任务不可读。");
            Assert.Equal(DownloadTaskStatus.Completed, edited!.Status);
            var editedFile = Assert.Single(edited.Files);
            Assert.Equal(fileId, editedFile.Id);
            Assert.Equal(originalPath, editedFile.OriginalRelativePath);
            Assert.Equal(ManualRelative, editedFile.CurrentRelativePath);
            Assert.True(editedFile.IsManualOverride);
            Assert.Equal(ManualOverride, editedFile.AnimeRelativePathOverride);
            var filesAfterManualEdit = await qb.GetTorrentFilesAsync(manifest.InfoHash);
            Assert.Single(filesAfterManualEdit);
            Assert.Equal(ManualRelative, filesAfterManualEdit[0].Name);

            // [模拟旧数据] 直接对宿主 SQLite 清空原始元数据（TorrentFile BLOB 列）；经 host 自身
            // 连接池写入规避跨池 WAL 可见性问题，写后读回验证。
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = await scope.ServiceProvider
                                   .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                                   .CreateDbContextAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE DownloadTasks SET TorrentFile = X'' WHERE Id = {0}", task.Id);
            }

            var wiped = await ScenarioSupport.LoadTaskByHashAsync(host, manifest.InfoHash);
            Assert.True(wiped is not null, "清空元数据后任务不可读。");
            Assert.Empty(wiped!.TorrentFile);

            // [被测] 正式服务链路 PreviewAsync：元数据经下载器导出补存；恢复前人工值由存量
            // override 推导目标（同一请求值），编辑草稿不落库、qB 零写操作。
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                var preview = await fileService.PreviewAsync(task.Id, 0,
                                                             new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                                 AnimeRelativePath : ManualOverride));
                Assert.Equal(ManualAbsolute, preview.TargetPath);
                Assert.Equal(ManualAbsolute, preview.CurrentPath);
            }

            var recovered = await ScenarioSupport.LoadTaskByHashAsync(host, manifest.InfoHash);
            Assert.True(recovered is not null, "预览后任务不可读。");
            AssertRestoredMetadata(recovered!.TorrentFile, manifest.InfoHash);
            var recoveredFile = Assert.Single(recovered.Files);
            Assert.Equal(fileId, recoveredFile.Id);
            Assert.Equal(originalPath, recoveredFile.OriginalRelativePath);
            Assert.Equal(ManualRelative, recoveredFile.CurrentRelativePath);
            Assert.True(recoveredFile.IsManualOverride);
            Assert.Equal(ManualOverride, recoveredFile.AnimeRelativePathOverride);
            Assert.Null(recoveredFile.EpisodeNumberOverride);
            var torrentAfterPreview = await qb.FindTorrentAsync(manifest.InfoHash) ??
                                      throw new InvalidOperationException("预览后种子不在 qB 中。");
            Assert.Equal(torrentBefore.SavePath, torrentAfterPreview.SavePath);
            Assert.Equal(torrentBefore.State, torrentAfterPreview.State);
            var filesAfterPreview = await qb.GetTorrentFilesAsync(manifest.InfoHash);
            Assert.Equal(ManualRelative, filesAfterPreview[0].Name);

            // [被测] ApplyAsync 提交与存量相同的手工值：目标即当前路径，不产生远端改名，
            // 补存后的既有编辑链照常确认并发布。
            var taskCountBefore   = await CountTasksAsync(host);
            var episodeRowsBefore = (await ScenarioSupport.LoadEpisodeVersionsAsync(host,
                                                                                       CrossSeedScenario.SubjectId))
                                   .Count;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                var view = await fileService.ApplyAsync(task.Id, 0,
                                                        new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                            AnimeRelativePath : ManualOverride));
                Assert.Equal(ManualRelative, view.Files.Single().CurrentPath);
            }

            var applied = await ScenarioSupport.LoadTaskByHashAsync(host, manifest.InfoHash);
            Assert.True(applied is not null, "应用后任务不可读。");
            Assert.Equal(taskId, applied!.Id);
            Assert.Equal(DownloadTaskStatus.Completed, applied.Status);
            Assert.Equal(taskCountBefore, await CountTasksAsync(host));
            AssertRestoredMetadata(applied.TorrentFile, manifest.InfoHash);
            var appliedFile = Assert.Single(applied.Files);
            Assert.Equal(fileId, appliedFile.Id);
            Assert.Equal(originalPath, appliedFile.OriginalRelativePath);
            Assert.Equal(ManualRelative, appliedFile.CurrentRelativePath);
            Assert.True(appliedFile.IsManualOverride);
            Assert.Equal(ManualOverride, appliedFile.AnimeRelativePathOverride);

            // qB 实际文件路径与容器内文件内容核对（volume mount 宿主侧）。
            var torrentAfterApply = await qb.FindTorrentAsync(manifest.InfoHash) ??
                                    throw new InvalidOperationException("应用后种子不在 qB 中。");
            Assert.Equal("/downloads", torrentAfterApply.SavePath);
            var filesAfterApply = await qb.GetTorrentFilesAsync(manifest.InfoHash);
            Assert.Single(filesAfterApply);
            Assert.Equal(ManualRelative, filesAfterApply[0].Name);
            Assert.False(File.Exists(hostFile), "改名后旧路径不应仍存在。");
            var manualHostFile = Path.Combine(host.DownloadRoot, "Media", "Anime", "Manual", "Kept E01.mkv");
            Assert.Equal(manifest.Files[0].Sha256, CrossSeedScenario.Sha256Of(manualHostFile));

            // 未新建任务、未走同集择优：DownloadTasks 行数与 EpisodeVersions 行数不变、现任不变。
            var episodeRowsAfter = (await ScenarioSupport.LoadEpisodeVersionsAsync(host,
                                                                                      CrossSeedScenario.SubjectId))
                                  .Count;
            Assert.Equal(episodeRowsBefore, episodeRowsAfter);
            var episodeRow = (await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId))
                            .FirstOrDefault(value => value.Episode == 1m);
            Assert.True(episodeRow is not null, "EpisodeVersions 没有第 1 集行。");
            Assert.Equal(fileId, episodeRow!.IncumbentVideoFileId);

            // [被测] 恢复后的实际改名链路：提交不同新路径（目标 != 当前）。qB 4.6.4 的 renameFile
            // 写后读存在可见延迟：沿用两阶段模式（首轮 ApplyAsync 等 qB 可见，再次调用确认发布）。
            const string renamedOverride = "Manual/Renamed E02.mkv";
            const string renamedRelative = "Media/Anime/Manual/Renamed E02.mkv";
            const string renamedAbsolute = "/downloads/Media/Anime/Manual/Renamed E02.mkv";
            var taskCountBeforeRename   = await CountTasksAsync(host);
            var episodeRowsBeforeRename = (await ScenarioSupport.LoadEpisodeVersionsAsync(host,
                                                                                            CrossSeedScenario
                                                                                               .SubjectId))
                                          .Count;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                var preview = await fileService.PreviewAsync(task.Id, 0,
                                                             new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                                 AnimeRelativePath : renamedOverride));
                Assert.Equal(renamedAbsolute, preview.TargetPath);
                Assert.Equal(ManualAbsolute, preview.CurrentPath);
            }

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                await fileService.ApplyAsync(task.Id, 0,
                                             new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                 AnimeRelativePath : renamedOverride));
            }

            await WaitQbFileAsync(qb, manifest.InfoHash, renamedRelative, TimeSpan.FromSeconds(60));
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var fileService = scope.ServiceProvider.GetRequiredService<DownloadFileService>();
                var view = await fileService.ApplyAsync(task.Id, 0,
                                                        new DownloadFileEditRequest(DownloadFileKind.Anime,
                                                            AnimeRelativePath : renamedOverride));
                Assert.Equal(renamedRelative, view.Files.Single().CurrentPath);
            }

            var renamed = await ScenarioSupport.LoadTaskByHashAsync(host, manifest.InfoHash);
            Assert.True(renamed is not null, "恢复后改名后任务不可读。");
            Assert.Equal(taskId, renamed!.Id);
            Assert.Equal(DownloadTaskStatus.Completed, renamed.Status);
            Assert.Equal(taskCountBeforeRename, await CountTasksAsync(host));
            AssertRestoredMetadata(renamed.TorrentFile, manifest.InfoHash);
            var renamedFile = Assert.Single(renamed.Files);
            Assert.Equal(fileId, renamedFile.Id);
            Assert.Equal(originalPath, renamedFile.OriginalRelativePath);
            Assert.Equal(renamedRelative, renamedFile.CurrentRelativePath);
            Assert.Equal(renamedOverride, renamedFile.AnimeRelativePathOverride);
            Assert.True(renamedFile.IsManualOverride);

            // qB 实际文件路径与容器内文件内容核对（volume mount 宿主侧）；旧手工路径不残留。
            var torrentAfterRename = await qb.FindTorrentAsync(manifest.InfoHash) ??
                                     throw new InvalidOperationException("恢复后改名后种子不在 qB 中。");
            Assert.Equal("/downloads", torrentAfterRename.SavePath);
            var filesAfterRename = await qb.GetTorrentFilesAsync(manifest.InfoHash);
            Assert.Single(filesAfterRename);
            Assert.Equal(renamedRelative, filesAfterRename[0].Name);
            Assert.DoesNotContain(filesAfterRename, value => value.Name == ManualRelative);
            Assert.False(File.Exists(Path.Combine(host.DownloadRoot, "Media", "Anime", "Manual", "Kept E01.mkv")),
                         "恢复后改名后旧手工路径不应仍存在。");
            var renamedHostFile = Path.Combine(host.DownloadRoot, "Media", "Anime", "Manual", "Renamed E02.mkv");
            Assert.Equal(manifest.Files[0].Sha256, CrossSeedScenario.Sha256Of(renamedHostFile));

            var episodeRowsAfterRename =
                (await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId)).Count;
            Assert.Equal(episodeRowsBeforeRename, episodeRowsAfterRename);
            // 集数登记证据用恢复后改名后的新鲜读取：改名与恢复没有切换现任。
            var renamedEpisodeRow =
                (await ScenarioSupport.LoadEpisodeVersionsAsync(host, CrossSeedScenario.SubjectId))
               .FirstOrDefault(value => value.Episode == 1m);
            Assert.True(renamedEpisodeRow is not null, "EpisodeVersions 没有第 1 集行。");
            Assert.Equal(fileId, renamedEpisodeRow!.IncumbentVideoFileId);

            // 补存后再次操作不重复导出仅作间接验证：全部链路结束后 DB 仍有完整元数据。
            await WriteEvidenceAsync(sceneRoot, qbVersion, task, renamed, torrentAfterRename, filesAfterRename,
                                     renamedHostFile, renamedEpisodeRow, originalPath, fileId,
                                     filesAfterManualEdit[0].Name!, renamedRelative, manifest);
        }
        finally
        {
            // 环境变量最先恢复：后续清理步骤的异常（收窄后只吞取消）不应阻断恢复。
            Environment.SetEnvironmentVariable("E2E_QB_BASE_URL", qbEnvironment.BaseUrl);
            Environment.SetEnvironmentVariable("E2E_QB_USERNAME", qbEnvironment.Username);
            Environment.SetEnvironmentVariable("E2E_QB_PASSWORD", qbEnvironment.Password);
            // 停机尽力而为：NotificationDeliveryWorker 的 PeriodicTimer 循环不捕获
            // OperationCanceledException（Workers/NotificationDeliveryWorker.cs:31，已另行记录），
            // .NET 11 的 Host 会把它从 StopAsync/DisposeAsync 重新抛出——只吞取消异常，
            // 真实清理失败（SQLite 文件锁、DI 释放错误等）向上抛出以便观察。
            try
            {
                if (host is not null) await host.ResetAsync();
            }
            catch (OperationCanceledException)
            {
            }
            try
            {
                if (host is not null) await host.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
            qb.Dispose();
            await ScenarioSupport.TryDownAsync(containerState);
        }
    }

    /// <summary>补存后的元数据断言：字节非空、可解析、v1 info-hash 与任务一致（qB 导出字节可能重编码，不做逐字节比较）。</summary>
    private static void AssertRestoredMetadata(byte[] torrentFile, string expectedInfoHash)
    {
        Assert.NotEmpty(torrentFile);
        Assert.True(TorrentDownloadPolicy.TryLoad(torrentFile, out var parsed, out _),
                    "补存的 TorrentFile 原始元数据无法解析。");
        Assert.Equal(expectedInfoHash, parsed.InfoHashes.V1!.ToHex().ToLowerInvariant());
    }

    /// <summary>轮询直到任务终态为 Completed（FinalRoot 正常完成链没有 RunRecovery 可观察）。</summary>
    private static async Task<DownloadTask?> WaitTaskCompletedAsync(AcceptanceHost host, string infoHash)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        DownloadTask? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ScenarioSupport.LoadTaskByHashAsync(host, infoHash);
            if (last is { Status: DownloadTaskStatus.Completed }) return last;
            await Task.Delay(1000);
        }
        throw new TimeoutException(
            $"等待任务完成超时（60s），最后状态：{last?.Status}，FailedStep={last?.FailedStep}，Error={last?.Error}。");
    }

    /// <summary>等待种子在 qB 可见（worker 停止态添加完成），元数据原始名不受此处时机影响。</summary>
    private static async Task WaitTorrentVisibleAsync(QbApiClient qb, string infoHash)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await qb.FindTorrentAsync(infoHash) is not null) return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"等待种子 {infoHash[..8]} 在 qB 可见超时（90s）。");
    }

    /// <summary>等待数据校验完成（4.6.4 实测：停止态添加已有数据 resume 自动校验到做种态）。</summary>
    private static async Task WaitContentCompleteAsync(QbApiClient qb, string infoHash)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var torrent = await qb.FindTorrentAsync(infoHash);
            if (torrent is { Progress: 1.0 } && torrent.AmountLeft == 0) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"等待种子 {infoHash[..8]} 数据校验完成（progress=1）超时（120s）。");
    }

    /// <summary>轮询直到 qB 文件清单显示期望路径（4.6.4 实测 renameFile 的读结果滞后于写请求）。</summary>
    private static async Task WaitQbFileAsync(QbApiClient qb, string infoHash, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var files = await qb.GetTorrentFilesAsync(infoHash);
            last = files.Count > 0 ? files[0].Name : null;
            if (string.Equals(last, expected, StringComparison.Ordinal)) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException(
            $"等待 qB 文件改名到 {expected} 超时（{timeout.TotalSeconds:F0}s），最后路径：{last ?? "<无>"}。");
    }

    private static async Task<int> CountTasksAsync(AcceptanceHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                             .CreateDbContextAsync();
        return await db.DownloadTasks.AsNoTracking().CountAsync();
    }

    /// <summary>终态快照 final.jsonl（单行）与 summary.json。</summary>
    private static async Task WriteEvidenceAsync(
        string sceneRoot, string qbVersion, DownloadTask taskAfterChain, DownloadTask taskAfterApply,
        QbTorrentInfo torrentAfterApply, IReadOnlyList<QbTorrentFile> filesAfterApply,
        string manualHostFile, EpisodeVersion episodeRow, string originalPath, Guid fileId,
        string preRecoveryQbFile, string postRecoveryRenameTo, ScenarioPrep.TorrentManifest manifest)
    {
        var final = new
        {
            timestamp_utc = DateTime.UtcNow,
            task = new
            {
                taskAfterApply.Id, taskAfterApply.InfoHash, taskAfterApply.Status, taskAfterApply.SubjectId,
                taskAfterApply.SiteSource, taskAfterApply.SavePath, taskAfterApply.MediaDirectory,
                taskAfterApply.PathDecision, taskAfterApply.ArchiveReview, taskAfterApply.Progress,
                taskAfterApply.FailedStep, taskAfterApply.Error,
                torrent_file_length = taskAfterApply.TorrentFile.Length,
                files = taskAfterApply.Files.Select(value => new
                {
                    value.Id, value.FileIndex, value.OriginalRelativePath, value.CurrentRelativePath,
                    value.Kind, value.IsManualOverride, value.EpisodeNumberOverride,
                    value.AnimeRelativePathOverride, value.Size
                })
            },
            task_after_chain = new
            {
                taskAfterChain.Id,
                current_relative_path = taskAfterChain.Files.Single().CurrentRelativePath
            },
            qb_torrent = torrentAfterApply,
            qb_files   = filesAfterApply,
            host_files = new Dictionary<string, string>
                         { [manualHostFile] = CrossSeedScenario.Sha256Of(manualHostFile) }
        };
        await File.WriteAllTextAsync(Path.Combine(sceneRoot, "final.jsonl"),
                                     JsonSerializer.Serialize(final, ScenarioSupport.JsonOptions) + "\n",
                                     Encoding.UTF8);

        var summary = new
        {
            scenario   = "meta-metadata-recovery",
            gate       = "E2E_META",
            qb_version = qbVersion,
            result     = "passed",
            info_hash  = manifest.InfoHash,
            torrent    = new { manifest.Name, manifest.TotalSize, manifest.PieceLength },
            metadata_recovery = new
            {
                original_relative_path          = originalPath,
                file_id                         = fileId,
                pre_recovery_override           = ManualOverride,
                qb_file_after_pre_recovery_edit = preRecoveryQbFile,
                manual_override                 = ManualOverride,
                post_recovery_rename_to         = postRecoveryRenameTo,
                qb_file_after_apply             = filesAfterApply[0].Name,
                torrent_file_restored_length    = taskAfterApply.TorrentFile.Length
            },
            episode_version = new
            {
                episodeRow.SubjectId, episodeRow.Episode, episodeRow.SourceScope,
                episodeRow.IncumbentVideoFileId,
                read_after = "post-recovery-rename"
            },
            evidence_path = Path.Combine(sceneRoot, "evidence.jsonl")
        };
        await ScenarioSupport.WriteJsonAsync(Path.Combine(sceneRoot, "summary.json"), summary);
    }
}
