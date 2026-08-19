using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// A peer that dialled in, and which torrent it came for.
/// </summary>
/// <param name="InfoHash">The torrent it asked for, which this client is holding.</param>
/// <param name="Introduction">Who it said it was.</param>
/// <param name="Wire">
/// The connection, decrypted if it needed to be, with anything read ahead of
/// the handshake already replayed onto it.
/// </param>
public sealed record PeerArrival(byte[] InfoHash, PeerHandshake Introduction, Stream Wire);

/// <summary>
/// Answering a peer that dialled in.
/// </summary>
/// <remarks>
/// <para>
/// Every connection this client made before this it made itself. A client that
/// only dials out is one nobody can reach: it never seeds to a peer that found
/// it, and it never meets the half of a swarm that is behind a router of its
/// own. docs/06-torrent-client.md gives it a listening socket for exactly this.
/// </para>
/// <para>
/// Plaintext and encrypted arrive on the same port and have to be told apart
/// from the first byte, because a peer says nothing about which it is going to
/// use. A BitTorrent handshake opens with nineteen — the length of the protocol
/// name — and an encryption handshake opens with ninety-six bytes of public
/// key, which is nineteen once in every two hundred and fifty-six tries. That
/// is why the whole handshake is checked and not just its first byte.
/// </para>
/// </remarks>
public static class PeerWelcome
{
    /// <summary>
    /// Takes a peer's introduction and answers it, or drops the connection.
    /// </summary>
    /// <remarks>
    /// Null for anything this client will not talk to: a peer asking for a
    /// torrent it is not holding, one that hung up mid-handshake, one whose
    /// encryption could not be agreed. None of them is a fault — a listening
    /// socket meets all three every day.
    /// </remarks>
    /// <param name="wire">The connection, as accepted.</param>
    /// <param name="torrents">Every info hash this client is holding.</param>
    /// <param name="peerId">What this client calls itself.</param>
    /// <param name="random">Where the encryption key and padding come from.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<PeerArrival?> AcceptAsync(
        Stream wire,
        IReadOnlyCollection<byte[]> torrents,
        byte[] peerId,
        RandomNumberGenerator random,
        CancellationToken ct)
    {
        try
        {
            byte[] first = new byte[1];

            await wire.ReadExactlyAsync(first, ct).ConfigureAwait(false);

            return first[0] == Handshake.Protocol.Length
                ? await PlainAsync(wire, torrents, peerId, first, ct).ConfigureAwait(false)
                : await EncryptedAsync(wire, torrents, peerId, first, random, ct).ConfigureAwait(false);
        }
        catch (Exception gone) when (gone is not OperationCanceledException)
        {
            // A peer that hung up, spoke nonsense, or wanted a torrent this
            // client is not holding. There are always more peers.
            return null;
        }
    }

    /// <summary>A handshake in the clear, with its first byte already read.</summary>
    private static async Task<PeerArrival?> PlainAsync(
        Stream wire,
        IReadOnlyCollection<byte[]> torrents,
        byte[] peerId,
        byte[] first,
        CancellationToken ct)
    {
        byte[] rest = new byte[Handshake.Length - 1];

        await wire.ReadExactlyAsync(rest, ct).ConfigureAwait(false);

        return await AnswerAsync(wire, torrents, peerId, [.. first, .. rest], default, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An encrypted dial, with its first byte already read.
    /// </summary>
    /// <remarks>
    /// The byte goes back on the front of the stream before the negotiation
    /// sees it: it is the first of the peer's public key, and a key ninety-five
    /// bytes long agrees with nobody.
    /// </remarks>
    private static async Task<PeerArrival?> EncryptedAsync(
        Stream wire,
        IReadOnlyCollection<byte[]> torrents,
        byte[] peerId,
        byte[] first,
        RandomNumberGenerator random,
        CancellationToken ct)
    {
        MseLink link = await MseNegotiation
            .AcceptAsync(new HeadStart(wire, first), torrents, MseMethod.Plaintext | MseMethod.Rc4, random, ct)
            .ConfigureAwait(false);

        byte[] theirs = new byte[Handshake.Length];
        ReadOnlyMemory<byte> already = link.Initial;

        if (already.Length >= Handshake.Length)
        {
            already[..Handshake.Length].CopyTo(theirs);
            already = already[Handshake.Length..];
        }
        else
        {
            already.CopyTo(theirs);

            await link.Stream
                .ReadExactlyAsync(theirs.AsMemory(already.Length), ct)
                .ConfigureAwait(false);

            already = default;
        }

        return await AnswerAsync(link.Stream, torrents, peerId, theirs, already, ct).ConfigureAwait(false);
    }

    /// <summary>Checks whose torrent it is, and answers with our own handshake.</summary>
    private static async Task<PeerArrival?> AnswerAsync(
        Stream wire,
        IReadOnlyCollection<byte[]> torrents,
        byte[] peerId,
        byte[] theirs,
        ReadOnlyMemory<byte> already,
        CancellationToken ct)
    {
        if (Handshake.Read(theirs) is not PeerHandshake introduction)
        {
            return null;
        }

        byte[]? wanted = torrents.FirstOrDefault(one => Handshake.IsFor(introduction, one));

        if (wanted is null)
        {
            // A different swarm, not a confused peer. Answering would be
            // agreeing to serve a file this client has never heard of.
            return null;
        }

        await wire.WriteAsync(Handshake.Write(wanted, peerId), ct).ConfigureAwait(false);
        await wire.FlushAsync(ct).ConfigureAwait(false);

        return new(wanted, introduction, already.IsEmpty ? wire : new HeadStart(wire, already));
    }
}
