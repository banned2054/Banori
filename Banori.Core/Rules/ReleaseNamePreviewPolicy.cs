using Banned.AniParser.Models;
using Banori.Core.Torrents;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Banori.Core.Rules;

/// <summary>命名模板令牌；旧令牌保持既有解释，新模板推荐使用带来源前缀的令牌。</summary>
public static class NamingTokens
{
    public const string Title            = "{title}";
    public const string Episode          = "{episode}";
    public const string Version          = "{version}";
    public const string Group            = "{group}";
    public const string Source           = "{source}";
    public const string AniMediaType     = "{ani.mediaType}";
    public const string AniTitle         = "{ani.title}";
    public const string AniEpisode       = "{ani.episode}";
    public const string AniVersion       = "{ani.version}";
    public const string AniGroup         = "{ani.group}";
    public const string AniGroupType     = "{ani.groupType}";
    public const string AniLanguage      = "{ani.language}";
    public const string AniSubtitleType  = "{ani.subtitleType}";
    public const string AniSource        = "{ani.source}";
    public const string AniWebSource     = "{ani.webSource}";
    public const string AniResolution    = "{ani.resolution}";
    public const string AniVideoCodec    = "{ani.videoCodec}";
    public const string AniAudioCodec    = "{ani.audioCodec}";
    public const string AniColorBitDepth = "{ani.colorBitDepth}";
    public const string BgmId            = "{bgm.id}";
    public const string BgmName          = "{bgm.name}";
    public const string BgmNameCn        = "{bgm.nameCn}";
    public const string BgmPlatform      = "{bgm.platform}";
    public const string BgmDate          = "{bgm.date}";
    public const string SiteSource       = "{site.source}";
}

public sealed class ReleaseNamePreviewPolicy
{
    public const string DefaultTemplate     = "[Anime]{title} E{episode} {source}";
    public const string DefaultFileTemplate = "[Anime]{title} E{episode} {source}";

    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public string Create(string subjectName, decimal episode, int version, string preference) =>
        Create(subjectName, episode, version, preference, null, null);

    /// <summary>按模板变量渲染；种子名使用 Template，内容文件名使用 FileTemplate。</summary>
    public string Create(TorrentNamingIngredients ingredients, bool fileName = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingredients.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(ingredients.EpisodeText);

        var legacyGroup = ingredients.Group;
        var legacyTitle = ingredients.Title;
        return Render(fileName ? ingredients.FileTemplate ?? ingredients.Template : ingredients.Template,
                      new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                      {
                          [NamingTokens.Title]           = legacyTitle,
                          [NamingTokens.Episode]         = ingredients.EpisodeText,
                          [NamingTokens.Version]         = VersionText(ingredients.Version),
                          [NamingTokens.Group]           = legacyGroup,
                          [NamingTokens.Source]          = ingredients.Source,
                          [NamingTokens.AniMediaType]    = ingredients.AniMediaType,
                          [NamingTokens.AniTitle]        = ingredients.AniTitle,
                          [NamingTokens.AniEpisode]      = ingredients.EpisodeText,
                          [NamingTokens.AniVersion]      = VersionText(ingredients.Version),
                          [NamingTokens.AniGroup]        = ingredients.AniGroup,
                          [NamingTokens.AniGroupType]    = ingredients.AniGroupType,
                          [NamingTokens.AniLanguage]     = ingredients.Language,
                          [NamingTokens.AniSubtitleType] = ingredients.AniSubtitleType,
                          [NamingTokens.AniSource]       = ingredients.AniSource,
                          [NamingTokens.AniWebSource]    = ingredients.AniWebSource,
                          [NamingTokens.AniResolution]   = ingredients.AniResolution ?? ingredients.Resolution,
                          [NamingTokens.AniVideoCodec]   = ingredients.AniVideoCodec,
                          [NamingTokens.AniAudioCodec]   = ingredients.AniAudioCodec,
                          [NamingTokens.AniColorBitDepth] =
                              ingredients.AniColorBitDepth?.ToString(CultureInfo.InvariantCulture),
                          [NamingTokens.BgmId]       = ingredients.BangumiId?.ToString(CultureInfo.InvariantCulture),
                          [NamingTokens.BgmName]     = ingredients.BangumiName,
                          [NamingTokens.BgmNameCn]   = ingredients.BangumiNameCn,
                          [NamingTokens.BgmPlatform] = ingredients.BangumiPlatform,
                          [NamingTokens.BgmDate]     = ingredients.BangumiDate,
                          [NamingTokens.SiteSource]  = ingredients.Source
                      }, fileName);
    }

    public static string FormatEpisodeText(decimal episode) => FormatEpisode(episode);

    public static string? FormatEpisodeText(ParseResult parsed)
    {
        if (parsed.Episode is { } episode) return FormatEpisode(episode);
        if (parsed.StartEpisode is { } start && parsed.EndEpisode is { } end)
            return start == end ? FormatEpisode(start) : $"{FormatEpisode(start)}-E{FormatEpisode(end)}";
        return null;
    }

    public string Create(string  subjectName, decimal episode, int version, string preference,
                         string? source,      string? template) =>
        Render(template, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [NamingTokens.Title]   = subjectName,
            [NamingTokens.Episode] = FormatEpisode(episode),
            [NamingTokens.Version] = VersionText(version),
            [NamingTokens.Group]   = preference,
            [NamingTokens.Source]  = source
        }, false);

    private static string VersionText(int version) => version > 1 ? $"v{version}" : string.Empty;

    private static string ReplaceFormattedTokens(string template, IReadOnlyDictionary<string, string?> values)
    {
        template = ReplaceFormattedToken(template, "{ani.episode:",
                                         values.GetValueOrDefault(NamingTokens.AniEpisode), FormatEpisodeTextValue);
        template = ReplaceFormattedToken(template, "{episode:",
                                         values.GetValueOrDefault(NamingTokens.Episode), FormatEpisodeTextValue);
        template = ReplaceFormattedToken(template, "{bgm.date:",
                                         values.GetValueOrDefault(NamingTokens.BgmDate), FormatDateTextValue);
        return ReplaceFormattedToken(template, "{date:",
                                     values.GetValueOrDefault(NamingTokens.BgmDate), FormatDateTextValue);
    }

    private static string ReplaceFormattedToken(string  template, string                       prefix,
                                                string? value,    Func<string, string, string> formatter)
    {
        var result = new StringBuilder(template.Length);
        for (var index = 0; index < template.Length;)
        {
            if (template.AsSpan(index).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var formatStart = index + prefix.Length;
                var close       = template.IndexOf('}', formatStart);
                if (close >= 0)
                {
                    var format = template[formatStart..close];
                    result.Append(value is null ? string.Empty : formatter(value, format));
                    index = close + 1;
                    continue;
                }
            }

            result.Append(template[index++]);
        }

        return result.ToString();
    }

    private static string FormatEpisodeTextValue(string value, string format)
    {
        if (format.Length is < 2 or > 3 || format.Any(character => char.ToLowerInvariant(character) != 'e'))
            return value;
        var width = format.Length;
        var match = Regex.Match(value, @"^(?<start>\d+(?:\.\d+)?)(?:-E(?<end>\d+(?:\.\d+)?))?$",
                                RegexOptions.IgnoreCase);
        if (!match.Success) return value;
        var start = FormatEpisodeComponent(match.Groups["start"].Value, width);
        if (!match.Groups["end"].Success) return start;
        return $"{start}-E{FormatEpisodeComponent(match.Groups["end"].Value, width)}";
    }

    private static string FormatEpisodeComponent(string value, int width)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var episode)) return value;
        var numberFormat = new string('0', width) + (episode == decimal.Truncate(episode) ? string.Empty : ".###");
        return episode.ToString(numberFormat, CultureInfo.InvariantCulture);
    }

    private static string FormatDateTextValue(string value, string format)
    {
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            try
            {
                return date.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return value;
            }

        return value;
    }

    private static string Render(string? template, Dictionary<string, string?> values, bool allowDirectories)
    {
        var pattern  = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var rendered = ReplaceFormattedTokens(pattern, values);
        foreach (var (token, value) in values)
            rendered = rendered.Replace(token, Sanitize(value ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        // 文件名模式下模板中书写的 / 与 \ 作为子目录分隔符保留；令牌取值里的分隔符已在 Sanitize
        // 阶段转成空格，因此目录层级只由模板结构决定。种子名模式沿旧行为折叠为空格（展示名单层）。
        var segments = (allowDirectories ? rendered.Replace('\\', '/') : rendered)
           .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(allowDirectories ? '/' : ' ',
                           segments.Select(CollapseIllegalCharacters).Where(segment => segment.Length > 0));
    }

    private static string CollapseIllegalCharacters(string segment)
    {
        var cleaned = new string(segment.Select(character =>
                                                    InvalidFileNameCharacters.Contains(character) ? ' ' : character)
                                        .ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Trim()
                                      .Select(character =>
                                                  InvalidFileNameCharacters.Contains(character) ? ' ' : character)
                                      .ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FormatEpisode(decimal episode) =>
        episode.ToString(episode == decimal.Truncate(episode) ? "00" : "00.###", CultureInfo.InvariantCulture);
}
