using Banori.Components.Pages.Anime;
using Banori.Data;
using Banori.Features.Anime;
using Banori.Tests.Downloads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Banori.Tests.Anime;

public sealed class AnimePageViewModelTests
{
    private static (AnimePageViewModel Vm, MemoryApplicationSettingStore Settings) CreateVm() =>
        CreateVm(new MemoryApplicationSettingStore());

    private static (AnimePageViewModel Vm, MemoryApplicationSettingStore Settings) CreateVm(
        MemoryApplicationSettingStore settings) =>
        (new AnimePageViewModel(null!, new AnimeSortStore(settings),
                                NullLogger<AnimePageViewModel>.Instance), settings);

    [Fact]
    public async Task SetSort_DefaultsToNameAscending()
    {
        var (vm, _) = CreateVm();

        Assert.Equal(AnimeSortField.Name, vm.SortField);
        Assert.False(vm.Descending);
        Assert.Empty(vm.SortedAnimes);
    }

    [Fact]
    public async Task SetSort_NewFieldStartsAscending_SameFieldFlips()
    {
        var (vm, _) = CreateVm();

        await vm.SetSortAsync(AnimeSortField.LastAdded);
        Assert.Equal(AnimeSortField.LastAdded, vm.SortField);
        Assert.False(vm.Descending);

        await vm.SetSortAsync(AnimeSortField.LastAdded);
        Assert.True(vm.Descending);

        await vm.SetSortAsync(AnimeSortField.AirDate);
        Assert.Equal(AnimeSortField.AirDate, vm.SortField);
        Assert.False(vm.Descending);

        await vm.SetSortAsync(AnimeSortField.Name);
        Assert.Equal(AnimeSortField.Name, vm.SortField);
        Assert.False(vm.Descending);
    }

    [Fact]
    public async Task Sort_PersistsAcrossPageReload()
    {
        using var harness = AnimeQueryHarness.Create();
        var settings    = new MemoryApplicationSettingStore();
        var first        = new AnimePageViewModel(harness.Queries, new AnimeSortStore(settings),
                                                  NullLogger<AnimePageViewModel>.Instance);
        await first.LoadAsync();
        await first.SetSortAsync(AnimeSortField.AirDate);
        await first.SetSortAsync(AnimeSortField.AirDate);

        var reloaded = new AnimePageViewModel(harness.Queries, new AnimeSortStore(settings),
                                              NullLogger<AnimePageViewModel>.Instance);
        await reloaded.LoadAsync();

        Assert.Multiple(() => Assert.Equal(AnimeSortField.AirDate, reloaded.SortField),
                        () => Assert.True(reloaded.Descending));
    }

    [Fact]
    public async Task LoadAsync_StoreFailureKeepsDefaultSort()
    {
        using var harness = AnimeQueryHarness.Create();
        var settings = new MemoryApplicationSettingStore
        {
            ThrowOnGet = new InvalidOperationException("模拟设置存储故障。")
        };
        var vm = new AnimePageViewModel(harness.Queries, new AnimeSortStore(settings),
                                        NullLogger<AnimePageViewModel>.Instance);

        await vm.LoadAsync();

        Assert.Multiple(() => Assert.Equal(AnimeSortField.Name, vm.SortField),
                        () => Assert.False(vm.Descending),
                        () => Assert.True(vm.Loaded));
    }

    [Fact]
    public async Task SetSort_StoreFailureKeepsCurrentSelection()
    {
        var (vm, settings) = CreateVm();
        settings.ThrowOnSet = new InvalidOperationException("模拟设置存储故障。");

        await vm.SetSortAsync(AnimeSortField.FirstAdded);

        Assert.Multiple(() => Assert.Equal(AnimeSortField.FirstAdded, vm.SortField),
                        () => Assert.False(vm.Descending));
    }

    [Fact]
    public async Task SetSortWhileStoredSortInFlight_KeepsUserSelection()
    {
        using var harness = AnimeQueryHarness.Create();
        var settings = new MemoryApplicationSettingStore();
        await new AnimeSortStore(settings).SaveAsync(AnimeSortField.LastAdded, true);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.GetGate = release.Task;
        var vm = new AnimePageViewModel(harness.Queries, new AnimeSortStore(settings),
                                        NullLogger<AnimePageViewModel>.Instance);

        var load = vm.LoadAsync();
        await vm.SetSortAsync(AnimeSortField.AirDate);
        release.SetResult();
        await load;

        Assert.Multiple(() => Assert.Equal(AnimeSortField.AirDate, vm.SortField),
                        () => Assert.False(vm.Descending));
    }

    [Fact]
    public async Task ConcurrentLoadsWhileStoredSortInFlight_UseRestoredSort()
    {
        using var harness = AnimeQueryHarness.Create();
        var settings = new MemoryApplicationSettingStore();
        await new AnimeSortStore(settings).SaveAsync(AnimeSortField.LastAdded, false);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.GetGate = release.Task;
        var vm = new AnimePageViewModel(harness.Queries, new AnimeSortStore(settings),
                                        NullLogger<AnimePageViewModel>.Instance);

        var firstLoad  = vm.LoadAsync();
        var secondLoad = vm.LoadAsync();
        release.SetResult();
        await Task.WhenAll(firstLoad, secondLoad);

        Assert.Multiple(() => Assert.Equal(AnimeSortField.LastAdded, vm.SortField),
                        () => Assert.False(vm.Descending));
    }

    [Fact]
    public async Task ConsecutiveSortChanges_WriteSelectionsInOrder()
    {
        using var harness = AnimeQueryHarness.Create();
        var settings = new MemoryApplicationSettingStore();
        var vm = new AnimePageViewModel(harness.Queries, new AnimeSortStore(settings),
                                        NullLogger<AnimePageViewModel>.Instance);
        await vm.LoadAsync();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.SetGates[AnimeSortStore.SortKey] = (1, release.Task);

        var first  = vm.SetSortAsync(AnimeSortField.AirDate);
        var second = vm.SetSortAsync(AnimeSortField.LastAdded);
        release.SetResult();
        await Task.WhenAll(first, second);

        var writes = settings.SetValues.Where(entry => entry.Key == AnimeSortStore.SortKey).ToArray();
        var final  = await new AnimeSortStore(settings).GetAsync();

        Assert.Multiple(() => Assert.Equal(2, writes.Length),
                        () => Assert.Equal(AnimeSortField.LastAdded, final.Field));
    }

    /// <summary>只提供加载所需的空数据库查询；排序恢复本身由内存设置替身控制。</summary>
    private sealed class AnimeQueryHarness : IDisposable
    {
        private readonly DirectoryInfo   _directory;
        private readonly ServiceProvider _provider;

        private AnimeQueryHarness(DirectoryInfo directory, ServiceProvider provider, AnimeQueries queries)
        {
            _directory = directory;
            _provider  = provider;
            Queries    = queries;
        }

        public AnimeQueries Queries { get; }

        public void Dispose()
        {
            _provider.Dispose();
            _directory.Delete(true);
        }

        public static AnimeQueryHarness Create()
        {
            var directory = Directory.CreateTempSubdirectory("Banori.AnimeVm.Tests.");
            var services  = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                         options.UseSqlite($"Data Source={Path.Combine(directory.FullName, "anime.db")};Pooling=False"));
            services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion =
                                                          IdentitySchemaVersions.Version3)
                    .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();
            var factory  = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            using (var db = factory.CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            return new AnimeQueryHarness(directory, provider, new AnimeQueries(factory));
        }
    }
}
