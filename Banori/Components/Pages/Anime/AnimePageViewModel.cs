using Banori.Features.Anime;

namespace Banori.Components.Pages.Anime;

/// <summary>动画总览页状态：封面墙数据、排序选择与当前展开的 Subject。随页面组件创建和释放。</summary>
public sealed class AnimePageViewModel(AnimeQueries queries)
{
    private readonly List<AnimeCard> _animes = [];

    public IReadOnlyList<AnimeCard> Animes            => _animes;
    public IReadOnlyList<AnimeCard> SortedAnimes      =>
        AnimeCardSort.Apply(_animes, SortField, Descending);
    public AnimeSortField           SortField         { get; private set; } = AnimeSortField.Name;
    public bool                     Descending        { get; private set; }
    public bool                     Loaded            { get; private set; }
    public int?                     ExpandedSubjectId { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _animes.Clear();
        _animes.AddRange(await queries.ListAsync(cancellationToken));
        Loaded = true;
    }

    public void SetSort(AnimeSortField field)
    {
        if (field == SortField) Descending = !Descending;
        else { SortField = field; Descending = false; }
    }

    public void Toggle(int subjectId)
    {
        ExpandedSubjectId = ExpandedSubjectId == subjectId ? null : subjectId;
    }
}
