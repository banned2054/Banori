using Banned.AniParser.Models;
using MediaManager.Core.Torrents;
using System.Globalization;

namespace MediaManager.Core.Rules;

/// <summary>命名模板的可用令牌与默认模板；令牌大小写不敏感，未知令牌按字面保留。</summary>
public static class NamingTokens
{
    public const string Title   = "{title}";
    public const string Episode = "{episode}";
    public const string Version = "{version}";
    public const string Group   = "{group}";
    public const string Source  = "{source}";
}

public sealed class ReleaseNamePreviewPolicy
{
    /// <summary>默认模板：[Anime]动画名 E集数 来源站点；来源缺失时自然收缩为无来源命名。</summary>
    public const string DefaultTemplate = "[Anime]{title} E{episode} {source}";

    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>按模板生成命名；template 为空时用默认模板（兼容既有调用）。</summary>
    public string Create(string subjectName, decimal episode, int version, string preference)
    {
        return Create(subjectName, episode, version, preference, null, null);
    }

    /// <summary>按命名要素渲染；EpisodeText 已是格式化好的集数段（单集 "07"、合集 "01-E12"）。</summary>
    public string Create(TorrentNamingIngredients ingredients)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingredients.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(ingredients.EpisodeText);

        return Render(ingredients.Template, ingredients.Title, ingredients.EpisodeText, ingredients.Version,
                      ingredients.Group, ingredients.Source);
    }

    /// <summary>单集的集数段：整数两位起（7 → "07"），小数集保留小数（2.5 → "02.5"）。</summary>
    public static string FormatEpisodeText(decimal episode)
    {
        return FormatEpisode(episode);
    }

    /// <summary>
    ///     解析结果的集数段：单集优先取集数；合集区间渲染为 "01-E12"，与 ByrTorrent 的合集种子
    ///     命名同形（模板中的 "E{episode}" 令牌渲染后是完整的区间）；识别不出返回 null。
    /// </summary>
    public static string? FormatEpisodeText(ParseResult parsed)
    {
        if (parsed.Episode is { } episode) return FormatEpisode(episode);
        if (parsed.StartEpisode is { } start && parsed.EndEpisode is { } end)
            return start == end ? FormatEpisode(start) : $"{FormatEpisode(start)}-E{FormatEpisode(end)}";
        return null;
    }

    /// <summary>
    ///     按模板生成命名：{title} 动画名、{episode} 集数、{version} 版本（v2，首版为空）、
    ///     {group} 字幕组或语言、{source} 来源站点。令牌替换后清理非法字符并收缩空白。
    /// </summary>
    public string Create(string  subjectName, decimal episode, int version, string preference,
                         string? source,      string? template)
    {
        return Render(template, subjectName, FormatEpisode(episode), version, preference, source);
    }

    private static string Render(string? template, string  title, string episodeText, int version,
                                 string? group,    string? source)
    {
        var safeSubjectName = new string(title.Trim()
                                              .Select(character =>
                                                          InvalidFileNameCharacters.Contains(character)
                                                              ? ' '
                                                              : character)
                                              .ToArray());
        safeSubjectName = string.Join(' ', safeSubjectName.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var pattern = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var rendered = pattern
                      .Replace(NamingTokens.Title, safeSubjectName, StringComparison.OrdinalIgnoreCase)
                      .Replace(NamingTokens.Episode, episodeText, StringComparison.OrdinalIgnoreCase)
                      .Replace(NamingTokens.Version, version > 1 ? $"v{version}" : string.Empty,
                               StringComparison.OrdinalIgnoreCase)
                      .Replace(NamingTokens.Group, group   ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                      .Replace(NamingTokens.Source, source ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var cleaned = new string(rendered.Select(character =>
                                                     InvalidFileNameCharacters.Contains(character)
                                                         ? ' '
                                                         : character)
                                         .ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FormatEpisode(decimal episode)
    {
        return episode.ToString(episode == decimal.Truncate(episode) ? "00" : "00.###",
                                CultureInfo.InvariantCulture);
    }
}
