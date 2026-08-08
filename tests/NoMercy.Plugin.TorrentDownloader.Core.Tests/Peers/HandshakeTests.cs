// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers;

public class HandshakeTests
{
    private static readonly byte[] InfoHash = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
    private static readonly byte[] PeerId = Encoding.ASCII.GetBytes("-NM0100-abcdefghijkl");

    [Fact]
    public void Write_ProducesTheSixtyEightByteHandshake()
    {
        byte[] written = Handshake.Write(InfoHash, PeerId);

        written.Should().HaveCount(68);
        written[0].Should().Be(19);
        Encoding.ASCII.GetString(written, 1, 19).Should().Be("BitTorrent protocol");
        written.AsSpan(28, 20).ToArray().Should().Equal(InfoHash);
        written.AsSpan(48, 20).ToArray().Should().Equal(PeerId);
    }

    [Fact]
    public void Write_AdvertisesTheExtensionProtocol()
    {
        byte[] written = Handshake.Write(InfoHash, PeerId);

        // BEP 10 lives in bit 20 of the reserved block, counting from the left.
        // Part two needs it for magnet metadata, and it costs nothing to say so now.
        (written[25] & 0x10).Should().NotBe(0);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheRemotePeerId()
    {
        byte[] remoteId = Encoding.ASCII.GetBytes("-XX0000-zyxwvutsrqpo");
        MemoryStream stream = new(Handshake.Write(InfoHash, remoteId));

        Handshake handshake = await Handshake.ReadAsync(stream, InfoHash, CancellationToken.None);

        handshake.PeerId.Should().Equal(remoteId);
        handshake.InfoHash.Should().Equal(InfoHash);
    }

    [Fact]
    public async Task ReadAsync_RejectsAHandshakeForAnotherTorrent()
    {
        byte[] otherHash = Enumerable.Repeat((byte)0xAB, 20).ToArray();
        MemoryStream stream = new(Handshake.Write(otherHash, PeerId));

        Func<Task> read = () => Handshake.ReadAsync(stream, InfoHash, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<PeerProtocolException>().WithMessage("*info hash*");
    }

    [Fact]
    public async Task ReadAsync_RejectsAWrongProtocolName()
    {
        byte[] handshake = Handshake.Write(InfoHash, PeerId);
        handshake[1] = (byte)'X';
        MemoryStream stream = new(handshake);

        Func<Task> read = () => Handshake.ReadAsync(stream, InfoHash, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<PeerProtocolException>();
    }

    [Fact]
    public async Task ReadAsync_RejectsAWrongProtocolLength()
    {
        byte[] handshake = Handshake.Write(InfoHash, PeerId);
        handshake[0] = 20;
        MemoryStream stream = new(handshake);

        Func<Task> read = () => Handshake.ReadAsync(stream, InfoHash, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<PeerProtocolException>();
    }

    [Fact]
    public async Task ReadAsync_RejectsAStreamThatEndsEarly()
    {
        MemoryStream stream = new(Handshake.Write(InfoHash, PeerId)[..40]);

        Func<Task> read = () => Handshake.ReadAsync(stream, InfoHash, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public void NewPeerId_IsTwentyBytesAndCarriesTheClientTag()
    {
        byte[] first = Handshake.NewPeerId();
        byte[] second = Handshake.NewPeerId();

        first.Should().HaveCount(20);
        Encoding.ASCII.GetString(first, 0, 8).Should().Be("-NM0100-");
        first.Should().NotEqual(second);
    }

    [Fact]
    public void Write_RejectsAnInfoHashOrPeerIdOfTheWrongSize()
    {
        Action shortHash = () => Handshake.Write([1, 2, 3], PeerId);
        Action shortId = () => Handshake.Write(InfoHash, [1, 2, 3]);

        shortHash.Should().Throw<ArgumentException>();
        shortId.Should().Throw<ArgumentException>();
    }
}
