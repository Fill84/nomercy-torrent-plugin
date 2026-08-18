using System.Buffers.Binary;
using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// What a peer said about other peers.
/// </summary>
/// <param name="Added">Who has joined since it last said anything.</param>
/// <param name="Dropped">Who has gone.</param>
public sealed record PexUpdate(IReadOnlyList<PeerAddress> Added, IReadOnlyList<PeerAddress> Dropped);

/// <summary>
/// BEP 11: peers telling each other who else is here.
/// </summary>
/// <remarks>
/// <para>
/// A swarm found through one tracker is the swarm that tracker knows. Peer
/// exchange is how the rest of it arrives, and on a torrent whose tracker is
/// slow it is most of what a client ever connects to.
/// </para>
/// <para>
/// Differences, never the whole list: what goes out is who joined and who left
/// since the last message to <em>that</em> peer, which is why the schedule is
/// per peer and not global.
/// </para>
/// </remarks>
public static class Pex
{
    /// <summary>How long a compact peer is: four of address, two of port.</summary>
    public const int CompactLength = 6;

    /// <summary>
    /// The least time between two messages to the same peer.
    /// </summary>
    /// <remarks>
    /// A minute, from docs/06-torrent-client.md. BEP 11 says a client sending
    /// them faster is misbehaving, and a peer that thinks so disconnects.
    /// </remarks>
    public static TimeSpan LeastInterval { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most peers one message carries.
    /// </summary>
    /// <remarks>
    /// Fifty, from BEP 11. A client with a thousand peers that sent all of them
    /// would send fifty kilobytes to every peer it has, which is bandwidth the
    /// download wanted.
    /// </remarks>
    public const int MostPerMessage = 50;

    /// <summary>Writes one update, under the id that peer asked for.</summary>
    /// <remarks>
    /// IPv4 only. <c>added6</c> and <c>dropped6</c> are BEP 11's as well and
    /// nothing in this client has an IPv6 address to put in them yet; sending
    /// an empty one would say something untrue about what we know.
    /// </remarks>
    public static PeerMessage Write(int theirId, IEnumerable<PeerAddress> added, IEnumerable<PeerAddress> dropped)
    {
        byte[] joined = Compact(added.Take(MostPerMessage));

        return Extensions.Extended(
            theirId,
            new BencodeDictionary(
            [
                new("added"u8.ToArray(), new BencodeBytes(joined)),

                // One flag byte per added peer, all nought: this client knows
                // nothing about whether a peer prefers encryption or is a seed,
                // and a length that did not match `added` is what makes a
                // client drop the message whole.
                new("added.f"u8.ToArray(), new BencodeBytes(new byte[joined.Length / CompactLength])),
                new("dropped"u8.ToArray(), new BencodeBytes(Compact(dropped.Take(MostPerMessage)))),
            ]));
    }

    /// <summary>Reads one.</summary>
    /// <exception cref="PeerProtocolException">It is not a <c>ut_pex</c> message.</exception>
    public static PexUpdate Read(PeerMessage message)
    {
        if (message.Id != PeerMessageId.Extended || message.Payload.Length < 1)
        {
            throw new PeerProtocolException("That is not an extended message.");
        }

        if (Bencode.ReadPrefix(message.Payload.AsSpan(1)).Root is not BencodeDictionary body)
        {
            throw new PeerProtocolException("A ut_pex message is a dictionary, and this one is not.");
        }

        return new(Peers(body.Bytes("added")), Peers(body.Bytes("dropped")));
    }

    /// <summary>The peers in a compact string.</summary>
    private static IReadOnlyList<PeerAddress> Peers(byte[]? compact)
    {
        List<PeerAddress> peers = [];

        for (int at = 0; compact is not null && at + CompactLength <= compact.Length; at += CompactLength)
        {
            peers.Add(new(
                new IPAddress(compact.AsSpan(at, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(compact.AsSpan(at + 4, 2))));
        }

        return peers;
    }

    private static byte[] Compact(IEnumerable<PeerAddress> peers)
    {
        List<byte> compact = [];

        foreach (PeerAddress peer in peers)
        {
            compact.AddRange(peer.Address.GetAddressBytes());
            compact.AddRange([(byte)(peer.Port >> 8), (byte)peer.Port]);
        }

        return [.. compact];
    }
}

/// <summary>
/// What each peer has been told, and when it was last told anything.
/// </summary>
/// <remarks>
/// <para>
/// One of these per torrent. It answers two questions: may this peer be sent an
/// update yet, and what has changed since the last one it got.
/// </para>
/// <para>
/// A private torrent is refused both. BEP 27's rule is the whole of it: a
/// private tracker knows every peer on the torrent, and a client that passed
/// peers around behind its back would have its owner's account closed — this is
/// the half that would be seen, because the peer being told is somebody else's
/// client.
/// </para>
/// </remarks>
public sealed class PeerExchange(TorrentMetadata torrent, TimeProvider time)
{
    private readonly Dictionary<string, Told> _told = new(StringComparer.Ordinal);

    /// <summary>Whether this torrent may use it at all.</summary>
    public bool Allowed => !torrent.Private;

    /// <summary>
    /// The message for this peer, or null when there is nothing to say or it is
    /// too soon to say it.
    /// </summary>
    /// <param name="peer">Which peer, as its address.</param>
    /// <param name="theirId">The id it asked for <c>ut_pex</c> under.</param>
    /// <param name="known">Everybody this client is connected to now.</param>
    public PeerMessage? Offer(string peer, int theirId, IReadOnlyCollection<PeerAddress> known)
    {
        if (!Allowed)
        {
            return null;
        }

        DateTimeOffset now = time.GetUtcNow();

        if (!_told.TryGetValue(peer, out Told? already))
        {
            already = new(DateTimeOffset.MinValue, []);
            _told[peer] = already;
        }
        else if (now - already.At < Pex.LeastInterval)
        {
            // Too soon. BEP 11 calls a client that sends them faster
            // misbehaving, and a peer that agrees disconnects.
            return null;
        }

        HashSet<string> now_ = new(known.Select(one => one.ToString()), StringComparer.Ordinal);

        PeerAddress[] added = [.. known.Where(one => !already.Peers.Contains(one.ToString()))];
        PeerAddress[] dropped = [.. already.Sent.Where(one => !now_.Contains(one.ToString()))];

        if (added.Length == 0 && dropped.Length == 0)
        {
            // Nothing has changed. A message saying so is a message the peer
            // has to read for nothing.
            return null;
        }

        _told[peer] = new(now, [.. known]);

        return Pex.Write(theirId, added, dropped);
    }

    /// <summary>What a peer told us, or nothing at all when the torrent is private.</summary>
    public PexUpdate Read(PeerMessage message)
    {
        return Allowed ? Pex.Read(message) : new([], []);
    }

    /// <summary>What one peer was last sent, and when.</summary>
    private sealed record Told(DateTimeOffset At, IReadOnlyList<PeerAddress> Sent)
    {
        public HashSet<string> Peers { get; } = new(Sent.Select(one => one.ToString()), StringComparer.Ordinal);
    }
}
