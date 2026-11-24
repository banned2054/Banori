using Banori.Components.Pages.Settings;
using Banori.Core.Torrents;
using Banori.Features.Downloads;
using Banori.Features.Notifications;
using Banori.Features.Settings;
using Banori.Integrations.Qbittorrent;
using Banori.Tests.Notifications;
using Banori.Tests.Proxies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Downloads;

public sealed partial class DownloadServiceTests
{
    [Fact]
    public async Task NotifyTorrentNamingChanged_SingleTemplateChange_UpdatesOnlySingleTasksAndIgnoresLegacyTemplate()
    {
        using var harness   = await CreateHarnessAsync();
        var       templates = harness.Provider.GetRequiredService<NamingTemplateStore>();
        await templates.SaveAsync("旧单集 {title} E{episode}");
        await templates.SaveCollectionTemplateAsync("旧合集 {title} {episode:Eee-Eee}");

        var singleCompletedId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")));
        var singlePendingId = await harness.CreateUploadAsync(
            TestTorrentBytes.Create(CollectionVideo("08")), subjectId : 42);
        var singleLostId = await harness.CreateUploadAsync(
            TestTorrentBytes.Create(CollectionVideo("09")), subjectId : 42);
        var collectionBytes = CreateMultiFileTorrent(CollectionTitle,
                                                     (CollectionVideo("01"), 1024),
                                                     (CollectionVideo("02"), 1024));
        var collectionId = await harness.CreateUploadAsync(collectionBytes);

        // 先让单集完成任务和合集任务进入下载器（Completed），保留另一个单集为 PendingSubmit、另一个为 Lost。
        var    completedTask  = await harness.GetTaskAsync(singleCompletedId);
        var    collectionTask = await harness.GetTaskAsync(collectionId);
        string legacyIngredientsJson;
        await using (var db = await harness.CreateDbContextAsync())
        {
            var completed = await db.DownloadTasks.SingleAsync(value => value.Id == singleCompletedId);
            completed.Status = DownloadTaskStatus.Completed;
            // 模拟旧数据库行里残留的历史 Template 快照：通知改名时必须忽略它，且不得回写 NamingIngredients。
            legacyIngredientsJson = (TorrentNamingIngredients.FromJson(completed.NamingIngredients)! with
            {
                Template = "非常旧的快照模板 {title}"
            }).ToJson();
            completed.NamingIngredients = legacyIngredientsJson;

            var collection = await db.DownloadTasks.SingleAsync(value => value.Id == collectionId);
            collection.Status = DownloadTaskStatus.Completed;

            var lost = await db.DownloadTasks.SingleAsync(value => value.Id == singleLostId);
            lost.Status = DownloadTaskStatus.Lost;

            await db.SaveChangesAsync();
        }

        harness.Store.Torrents[completedTask.InfoHash] = new TorrentSnapshot(
            completedTask.InfoHash, "旧单集 测试作品 E12", QbittorrentTorrentCategory.Seeding, 1, 1024, "/downloads", []);
        harness.Store.Torrents[collectionTask.InfoHash] = new TorrentSnapshot(
            collectionTask.InfoHash, "旧合集 测试作品 E01-E02", QbittorrentTorrentCategory.Seeding, 1, 2048, "/downloads",
            []);

        // 仅修改单集模板并通知。
        await templates.SaveAsync("新单集 {title} E{episode}");
        var affected = await harness.Service.NotifyTorrentNamingChangedAsync(true, false);
        Assert.Equal(2, affected);

        var updatedCompleted = await harness.GetTaskAsync(singleCompletedId);
        Assert.Equal("新单集 测试作品 E12", updatedCompleted.NamingSnapshot);
        Assert.True(updatedCompleted.TorrentRenamePending);
        Assert.Equal(legacyIngredientsJson, updatedCompleted.NamingIngredients);

        var updatedPending = await harness.GetTaskAsync(singlePendingId);
        Assert.Equal("新单集 测试作品 E08", updatedPending.NamingSnapshot);
        Assert.False(updatedPending.TorrentRenamePending);

        var untouchedLost = await harness.GetTaskAsync(singleLostId);
        Assert.Equal("旧单集 测试作品 E09", untouchedLost.NamingSnapshot);
        Assert.False(untouchedLost.TorrentRenamePending);

        var untouchedCollection = await harness.GetTaskAsync(collectionId);
        Assert.Equal("旧合集 测试作品 E01-E02", untouchedCollection.NamingSnapshot);
        Assert.False(untouchedCollection.TorrentRenamePending);

        // 对账推送改名到 qBittorrent，并清除待改名标记。
        await harness.ReconcileAsync();
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(completedTask.InfoHash, rename.Hash);
        Assert.Equal("新单集 测试作品 E12", rename.Name);
        Assert.False((await harness.GetTaskAsync(singleCompletedId)).TorrentRenamePending);
    }

    [Fact]
    public async Task NotifyTorrentNamingChanged_CollectionTemplateChange_UpdatesOnlyCollectionTasks()
    {
        using var harness   = await CreateHarnessAsync();
        var       templates = harness.Provider.GetRequiredService<NamingTemplateStore>();
        await templates.SaveAsync("单集 {title} E{episode}");
        await templates.SaveCollectionTemplateAsync("旧合集 {title} {episode:Eee-Eee}");

        var singleId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")));
        var collectionBytes = CreateMultiFileTorrent(CollectionTitle,
                                                     (CollectionVideo("01"), 1024),
                                                     (CollectionVideo("02"), 1024));
        var collectionId = await harness.CreateUploadAsync(collectionBytes);

        var singleTask     = await harness.GetTaskAsync(singleId);
        var collectionTask = await harness.GetTaskAsync(collectionId);
        await using (var db = await harness.CreateDbContextAsync())
        {
            (await db.DownloadTasks.SingleAsync(value => value.Id == singleId)).Status     = DownloadTaskStatus.Completed;
            (await db.DownloadTasks.SingleAsync(value => value.Id == collectionId)).Status = DownloadTaskStatus.Completed;
            await db.SaveChangesAsync();
        }

        harness.Store.Torrents[singleTask.InfoHash] = new TorrentSnapshot(
            singleTask.InfoHash, "单集 测试作品 E12", QbittorrentTorrentCategory.Seeding, 1, 1024, "/downloads", []);
        harness.Store.Torrents[collectionTask.InfoHash] = new TorrentSnapshot(
            collectionTask.InfoHash, "旧合集 测试作品 E01-E02", QbittorrentTorrentCategory.Seeding, 1, 2048, "/downloads",
            []);

        await templates.SaveCollectionTemplateAsync("新合集 {title} {episode:ee-ee}");
        var affected = await harness.Service.NotifyTorrentNamingChangedAsync(false, true);
        Assert.Equal(1, affected);

        Assert.Equal("单集 测试作品 E12", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.False((await harness.GetTaskAsync(singleId)).TorrentRenamePending);
        Assert.Equal("新合集 测试作品 01-02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);
        Assert.True((await harness.GetTaskAsync(collectionId)).TorrentRenamePending);

        await harness.ReconcileAsync();
        var rename = Assert.Single(harness.Store.RenameCalls);
        Assert.Equal(collectionTask.InfoHash, rename.Hash);
        Assert.Equal("新合集 测试作品 01-02", rename.Name);
        Assert.False((await harness.GetTaskAsync(collectionId)).TorrentRenamePending);
    }

    [Fact]
    public async Task SettingsPageViewModel_SaveNaming_PropagatesOnlyChangedTemplatesAndHandlesInheritedCollection()
    {
        using var harness   = await CreateHarnessAsync();
        var       templates = harness.Provider.GetRequiredService<NamingTemplateStore>();
        await templates.SaveAsync("初始 {title} E{episode}");

        var singleId = await harness.CreateUploadAsync(TestTorrentBytes.Create(NekoTitle("简日双语")));
        var collectionBytes = CreateMultiFileTorrent(CollectionTitle,
                                                     (CollectionVideo("01"), 1024),
                                                     (CollectionVideo("02"), 1024));
        var collectionId = await harness.CreateUploadAsync(collectionBytes);
        Assert.Equal("初始 测试作品 E12", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.Equal("初始 测试作品 E01-E02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);

        var settings    = harness.Provider.GetRequiredService<IApplicationSettingStore>();
        var directories = harness.Provider.GetRequiredService<DownloadDirectorySettingsStore>();
        var vm = new SettingsPageViewModel(
            harness.Provider.GetRequiredService<ManageQbittorrentSettings>(),
            templates,
            directories,
            new ManageTelegramSettings(settings, new FakeTelegramSender()),
            new ManageGotifySettings(settings, new FakeGotifySender()),
            ProxyServiceTestFactory.CreateEphemeral(),
            NullLogger<SettingsPageViewModel>.Instance,
            harness.Service);
        vm.Naming.NamingTemplate           = "初始 {title} E{episode}";
        vm.Naming.FileNamingTemplate       = await templates.GetFileTemplateAsync();
        vm.Naming.CollectionNamingTemplate = await templates.GetConfiguredCollectionTemplateAsync();
        var currentDirectories = await directories.GetAsync();
        vm.Naming.MediaDirectory = currentDirectories.MediaDirectory;
        vm.Naming.SeedDirectory  = currentDirectories.SeedDirectory;

        // 1. 仅修改文件模板时，不触发任何种子名更新。
        vm.Naming.FileNamingTemplate = "{title} - {episode}";
        await vm.SaveNamingAsync();
        Assert.True(vm.SaveSucceeded);
        Assert.Equal("初始 测试作品 E12", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.Equal("初始 测试作品 E01-E02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);

        // 2. 合集模板留空（沿用单集模板）时，修改单集模板会同时更新单集和合集任务。
        vm.Naming.NamingTemplate = "公共 {title} E{episode}";
        await vm.SaveNamingAsync();
        Assert.True(vm.SaveSucceeded);
        Assert.Equal("公共 测试作品 E12", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.Equal("公共 测试作品 E01-E02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);

        // 3. 为合集配置独立模板时，仅更新合集任务，不动单集任务。
        vm.Naming.CollectionNamingTemplate = "独立合集 {title} {episode:ee-ee}";
        await vm.SaveNamingAsync();
        Assert.True(vm.SaveSucceeded);
        Assert.Equal("公共 测试作品 E12", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.Equal("独立合集 测试作品 01-02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);

        // 4. 合集已有独立模板后，再改单集模板仅更新单集任务，不动合集任务。
        vm.Naming.NamingTemplate = "单集专用 {title} E{episode}";
        await vm.SaveNamingAsync();
        Assert.True(vm.SaveSucceeded);
        Assert.Equal("单集专用 测试作品 E12", (await harness.GetTaskAsync(singleId)).NamingSnapshot);
        Assert.Equal("独立合集 测试作品 01-02", (await harness.GetTaskAsync(collectionId)).NamingSnapshot);
    }
}
