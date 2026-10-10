using MediaManager.Features.Anime;

namespace MediaManager.Components.Pages.Anime;

/// <summary>动画总览页状态：封面墙数据与当前展开的 Subject。随页面组件创建和释放。</summary>
public sealed class AnimePageViewModel(AnimeQueries queries)
{
    private readonly List<AnimeCard> _animes = [];

    public IReadOnlyList<AnimeCard> Animes            => _animes;
    public bool                     Loaded            { get; private set; }
    public int?                     ExpandedSubjectId { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _animes.Clear();
        _animes.AddRange(await queries.ListAsync(cancellationToken));
        Loaded = true;
    }

    public void Toggle(int subjectId)
    {
        ExpandedSubjectId = ExpandedSubjectId == subjectId ? null : subjectId;
    }
}
