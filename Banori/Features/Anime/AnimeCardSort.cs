using System.Globalization;

namespace Banori.Features.Anime;

/// <summary>动画总览页的排序字段。</summary>
public enum AnimeSortField
{
    Name,
    AirDate,
    FirstAdded,
    LastAdded
}

/// <summary>动画卡片排序：纯内存比较，空日期恒排末尾，同值按 SubjectId 升序。</summary>
public static class AnimeCardSort
{
    private static readonly string[] AirDateFormats = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];

    public static IReadOnlyList<AnimeCard> Apply(
        IReadOnlyList<AnimeCard> cards, AnimeSortField field, bool descending) =>
        field switch
        {
            AnimeSortField.Name       => ByName(cards, descending),
            AnimeSortField.AirDate    => ByKey(cards, card => ParseAirDate(card.AirDate), descending),
            AnimeSortField.FirstAdded => ByKey(cards, card => card.FirstTorrentAtUtc, descending),
            AnimeSortField.LastAdded  => ByKey(cards, card => card.LastTorrentAtUtc, descending),
            _                         => cards
        };

    private static IReadOnlyList<AnimeCard> ByName(
        IReadOnlyList<AnimeCard> cards, bool descending) =>
        (descending
            ? cards.OrderByDescending(card => card.DisplayName, StringComparer.CurrentCulture)
            : cards.OrderBy(card => card.DisplayName, StringComparer.CurrentCulture))
        .ThenBy(card => card.SubjectId)
        .ToArray();

    private static IReadOnlyList<AnimeCard> ByKey<T>(
        IReadOnlyList<AnimeCard> cards, Func<AnimeCard, T?> keySelector, bool descending)
        where T : struct, IComparable<T> =>
        cards.OrderBy(keySelector, new NullsLastComparer<T>(descending))
             .ThenBy(card => card.SubjectId)
             .ToArray();

    private static DateOnly? ParseAirDate(string? airDate)
    {
        if (string.IsNullOrWhiteSpace(airDate)) return null;

        return DateOnly.TryParseExact(airDate.Trim(), AirDateFormats, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private sealed class NullsLastComparer<T>(bool descending) : IComparer<T?>
        where T : struct, IComparable<T>
    {
        public int Compare(T? x, T? y)
        {
            if (x is null) return y is null ? 0 : 1;
            if (y is null) return -1;
            return descending ? y.Value.CompareTo(x.Value) : x.Value.CompareTo(y.Value);
        }
    }
}
