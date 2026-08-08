// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers;

public class PeerConnectionTests
{
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static TorrentMetadata Season() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(8)
        .WithFile("season/a.bin", "aaaabbbb")
        .WithFile("season/b.bin", "cccc")
        .Build());

    private static async Task<(PeerConnection Dialler, PeerConnection Answerer)> ConnectAsync(
        TorrentMetadata metadata,
        CancellationToken ct)
    {
        (Stream left, Stream right) = DuplexPair.Create();

        Task<PeerConnection> dialling = PeerConnection.DialAsync(left, metadata, Handshake.NewPeerId(), ct);
        Task<PeerConnection> answering = PeerConnection.AcceptAsync(right, metadata, Handshake.NewPeerId(), ct);

        return (await dialling, await answering);
    }

    [Fact]
    public async Task DialAndAccept_EachLearnTheOthersPeerId()
    {
        CancellationToken ct = Timeout();
        (PeerConnection dialler, PeerConnection answerer) = await ConnectAsync(Season(), ct);

        dialler.RemotePeerId.Should().Equal(answerer.LocalPeerId);
        answerer.RemotePeerId.Should().Equal(dialler.LocalPeerId);
        dialler.RemotePeerId.Should().NotEqual(dialler.LocalPeerId);
    }

    [Fact]
    public async Task DialAndAccept_BothReportExtensionProtocolSupport()
    {
        CancellationToken ct = Timeout();
        (PeerConnection dialler, PeerConnection answerer) = await ConnectAsync(Season(), ct);

        // Part two needs BEP 10 for magnet metadata, and the only chance to learn
        // whether a peer speaks it is this handshake.
        dialler.SupportsExtensionProtocol.Should().BeTrue();
        answerer.SupportsExtensionProtocol.Should().BeTrue();
    }

    [Fact]
    public async Task SendAndReceive_CarryMessagesBothWays()
    {
        CancellationToken ct = Timeout();
        (PeerConnection dialler, PeerConnection answerer) = await ConnectAsync(Season(), ct);

        await dialler.SendAsync(new Interested(), ct);
        (await answerer.ReceiveAsync(ct)).Should().Be(new Interested());

        await answerer.SendAsync(new Unchoke(), ct);
        (await dialler.ReceiveAsync(ct)).Should().Be(new Unchoke());

        await answerer.SendAsync(new PieceBlock(0, 0, [1, 2, 3, 4]), ct);
        (await dialler.ReceiveAsync(ct)).Should().Be(new PieceBlock(0, 0, [1, 2, 3, 4]));
    }

    [Fact]
    public async Task SendAsync_KeepsMessagesIntactWhenCalledConcurrently()
    {
        CancellationToken ct = Timeout();
        (PeerConnection dialler, PeerConnection answerer) = await ConnectAsync(Season(), ct);

        // RC4 is a stream cipher: two writes interleaving would corrupt both. The
        // coordinator will have several reasons to send at once, so the connection
        // has to serialise for itself rather than trusting every caller to.
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            dialler.SendAsync(new Have(index), ct)));

        List<int> received = [];

        for (int index = 0; index < 40; index++)
            received.Add(((Have)await answerer.ReceiveAsync(ct)).PieceIndex);

        received.Should().BeEquivalentTo(Enumerable.Range(0, 40));
    }

    [Fact]
    public async Task DialAsync_RefusesAPeerServingAnotherTorrent()
    {
        CancellationToken ct = Timeout();
        (Stream left, Stream right) = DuplexPair.Create();

        TorrentMetadata other = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("other")
            .WithPieceLength(4)
            .WithFile("other", "zzzz")
            .Build());

        Task<PeerConnection> dialling = PeerConnection.DialAsync(left, Season(), Handshake.NewPeerId(), ct);
        Task<PeerConnection> answering = PeerConnection.AcceptAsync(right, other, Handshake.NewPeerId(), ct);

        Func<Task> accept = () => answering;
        await accept.Should().ThrowAsync<PeerProtocolException>();

        dialling.IsCompletedSuccessfully.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledTwice()
    {
        CancellationToken ct = Timeout();
        (PeerConnection dialler, _) = await ConnectAsync(Season(), ct);

        await dialler.DisposeAsync();

        Func<Task> again = async () => await dialler.DisposeAsync();
        await again.Should().NotThrowAsync();
    }
}
