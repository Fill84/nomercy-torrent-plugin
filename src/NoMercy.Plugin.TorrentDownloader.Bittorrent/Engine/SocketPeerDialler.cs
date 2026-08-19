using System.Net.Sockets;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Dialling a peer over a real socket.
/// </summary>
/// <remarks>
/// <para>
/// Encrypted first and in the clear when the peer will not have it, which is
/// what <see cref="PeerDial"/> decides. A great many peers refuse a connection
/// that arrives in the clear and a great many refuse the negotiation, so a
/// client that only ever did one of the two throws away half a swarm.
/// </para>
/// <para>
/// Every failure is a peer that would not talk, which is the ordinary case:
/// most of the addresses a tracker hands out are stale. It answers nothing
/// rather than throwing, so one dead address costs nothing above it.
/// </para>
/// </remarks>
public sealed class SocketPeerDialler(TimeSpan patience) : IPeerDialler
{
    private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();

    /// <summary>How long a peer is given to answer before it is left alone.</summary>
    /// <remarks>
    /// A tracker hands out fifty addresses and most of them are gone. Waiting
    /// the operating system's own connect timeout on each is minutes spent on
    /// machines that are not there, while the ones that would answer wait.
    /// </remarks>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(10);

    public SocketPeerDialler()
        : this(DefaultPatience)
    {
    }

    public async Task<PeerConnection?> DialAsync(
        PeerAddress peer,
        byte[] infoHash,
        byte[] peerId,
        int pieces,
        CancellationToken ct)
    {
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

        waiting.CancelAfter(patience);

        try
        {
            MseLink link = await PeerDial
                .ConnectAsync(
                    token => ConnectAsync(peer, token),
                    infoHash,
                    Handshake.Write(infoHash, peerId),
                    _random,
                    waiting.Token)
                .ConfigureAwait(false);

            // Our handshake went out inside the negotiation, and theirs came
            // back with it — along with whatever it sent afterwards.
            return await PeerConnection
                .IntroducedAsync(link.Stream, infoHash, pieces, link.Initial, waiting.Token)
                .ConfigureAwait(false);
        }
        catch (Exception gone) when (gone is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A refused connection, a machine that is not there, a peer that
            // hung up mid-negotiation, or one that took longer than we will
            // wait. None of them is worth a line: there are always more peers.
            return null;
        }
    }

    private static async Task<Stream> ConnectAsync(PeerAddress peer, CancellationToken ct)
    {
        TcpClient socket = new();

        try
        {
            await socket.ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);

            // Small writes go out at once. A handshake held back for forty
            // milliseconds by Nagle's algorithm is a peer that has hung up
            // before this client has finished introducing itself.
            socket.NoDelay = true;

            return socket.GetStream();
        }
        catch
        {
            socket.Dispose();

            throw;
        }
    }
}
