// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Torrents;

public class PieceLayoutTests
{
    // Three files of 4, 6 and 2 bytes with a piece length of 8 gives:
    //   piece 0 = a[0..4] + b[0..4]
    //   piece 1 = b[4..6] + c[0..2]
    private static TorrentMetadata Season() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(8)
        .WithFile("season/a.bin", "aaaa")
        .WithFile("season/b.bin", "bbbbbb")
        .WithFile("season/c.bin", "cc")
        .Build());

    [Fact]
    public void Segments_SplitsAPieceThatSpansTwoFiles()
    {
        IReadOnlyList<FileSegment> segments = PieceLayout.Segments(Season(), 0);

        segments.Should().HaveCount(2);
        segments[0].File.Path.Should().Equal("season", "a.bin");
        segments[0].OffsetInFile.Should().Be(0);
        segments[0].Length.Should().Be(4);
        segments[1].File.Path.Should().Equal("season", "b.bin");
        segments[1].OffsetInFile.Should().Be(0);
        segments[1].Length.Should().Be(4);
    }

    [Fact]
    public void Segments_StartsMidFileWhenThePieceDoes()
    {
        IReadOnlyList<FileSegment> segments = PieceLayout.Segments(Season(), 1);

        segments.Should().HaveCount(2);
        segments[0].File.Path.Should().Equal("season", "b.bin");
        segments[0].OffsetInFile.Should().Be(4);
        segments[0].Length.Should().Be(2);
        segments[1].File.Path.Should().Equal("season", "c.bin");
        segments[1].OffsetInFile.Should().Be(0);
        segments[1].Length.Should().Be(2);
    }

    [Fact]
    public void Segments_CoversExactlyThePieceLength()
    {
        TorrentMetadata metadata = Season();

        for (int index = 0; index < metadata.PieceCount; index++)
        {
            PieceLayout.Segments(metadata, index).Sum(segment => segment.Length)
                .Should().Be(metadata.LengthOfPiece(index));
        }
    }

    [Fact]
    public void Segments_ReturnsOneSegmentForASingleFileTorrent()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("one")
            .WithPieceLength(4)
            .WithFile("one", "abcdefgh")
            .Build());

        PieceLayout.Segments(metadata, 1).Should().ContainSingle()
            .Which.OffsetInFile.Should().Be(4);
    }

    [Fact]
    public void Segments_SkipsAZeroLengthFileEntirely()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("gap")
            .WithPieceLength(8)
            .WithFile("gap/a.bin", "aaaa")
            .WithFile("gap/empty.nfo", "")
            .WithFile("gap/b.bin", "bbbb")
            .Build());

        IReadOnlyList<FileSegment> segments = PieceLayout.Segments(metadata, 0);

        segments.Should().HaveCount(2);
        segments.Should().NotContain(segment => segment.File.Path[1] == "empty.nfo");
    }

    [Fact]
    public void Segments_RejectsAPieceIndexThatDoesNotExist()
    {
        TorrentMetadata metadata = Season();

        Action beyondEnd = () => PieceLayout.Segments(metadata, metadata.PieceCount);

        beyondEnd.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SegmentsFor_RejectsARangeOutsideTheTorrent()
    {
        TorrentMetadata metadata = Season();

        Action beyondEnd = () => PieceLayout.SegmentsFor(metadata, metadata.TotalLength, 1);

        beyondEnd.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SegmentsFor_ReadsABlockThatSitsInsideOneFile()
    {
        IReadOnlyList<FileSegment> segments = PieceLayout.SegmentsFor(Season(), 5, 2);

        segments.Should().ContainSingle();
        segments[0].File.Path.Should().Equal("season", "b.bin");
        segments[0].OffsetInFile.Should().Be(1);
        segments[0].Length.Should().Be(2);
    }
}
