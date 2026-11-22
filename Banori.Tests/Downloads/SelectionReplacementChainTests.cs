using Banned.AniParser;
using Banned.AniParser.Models.Enums;
using Banori.Core.Rules;
using Banori.Core.Rules.Selection;
using Banori.Core.Torrents;
using Banori.Data;
using Banori.Features.Downloads;
using Banori.Features.Settings;
using Banori.Features.Subscriptions;
using Banori.Integrations.Qbittorrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Banori.Tests.Downloads;

/// <summary>
///     订阅择优胜出到旧版替换链的回归：已确认的同源同集规则优胜应把已完成旧任务记入每集
///     登记表，完成期经旧文件迁让与逐集现任切换完成替换；平级、较差、跨来源或未确认的条目
///     不得借版本号、完成顺序或同名路径反向替换优胜者。存量失败任务重试时按同一已落库
///     优胜选择补建关系；种子已提交到正式根时先恢复任务专属暂存布局再接管；旧文件被其他
///     种子共享时仍受占用保护。手动创建在下载种子期间遇到优胜切换时，锁内复核必须拒绝过期条目。
/// </summary>
public sealed partial class DownloadServiceTests
{
    private const string MikanFeed = "https://mikanani.me/RSS/bangumi";
    private const string AnibtFeed = "https://anibt.net/rss";

    [Fact]
    public async Task SelectionWinner_CrossGroupAndLanguage_RegistersReplacementAndArchivesAfterCompletion()
    {
        using var harness          = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        var       selectionService = CreateRealSelectionService(harness);
        var       key              = new SelectionGroupKey(42, "SingleEpisode", 12, "mikan");
        await harness.SaveSelectionRulesAsync(NekoRules());

        // 旧任务：ANi／Tc（规则未列出的组与语言），先经真实订阅择优登记并完成。
        var oldItemId = await SeedSelectionItemAsync(harness, "chain-old", AniTcTitle(), "ANi", "JpTc");
        await selectionService.SelectAndRegisterAsync([key]);
        var oldTaskId = (await GetSelectionAsync(harness)).IntentTaskId!.Value;
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(oldTaskId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(oldTaskId)).Status);
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;

        // 新任务：喵萌／JpSc，规则严格更优，登记后立即建立“旧成片为现任、新正片为待完成优胜”的关系。
        // 新种子大小与旧成片不同：排除复用候选，直接走替换暂存路径（与真实不同类型版本一致）。
        var newItemId = await SeedSelectionItemAsync(harness, "chain-new", NekoTitle("简日双语"), "喵萌奶茶屋",
                                                     "JpSc", length : 2048);
        await selectionService.SelectAndRegisterAsync([key]);
        var newSelection = await GetSelectionAsync(harness);
        Assert.Equal(newItemId, newSelection.WinnerFeedItemId);
        var newTaskId = newSelection.IntentTaskId!.Value;
        var newFileId = (await FindAnimeFileIdAsync(harness, newTaskId))!.Value;

        var registered = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(registered);
        Assert.Equal(oldFileId, registered.IncumbentVideoFileId);
        Assert.Equal(newFileId, registered.PendingVideoFileId);
        // 新版完成前旧成片保留：任务仍是已完成、未被声明替换，文件仍在正式位置。
        var incumbentTask = await harness.GetTaskAsync(oldTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, incumbentTask.Status);
        Assert.Null(incumbentTask.ReplacedByTaskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var incumbentFile = await db.DownloadTaskFiles.AsNoTracking()
                                        .SingleAsync(file => file.Id == oldFileId);
            Assert.Equal("测试作品 E12.mp4", incumbentFile.CurrentRelativePath);
        }

        var fileService = harness.Provider.GetRequiredService<DownloadFileService>();
        Assert.True(await fileService.HasReplacementAsync(newTaskId, default));

        // 有待替换旧版时先下载到任务专属根，完成后才迁让旧文件并迁入正式位置。
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskPlacement.TaskRoot("/downloads", newTaskId),
                     (await harness.GetTaskAsync(newTaskId)).SavePath);
        CompleteTorrent(harness, (await harness.GetTaskAsync(newTaskId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(newTaskId)).Status);
        var switched = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(switched);
        Assert.Equal(newFileId, switched.IncumbentVideoFileId);
        Assert.Equal(oldFileId, switched.SupersededVideoFileId);
        Assert.Null(switched.PendingVideoFileId);
        var retired = await harness.GetTaskAsync(oldTaskId);
        Assert.Equal(DownloadTaskStatus.Replaced, retired.Status);
        Assert.Equal(newTaskId, retired.ReplacedByTaskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var incumbentFile = await db.DownloadTaskFiles.AsNoTracking()
                                        .SingleAsync(file => file.Id == oldFileId);
            Assert.StartsWith(".replaced/", incumbentFile.CurrentRelativePath);
            var replacement = await db.DownloadTaskFiles.AsNoTracking()
                                      .SingleAsync(file => file.Id == newFileId);
            Assert.Equal("测试作品 E12.mp4", replacement.CurrentRelativePath);
        }
    }

    [Fact]
    public async Task SelectionWinner_SameGroupCrossLanguage_RegistersRuleUpgrade()
    {
        using var harness          = await CreateHarnessAsync();
        var       selectionService = CreateRealSelectionService(harness);
        var       key              = new SelectionGroupKey(42, "SingleEpisode", 12, "mikan");
        await harness.SaveSelectionRulesAsync(NekoRules(EnumLanguage.JpSc, EnumLanguage.JpTc));

        var oldTaskId = await CompleteSelectionWinnerAsync(harness, selectionService, key, "rule-language-old",
                                                           NekoTitle("繁日双语"), "喵萌奶茶屋", "JpTc");
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;

        // 同组但语言不同：版本守卫不成立，规则语言序判断新候选严格更优后建立替换关系。
        var newItemId = await SeedSelectionItemAsync(harness, "rule-language-new", NekoTitle("简日双语"),
                                                     "喵萌奶茶屋", "JpSc");
        await selectionService.SelectAndRegisterAsync([key]);
        var newTaskId = (await GetSelectionAsync(harness)).IntentTaskId!.Value;
        Assert.Equal(newItemId, (await GetSelectionAsync(harness)).WinnerFeedItemId);
        Assert.Equal(oldFileId, (await FindSourceEpisodeVersionAsync(harness, 12, "mikan"))!.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, newTaskId),
                     (await FindSourceEpisodeVersionAsync(harness, 12, "mikan"))!.PendingVideoFileId);
    }

    [Fact]
    public async Task SelectionWinner_CrossGroupSameLanguage_RegistersRuleUpgrade()
    {
        using var harness          = await CreateHarnessAsync();
        var       selectionService = CreateRealSelectionService(harness);
        var       key              = new SelectionGroupKey(42, "SingleEpisode", 12, "mikan");
        await harness.SaveSelectionRulesAsync(NekoRules() with { GroupOrder = ["喵萌奶茶屋", "漫猫字幕组"] });

        var oldTaskId = await CompleteSelectionWinnerAsync(harness, selectionService, key, "rule-group-old",
                                                           ComicatJpScTitle(), "漫猫字幕组", "JpSc");
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;

        // 同语言但字幕组不同：版本守卫不成立，规则字幕组序判断新候选严格更优后建立替换关系。
        var newItemId = await SeedSelectionItemAsync(harness, "rule-group-new", NekoTitle("简日双语"), "喵萌奶茶屋",
                                                     "JpSc");
        await selectionService.SelectAndRegisterAsync([key]);
        var newTaskId = (await GetSelectionAsync(harness)).IntentTaskId!.Value;
        Assert.Equal(newItemId, (await GetSelectionAsync(harness)).WinnerFeedItemId);
        Assert.Equal(oldFileId, (await FindSourceEpisodeVersionAsync(harness, 12, "mikan"))!.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, newTaskId),
                     (await FindSourceEpisodeVersionAsync(harness, 12, "mikan"))!.PendingVideoFileId);
    }

    [Fact]
    public async Task SelectionWinner_EqualOrLowerPriority_DoesNotReplaceCompletedIncumbent()
    {
        // 平级：同语言、同版本、同正片数，字幕组不同且都未入规则；仅发布时间/稳定标识的兜底顺序
        // 不构成“规则严格更优”，不得建立替换关系。
        using (var harness = await CreateHarnessAsync())
        {
            await harness.SaveSelectionRulesAsync(SelectionRuleConfig.CreateDefault() with { GroupOrder = [] });
            var oldTaskId = await CompleteManualWinnerAsync(harness, "equal-old", NekoTitle("简日双语"), "喵萌奶茶屋",
                                                            "JpSc");
            var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;
            var newItemId = await SeedSelectionItemAsync(harness, "equal-new", ComicatJpScTitle(), "漫猫字幕组",
                                                         "JpSc");
            await SetSelectionWinnerAsync(harness, newItemId);
            var newTaskId = await harness.CreateDownloadAsync(newItemId);

            var registered = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
            Assert.NotNull(registered);
            Assert.Equal(oldFileId, registered.IncumbentVideoFileId);
            Assert.Null(registered.PendingVideoFileId);
            Assert.Null((await harness.GetTaskAsync(newTaskId)).ReplacesTaskId);
        }

        // 较差：新候选的字幕组不在规则内（排最后），已完成现任虽可比较也不得被反向顶替。
        using (var harness = await CreateHarnessAsync())
        {
            await harness.SaveSelectionRulesAsync(NekoRules());
            var oldTaskId = await CompleteManualWinnerAsync(harness, "lower-old", NekoTitle("简日双语"), "喵萌奶茶屋",
                                                            "JpSc");
            var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;
            var newItemId = await SeedSelectionItemAsync(harness, "lower-new", ComicatJpScTitle(), "漫猫字幕组",
                                                         "JpSc");
            await SetSelectionWinnerAsync(harness, newItemId);
            var newTaskId = await harness.CreateDownloadAsync(newItemId);

            var registered = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
            Assert.NotNull(registered);
            Assert.Equal(oldFileId, registered.IncumbentVideoFileId);
            Assert.Null(registered.PendingVideoFileId);
            Assert.Null((await harness.GetTaskAsync(newTaskId)).ReplacesTaskId);
        }
    }

    [Fact]
    public async Task SelectionWinner_OtherSource_DoesNotReplaceCompletedIncumbent()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldTaskId = await CompleteManualWinnerAsync(harness, "source-old", NekoTitle("简日双语"), "喵萌奶茶屋", "JpSc",
                                                        sourceScope : "mikan", feedUrl : MikanFeed);
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;

        // 新条目属于 anibt 来源：真实 SourceScope 不同，不参与 mikan 的规则对决，也不写 mikan 登记表。
        var newItemId = await SeedSelectionItemAsync(harness, "source-new",
                                                     NekoTitle("简日双语").Replace(".mp4", ".mkv",
                                                                               StringComparison.Ordinal),
                                                     "喵萌奶茶屋", "JpSc", sourceScope : "anibt",
                                                     feedUrl : AnibtFeed);
        await SetSelectionWinnerAsync(harness, newItemId, 12, "anibt");
        await harness.CreateDownloadAsync(newItemId);

        var mikanRow = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(mikanRow);
        Assert.Equal(oldFileId, mikanRow.IncumbentVideoFileId);
        Assert.Null(mikanRow.PendingVideoFileId);
        Assert.Null(await FindSourceEpisodeVersionAsync(harness, 12, "anibt"));
    }

    [Fact]
    public async Task SelectionWinner_LabelSourceMismatch_DoesNotBypassRealSourceScope()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var oldTaskId = await CompleteManualWinnerAsync(harness, "label-old", NekoTitle("简日双语"), "喵萌奶茶屋", "JpSc",
                                                        sourceScope : "anibt", feedUrl : AnibtFeed);
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;

        // 模拟历史展示标签漂移：任务的 mikan 标签与条目真实 SourceScope（anibt）不一致。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var task = await db.DownloadTasks.SingleAsync(value => value.Id == oldTaskId);
            task.SiteSource = "mikan";
            await db.SaveChangesAsync();
        }

        // mikan 优胜不得凭界面标签把 anibt 的已完成任务当作同源旧版替换。
        var newItemId = await SeedSelectionItemAsync(harness, "label-new", NekoTitle("简日双语"), "喵萌奶茶屋", "JpSc",
                                                     length : 2048);
        await SetSelectionWinnerAsync(harness, newItemId);
        await harness.CreateDownloadAsync(newItemId);

        var sourceRow = await FindSourceEpisodeVersionAsync(harness, 12, "anibt");
        Assert.NotNull(sourceRow);
        Assert.Equal(oldFileId, sourceRow.IncumbentVideoFileId);
        Assert.Null(sourceRow.PendingVideoFileId);
        Assert.Null(await FindSourceEpisodeVersionAsync(harness, 12, "mikan"));
    }

    [Fact]
    public async Task SelectionWinner_SameGroupSameLanguageVersionUpgrade_StillTakesOver_AndLowerVersionDoesNot()
    {
        using var harness          = await CreateHarnessAsync();
        var       selectionService = CreateRealSelectionService(harness);
        var       key              = new SelectionGroupKey(42, "SingleEpisode", 12, "mikan");
        await harness.SaveSelectionRulesAsync(NekoRules());

        var v1TaskId = await CompleteSelectionWinnerAsync(harness, selectionService, key, "version-v1",
                                                          NekoTitle("简日双语"), "喵萌奶茶屋", "JpSc");
        var v1FileId = (await FindAnimeFileIdAsync(harness, v1TaskId))!.Value;

        // 同组同语言的更高版本：沿用版本守卫登记待完成优胜（与规则升级路径不冲突）。
        var v2ItemId = await SeedSelectionItemAsync(harness, "version-v2", NekoTitleV2("简日双语"), "喵萌奶茶屋", "JpSc",
                                                    2);
        await selectionService.SelectAndRegisterAsync([key]);
        var v2TaskId = (await GetSelectionAsync(harness)).IntentTaskId!.Value;
        Assert.Equal(v2ItemId, (await GetSelectionAsync(harness)).WinnerFeedItemId);
        var upgraded = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.Equal(v1FileId, upgraded!.IncumbentVideoFileId);
        Assert.Equal(await FindAnimeFileIdAsync(harness, v2TaskId), upgraded.PendingVideoFileId);

        // 反向：已完成 v2 现任在前，被强制选为优胜的 v1 不得凭更旧版本顶替。
        using var reverse = await CreateHarnessAsync();
        await reverse.SaveSelectionRulesAsync(NekoRules());
        var v2IncumbentId = await CompleteManualWinnerAsync(reverse, "reverse-v2", NekoTitleV2("简日双语"), "喵萌奶茶屋",
                                                            "JpSc", 2);
        var v2IncumbentFileId = (await FindAnimeFileIdAsync(reverse, v2IncumbentId))!.Value;
        var v1ItemId          = await SeedSelectionItemAsync(reverse, "reverse-v1", NekoTitle("简日双语"), "喵萌奶茶屋", "JpSc");
        await SetSelectionWinnerAsync(reverse, v1ItemId);
        await reverse.CreateDownloadAsync(v1ItemId);

        var unchanged = await FindSourceEpisodeVersionAsync(reverse, 12, "mikan");
        Assert.NotNull(unchanged);
        Assert.Equal(v2IncumbentFileId, unchanged.IncumbentVideoFileId);
        Assert.Null(unchanged.PendingVideoFileId);
        Assert.Equal(DownloadTaskStatus.Completed, (await reverse.GetTaskAsync(v2IncumbentId)).Status);
    }

    [Fact]
    public async Task Retry_FailedSelectionWinnerTask_RebuildsReplacementAndContinues()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        await harness.SaveSelectionRulesAsync(NekoRules());
        var (oldTaskId, oldFileId, newTaskId, newFileId) =
            await CreateCrossGroupReplacementStateAsync(harness);

        // 模拟修复前创建的存量任务：登记时没有待完成优胜关系，归档阶段被旧成片占用而失败。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var row = await db.EpisodeVersions.SingleAsync(value => value.SubjectId   == 42 &&
                                                                    value.Episode     == 12 &&
                                                                    value.SourceScope == "mikan");
            row.PendingVideoFileId = null;
            var failed = await db.DownloadTasks.SingleAsync(value => value.Id == newTaskId);
            failed.Status     = DownloadTaskStatus.Failed;
            failed.FailedStep = "文件整理";
            failed.Error      = "目标文件路径被下载任务占用。";
            await db.SaveChangesAsync();
        }

        await harness.Service.RetryAsync(newTaskId);

        Assert.Equal(DownloadTaskStatus.PendingSubmit, (await harness.GetTaskAsync(newTaskId)).Status);
        var rebuilt = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(rebuilt);
        Assert.Equal(oldFileId, rebuilt.IncumbentVideoFileId);
        Assert.Equal(newFileId, rebuilt.PendingVideoFileId);

        // 补建的关系立即参与后续链路：暂存下载、完成期迁让旧文件并切换现任。
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskPlacement.TaskRoot("/downloads", newTaskId),
                     (await harness.GetTaskAsync(newTaskId)).SavePath);
        CompleteTorrent(harness, (await harness.GetTaskAsync(newTaskId)).InfoHash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(newTaskId)).Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(oldTaskId)).Status);
        var switched = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.Equal(newFileId, switched!.IncumbentVideoFileId);
        Assert.Equal(oldFileId, switched.SupersededVideoFileId);
    }

    [Fact]
    public async Task Retry_TaskNoLongerSelectionWinner_DoesNotRebuildReplacement()
    {
        using var harness = await CreateHarnessAsync();
        await harness.SaveSelectionRulesAsync(NekoRules());
        var (oldTaskId, oldFileId, newTaskId, _) =
            await CreateCrossGroupReplacementStateAsync(harness);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var row = await db.EpisodeVersions.SingleAsync(value => value.SubjectId   == 42 &&
                                                                    value.Episode     == 12 &&
                                                                    value.SourceScope == "mikan");
            row.PendingVideoFileId = null;
            // 选择记录已改回旧条目：重试的条目不再是当前优胜，不得凭“失败过”补建替换关系。
            var selection = await db.CandidateSelections.SingleAsync(value => value.SubjectId   == 42 &&
                                                                              value.Episode     == 12 &&
                                                                              value.SourceScope == "mikan");
            var oldItemId = (await db.DownloadTasks.AsNoTracking()
                                     .SingleAsync(value => value.Id == oldTaskId)).FeedItemId!.Value;
            selection.WinnerFeedItemId = oldItemId;
            var failed = await db.DownloadTasks.SingleAsync(value => value.Id == newTaskId);
            failed.Status     = DownloadTaskStatus.Failed;
            failed.FailedStep = "文件整理";
            failed.Error      = "目标文件路径被下载任务占用。";
            await db.SaveChangesAsync();
        }

        await harness.Service.RetryAsync(newTaskId);

        Assert.Equal(DownloadTaskStatus.PendingSubmit, (await harness.GetTaskAsync(newTaskId)).Status);
        var unchanged = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(unchanged);
        Assert.Equal(oldFileId, unchanged.IncumbentVideoFileId);
        Assert.Null(unchanged.PendingVideoFileId);
    }

    [Fact]
    public async Task Retry_SharedOldFileReference_StillBlocksAside()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        await harness.SaveSelectionRulesAsync(NekoRules());
        var (oldTaskId, oldFileId, newTaskId, _) =
            await CreateCrossGroupReplacementStateAsync(harness);

        await using (var db = await harness.CreateDbContextAsync())
        {
            var row = await db.EpisodeVersions.SingleAsync(value => value.SubjectId   == 42 &&
                                                                    value.Episode     == 12 &&
                                                                    value.SourceScope == "mikan");
            row.PendingVideoFileId = null;
            var failed = await db.DownloadTasks.SingleAsync(value => value.Id == newTaskId);
            failed.Status     = DownloadTaskStatus.Failed;
            failed.FailedStep = "文件整理";
            failed.Error      = "目标文件路径被下载任务占用。";
            await db.SaveChangesAsync();
        }

        // 旧成片被另一个下载器种子交叉引用：迁让前置检查必须阻断，不得移动共享文件。
        var oldTask = await harness.GetTaskAsync(oldTaskId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var oldFile = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(file => file.Id == oldFileId);
            SeedExternalOccupant(harness, oldTask.SavePath, oldFile.CurrentRelativePath, oldFile.Size);
        }

        await harness.Service.RetryAsync(newTaskId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(newTaskId)).InfoHash);
        await harness.ReconcileAsync();

        var blocked = await harness.GetTaskAsync(newTaskId);
        Assert.Equal(DownloadTaskStatus.Completed, blocked.Status);
        var review = DownloadArchiveReview.FromJson(blocked.ArchiveReview);
        Assert.NotNull(review);
        Assert.Contains("引用", review.Reason, StringComparison.Ordinal);
        Assert.Equal(DownloadTaskPlacement.TaskRoot("/downloads", newTaskId), blocked.SavePath);
        await using (var db = await harness.CreateDbContextAsync())
        {
            var oldFile = await db.DownloadTaskFiles.AsNoTracking().SingleAsync(file => file.Id == oldFileId);
            // 共享引用保护：旧成片仍留在正式位置，未被迁让或覆盖。
            Assert.Equal("测试作品 E12.mp4", oldFile.CurrentRelativePath);
        }

        Assert.DoesNotContain(harness.Store.RenameFileCalls,
                              call => call.Hash == oldTask.InfoHash &&
                                      call.NewPath.StartsWith(".replaced/", StringComparison.Ordinal));
        // 自动清理链恒保留内容：即使旧任务退出，也不得删除被其他种子共享的磁盘文件。
        await harness.ReconcileAsync();
        Assert.DoesNotContain(harness.Store.DeleteCalls,
                              call => call.Hash == oldTask.InfoHash && call.DeleteFiles);
    }

    [Fact]
    public async Task Retry_SubmittedTaskStoppedInFormalRoot_RestoresStagingBeforePreparing()
    {
        using var harness = await CreateHarnessAsync(enableFileArchive : true);
        await harness.SaveFileNamingTemplateAsync("{title} E{episode}");
        await harness.SaveSelectionRulesAsync(NekoRules());
        var (oldTaskId, oldFileId, newTaskId, newFileId) =
            await CreateCrossGroupReplacementStateAsync(harness);

        // 模拟修复前创建的存量任务：登记时没有替换关系，种子已提交到正式根。
        await using (var db = await harness.CreateDbContextAsync())
        {
            var row = await db.EpisodeVersions.SingleAsync(value => value.SubjectId   == 42 &&
                                                                    value.Episode     == 12 &&
                                                                    value.SourceScope == "mikan");
            row.PendingVideoFileId = null;
            await db.SaveChangesAsync();
        }

        await harness.ReconcileAsync(); // 提交：没有替换关系，种子直接添加到正式根。
        var submitted = await harness.GetTaskAsync(newTaskId);
        var hash      = submitted.InfoHash;
        Assert.Equal("/downloads/Media/Anime", submitted.SavePath);
        Assert.Contains(hash, harness.Store.StoppedAdds);
        Assert.Equal("/downloads/Media/Anime", harness.Store.Torrents[hash].SavePath);

        await harness.ReconcileAsync(); // 准备：整理最终命名撞旧成片，任务失败但种子保留。
        var failed = await harness.GetTaskAsync(newTaskId);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal("文件整理", failed.FailedStep);
        Assert.True(harness.Store.Torrents.ContainsKey(hash));
        Assert.Null(failed.PendingSavePath);

        await harness.Service.RetryAsync(newTaskId);
        await harness.ReconcileAsync();

        // 接管已提交种子时先把根迁入任务专属目录，不再在正式根整理。
        var recovered = await harness.GetTaskAsync(newTaskId);
        Assert.Equal(DownloadTaskPlacement.TaskRoot("/downloads", newTaskId), recovered.SavePath);
        Assert.Equal(DownloadTaskPlacement.TaskRoot("/downloads", newTaskId), harness.Store.Torrents[hash].SavePath);
        Assert.Null(recovered.PendingSavePath);
        Assert.NotEqual(DownloadTaskStatus.Failed, recovered.Status);
        var registered = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(registered);
        Assert.Equal(oldFileId, registered.IncumbentVideoFileId);
        Assert.Equal(newFileId, registered.PendingVideoFileId);

        // 既有语义：接管的停止种子按人工暂停呈现；人工恢复后继续准备，完成期迁让旧文件并归档。
        await harness.Service.ResumeAsync(newTaskId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, hash);
        await harness.ReconcileAsync();

        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(newTaskId)).Status);
        Assert.Equal(DownloadTaskStatus.Replaced, (await harness.GetTaskAsync(oldTaskId)).Status);
        var switched = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(switched);
        Assert.Equal(newFileId, switched.IncumbentVideoFileId);
        Assert.Equal(oldFileId, switched.SupersededVideoFileId);
    }

    [Fact]
    public async Task ManualCreate_WinnerSwitchedWhileTorrentDownloads_RejectsStaleWinner()
    {
        using var harness          = await CreateHarnessAsync();
        var       selectionService = CreateRealSelectionService(harness);
        var       key              = new SelectionGroupKey(42, "SingleEpisode", 12, "mikan");
        await harness.SaveSelectionRulesAsync(NekoRules());

        // 先完成一个 ANi／JpTc 现任，让“过期优胜”确实有可替换对象。
        var oldTaskId = await CompleteSelectionWinnerAsync(harness, selectionService, key, "stale-incumbent",
                                                           AniTcTitle(), "ANi", "JpTc");
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;

        var staleItemId = await SeedSelectionItemAsync(harness, "stale-winner", NekoTitle("简日双语"),
                                                       "喵萌奶茶屋", "JpSc");
        var switchedItemId = await SeedSelectionItemAsync(harness, "switched-winner", ComicatJpScTitle(),
                                                          "漫猫字幕组", "JpSc");
        await SetSelectionWinnerAsync(harness, staleItemId);

        // 预检通过并开始下载种子后、进入协调锁之前，自动择优把同集组优胜切到另一条目。
        var switched = false;
        harness.TorrentSource.BeforeDownload = async _ =>
        {
            switched = true;
            await SetSelectionWinnerAsync(harness, switchedItemId);
        };

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateDownloadAsync(staleItemId));
        Assert.True(switched);
        Assert.Contains("优胜", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await CountTasksAsync(harness));
        var unchanged = await FindSourceEpisodeVersionAsync(harness, 12, "mikan");
        Assert.NotNull(unchanged);
        Assert.Equal(oldFileId, unchanged.IncumbentVideoFileId);
        Assert.Null(unchanged.PendingVideoFileId);
    }

    /// <summary>完整替换链的前置状态：旧 ANi／Tc 任务已完成，新喵萌／JpSc 优胜任务已登记关系。</summary>
    private static async Task<(Guid OldTaskId, Guid OldFileId, Guid NewTaskId, Guid NewFileId)>
        CreateCrossGroupReplacementStateAsync(TestHarness harness)
    {
        var selectionService = CreateRealSelectionService(harness);
        var key              = new SelectionGroupKey(42, "SingleEpisode", 12, "mikan");
        var oldTaskId = await CompleteSelectionWinnerAsync(harness, selectionService, key, "retry-old", AniTcTitle(),
                                                           "ANi", "JpTc");
        var oldFileId = (await FindAnimeFileIdAsync(harness, oldTaskId))!.Value;
        var newItemId = await SeedSelectionItemAsync(harness, "retry-new", NekoTitle("简日双语"), "喵萌奶茶屋", "JpSc",
                                                     length : 2048);
        await selectionService.SelectAndRegisterAsync([key]);
        var newTaskId = (await GetSelectionAsync(harness)).IntentTaskId!.Value;
        var newFileId = (await FindAnimeFileIdAsync(harness, newTaskId))!.Value;
        return (oldTaskId, oldFileId, newTaskId, newFileId);
    }

    /// <summary>经真实订阅择优登记并完成一个第 12 集任务，返回任务 Id。</summary>
    private static async Task<Guid> CompleteSelectionWinnerAsync(
        TestHarness harness,     CandidateSelectionService selectionService, SelectionGroupKey key, string stableId,
        string      torrentName, string                    group,            string            language)
    {
        await SeedSelectionItemAsync(harness, stableId, torrentName, group, language);
        await selectionService.SelectAndRegisterAsync([key]);
        var taskId = (await GetSelectionAsync(harness)).IntentTaskId!.Value;
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(taskId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        return taskId;
    }

    /// <summary>经手动创建前置核验登记并完成一个第 12 集任务：先强制该条目为同集组优胜。</summary>
    private static async Task<Guid> CompleteManualWinnerAsync(
        TestHarness harness, string stableId, string torrentName, string group, string language, int version = 1,
        string      sourceScope = "mikan", string feedUrl = MikanFeed)
    {
        var itemId = await SeedSelectionItemAsync(harness, stableId, torrentName, group, language, version,
                                                  12, sourceScope, feedUrl);
        await SetSelectionWinnerAsync(harness, itemId, 12, sourceScope);
        var taskId = await harness.CreateDownloadAsync(itemId);
        await harness.ReconcileAsync();
        CompleteTorrent(harness, (await harness.GetTaskAsync(taskId)).InfoHash);
        await harness.ReconcileAsync();
        Assert.Equal(DownloadTaskStatus.Completed, (await harness.GetTaskAsync(taskId)).Status);
        return taskId;
    }

    /// <summary>播种一条真实的订阅条目：字段、命名要素与种子名同源，可由选择服务真实择优并登记任务。</summary>
    private static async Task<Guid> SeedSelectionItemAsync(
        TestHarness harness,      string stableId, string torrentName, string group, string language, int version = 1,
        decimal     episode = 12, string sourceScope = "mikan", string feedUrl = MikanFeed, int length = 1024)
    {
        harness.TorrentNames[stableId] = torrentName;
        if (length != 1024)
            // 只有长度与旧成片不同，才不会被提交前的复用候选发现（复用校验不是替换链）。
            harness.TorrentBytes[stableId] =
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(TestTorrentBytes.Create(torrentName))
                                               .Replace("6:lengthi1024e", $"6:lengthi{length}e",
                                                        StringComparison.Ordinal));
        var parsed = new AniParser().Parse(torrentName)
                  ?? throw new InvalidOperationException($"测试发布名无法解析：{torrentName}");
        var ingredients = new TorrentNamingIngredients(
                                                       "{title} E{episode}", "测试作品",
                                                       ReleaseNamePreviewPolicy.FormatEpisodeText(episode), version,
                                                       group, TorrentSiteSource.FromFeedUrl(feedUrl), language,
                                                       "R1080P", "{title} E{episode}", BangumiId : 42,
                                                       BangumiName : "Test Subject", BangumiNameCn : "测试作品",
                                                       BangumiPlatform : "TV");
        var protector = harness.Provider.GetRequiredService<IDataProtectionProvider>()
                               .CreateProtector("Banori.MediaSubscriptions.FeedUrl.v1");
        await using var db             = await harness.CreateDbContextAsync();
        var             subscriptionId = Guid.NewGuid();
        db.MediaSubscriptions.Add(new MediaSubscription
        {
            Id               = subscriptionId, Name                  = $"订阅-{stableId}",
            FeedUrlProtected = protector.Protect(feedUrl), IsEnabled = false, RefreshIntervalMinutes = 30
        });
        var itemId = Guid.NewGuid();
        db.FeedItems.Add(new FeedItem
        {
            Id                = itemId, SubscriptionId = subscriptionId, StableId = stableId, Title = torrentName,
            TorrentUrl        = $"https://mikanani.me/Download/{stableId}.torrent",
            FirstSeenAtUtc    = harness.Timestamp.AddHours(-1), Status = FeedItemStatus.Accepted,
            DecisionStatus    = ReleaseDecisionStatus.Accepted, ParsedTitle = parsed.Title,
            MediaType         = "SingleEpisode", Episode = episode, Version = version,
            Group             = group, Language = language, Resolution = "R1080P", SubjectId = 42,
            Preference        = group, SourceScope = sourceScope,
            Reason            = "测试决策。", NamingPreview = new ReleaseNamePreviewPolicy().Create(ingredients),
            NamingIngredients = ingredients.ToJson(), EvaluatedAtUtc = harness.Timestamp
        });
        await db.SaveChangesAsync();
        return itemId;
    }

    /// <summary>把指定条目写成其同集组的当前优胜（用于构造“非优胜不得替换”的负向守卫场景）。</summary>
    private static async Task SetSelectionWinnerAsync(TestHarness harness, Guid itemId, decimal episode = 12,
                                                      string      sourceScope = "mikan")
    {
        await using var db = await harness.CreateDbContextAsync();
        var selection = await db.CandidateSelections.SingleOrDefaultAsync(value => value.SubjectId == 42 &&
                                                                              value.MediaType ==
                                                                              "SingleEpisode"          &&
                                                                              value.Episode == episode &&
                                                                              value.SourceScope ==
                                                                              sourceScope);
        if (selection is null)
            db.CandidateSelections.Add(new CandidateSelection
            {
                Id           = Guid.NewGuid(), SubjectId     = 42, MediaType = "SingleEpisode", Episode = episode,
                SourceScope  = sourceScope, WinnerFeedItemId = itemId, RuleVersion = 1,
                RankingBasis = "测试优胜", NamingSnapshot        = "测试作品 E12", SelectedAtUtc = harness.Timestamp
            });
        else
            selection.WinnerFeedItemId = itemId;
        await db.SaveChangesAsync();
    }

    private static async Task<CandidateSelection> GetSelectionAsync(TestHarness harness, decimal episode = 12,
                                                                    string      sourceScope = "mikan")
    {
        await using var db = await harness.CreateDbContextAsync();
        return await db.CandidateSelections.AsNoTracking()
                       .SingleAsync(value => value.SubjectId   == 42              &&
                                             value.MediaType   == "SingleEpisode" &&
                                             value.Episode     == episode         &&
                                             value.SourceScope == sourceScope);
    }

    /// <summary>用 harness 的真实规则、下载器与登记服务构造订阅择优服务，走与生产一致的登记链。</summary>
    private static CandidateSelectionService CreateRealSelectionService(TestHarness harness)
    {
        var provider = harness.Provider;
        return new CandidateSelectionService(
                                             provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
                                             provider.GetRequiredService<ISelectionRuleProvider>(),
                                             new SelectionGroupGate(),
                                             provider.GetRequiredService<DownloadCoordinationGate>(),
                                             provider.GetRequiredService<CandidateEligibilityPolicy>(),
                                             provider.GetRequiredService<CandidateRankingPolicy>(),
                                             provider.GetRequiredService<ManageQbittorrentSettings>(),
                                             new TestHostApplicationLifetime(),
                                             provider.GetRequiredService<DownloadIntakeService>(),
                                             provider.GetRequiredService<TimeProvider>(),
                                             NullLogger<CandidateSelectionService>.Instance);
    }

    /// <summary>在下载器中播种一个引用旧成片路径的外部种子，构造“共享文件受保护”的占用证据。</summary>
    private static void SeedExternalOccupant(TestHarness harness, string taskSavePath, string relativePath, long size)
    {
        var slash  = relativePath.LastIndexOf('/');
        var parent = slash < 0 ? taskSavePath : $"{taskSavePath}/{relativePath[..slash]}";
        var name   = slash < 0 ? relativePath : relativePath[(slash + 1)..];
        var hash   = new string('e', 40);
        harness.Store.AddTorrent(hash, "External Seed", QbittorrentTorrentCategory.Seeding, 1.0, parent);
        harness.Store.FileIdentities[hash] = [new TorrentFileSnapshot(0, name, size, 1.0)];
    }

    private static string AniTcTitle(int episode = 12) =>
        $"[ANi] 测试作品 - {episode:00} [1080P][Baha][WEB-DL][AAC AVC][CHT].mp4";

    private static string ComicatJpScTitle() => "[Comicat][Test Anime][12][1080p][简日双语][MP4].mp4";

    private static string NekoTitleV2(string language) =>
        NekoTitle(language).Replace("[12]", "[12v2]", StringComparison.Ordinal);
}
