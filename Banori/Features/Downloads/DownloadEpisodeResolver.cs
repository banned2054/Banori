using Banned.AniParser;
using Banori.Core.Subjects;
using Banori.Data;

namespace Banori.Features.Downloads;

/// <summary>
///     文件级单集身份：人工单集覆盖优先，其次修正后的文件名解析集数。人工或原始合集区间与
///     解析失败的文件不参与单集身份——跨集视频不可拆分，不能拆成多个独立集参与择优或替换。
///     Seed 不承担单集身份。与命名用的
///     集数合成（有任务级兜底）分开：身份判定不能拿任务级集数冒充文件级身份。
/// </summary>
public sealed class DownloadEpisodeResolver(AniParser aniParser, SubjectEpisodeCorrectionPolicy correctionPolicy)
{
    public decimal? EpisodeOf(DownloadTask task, DownloadTaskFile file)
    {
        if (file.Kind == DownloadFileKind.Seed) return null;
        if (file.EpisodeEndOverride is { } episodeEnd)
            return file.EpisodeNumberOverride == episodeEnd ? episodeEnd : null;
        if (file.EpisodeNumberOverride is { } overrideEpisode) return overrideEpisode;
        var parsed = aniParser.Parse(FileName(file.OriginalRelativePath));
        if (parsed?.Episode is not { } raw) return null;
        return correctionPolicy.Apply(task.SubjectId, raw).Episode;
    }

    /// <summary>取 unix 风格相对路径的文件名供解析用；下载服务的共用助手，避免多处拷贝漂移。</summary>
    internal static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
