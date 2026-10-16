using Banori.Core.Torrents;
using System.Text;

namespace Banori.Tests.Downloads;

public sealed class TorrentDownloadFileTests
{
    [Fact]
    public void SingleFile_UsesUtf8NameAndLongLength()
    {
        const long length = 4294967296;
        var bytes = Torrent("d6:lengthi4294967296e4:name" + Str("fallback.mkv") + "10:name.utf-8" + Str("作品.mkv") +
                            PieceFields(length, 4 * 1024 * 1024) + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        Assert.Equal("作品.mkv", torrent.Name);
        Assert.False(isMultiFile);
        var file = Assert.Single(torrent.Files);
        Assert.Equal("作品.mkv", file.Path);
        Assert.Equal("作品.mkv", TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile));
        Assert.Equal(length, file.Length);
        Assert.Equal(0, TorrentDownloadPolicy.GetQbittorrentFileIndices(bytes, torrent, isMultiFile)["作品.mkv"]);
    }

    [Fact]
    public void MultiFile_IncludesRootUtf8PathsAndZeroLengthFileWithoutDependingOnOrder()
    {
        var bytes = Torrent("d5:filesl"           + File(37, "Extras", "notes.txt") + File(123, "Video", "01.mkv") +
                            "d6:lengthi0e4:pathl" + Str("legacy.ass") + "e10:path.utf-8l" + Str("字幕") + Str("01.ass") +
                            "eee4:name"           + Str("作品") + PieceFields(160) + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        Assert.Equal("作品", torrent.Name);
        Assert.True(isMultiFile);
        var filesByPath =
            torrent.Files.ToDictionary(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile),
                                       StringComparer.Ordinal);
        Dictionary<string, long> expected = new()
        {
            ["作品/Extras/notes.txt"] = 37,
            ["作品/Video/01.mkv"]     = 123,
            ["作品/字幕/01.ass"]        = 0
        };
        Assert.Equal(expected.Count, filesByPath.Count);
        foreach (var (path, length) in expected)
        {
            Assert.True(filesByPath.TryGetValue(path, out var file), $"缺少文件：{path}");
            Assert.Equal(length, file.Length);
        }

        Assert.Equal(["Extras/notes.txt", "Video/01.mkv", "字幕/01.ass"],
                     torrent.Files.Select(file => file.Path.Replace('\\', '/'))
                            .OrderBy(path => path, StringComparer.Ordinal));
        var indices = TorrentDownloadPolicy.GetQbittorrentFileIndices(bytes, torrent, isMultiFile);
        Assert.Equal(0, indices["作品/Extras/notes.txt"]);
        Assert.Equal(1, indices["作品/Video/01.mkv"]);
        Assert.Equal(2, indices["作品/字幕/01.ass"]);
    }

    [Fact]
    public void MultiFile_WithOnlyOneFileMatchingRootName_StillIncludesRoot()
    {
        var bytes = Torrent("d5:filesl" + File(1, "same") + "e4:name" + Str("same") + PieceFields(1) + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        Assert.Equal("same", torrent.Name);
        Assert.True(isMultiFile);
        var file = Assert.Single(torrent.Files);
        Assert.Equal("same", file.Path);
        Assert.Equal("same/same", TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile));
        Assert.Equal(1, file.Length);
        Assert.Equal(0, TorrentDownloadPolicy.GetQbittorrentFileIndices(bytes, torrent, isMultiFile)["same/same"]);
    }

    [Fact]
    public void MultiFile_PaddingEntriesAreExcludedFromRealFileList()
    {
        var padding = "d4:attr1:p6:lengthi16383e4:pathl" + Str(".pad") + Str("16383") + "ee";
        var bytes = Torrent("d5:filesl" + File(1, "A.mkv") + padding + File(1, "B.mkv") + "e4:name" + Str("root") +
                            PieceFields(16385) + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        Assert.True(isMultiFile);
        var filesByPath =
            torrent.Files.ToDictionary(file => TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile),
                                       StringComparer.Ordinal);
        Assert.Equal(2, filesByPath.Count);
        Assert.Equal(1, filesByPath["root/A.mkv"].Length);
        Assert.Equal(1, filesByPath["root/B.mkv"].Length);
        var indices = TorrentDownloadPolicy.GetQbittorrentFileIndices(bytes, torrent, isMultiFile);
        Assert.Equal(0, indices["root/A.mkv"]);
        Assert.Equal(1, indices["root/B.mkv"]);
        Assert.Equal(2, indices.Count);
    }

    [Theory]
    [InlineData("Video/01.mkv")]
    [InlineData("Video\\01.mkv")]
    public void SafeCompoundPathInSingleElement_IsNormalized(string path)
    {
        var bytes = Torrent("d5:filesl" + File(1, path) + "e4:name" + Str("root") + PieceFields(1) + "e");

        Assert.True(TorrentDownloadPolicy.TryLoad(bytes, out var torrent, out var isMultiFile));
        Assert.True(isMultiFile);
        var file = Assert.Single(torrent.Files);
        Assert.Equal("root/Video/01.mkv", TorrentDownloadPolicy.GetRelativePath(torrent, file, isMultiFile));
        Assert.Equal(1, file.Length);
    }

    [Theory]
    [InlineData("d6:lengthi1e4:name1:ae")]
    [InlineData("d6:lengthi1e4:name1:a12:piece lengthi16384ee")]
    [InlineData("d6:lengthi1e4:name1:a6:pieces20:00000000000000000000e")]
    public void MissingRequiredPieceMetadata_IsRejected(string info)
    {
        Assert.False(TorrentDownloadPolicy.TryLoad(Torrent(info), out var torrent, out _));
        Assert.Null(torrent);
    }

    [Fact]
    public void EveryTruncation_IsRejectedWithoutPartialResult()
    {
        var valid = Torrent("d6:lengthi1e4:name1:a" + PieceFields(1) + "e");
        for (var length = 0; length < valid.Length; length++)
        {
            Assert.False(TorrentDownloadPolicy.TryLoad(valid.AsSpan(0, length), out var torrent, out _));
            Assert.Null(torrent);
        }
    }

    private static byte[] Torrent(string info) => Encoding.UTF8.GetBytes("d4:info" + info + "e");

    private static string Str(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    private static string File(long length, params string[] path) =>
        $"d6:lengthi{length}e4:pathl{string.Concat(path.Select(Str))}ee";

    private static string PieceFields(long totalLength, int pieceLength = 16384)
    {
        var pieceCount = checked((int)((totalLength + pieceLength - 1) / pieceLength));
        var pieces     = new string('0', checked(pieceCount * 20));
        return $"12:piece lengthi{pieceLength}e6:pieces{Str(pieces)}";
    }
}
