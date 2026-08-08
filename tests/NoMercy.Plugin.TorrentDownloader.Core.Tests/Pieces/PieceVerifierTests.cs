// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pieces;

public class PieceVerifierTests
{
    private static TorrentMetadata TwoPieces() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("v")
        .WithPieceLength(4)
        .WithFile("v", "abcdefgh")
        .Build());

    [Fact]
    public void Matches_AcceptsTheRealBytes()
    {
        TorrentMetadata metadata = TwoPieces();

        PieceVerifier.Matches(metadata, 0, Encoding.UTF8.GetBytes("abcd")).Should().BeTrue();
        PieceVerifier.Matches(metadata, 1, Encoding.UTF8.GetBytes("efgh")).Should().BeTrue();
    }

    [Fact]
    public void Matches_RejectsAPieceWithOneWrongByte()
    {
        PieceVerifier.Matches(TwoPieces(), 0, Encoding.UTF8.GetBytes("abcX")).Should().BeFalse();
    }

    [Fact]
    public void Matches_RejectsTheRightBytesAtTheWrongIndex()
    {
        PieceVerifier.Matches(TwoPieces(), 1, Encoding.UTF8.GetBytes("abcd")).Should().BeFalse();
    }

    [Fact]
    public void Matches_RejectsAPieceOfTheWrongLength()
    {
        PieceVerifier.Matches(TwoPieces(), 0, Encoding.UTF8.GetBytes("abc")).Should().BeFalse();
    }

    [Fact]
    public void Matches_RejectsAPieceIndexThatDoesNotExist()
    {
        PieceVerifier.Matches(TwoPieces(), 2, Encoding.UTF8.GetBytes("abcd")).Should().BeFalse();
        PieceVerifier.Matches(TwoPieces(), -1, Encoding.UTF8.GetBytes("abcd")).Should().BeFalse();
    }

    [Fact]
    public void Matches_AcceptsAShortFinalPiece()
    {
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("short")
            .WithPieceLength(4)
            .WithFile("short", "abcde")
            .Build());

        PieceVerifier.Matches(metadata, 1, Encoding.UTF8.GetBytes("e")).Should().BeTrue();
    }
}
