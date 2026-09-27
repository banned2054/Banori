using MediaManager.Features.FeedItems;
using MediaManager.Features.Subscriptions;

namespace MediaManager.Components.Pages.FeedItems;

/// <summary>条目列表页状态：当前筛选与列表数据。随页面组件创建和释放。</summary>
public sealed class FeedItemsPageViewModel(FeedItemQueries queries)
{
    private readonly List<FeedItemView> _items = [];

    public IReadOnlyList<FeedItemView> Items  => _items;
    public FeedItemStatus?             Filter { get; private set; }
    public bool                        Loaded { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _items.Clear();
        _items.AddRange(await queries.ListAsync(Filter, cancellationToken : cancellationToken));
        Loaded = true;
    }

    public async Task SetFilterAsync(FeedItemStatus? filter, CancellationToken cancellationToken = default)
    {
        Filter = filter;
        await LoadAsync(cancellationToken);
    }

    public bool IsSelected(FeedItemStatus? status)
    {
        return Filter == status;
    }
}
