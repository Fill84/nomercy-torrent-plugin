// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Swarm;

public class PieceServerTests
{
    private static TorrentMetadata Metadata() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("four.mkv")
        .WithPieceLength(4)
        .WithFile("four.mkv", "aaaabbbbccccdddd")
        .Build());

    private static Bitfield Everything(int count)
    {
        Bitfield field = new(count);

        for (int index = 0; index < count; index++)
            field[index] = true;

        return field;
    }

    private static async Task<(PieceServer Server, TorrentMetadata Metadata)> ServerAsync(
        TempFolder folder,
        TorrentOrigin origin,
        Bitfield? have = null)
    {
        TorrentMetadata metadata = Metadata();
        FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(0, "aaaa"u8.ToArray(), CancellationToken.None);
        await store.WritePieceAsync(1, "bbbb"u8.ToArray(), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        return (new PieceServer(metadata, store, SwarmPolicy.Default, origin, have ?? Everything(metadata.PieceCount)), metadata);
    }

    [Fact]
    public async Task ServeAsync_RefusesEverythingForAPublicTorrent()
    {
        using TempFolder folder = new();
        (PieceServer server, _) = await ServerAsync(folder, TorrentOrigin.Public);

        // The requirement is not "usually declines". A public torrent has no path to
        // uploading, so this returns nothing no matter what is asked for.
        (await server.ServeAsync(new Request(0, 0, 4), CancellationToken.None)).Should().BeNull();
        server.UploadedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ServeAsync_RefusesForAPrivateTorrentThatIsNotSeeding()
    {
        using TempFolder folder = new();
        (PieceServer server, _) = await ServerAsync(folder, TorrentOrigin.PrivateWithoutSeeding);

        (await server.ServeAsync(new Request(0, 0, 4), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ServeAsync_SendsThePieceForAPrivateTorrentConfiguredToSeed()
    {
        using TempFolder folder = new();
        (PieceServer server, _) = await ServerAsync(folder, TorrentOrigin.PrivateSeeding);

        PieceBlock? block = await server.ServeAsync(new Request(0, 0, 4), CancellationToken.None);

        block.Should().NotBeNull();
        Encoding.ASCII.GetString(block!.Block).Should().Be("aaaa");
        server.UploadedBytes.Should().Be(4);
    }

    [Fact]
    public async Task ServeAsync_RefusesAPieceWeDoNotHold()
    {
        using TempFolder folder = new();
        Bitfield partial = new(4);
        partial[0] = true;

        (PieceServer server, _) = await ServerAsync(folder, TorrentOrigin.PrivateSeeding, partial);

        (await server.ServeAsync(new Request(2, 0, 4), CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(-1, 0, 4)]
    [InlineData(99, 0, 4)]
    [InlineData(0, -1, 4)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 5)]
    [InlineData(0, 2, 4)]
    public async Task ServeAsync_RefusesARequestThatDoesNotFitThePiece(int piece, int begin, int length)
    {
        using TempFolder folder = new();
        (PieceServer server, _) = await ServerAsync(folder, TorrentOrigin.PrivateSeeding);

        // A peer choosing the offsets is untrusted input reaching a file read.
        (await server.ServeAsync(new Request(piece, begin, length), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ServeAsync_RefusesAnAbsurdlyLargeRequest()
    {
        using TempFolder folder = new();
        (PieceServer server, _) = await ServerAsync(folder, TorrentOrigin.PrivateSeeding);

        (await server.ServeAsync(new Request(0, 0, 64 * 1024 * 1024), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ServeAsync_StopsOnceTheRatioTargetIsMet()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = Metadata();
        FilePieceStore store = new(metadata, folder.Path);
        await store.WritePieceAsync(0, "aaaa"u8.ToArray(), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        // Downloaded 8 bytes, target ratio 1.0, so seeding ends after 8 uploaded.
        PieceServer server = new(
            metadata,
            store,
            SwarmPolicy.Default with { SeedRatioTarget = 1.0 },
            TorrentOrigin.PrivateSeeding,
            Everything(metadata.PieceCount)) { DownloadedBytes = 8 };

        (await server.ServeAsync(new Request(0, 0, 4), CancellationToken.None)).Should().NotBeNull();
        (await server.ServeAsync(new Request(0, 0, 4), CancellationToken.None)).Should().NotBeNull();

        server.UploadedBytes.Should().Be(8);
        server.HasMetItsTarget.Should().BeTrue();

        // Target met: this account has given back what it agreed to, and stops.
        (await server.ServeAsync(new Request(0, 0, 4), CancellationToken.None)).Should().BeNull();
    }
}
