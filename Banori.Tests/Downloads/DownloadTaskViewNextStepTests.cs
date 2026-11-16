using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

/// <summary>
///     「等待对象 / 下一步」派生文案：只读展示，不参与推进。覆盖全部显示阶段、失败步骤恢复建议
///     与墓碑/退役的区分，并锁定查询层新增的三个投影字段。
/// </summary>
public sealed class DownloadTaskViewNextStepTests
{
    [Fact]
    public void AllDisplayPhases_HaveConciseTextsWithoutInternalTerms()
    {
        var scenarios = BuildPhaseScenarios();

        Assert.True(scenarios.Count >= 20, $"阶段场景只有 {scenarios.Count} 组，未达到覆盖下限。");
        var covered = scenarios.Select(scenario => scenario.Phase).ToHashSet();
        foreach (var phase in Enum.GetValues<DownloadDisplayPhase>())
            Assert.True(covered.Contains(phase), $"显示阶段 {phase} 没有任何场景覆盖。");

        foreach (var scenario in scenarios)
        {
            Assert.Equal(scenario.Phase, scenario.Task.Phase);
            AssertReadableText(scenario.Task.WaitingForText, $"{scenario.Name} / WaitingForText");
            if (scenario.Phase == DownloadDisplayPhase.Completed ||
                scenario is { Phase: DownloadDisplayPhase.Replaced, Task.IsSelectionTombstone: false })
                Assert.Empty(scenario.Task.NextStepText);
            else
                AssertReadableText(scenario.Task.NextStepText, $"{scenario.Name} / NextStepText");
        }
    }

    [Fact]
    public void WaitingSubmit_DownloaderNotReadyExplainsWhyAndDiffersFromReady()
    {
        var notReady = CreateTask(DownloadTaskStatus.PendingSubmit);
        var ready    = notReady with { DownloaderReady = true };

        Assert.Equal(DownloadDisplayPhase.WaitingSubmit, notReady.Phase);
        Assert.Contains("未启用或未配置", notReady.WaitingForText);
        Assert.Contains("设置页", notReady.NextStepText);
        Assert.NotEqual(notReady.WaitingForText, ready.WaitingForText);
        Assert.NotEqual(notReady.NextStepText, ready.NextStepText);
        Assert.DoesNotContain("未启用或未配置", ready.WaitingForText);
    }

    [Fact]
    public void Replaced_SelectionTombstoneAndRetiredTaskUseDifferentTexts()
    {
        var tombstone = CreateTask(DownloadTaskStatus.Replaced) with
        {
            FailedStep = "择优", ReplacedByNaming = "胜出任务"
        };
        var retired = CreateTask(DownloadTaskStatus.Replaced) with { ReplacedByNaming = "新版本" };

        Assert.True(tombstone.IsSelectionTombstone);
        Assert.False(retired.IsSelectionTombstone);
        Assert.Contains("同集落选，未下载任何文件", tombstone.WaitingForText);
        Assert.DoesNotContain("已被", tombstone.WaitingForText);
        Assert.Contains("已被更新版本替代", retired.WaitingForText);
        Assert.NotEqual(tombstone.WaitingForText, retired.WaitingForText);
        Assert.NotEqual(tombstone.NextStepText, retired.NextStepText);
        Assert.Equal("同集落选（未下载）", tombstone.StatusText);
        Assert.Equal("已被替换", retired.StatusText);
        Assert.Empty(retired.NextStepText);
        Assert.Contains("如需重新下载请删除后重新添加", tombstone.NextStepText);
        Assert.DoesNotContain("自动重新排队", tombstone.NextStepText);
    }

    /// <summary>已完成但归档待核对：不能再说「无需操作」，要指向页面上的重新核对入口。</summary>
    [Fact]
    public void CompletedWithArchiveReview_AdvisesRerunArchiveInsteadOfNoAction()
    {
        var completed = CreateTask(DownloadTaskStatus.Completed);
        var blocked   = completed with { ArchiveReviewReason = "目标被其他任务占用" };

        Assert.Empty(completed.NextStepText);
        Assert.Contains("请核对归档冲突后点击「重新核对归档」", blocked.NextStepText);
        Assert.DoesNotContain("无需操作", blocked.NextStepText);
    }

    [Fact]
    public void VerificationUnconfirmed_OffersReverifyWithoutRedownloadAndExplainsSendFailure()
    {
        var unconfirmed = CreateTask(DownloadTaskStatus.Paused) with
        {
            VerificationStep = DownloadVerificationStep.Unconfirmed
        };
        var sendFailed = unconfirmed with
        {
            VerificationRequestState = DownloadVerificationRequestState.SendFailed
        };

        Assert.Equal(DownloadDisplayPhase.VerificationUnconfirmed, unconfirmed.Phase);
        Assert.Contains("文件保持原样", unconfirmed.WaitingForText);
        Assert.Contains("可重新校验，不重新下载", unconfirmed.NextStepText);
        Assert.Contains("可能已送达", sendFailed.NextStepText);
        Assert.Contains("未计入请求次数", sendFailed.NextStepText);
        Assert.DoesNotContain("损坏", sendFailed.NextStepText);
    }

    [Theory]
    [InlineData("认证", "地址与账号")]
    [InlineData("提交", "重试")]
    [InlineData("提交路径", "占用")]
    [InlineData("对账", "手动删除")]
    [InlineData("文件整理", "文件清单")]
    [InlineData("改名", "人工核对")]
    [InlineData("复用校验", "放弃复用")]
    [InlineData("下载器", "下载器连接")]
    [InlineData("择优", "不是故障")]
    [InlineData("未知步骤", "未知步骤")]
    [InlineData(null, "未知步骤")]
    public void Failed_NextStepGivesStaticRecoveryAdvice(string? failedStep, string expected)
    {
        var task = CreateTask(DownloadTaskStatus.Failed) with { FailedStep = failedStep };

        Assert.Contains(expected, task.NextStepText);
    }

    [Fact]
    public void Failed_VerificationStepsPreferReverifyOrSendFailureAdvice()
    {
        var unconfirmed = CreateTask(DownloadTaskStatus.Failed) with
        {
            FailedStep = "复用校验", VerificationStep = DownloadVerificationStep.Unconfirmed
        };
        var sendFailed = CreateTask(DownloadTaskStatus.Failed) with
        {
            FailedStep = "复用校验", VerificationRequestState = DownloadVerificationRequestState.SendFailed
        };

        Assert.Contains("可重新校验，不重新下载", unconfirmed.NextStepText);
        Assert.Contains("可能已送达", sendFailed.NextStepText);
        Assert.Contains("未计入请求次数", sendFailed.NextStepText);
    }

    /// <summary>ToView 投影：下次观察时间、提交尝试次数与下载器就绪状态必须进入视图，且就绪只按设置快照读取。</summary>
    [Fact]
    public async Task ListAsync_ProjectsNextObservationAttemptCountAndDownloaderReadiness()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.NextStep.Projection.");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "next-step.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            var taskId          = Guid.NewGuid();
            var nextObservation = DateTimeOffset.UtcNow.AddSeconds(15);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.DownloadTasks.Add(new DownloadTask
                {
                    Id                   = taskId,
                    InfoHash             = new string('c', 40),
                    TorrentFile          = [],
                    SavePath             = "/downloads/.tasks/" + taskId,
                    NamingSnapshot       = "pending-verification",
                    Status               = DownloadTaskStatus.Paused,
                    IsSystemPaused       = true,
                    FailedStep           = "复用校验",
                    CreatedAtUtc         = DateTimeOffset.UtcNow,
                    NextObservationAtUtc = nextObservation,
                    AttemptCount         = 3,
                    VerificationIntent   = new DownloadVerificationIntent
                    {
                        Round        = 1,
                        Step         = DownloadVerificationStep.Unconfirmed,
                        RequestState = DownloadVerificationRequestState.SendFailed,
                        Files        = []
                    }.ToJson()
                });
                await db.SaveChangesAsync();
            }

            var page = await new DownloadTaskQueries(factory).ListAsync(new DownloadTaskListFilter());
            var view = Assert.Single(page.Items);
            Assert.Equal(nextObservation, view.NextObservationAtUtc);
            Assert.Equal(3, view.AttemptCount);
            Assert.False(view.DownloaderReady);
            Assert.Equal(DownloadVerificationRequestState.SendFailed, view.VerificationRequestState);

            var settingsStore   = new MemoryApplicationSettingStore();
            var sessionFactory  = new FakeQbittorrentSessionFactory(new FakeTorrentStore());
            var settings        = new ManageQbittorrentSettings(settingsStore, sessionFactory);
            await settings.SaveAsync(new QbittorrentSettings("http://qb.test", "user", "/downloads", true));

            var readyPage = await new DownloadTaskQueries(factory, settings).ListAsync(new DownloadTaskListFilter());
            var readyView = Assert.Single(readyPage.Items);
            Assert.True(readyView.DownloaderReady);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static IReadOnlyList<PhaseScenario> BuildPhaseScenarios() =>
    [
        new("等待提交（下载器未就绪）", CreateTask(DownloadTaskStatus.PendingSubmit), DownloadDisplayPhase.WaitingSubmit),
        new("等待提交（下载器就绪）", CreateTask(DownloadTaskStatus.PendingSubmit) with { DownloaderReady = true },
            DownloadDisplayPhase.WaitingSubmit),
        new("等待添加确认", CreateTask(DownloadTaskStatus.Submitting), DownloadDisplayPhase.WaitingAddConfirm),
        new("准备中", CreatePreparing(DownloadPreparationStage.None), DownloadDisplayPhase.Preparing),
        new("整理路径", CreatePreparing(DownloadPreparationStage.Planned), DownloadDisplayPhase.PreparingLayout),
        new("等待校验开始", CreateVerifying(DownloadVerificationStep.Requested),
            DownloadDisplayPhase.WaitingCheckStart),
        new("校验中", CreateVerifying(DownloadVerificationStep.Observing) with { ObservedChecking = true },
            DownloadDisplayPhase.Checking),
        new("等待结果稳定", CreateVerifying(DownloadVerificationStep.Observing) with
        {
            ObservedChecking = true, ResultStabilizing = true
        }, DownloadDisplayPhase.WaitingResultStable),
        new("等待停止确认", CreateVerifying(DownloadVerificationStep.Observing) with
        {
            ObservedChecking = true, StopConfirmPending = true
        }, DownloadDisplayPhase.WaitingStopConfirm),
        new("等待启动确认", CreateVerifying(DownloadVerificationStep.ReadyToRun),
            DownloadDisplayPhase.WaitingStartConfirm),
        new("隔离迁移", CreatePreparing(DownloadPreparationStage.Planned) with
        {
            IsolationState = DownloadIsolationState.Readding
        }, DownloadDisplayPhase.IsolationMigrating),
        new("校验未确认", CreateTask(DownloadTaskStatus.Paused) with
        {
            VerificationStep = DownloadVerificationStep.Unconfirmed
        }, DownloadDisplayPhase.VerificationUnconfirmed),
        new("归档中", CreateTask(DownloadTaskStatus.Completed) with { HasPendingFileMoves = true },
            DownloadDisplayPhase.Archiving),
        new("下载中", CreateTask(DownloadTaskStatus.Downloading), DownloadDisplayPhase.Downloading),
        new("人工暂停", CreateTask(DownloadTaskStatus.Paused) with { IsUserPaused = true },
            DownloadDisplayPhase.PausedManually),
        new("下载器端停止", CreateTask(DownloadTaskStatus.Paused) with
        {
            IsUserPaused = true, ExternalStopObservedAtUtc = DateTimeOffset.UtcNow
        }, DownloadDisplayPhase.PausedByDownloader),
        new("暂停原因未记录", CreateTask(DownloadTaskStatus.Paused), DownloadDisplayPhase.Paused),
        new("已完成", CreateTask(DownloadTaskStatus.Completed), DownloadDisplayPhase.Completed),
        new("失败", CreateTask(DownloadTaskStatus.Failed) with { FailedStep = "认证" }, DownloadDisplayPhase.Failed),
        new("丢失", CreateTask(DownloadTaskStatus.Lost), DownloadDisplayPhase.Lost),
        new("同集落选墓碑", CreateTask(DownloadTaskStatus.Replaced) with
        {
            FailedStep = "择优", ReplacedByNaming = "胜出任务"
        }, DownloadDisplayPhase.Replaced),
        new("已退役", CreateTask(DownloadTaskStatus.Replaced) with { ReplacedByNaming = "新版本" },
            DownloadDisplayPhase.Replaced)
    ];

    private static void AssertReadableText(string text, string scenario)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"{scenario}：展示文案为空。");
        foreach (var term in new[] { "intent", "JSON", "Guid", "损坏", "100%" })
            Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase);
    }

    private static DownloadTaskView CreateTask(DownloadTaskStatus status) => new()
    {
        Id           = Guid.NewGuid(),
        FeedItemId   = null,
        Status       = status,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    /// <summary>准备暂停中的任务：阶段派生走隔离/校验/准备分支的前提。</summary>
    private static DownloadTaskView CreatePreparing(DownloadPreparationStage stage) =>
        CreateTask(DownloadTaskStatus.Downloading) with
        {
            IsSystemPaused = true, PreparationStage = stage
        };

    private static DownloadTaskView CreateVerifying(string step) =>
        CreatePreparing(DownloadPreparationStage.AwaitingVerification) with
        {
            VerificationStep = step
        };

    private sealed record PhaseScenario(string Name, DownloadTaskView Task, DownloadDisplayPhase Phase);
}
