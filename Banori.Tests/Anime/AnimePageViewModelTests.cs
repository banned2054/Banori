using Banori.Components.Pages.Anime;
using Banori.Features.Anime;

namespace Banori.Tests.Anime;

public sealed class AnimePageViewModelTests
{
    private static AnimePageViewModel CreateVm() => new(null!);

    [Fact]
    public void SetSort_DefaultsToNameAscending()
    {
        var vm = CreateVm();

        Assert.Equal(AnimeSortField.Name, vm.SortField);
        Assert.False(vm.Descending);
        Assert.Empty(vm.SortedAnimes);
    }

    [Fact]
    public void SetSort_NewFieldStartsAscending_SameFieldFlips()
    {
        var vm = CreateVm();

        vm.SetSort(AnimeSortField.LastAdded);
        Assert.Equal(AnimeSortField.LastAdded, vm.SortField);
        Assert.False(vm.Descending);

        vm.SetSort(AnimeSortField.LastAdded);
        Assert.True(vm.Descending);

        vm.SetSort(AnimeSortField.AirDate);
        Assert.Equal(AnimeSortField.AirDate, vm.SortField);
        Assert.False(vm.Descending);

        vm.SetSort(AnimeSortField.Name);
        Assert.Equal(AnimeSortField.Name, vm.SortField);
        Assert.False(vm.Descending);
    }
}
