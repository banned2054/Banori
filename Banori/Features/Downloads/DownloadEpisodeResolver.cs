using Banned.AniParser;
using Banori.Core.Subjects;
using Banori.Data;

namespace Banori.Features.Downloads;

/// <summary>
///     文件级单集身份：人工覆盖优先，其次修正后的文件名解析集数。合集区间与解析失败的文件
///     不参与单集身份——跨集视频不可拆分，不能默认拆成多个独立集参与择优或替换。与命名用的
///     集数合成（有任务级兜底）分开：身份判定不能拿任务级集数冒充文件级身份。
/// </summary>
public sealed class DownloadEpisodeResolver(AniParser aniParser, SubjectEpisodeCorrectionPolicy correctionPolicy)
{
    public decimal? EpisodeOf(DownloadTask task, DownloadTaskFile file)
    {
        if (file.EpisodeNumberOverride is { } overrideEpisode) return overrideEpisode;
        if (file.EpisodeEndOverride is not null) return null;
        if (file.Kind == DownloadFileKind.Seed) return null;

        var parsed = aniParser.Parse(FileName(file.OriginalRelativePath));
        if (parsed?.Episode is not { } raw) return null;
        return correctionPolicy.Apply(task.SubjectId, raw).Episode;
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
