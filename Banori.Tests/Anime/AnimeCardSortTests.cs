using Banori.Features.Anime;

namespace Banori.Tests.Anime;

public sealed class AnimeCardSortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static AnimeCard Card(int id, string name, string? airDate = null,
                                  DateTimeOffset? first = null, DateTimeOffset? last = null) =>
        new(id, name, null, [], airDate, first, last);

    [Fact]
    public void Apply_ByName_BothDirectionsKeepSubjectIdTieBreak()
    {
        var cards = new[]
        {
            Card(3, "Beta"),
            Card(1, "Alpha"),
            Card(2, "Alpha")
        };

        Assert.Equal([1, 2, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.Name, false).Select(card => card.SubjectId));
        Assert.Equal([3, 1, 2],
                     AnimeCardSort.Apply(cards, AnimeSortField.Name, true).Select(card => card.SubjectId));
    }

    [Fact]
    public void Apply_ByAirDate_ParsesFixedFormatsWithMissingPartsDefaultingToOne()
    {
        var cards = new[]
        {
            Card(1, "A", "2026-02-03"),
            Card(2, "B", "2026-02"),
            Card(3, "C", "2026")
        };

        Assert.Equal([3, 2, 1],
                     AnimeCardSort.Apply(cards, AnimeSortField.AirDate, false).Select(card => card.SubjectId));
        Assert.Equal([1, 2, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.AirDate, true).Select(card => card.SubjectId));
    }

    [Fact]
    public void Apply_ByAirDate_InvalidAndEmptyValuesAlwaysSortLast()
    {
        var cards = new[]
        {
            Card(1, "A", "not-a-date"),
            Card(2, "B", "2026-01-02"),
            Card(3, "C", null),
            Card(4, "D", "  ")
        };

        Assert.Equal([2, 1, 3, 4],
                     AnimeCardSort.Apply(cards, AnimeSortField.AirDate, false).Select(card => card.SubjectId));
        Assert.Equal([2, 1, 3, 4],
                     AnimeCardSort.Apply(cards, AnimeSortField.AirDate, true).Select(card => card.SubjectId));
    }

    [Fact]
    public void Apply_ByTorrentDates_BothDirectionsKeepNullsLast()
    {
        var cards = new[]
        {
            Card(1, "A", first : T2, last : T1),
            Card(2, "B", first : T0, last : T2),
            Card(3, "C")
        };

        Assert.Equal([2, 1, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.FirstAdded, false).Select(card => card.SubjectId));
        Assert.Equal([1, 2, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.FirstAdded, true).Select(card => card.SubjectId));
        Assert.Equal([1, 2, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.LastAdded, false).Select(card => card.SubjectId));
        Assert.Equal([2, 1, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.LastAdded, true).Select(card => card.SubjectId));
    }

    [Fact]
    public void Apply_EqualKeys_TieBreakBySubjectIdAscending()
    {
        var cards = new[]
        {
            Card(3, "A", "2026-01-01", T0, T0),
            Card(1, "B", "2026-01-01", T0, T0),
            Card(2, "C", "2026-01-01", T0, T0)
        };

        Assert.Equal([1, 2, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.AirDate, false).Select(card => card.SubjectId));
        Assert.Equal([1, 2, 3],
                     AnimeCardSort.Apply(cards, AnimeSortField.FirstAdded, true).Select(card => card.SubjectId));
    }
}
