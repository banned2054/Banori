using System.Globalization;

namespace MediaManager.Core.Rules;

/// <summary>下载器保存路径的目录名与文件名净化。下载器可能在 Windows 或 Linux，按两者并集检查非法字符。</summary>
public static class DownloadPathNamePolicy
{
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>把任意显示文本净化为单级路径段；无法保留内容时返回空字符串，由调用方决定回退值。</summary>
    public static string SanitizePathSegment(string? value, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = new string(value.Trim()
                                      .Select(character => character switch
                                       {
                                           _ when InvalidCharacters.Contains(character) => ' ',
                                           _ when char.IsControl(character)             => ' ',
                                           _ when char.GetUnicodeCategory(character) ==
                                                  UnicodeCategory.Format => ' ',
                                           _ => character
                                       })
                                      .ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        cleaned = cleaned.TrimEnd('.');

        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].TrimEnd();

        return cleaned;
    }
}
