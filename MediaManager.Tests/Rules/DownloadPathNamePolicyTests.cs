using MediaManager.Core.Rules;

namespace MediaManager.Tests.Rules;

public sealed class DownloadPathNamePolicyTests
{
    [Theory]
    [InlineData("作品 / 第一季", "作品 第一季")]
    [InlineData("作品: *特别篇?", "作品 特别篇")]
    [InlineData("  多个   空格  ", "多个 空格")]
    [InlineData("尾随点.", "尾随点")]
    [InlineData("控制符\u0001值", "控制符 值")]
    public void Sanitize_ReplacesInvalidAndControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, DownloadPathNamePolicy.SanitizePathSegment(input));
    }

    [Fact]
    public void Sanitize_CapsLengthAndKeepsContent()
    {
        var longName = new string('名', 200);
        var result   = DownloadPathNamePolicy.SanitizePathSegment(longName);
        Assert.Equal(120, result.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_ReturnsEmptyForBlankInput(string? input)
    {
        Assert.Equal(string.Empty, DownloadPathNamePolicy.SanitizePathSegment(input));
    }
}
