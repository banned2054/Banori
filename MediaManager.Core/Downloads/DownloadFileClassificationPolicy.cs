using Banned.AniParser;
using Banned.AniParser.Models;
using System.Text.RegularExpressions;

namespace MediaManager.Core.Downloads;

public enum DownloadFileKind
{
    Content,
    Seed
}

public sealed record DownloadFileClassification(string Path, DownloadFileKind Kind, ParseResult? Parsed, string Reason);

/// <summary>沿用 ByrTorrent 附属文件规则及当前扩展名集合，以 AniParser 的集数解析作为正片判据。</summary>
public sealed class DownloadFileClassificationPolicy(AniParser aniParser)
{
    private static readonly HashSet<string> ContentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".mov", ".flv", ".ass", ".ssa", ".srt", ".sup"
    };

    private static readonly string[] FilterWords = ["Deculture Ver.", "SPs", "Scans", "CDs", "Bonus"];

    private static readonly Regex SpecialFragment = new(@"\[(?:cm|menu|nced|pv|ncop)\d{2}\]",
                                                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                                                        TimeSpan.FromSeconds(1));

    public DownloadFileClassification Classify(string path)
    {
        var normalized = RelativeDownloadPathPolicy.Normalize(path);
        var fileName   = normalized[(normalized.LastIndexOf('/') + 1)..];
        var extension  = Path.GetExtension(fileName);
        if (!ContentExtensions.Contains(extension)) return Seed("不支持的正片扩展名");
        if (FilterWords.Any(word => normalized.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return Seed("命中过滤词");
        if (SpecialFragment.IsMatch(fileName)) return Seed("特殊片段");
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) &&
            (stem.EndsWith(".tc", StringComparison.OrdinalIgnoreCase) ||
             stem.EndsWith(".cht", StringComparison.OrdinalIgnoreCase)))
            return Seed("ByrTorrent 繁体字幕过滤");

        var parsed = aniParser.Parse(fileName);
        if (parsed is null || (parsed.Episode is null && (parsed.StartEpisode is null || parsed.EndEpisode is null)))
            return new DownloadFileClassification(normalized, DownloadFileKind.Seed, parsed, "AniParser 未解析到集数");
        return new DownloadFileClassification(normalized, DownloadFileKind.Content, parsed, "AniParser 已解析到集数");

        DownloadFileClassification Seed(string reason) => new(normalized, DownloadFileKind.Seed, null, reason);
    }
}
