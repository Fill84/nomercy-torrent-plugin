// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Torrents;

public class MetadataParserTests
{
    [Fact]
    public void FromTorrentFile_ReadsASingleFileTorrent()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("single")
            .WithPieceLength(4)
            .WithFile("single", "abcdefgh");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.Name.Should().Be("single");
        metadata.PieceLength.Should().Be(4);
        metadata.TotalLength.Should().Be(8);
        metadata.PieceCount.Should().Be(2);
        metadata.Files.Should().ContainSingle();
        metadata.Files[0].Path.Should().Equal("single");
        metadata.Files[0].Offset.Should().Be(0);
    }

    [Fact]
    public void FromTorrentFile_ComputesTheInfoHashOverTheReEncodedInfoDictionary()
    {
        TorrentBuilder builder = new TorrentBuilder().WithName("a.bin").WithFile("a.bin", "content");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.InfoHash.Should().Equal(builder.ExpectedInfoHash());
    }

    [Fact]
    public void FromTorrentFile_LaysMultipleFilesOutEndToEnd()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("season")
            .WithPieceLength(8)
            .WithFile("season/e01.mkv", "aaaa")
            .WithFile("season/e02.mkv", "bbbbbb")
            .WithFile("season/info.nfo", "cc");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.Files.Should().HaveCount(3);
        metadata.Files[0].Offset.Should().Be(0);
        metadata.Files[1].Offset.Should().Be(4);
        metadata.Files[2].Offset.Should().Be(10);
        metadata.TotalLength.Should().Be(12);
        metadata.Files[1].Path.Should().Equal("season", "e02.mkv");
    }

    [Fact]
    public void FromTorrentFile_RoundsThePieceCountUpForAPartialLastPiece()
    {
        TorrentBuilder builder = new TorrentBuilder().WithName("a.bin").WithPieceLength(4).WithFile("a.bin", "abcde");

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        metadata.PieceCount.Should().Be(2);
        metadata.PieceHashes.Should().HaveCount(2);
        metadata.LengthOfPiece(1).Should().Be(1);
    }

    [Fact]
    public void FromTorrentFile_ReadsTheAnnounceUrl()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(
            new TorrentBuilder().WithName("a.bin").WithFile("a.bin", "x").Build());

        metadata.Trackers.Should().Equal("http://tracker.test/announce");
    }

    [Fact]
    public void FromTorrentFile_RejectsAPieceListThatIsNotAMultipleOfTwenty()
    {
        byte[] torrent = Encoding.UTF8.GetBytes(
            "d8:announce19:http://tracker.test4:infod6:lengthi4e4:name1:a12:piece lengthi4e6:pieces5:abcdeee");

        Action parse = () => MetadataParser.FromTorrentFile(torrent);

        parse.Should().Throw<MetadataException>().WithMessage("*20*");
    }

    [Fact]
    public void FromTorrentFile_RejectsAPathThatEscapesTheTorrentFolder()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("evil")
            .WithFile("evil/../../../etc/passwd", "pwned")
            .WithFile("evil/ok.bin", "fine");

        Action parse = () => MetadataParser.FromTorrentFile(builder.Build());

        parse.Should().Throw<MetadataException>();
    }

    [Fact]
    public void FromTorrentFile_RejectsATorrentWithNeitherLengthNorFiles()
    {
        byte[] torrent = Encoding.UTF8.GetBytes(
            "d4:infod4:name1:a12:piece lengthi4e6:pieces0:ee");

        Action parse = () => MetadataParser.FromTorrentFile(torrent);

        parse.Should().Throw<MetadataException>();
    }

    [Fact]
    public void FromTorrentFile_RejectsAPieceLengthOfZero()
    {
        byte[] torrent = Encoding.UTF8.GetBytes(
            "d4:infod6:lengthi4e4:name1:a12:piece lengthi0e6:pieces0:ee");

        Action parse = () => MetadataParser.FromTorrentFile(torrent);

        parse.Should().Throw<MetadataException>();
    }
}
