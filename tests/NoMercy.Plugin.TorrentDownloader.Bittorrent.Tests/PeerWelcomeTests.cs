using System.Security.Cryptography;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Answering a peer that dialled in.
/// </summary>
/// <remarks>
/// Every connection this client has ever made it made itself. A client that
/// only dials is one no peer can reach, so it never seeds and never sees a peer
/// behind its own kind of router — half a swarm, and the half that would have
/// been fastest.
/// </remarks>
public class PeerWelcomeTests
{
    [Fact]
    public async Task APeerThatDialsInInTheClearIsAnsweredAndSaysWhichTorrentItWants()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));

        PeerWire wire = new();

        Task<PeerArrival?> welcoming = PeerWelcome.AcceptAsync(
            wire.Receiver,
            [Silo, Other],
            Id("HOST00"),
            RandomNumberGenerator.Create(),
            stopping.Token);

        Task<PeerConnection?> dialling = PeerConnection.IntroduceAsync(
            wire.Initiator, Silo, Id("GUEST0"), pieces: 8, dialling: true, stopping.Token);

        PeerArrival arrival = Assert.IsType<PeerArrival>(await welcoming);

        Assert.Equal(Silo, arrival.InfoHash);
        Assert.NotNull(await dialling);

        // And it is a conversation, not just a handshake: the connection built
        // from it reads what the peer says next.
        using PeerConnection theirs = new(arrival.Wire, arrival.Introduction, pieces: 8);

        await (await dialling)!.SendAsync(PeerMessage.Of(PeerMessageId.Interested), stopping.Token);

        PeerMessage said = Assert.IsType<PeerMessage>(await theirs.NextAsync(stopping.Token));

        Assert.Equal(PeerMessageId.Interested, said.Id);
    }

    /// <remarks>
    /// A peer asking for a torrent this client is not holding is a different
    /// swarm, not a confused peer — BEP 3 says to drop it. Answering would be
    /// agreeing to serve a file this client has never heard of.
    /// </remarks>
    [Fact]
    public async Task APeerAskingForATorrentThisClientIsNotHoldingIsDropped()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));

        PeerWire wire = new();

        Task<PeerArrival?> welcoming = PeerWelcome.AcceptAsync(
            wire.Receiver,
            [Other],
            Id("HOST00"),
            RandomNumberGenerator.Create(),
            stopping.Token);

        await wire.Initiator.WriteAsync(Handshake.Write(Silo, Id("GUEST0")), stopping.Token);
        await wire.Initiator.FlushAsync(stopping.Token);

        Assert.Null(await welcoming);
    }

    /// <remarks>
    /// The same the other way round, encrypted. A great many peers dial only
    /// like this, and a client that answered nothing but plaintext would be
    /// unreachable to them while looking perfectly healthy.
    /// </remarks>
    [Fact]
    public async Task APeerThatDialsInEncryptedIsAnsweredToo()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));

        PeerWire wire = new();

        Task<PeerArrival?> welcoming = PeerWelcome.AcceptAsync(
            wire.Receiver,
            [Silo],
            Id("HOST00"),
            RandomNumberGenerator.Create(),
            stopping.Token);

        Task<MseLink> dialling = MseNegotiation.InitiateAsync(
            wire.Initiator,
            Silo,
            Handshake.Write(Silo, Id("GUEST0")),
            MseMethod.Rc4,
            RandomNumberGenerator.Create(),
            stopping.Token);

        PeerArrival arrival = Assert.IsType<PeerArrival>(await welcoming);

        Assert.Equal(Silo, arrival.InfoHash);

        MseLink link = await dialling;

        Assert.Equal(MseMethod.Rc4, link.Method);
    }

    private static byte[] Silo => [.. Enumerable.Range(0, 20).Select(one => (byte)one)];

    private static byte[] Other => [.. Enumerable.Range(100, 20).Select(one => (byte)one)];

    private static byte[] Id(string name)
    {
        byte[] id = new byte[20];

        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(id, 0);

        return id;
    }
}
