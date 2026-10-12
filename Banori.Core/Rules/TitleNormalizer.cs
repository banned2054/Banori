namespace Banori.Core.Rules;

/// <summary>
///     标题归一化：Trim 后转大写并去除全部空白字符，作为标题映射与同集互认的比较键。
///     算法必须与历史 SubjectTitleMapping 的映射键完全一致，改动会破坏既有映射命中。
/// </summary>
public static class TitleNormalizer
{
    public static string Normalize(string? title)
    {
        return string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : string.Concat(title.Trim().ToUpperInvariant().Where(character => !char.IsWhiteSpace(character)));
    }
}
