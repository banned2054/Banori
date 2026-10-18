namespace Banori.Data;

/// <summary>
///     每集版本登记：一个 Subject + 集数一行，独立于任务级替换字段记录「当前可播放现任」与
///     「待完成优胜」两个视频文件引用。新任务胜出时写 Pending；新任务完成时把 Incumbent 切到
///     新文件并保留 Superseded 供单级回退。任务可以只被承接部分集数，反之一个旧任务的集数可
///     分别由多个新任务接替；引用不设外键，任务删除时由清理逻辑显式回退。
/// </summary>
public sealed class EpisodeVersion
{
    public int Id { get; set; }

    public int SubjectId { get; set; }

    /// <summary>文件级合成集数（人工覆盖 &gt; 修正后的文件名解析）；与现任对决在同一口径下比较。</summary>
    public decimal Episode { get; set; }

    /// <summary>记录时的媒体类型快照（SingleEpisode/Movie 等），仅作解释，不参与身份匹配。</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>当前可播放现任的视频文件（所属任务已完成且整理成功）。</summary>
    public Guid? IncumbentVideoFileId { get; set; }

    /// <summary>待完成优胜的视频文件（新版本在准备或下载中；完成后切换为现任）。</summary>
    public Guid? PendingVideoFileId { get; set; }

    /// <summary>现任所承接的上一任视频文件，删除现任所属任务时单级回退用。</summary>
    public Guid? SupersededVideoFileId { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
