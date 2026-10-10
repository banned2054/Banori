using System.Globalization;
using System.Text;

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
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>默认模板：[Anime]动画名 E集数 来源站点；来源缺失时自然收缩为无来源命名。</summary>
    public const string DefaultTemplate = "[Anime]{title} E{episode} {source}";

    /// <summary>按模板生成命名；template 为空时用默认模板（兼容既有调用）。</summary>
    public string Create(string subjectName, decimal episode, int version, string preference)
    {
        return Create(subjectName, episode, version, preference, null, null);
    }

    /// <summary>
    ///     按模板生成命名：{title} 动画名、{episode} 集数、{version} 版本（v2，首版为空）、
    ///     {group} 字幕组或语言、{source} 来源站点。令牌替换后清理非法字符并收缩空白。
    /// </summary>
    public string Create(string subjectName, decimal episode, int version, string preference,
                         string? source, string? template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);

        var safeSubjectName = new string(subjectName.Trim()
                                                    .Select(character =>
                                                                InvalidFileNameCharacters.Contains(character)
                                                                    ? ' '
                                                                    : character)
                                                    .ToArray());
        safeSubjectName = string.Join(' ', safeSubjectName.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var pattern = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var rendered = pattern
                       .Replace(NamingTokens.Title, safeSubjectName, StringComparison.OrdinalIgnoreCase)
                       .Replace(NamingTokens.Episode, FormatEpisode(episode), StringComparison.OrdinalIgnoreCase)
                       .Replace(NamingTokens.Version, version > 1 ? $"v{version}" : string.Empty,
                                StringComparison.OrdinalIgnoreCase)
                       .Replace(NamingTokens.Group, preference ?? string.Empty, StringComparison.OrdinalIgnoreCase)
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
