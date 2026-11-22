using Banori.Features.Anime;

namespace Banori.Components.Pages.Anime;

/// <summary>动画总览页状态：封面墙数据、排序选择与当前展开的 Subject。随页面组件创建和释放。</summary>
public sealed class AnimePageViewModel(
    AnimeQueries                queries,
    AnimeSortStore              sortStore,
    ILogger<AnimePageViewModel> logger)
{
    private readonly List<AnimeCard> _animes = [];

    // 已保存的排序只在页面首次加载时读取，之后由用户切换维护；并发加载共用同一次读取。
    private Task? _sortLoadTask;

    // 用户在读取已保存排序期间切换过排序：读取结果作废，不能把新选择覆盖成旧值。
    private bool _sortTouched;

    // 连续切换串行写入同一设置项：保存链条的尾部保证较早的选择不会晚于较新的选择落库。
    private Task _sortSave = Task.CompletedTask;

    public IReadOnlyList<AnimeCard> Animes       => _animes;
    public IReadOnlyList<AnimeCard> SortedAnimes =>
        AnimeCardSort.Apply(_animes, SortField, Descending);
    public AnimeSortField           SortField         { get; private set; } = AnimeSortField.Name;
    public bool                     Descending        { get; private set; }
    public bool                     Loaded            { get; private set; }
    public int?                     ExpandedSubjectId { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSortLoadedAsync(cancellationToken);
        _animes.Clear();
        _animes.AddRange(await queries.ListAsync(cancellationToken));
        Loaded = true;
    }

    /// <summary>页面生命周期内只恢复一次已保存的排序；用户已切换时不必等待读取结果。</summary>
    private async Task EnsureSortLoadedAsync(CancellationToken cancellationToken)
    {
        _sortLoadTask ??= LoadSortAsync();
        if (_sortTouched) return;
        await _sortLoadTask.WaitAsync(cancellationToken);
    }

    /// <summary>读取已保存排序并恢复；读取本身不随某次页面加载取消，结果供所有等待者共用。</summary>
    private async Task LoadSortAsync()
    {
        try
        {
            var stored = await sortStore.GetAsync();
            if (_sortTouched) return;
            SortField  = stored.Field;
            Descending = stored.Descending;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "读取动画页排序设置失败，按名称升序显示。");
        }
    }

    /// <summary>切换排序：同字段翻转方向，换字段从升序开始；结果写入设置供刷新后恢复。</summary>
    public async Task SetSortAsync(AnimeSortField field)
    {
        if (field == SortField) Descending = !Descending;
        else { SortField = field; Descending = false; }
        _sortTouched = true;
        await SaveSortAsync();
    }

    /// <summary>保存当前排序供刷新后恢复；按调用顺序串行写入，失败只记录日志，当前页面仍按新排序显示。</summary>
    private async Task SaveSortAsync()
    {
        // 先快照当前选择：串行化的是写入次数与顺序，后一次保存必须仍是调用时的排序。
        var field      = SortField;
        var descending = Descending;
        var save       = SaveSortCoreAsync(_sortSave, field, descending);
        _sortSave      = save;
        await save;
    }

    private async Task SaveSortCoreAsync(Task previousSave, AnimeSortField field, bool descending)
    {
        await previousSave;
        try
        {
            await sortStore.SaveAsync(field, descending);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "保存动画页排序设置失败，本次排序仅在当前页面生效。");
        }
    }

    public void Toggle(int subjectId)
    {
        ExpandedSubjectId = ExpandedSubjectId == subjectId ? null : subjectId;
    }
}
