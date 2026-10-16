namespace Banori.Core.Downloads;

/// <summary>与宿主操作系统无关的下载器相对路径校验；规范化结果统一使用 /。</summary>
public static class RelativeDownloadPathPolicy
{
    public const int MaxLength = 500;

    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.Length is 0 or > MaxLength)
            throw new ArgumentException($"相对目录必须为 1 至 {MaxLength} 个字符。", nameof(path));
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new ArgumentException("目录必须相对于下载根目录，不能使用绝对路径或盘符。", nameof(path));

        var segments = normalized.Split('/');
        return segments.Any(segment => segment.Length == 0       ||
                                       segment is "." or ".."    ||
                                       segment != segment.Trim() ||
                                       segment.Any(character => char.IsControl(character) ||
                                                                "<>\"|?*".Contains(character)))
            ? throw new ArgumentException("相对目录不能包含空路径段、.、.. 或无效字符。", nameof(path))
            : normalized;
    }

    public static void ValidateDirectories(string mediaDirectory, string seedDirectory)
    {
        var media = Normalize(mediaDirectory);
        var seed  = Normalize(seedDirectory);
        if (string.Equals(media, seed, StringComparison.OrdinalIgnoreCase)   ||
            media.StartsWith(seed + "/", StringComparison.OrdinalIgnoreCase) ||
            seed.StartsWith(media + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("媒体目录与做种目录不能相同或互为父子目录。");
    }
}
