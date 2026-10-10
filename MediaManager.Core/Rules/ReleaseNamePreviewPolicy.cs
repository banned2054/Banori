using Banned.AniParser.Models;
using MediaManager.Core.Torrents;
using System.Globalization;

namespace MediaManager.Core.Rules;

/// <summary>命名模板令牌；旧令牌保持既有解释，新模板推荐使用带来源前缀的令牌。</summary>
public static class NamingTokens
{
    public const string Title = "{title}";
    public const string Episode = "{episode}";
    public const string Version = "{version}";
    public const string Group = "{group}";
    public const string Source = "{source}";
    public const string AniMediaType = "{ani.mediaType}";
    public const string AniTitle = "{ani.title}";
    public const string AniEpisode = "{ani.episode}";
    public const string AniVersion = "{ani.version}";
    public const string AniGroup = "{ani.group}";
    public const string AniGroupType = "{ani.groupType}";
    public const string AniLanguage = "{ani.language}";
    public const string AniSubtitleType = "{ani.subtitleType}";
    public const string AniSource = "{ani.source}";
    public const string AniWebSource = "{ani.webSource}";
    public const string AniResolution = "{ani.resolution}";
    public const string AniVideoCodec = "{ani.videoCodec}";
    public const string AniAudioCodec = "{ani.audioCodec}";
    public const string AniColorBitDepth = "{ani.colorBitDepth}";
    public const string BgmId = "{bgm.id}";
    public const string BgmName = "{bgm.name}";
    public const string BgmNameCn = "{bgm.nameCn}";
    public const string BgmPlatform = "{bgm.platform}";
    public const string BgmDate = "{bgm.date}";
    public const string SiteSource = "{site.source}";
}

public sealed class ReleaseNamePreviewPolicy
{
    public const string DefaultTemplate = "[Anime]{title} E{episode} {source}";
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
                          [NamingTokens.Title] = legacyTitle,
                          [NamingTokens.Episode] = ingredients.EpisodeText,
                          [NamingTokens.Version] = VersionText(ingredients.Version),
                          [NamingTokens.Group] = legacyGroup,
                          [NamingTokens.Source] = ingredients.Source,
                          [NamingTokens.AniMediaType] = ingredients.AniMediaType,
                          [NamingTokens.AniTitle] = ingredients.AniTitle,
                          [NamingTokens.AniEpisode] = ingredients.EpisodeText,
                          [NamingTokens.AniVersion] = VersionText(ingredients.Version),
                          [NamingTokens.AniGroup] = ingredients.AniGroup,
                          [NamingTokens.AniGroupType] = ingredients.AniGroupType,
                          [NamingTokens.AniLanguage] = ingredients.Language,
                          [NamingTokens.AniSubtitleType] = ingredients.AniSubtitleType,
                          [NamingTokens.AniSource] = ingredients.AniSource,
                          [NamingTokens.AniWebSource] = ingredients.AniWebSource,
                          [NamingTokens.AniResolution] = ingredients.AniResolution ?? ingredients.Resolution,
                          [NamingTokens.AniVideoCodec] = ingredients.AniVideoCodec,
                          [NamingTokens.AniAudioCodec] = ingredients.AniAudioCodec,
                          [NamingTokens.AniColorBitDepth] = ingredients.AniColorBitDepth?.ToString(CultureInfo.InvariantCulture),
                          [NamingTokens.BgmId] = ingredients.BangumiId?.ToString(CultureInfo.InvariantCulture),
                          [NamingTokens.BgmName] = ingredients.BangumiName,
                          [NamingTokens.BgmNameCn] = ingredients.BangumiNameCn,
                          [NamingTokens.BgmPlatform] = ingredients.BangumiPlatform,
                          [NamingTokens.BgmDate] = ingredients.BangumiDate,
                          [NamingTokens.SiteSource] = ingredients.Source
                      });
    }

    public static string FormatEpisodeText(decimal episode) => FormatEpisode(episode);

    public static string? FormatEpisodeText(ParseResult parsed)
    {
        if (parsed.Episode is { } episode) return FormatEpisode(episode);
        if (parsed.StartEpisode is { } start && parsed.EndEpisode is { } end)
            return start == end ? FormatEpisode(start) : $"{FormatEpisode(start)}-E{FormatEpisode(end)}";
        return null;
    }

    public string Create(string subjectName, decimal episode, int version, string preference,
                         string? source, string? template)
    {
        return Render(template, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [NamingTokens.Title] = subjectName,
            [NamingTokens.Episode] = FormatEpisode(episode),
            [NamingTokens.Version] = VersionText(version),
            [NamingTokens.Group] = preference,
            [NamingTokens.Source] = source
        });
    }

    private static string VersionText(int version) => version > 1 ? $"v{version}" : string.Empty;

    private static string Render(string? template, Dictionary<string, string?> values)
    {
        var pattern = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var rendered = pattern;
        foreach (var (token, value) in values)
            rendered = rendered.Replace(token, Sanitize(value ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        var cleaned = new string(rendered.Select(character => InvalidFileNameCharacters.Contains(character) ? ' ' : character).ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Trim().Select(character => InvalidFileNameCharacters.Contains(character) ? ' ' : character).ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FormatEpisode(decimal episode) =>
        episode.ToString(episode == decimal.Truncate(episode) ? "00" : "00.###", CultureInfo.InvariantCulture);
}
