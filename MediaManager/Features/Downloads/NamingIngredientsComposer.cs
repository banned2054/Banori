using Banned.AniParser.Models;
using MediaManager.Core.Torrents;

namespace MediaManager.Features.Downloads;

/// <summary>
///     命名要素展开的唯一实现：把解析结果、Bangumi 元数据与种子/文件双模板装配为命名要素。
///     {title} 用 Bangumi 作品名（中文名非空用之，否则原名）；来源变量由调用方按入口解析后传入。
///     订阅评估（修正后集数）与下载登记（多级回退）的入口流程差异由调用方保留，仅共用字段展开。
/// </summary>
public static class NamingIngredientsComposer
{
    public static TorrentNamingIngredients Compose(
        string template, string fileTemplate, ParseResult parsed, string episodeText, string preference,
        string? siteSource, int bangumiId, string bangumiName, string bangumiNameCn,
        string bangumiPlatform, string? bangumiDate)
    {
        var displayName = string.IsNullOrWhiteSpace(bangumiNameCn) ? bangumiName : bangumiNameCn;
        return new TorrentNamingIngredients(template, displayName, episodeText,
                                            parsed.Version, preference, siteSource,
                                            parsed.Language.ToString(), parsed.Resolution.ToString(),
                                            FileTemplate: fileTemplate,
                                            AniMediaType: parsed.MediaType.ToString(),
                                            AniTitle: parsed.Title,
                                            AniGroup: parsed.Group, AniGroupType: parsed.GroupType.ToString(),
                                            AniSubtitleType: parsed.SubtitleType.ToString(),
                                            AniSource: parsed.Source.ToString(), AniWebSource: parsed.WebSource,
                                            AniResolution: parsed.Resolution.ToString(),
                                            AniVideoCodec: parsed.VideoCodec, AniAudioCodec: parsed.AudioCodec,
                                            AniColorBitDepth: parsed.ColorBitDepth, BangumiId: bangumiId,
                                            BangumiName: bangumiName, BangumiNameCn: bangumiNameCn,
                                            BangumiPlatform: bangumiPlatform, BangumiDate: bangumiDate);
    }
}
