using Banori.Data;
using Banori.Features.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banori.Tests.Downloads;

public sealed class DownloadTaskViewTests
{
    [Theory]
    [InlineData(DownloadTaskStatus.PendingSubmit, true)]
    [InlineData(DownloadTaskStatus.Submitting, true)]
    [InlineData(DownloadTaskStatus.Downloading, true)]
    [InlineData(DownloadTaskStatus.Paused, true)]
    [InlineData(DownloadTaskStatus.Completed, false)]
    [InlineData(DownloadTaskStatus.Failed, true)]
    [InlineData(DownloadTaskStatus.Lost, true)]
    [InlineData(DownloadTaskStatus.Replaced, false)]
    public void ShowPendingReplacement_HidesCompletedAndReplacedTasksWithoutClearingHistory(
        DownloadTaskStatus status, bool expected)
    {
        var replacedId = Guid.NewGuid();
        var task = CreateTask(status) with
        {
            ReplacesTaskId = replacedId,
            ReplacesNaming = "previous-release",
            Progress = 1
        };

        Assert.Equal(expected, task.ShowPendingReplacement);
        Assert.Equal(replacedId, task.ReplacesTaskId);
        Assert.Equal("previous-release", task.ReplacesNaming);
    }

    [Theory]
    [InlineData(DownloadTaskStatus.PendingSubmit)]
    [InlineData(DownloadTaskStatus.Submitting)]
    [InlineData(DownloadTaskStatus.Downloading)]
    [InlineData(DownloadTaskStatus.Paused)]
    [InlineData(DownloadTaskStatus.Completed)]
    [InlineData(DownloadTaskStatus.Failed)]
    [InlineData(DownloadTaskStatus.Lost)]
    [InlineData(DownloadTaskStatus.Replaced)]
    public void ShowPendingReplacement_WithoutRelatedName_IsHidden(DownloadTaskStatus status)
    {
        Assert.False(CreateTask(status).ShowPendingReplacement);
    }

    [Theory]
    [InlineData(DownloadTaskStatus.PendingSubmit, DownloadDisplayPhase.WaitingSubmit, "等待提交")]
    [InlineData(DownloadTaskStatus.Submitting, DownloadDisplayPhase.WaitingAddConfirm, "等待添加确认")]
    [InlineData(DownloadTaskStatus.Completed, DownloadDisplayPhase.Completed, "已完成")]
    [InlineData(DownloadTaskStatus.Failed, DownloadDisplayPhase.Failed, "失败")]
    [InlineData(DownloadTaskStatus.Lost, DownloadDisplayPhase.Lost, "任务丢失")]
    [InlineData(DownloadTaskStatus.Replaced, DownloadDisplayPhase.Replaced, "已被替换")]
    [InlineData(DownloadTaskStatus.Downloading, DownloadDisplayPhase.Downloading, "下载中")]
    public void Phase_TasksOutsidePreparation_MapDirectly(
        DownloadTaskStatus status, DownloadDisplayPhase expectedPhase, string expectedText)
    {
        var task = CreateTask(status);

        Assert.Equal(expectedPhase, task.Phase);
        Assert.Equal(expectedText, task.StatusText);
    }

    [Fact]
    public void Phase_CompletedWithPendingMoves_IsArchiving()
    {
        var task = CreateTask(DownloadTaskStatus.Completed) with { HasPendingFileMoves = true };

        Assert.Equal(DownloadDisplayPhase.Archiving, task.Phase);
        Assert.Equal("归档中", task.StatusText);
    }

    [Theory]
    [InlineData(DownloadPreparationStage.Planned)]
    [InlineData(DownloadPreparationStage.Applying)]
    public void Phase_PreparationStages_ArePreparingLayout(DownloadPreparationStage stage)
    {
        var task = CreatePreparing(stage);

        Assert.Equal(DownloadDisplayPhase.PreparingLayout, task.Phase);
        Assert.Equal("整理路径中", task.StatusText);
    }

    [Theory]
    [InlineData(DownloadPreparationStage.None, DownloadDisplayPhase.Preparing, "准备中（未启动下载）")]
    [InlineData(DownloadPreparationStage.ReadyToRun, DownloadDisplayPhase.WaitingStartConfirm, "等待启动确认")]
    public void Phase_OtherPreparationStages_KeepExplanatoryText(
        DownloadPreparationStage stage, DownloadDisplayPhase expectedPhase, string expectedText)
    {
        var task = CreatePreparing(stage);

        Assert.Equal(expectedPhase, task.Phase);
        Assert.Equal(expectedText, task.StatusText);
    }

    [Fact]
    public void Phase_VerificationMapping_IsPreparingLayoutWithRoundNote()
    {
        var task = CreateVerifying(DownloadVerificationStep.Mapping) with { VerificationRound = 3 };

        Assert.Equal(DownloadDisplayPhase.PreparingLayout, task.Phase);
        Assert.Contains("复用校验第 3 轮", task.PhaseNotes);
    }

    [Fact]
    public void Phase_RecheckSentWithoutCheckingEvidence_WaitsForCheckStart()
    {
        var task = CreateVerifying(DownloadVerificationStep.Requested);

        Assert.Equal(DownloadDisplayPhase.WaitingCheckStart, task.Phase);
        Assert.Equal("等待校验开始", task.StatusText);
    }

    [Fact]
    public void Phase_CheckingObservedWithoutStabilitySample_IsCheckingWithCaption()
    {
        var task = CreateVerifying(DownloadVerificationStep.Observing) with { ObservedChecking = true };

        Assert.Equal(DownloadDisplayPhase.Checking, task.Phase);
        Assert.Equal("校验中", task.StatusText);
        Assert.Equal("校验 ", task.ProgressCaption);
    }

    [Fact]
    public void Phase_StabilitySamplingStarted_WaitsForStableResult()
    {
        var task = CreateVerifying(DownloadVerificationStep.Observing) with
        {
            ObservedChecking = true, ResultStabilizing = true
        };

        Assert.Equal(DownloadDisplayPhase.WaitingResultStable, task.Phase);
        Assert.Equal("等待结果稳定", task.StatusText);
        // 稳定期进度回到内容语义，不保留校验前缀。
        Assert.Null(task.ProgressCaption);
    }

    [Fact]
    public void Phase_StopRequestOutstanding_WaitsForStopConfirmBeforeOtherObservations()
    {
        var task = CreateVerifying(DownloadVerificationStep.Observing) with
        {
            ObservedChecking = true, StopConfirmPending = true
        };

        Assert.Equal(DownloadDisplayPhase.WaitingStopConfirm, task.Phase);
        Assert.Equal("等待停止确认", task.StatusText);
    }

    [Fact]
    public void Phase_VerificationReadyToRun_WaitsForStartConfirm()
    {
        var task = CreateVerifying(DownloadVerificationStep.ReadyToRun);

        Assert.Equal(DownloadDisplayPhase.WaitingStartConfirm, task.Phase);
    }

    [Fact]
    public void Phase_IsolationMigration_ExplainsMigration()
    {
        var task = CreatePreparing(DownloadPreparationStage.Planned) with
        {
            IsolationState = DownloadIsolationState.Readding
        };

        Assert.Equal(DownloadDisplayPhase.IsolationMigrating, task.Phase);
        Assert.Equal("隔离迁移中", task.StatusText);
    }

    [Fact]
    public void Phase_ManualPauseFromPage_KeepsManualText()
    {
        var task = CreateTask(DownloadTaskStatus.Paused) with { IsUserPaused = true };

        Assert.Equal(DownloadDisplayPhase.PausedManually, task.Phase);
        Assert.Equal("已暂停（人工）", task.StatusText);
        Assert.DoesNotContain(task.PhaseNotes, note => note.Contains("原因未记录"));
    }

    [Fact]
    public void Phase_DownloaderSideStop_ExplainsUnknownCauseInsteadOfFabricatingManualPause()
    {
        var task = CreateTask(DownloadTaskStatus.Paused) with
        {
            IsUserPaused = true, ExternalStopObservedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.Equal(DownloadDisplayPhase.PausedByDownloader, task.Phase);
        Assert.Equal("已暂停（下载器端停止）", task.StatusText);
        Assert.Contains(task.PhaseNotes, note => note.Contains("原因未记录"));
    }

    [Theory]
    [InlineData(DownloadTaskStatus.Downloading, true)]
    [InlineData(DownloadTaskStatus.Paused, true)]
    [InlineData(DownloadTaskStatus.Failed, true)]
    [InlineData(DownloadTaskStatus.Lost, true)]
    [InlineData(DownloadTaskStatus.Completed, false)]
    [InlineData(DownloadTaskStatus.Replaced, false)]
    [InlineData(DownloadTaskStatus.PendingSubmit, false)]
    [InlineData(DownloadTaskStatus.Submitting, false)]
    public void ShowsProgress_KeepsLastObservedProgressForBlockedTasks(DownloadTaskStatus status, bool expected)
    {
        var task = CreateTask(status) with { Progress = 0.4 };

        Assert.Equal(expected, task.ShowsProgress);
    }

    [Fact]
    public void ProgressNote_VerificationBlockedTask_StatesObservationIsNotConfirmation()
    {
        var blocked = CreateTask(DownloadTaskStatus.Failed) with
        {
            Progress = 0.8, FailedStep = "复用校验", Error = "本轮校验执行未确认。"
        };
        var tidyFailed = CreateTask(DownloadTaskStatus.Failed) with
        {
            Progress = 1.0, FailedStep = "文件整理", Error = "目标被占用。"
        };

        Assert.Equal("最后观察进度，不代表本轮校验已确认", blocked.ProgressNote);
        Assert.Null(tidyFailed.ProgressNote);
    }

    [Fact]
    public void ShowsLastSync_OnlyForTasksWhoseObservationStillMatters()
    {
        var active = CreateTask(DownloadTaskStatus.Downloading) with
        {
            LastSyncedAtUtc = DateTimeOffset.UtcNow
        };
        var completed = CreateTask(DownloadTaskStatus.Completed) with
        {
            LastSyncedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.True(active.ShowsLastSync);
        Assert.False(completed.ShowsLastSync);
    }

    [Fact]
    public void PathDecisionNote_StagingTaskExplainsRootWithRelatedTask()
    {
        var task = CreatePreparing(DownloadPreparationStage.Planned) with
        {
            PathDecisionKind    = DownloadPathDecisionKinds.ReplacementStaging,
            PathDecisionReason  = "存在待替换旧版，先在任务目录暂存。",
            PathDecisionRelatedNaming = "旧版本"
        };

        Assert.Equal("存在待替换旧版，先在任务目录暂存。（关联任务：旧版本）", task.PathDecisionNote);
        Assert.Contains(task.PathDecisionNote!, task.PhaseNotes);
    }

    [Fact]
    public void PathDecisionNote_FinalRootAndFinalTasksNeedNoExplanation()
    {
        var finalRoot = CreatePreparing(DownloadPreparationStage.Planned) with
        {
            PathDecisionKind   = DownloadPathDecisionKinds.FinalRoot,
            PathDecisionReason = "无候选且无待替换版本。"
        };
        var completed = CreateTask(DownloadTaskStatus.Completed) with
        {
            PathDecisionKind   = DownloadPathDecisionKinds.ReplacementStaging,
            PathDecisionReason = "存在待替换旧版，先在任务目录暂存。"
        };

        Assert.Null(finalRoot.PathDecisionNote);
        // 完成后的暂存早已解决，展示过期理由只会误导。
        Assert.Null(completed.PathDecisionNote);
    }

    /// <summary>ToView 投影：校验意图、路径决策、观察时间与归档移动标志必须进入视图，派生阶段才有依据。</summary>
    [Fact]
    public async Task ListAsync_ProjectsIntentDecisionAndObservationFields()
    {
        var directory = Directory.CreateTempSubdirectory("Banori.Phase4.ViewProjection.");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "view.db")};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            var relatedId = Guid.NewGuid();
            var taskId    = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.DownloadTasks.Add(new DownloadTask
                {
                    Id            = relatedId,
                    InfoHash      = new string('a', 40),
                    TorrentFile   = [],
                    SavePath      = "/downloads/final",
                    NamingSnapshot = "related",
                    Status        = DownloadTaskStatus.Downloading,
                    CreatedAtUtc  = DateTimeOffset.UtcNow
                });
                db.DownloadTasks.Add(new DownloadTask
                {
                    Id            = taskId,
                    InfoHash      = new string('b', 40),
                    TorrentFile   = [],
                    SavePath      = "/downloads/.tasks/" + taskId,
                    NamingSnapshot = "staging",
                    Status        = DownloadTaskStatus.Downloading,
                    IsSystemPaused = true,
                    PreparationStage = DownloadPreparationStage.AwaitingVerification,
                    IsolationState   = DownloadIsolationState.None,
                    CreatedAtUtc  = DateTimeOffset.UtcNow,
                    LastSyncedAtUtc = DateTimeOffset.UtcNow,
                    ExternalStopObservedAtUtc = DateTimeOffset.UtcNow,
                    PendingSavePath = "/downloads/.tasks/" + taskId + "/moved",
                    VerificationIntent = new DownloadVerificationIntent
                    {
                        Round = 2, Step = DownloadVerificationStep.Observing,
                        ObservedChecking = true, StabilityObservedAtUtc = DateTimeOffset.UtcNow, Files = []
                    }.ToJson(),
                    PathDecision = new DownloadPathDecision
                    {
                        Kind = DownloadPathDecisionKinds.ReplacementStaging,
                        Reason = "存在待替换旧版。", RelatedTaskId = relatedId, DecidedAtUtc = DateTimeOffset.UtcNow
                    }.ToJson()
                });
                db.DownloadTaskFiles.Add(new DownloadTaskFile
                {
                    DownloadTaskId = taskId, FileIndex = 0, OriginalRelativePath = "a.mkv",
                    CurrentRelativePath = "a.mkv", Size = 100
                });
                await db.SaveChangesAsync();
            }

            var view = Assert.Single(await new DownloadTaskQueries(factory).ListAsync(), item => item.Id == taskId);

            Assert.Equal(DownloadVerificationStep.Observing, view.VerificationStep);
            Assert.Equal(2, view.VerificationRound);
            Assert.True(view.ObservedChecking);
            Assert.True(view.ResultStabilizing);
            Assert.False(view.StopConfirmPending);
            Assert.NotNull(view.LastSyncedAtUtc);
            Assert.NotNull(view.ExternalStopObservedAtUtc);
            Assert.True(view.HasPendingFileMoves);
            Assert.Equal(DownloadPathDecisionKinds.ReplacementStaging, view.PathDecisionKind);
            Assert.Equal("related", view.PathDecisionRelatedNaming);
            Assert.Equal(DownloadDisplayPhase.WaitingResultStable, view.Phase);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static DownloadTaskView CreateTask(DownloadTaskStatus status) => new()
    {
        Id           = Guid.NewGuid(),
        FeedItemId   = null,
        Status       = status,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    /// <summary>准备暂停中的任务：阶段派生走隔离/校验/准备分支的前提。</summary>
    private static DownloadTaskView CreatePreparing(DownloadPreparationStage stage) => CreateTask(DownloadTaskStatus.Downloading) with
    {
        IsSystemPaused = true, PreparationStage = stage
    };

    private static DownloadTaskView CreateVerifying(string step) => CreatePreparing(DownloadPreparationStage.AwaitingVerification) with
    {
        VerificationStep = step
    };
}
