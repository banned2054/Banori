using Banori.Core.Torrents;
using System.Text;

namespace Banori.Tests.Downloads;

public sealed class TorrentFileManifestParserTests
{
    [Fact]
    public void SingleFile_UsesUtf8NameAndLongLength()
    {
        var bytes = Torrent("d4:name" + Str("fallback.mkv") + "10:name.utf-8" + Str("作品.mkv") +
                            "6:lengthi4294967296ee");
        Assert.True(TorrentFileManifestParser.TryParse(bytes, out var manifest));
        Assert.Equal("作品.mkv", manifest.Name);
        Assert.False(manifest.IsMultiFile);
        Assert.Equal(new TorrentFileEntry(0, "作品.mkv", 4294967296), Assert.Single(manifest.Files));
    }

    [Fact]
    public void MultiFile_PreservesOrderRootAndUtf8Paths()
    {
        var bytes = Torrent("d4:name"             + Str("作品") + "5:filesl" +
                            File(123, "01.mkv")   +
                            "d6:lengthi0e4:pathl" + Str("legacy.ass") + "e10:path.utf-8l" + Str("字幕") + Str("01.ass") +
                            "ee"                  + "ee");
        Assert.True(TorrentFileManifestParser.TryParse(bytes, out var manifest));
        Assert.True(manifest.IsMultiFile);
        Assert.Equal([new TorrentFileEntry(0, "作品/01.mkv", 123), new TorrentFileEntry(1, "作品/字幕/01.ass", 0)],
                     manifest.Files);
    }

    [Theory]
    [InlineData("d4:name1:a6:lengthi-1ee")]
    [InlineData("d4:name1:a6:lengthi01ee")]
    [InlineData("d4:name1:a6:lengthi9223372036854775808ee")]
    [InlineData("d4:name1:ae")]
    [InlineData("d4:name1:a5:fileslee")]
    [InlineData("d4:name1:a6:lengthi1e6:lengthi2ee")]
    [InlineData("d4:name2:..6:lengthi1ee")]
    [InlineData("d4:name2147483648:a6:lengthi1ee")]
    public void InvalidManifest_ReturnsFalseWithoutPartialResult(string info)
    {
        Assert.False(TorrentFileManifestParser.TryParse(Torrent(info), out var manifest));
        Assert.Null(manifest);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/absolute")]
    [InlineData("D:drive")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void UnsafePathSegments_AreRejected(string segment)
    {
        var bytes = Torrent("d4:name1:a5:filesl" + File(1, segment) + "ee");
        Assert.False(TorrentFileManifestParser.TryParse(bytes, out _));
    }

    [Fact]
    public void TruncationTrailingGarbageDuplicatePathsAndInvalidUtf8_AreRejected()
    {
        var bytes = Torrent("d4:name1:a5:filesl" + File(1, "01.mkv") + File(2, "01.MKV") + "ee");
        Assert.False(TorrentFileManifestParser.TryParse(bytes, out _));
        var valid = Torrent("d4:name1:a6:lengthi1ee");
        for (var length = 0; length < valid.Length; length++)
            Assert.False(TorrentFileManifestParser.TryParse(valid.AsSpan(0, length), out _));
        Assert.False(TorrentFileManifestParser.TryParse([..valid, (byte)'e'], out _));
        var invalidUtf8 = Torrent("d4:name1:a6:lengthi1ee");
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'a')] = 0xff;
        Assert.False(TorrentFileManifestParser.TryParse(invalidUtf8, out _));
    }

    private static byte[] Torrent(string info) => Encoding.UTF8.GetBytes("d4:info" + info + "e");

    private static string Str(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    private static string File(long length, string path) => $"d6:lengthi{length}e4:pathl{Str(path)}ee";
}
