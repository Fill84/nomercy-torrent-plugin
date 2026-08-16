using System.Buffers.Binary;
using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// The UDP tracker protocol: BEP 15.
/// </summary>
/// <remarks>
/// Two exchanges. The first exists only to be given a connection id, which is
/// then good for one minute; the second is the announce itself. Only the bytes
/// are here — when to retry and how long an id is kept are the client's, and
/// keeping them apart is what lets this be tested against a real tracker's real
/// datagrams.
/// </remarks>
public static class UdpAnnounce
{
    /// <summary>
    /// The number every connect request starts with.
    /// </summary>
    /// <remarks>
    /// BEP 15's own magic. A tracker that does not see it answers nothing at
    /// all, which is indistinguishable from a tracker that is down.
    /// </remarks>
    public const long ProtocolId = 0x41727101980L;

    /// <summary>How long a connection id is good for.</summary>
    /// <remarks>
    /// One minute, from BEP 15. Reusing it past that earns an error rather than
    /// an announce; asking for a new one every time doubles every announce.
    /// </remarks>
    public static readonly TimeSpan ConnectionIdLife = TimeSpan.FromMinutes(1);

    /// <summary>What the tracker is being asked to do.</summary>
    public enum Action
    {
        Connect = 0,
        Announce = 1,
        Scrape = 2,
        Error = 3,
    }

    /// <summary>The sixteen bytes that ask for a connection id.</summary>
    public static byte[] ConnectRequest(int transactionId)
    {
        byte[] datagram = new byte[16];

        BinaryPrimitives.WriteInt64BigEndian(datagram, ProtocolId);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), (int)Action.Connect);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(12), transactionId);

        return datagram;
    }

    /// <summary>
    /// The connection id in a connect answer.
    /// </summary>
    /// <remarks>
    /// The transaction id is checked rather than trusted: UDP has no
    /// connection, and an answer to somebody else's question arrives at this
    /// socket looking exactly like an answer to ours.
    /// </remarks>
    public static long ReadConnect(ReadOnlySpan<byte> answer, int transactionId)
    {
        if (answer.Length < 16)
        {
            throw new TrackerException($"A connect answer is sixteen bytes and this one is {answer.Length}.");
        }

        int action = BinaryPrimitives.ReadInt32BigEndian(answer);
        int echoed = BinaryPrimitives.ReadInt32BigEndian(answer[4..]);

        if (echoed != transactionId)
        {
            throw new TrackerException("The tracker answered a question nobody here asked.");
        }

        if (action == (int)Action.Error)
        {
            throw new TrackerException(Error(answer));
        }

        if (action != (int)Action.Connect)
        {
            throw new TrackerException($"A connect answer says action 0, and this one says {action}.");
        }

        return BinaryPrimitives.ReadInt64BigEndian(answer[8..]);
    }

    /// <summary>The ninety-eight bytes of an announce.</summary>
    public static byte[] AnnounceRequest(long connectionId, int transactionId, AnnounceRequest request)
    {
        byte[] datagram = new byte[98];

        BinaryPrimitives.WriteInt64BigEndian(datagram, connectionId);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), (int)Action.Announce);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(12), transactionId);

        request.InfoHash.CopyTo(datagram.AsSpan(16));
        request.PeerId.CopyTo(datagram.AsSpan(36));

        BinaryPrimitives.WriteInt64BigEndian(datagram.AsSpan(56), request.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(datagram.AsSpan(64), request.Left);
        BinaryPrimitives.WriteInt64BigEndian(datagram.AsSpan(72), request.Uploaded);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(80), (int)request.Event);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(84), 0);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(88), 0);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(92), request.NumWant);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(96), (ushort)request.Port);

        return datagram;
    }

    /// <summary>Reads an announce answer: the counts, then six bytes per peer.</summary>
    public static AnnounceResponse ReadAnnounce(ReadOnlySpan<byte> answer, int transactionId)
    {
        if (answer.Length < 20)
        {
            throw new TrackerException($"An announce answer is at least twenty bytes and this one is {answer.Length}.");
        }

        int action = BinaryPrimitives.ReadInt32BigEndian(answer);
        int echoed = BinaryPrimitives.ReadInt32BigEndian(answer[4..]);

        if (echoed != transactionId)
        {
            throw new TrackerException("The tracker answered a question nobody here asked.");
        }

        if (action == (int)Action.Error)
        {
            return new(TimeSpan.Zero, null, null, null, [], Error(answer));
        }

        if (action != (int)Action.Announce)
        {
            throw new TrackerException($"An announce answer says action 1, and this one says {action}.");
        }

        List<PeerAddress> peers = [];

        for (int at = 20; at + 6 <= answer.Length; at += 6)
        {
            peers.Add(new(
                new IPAddress(answer.Slice(at, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(answer.Slice(at + 4, 2))));
        }

        return new(
            TimeSpan.FromSeconds(BinaryPrimitives.ReadInt32BigEndian(answer[8..])),
            null,
            BinaryPrimitives.ReadInt32BigEndian(answer[16..]),
            BinaryPrimitives.ReadInt32BigEndian(answer[12..]),
            peers);
    }

    /// <summary>
    /// How long to wait before the nth try.
    /// </summary>
    /// <remarks>
    /// <c>15 * 2^n</c> seconds, from BEP 15, up to eight tries — a quarter of
    /// an hour by the last one. UDP loses datagrams silently, so a client that
    /// gave up after one would call a working tracker dead; one that retried
    /// tightly would be a client the tracker blocks.
    /// </remarks>
    public static TimeSpan Backoff(int attempt)
    {
        return TimeSpan.FromSeconds(15 * Math.Pow(2, Math.Clamp(attempt, 0, Tries - 1)));
    }

    /// <summary>How many times a datagram is worth sending.</summary>
    public const int Tries = 8;

    private static string Error(ReadOnlySpan<byte> answer)
    {
        return answer.Length > 8
            ? System.Text.Encoding.UTF8.GetString(answer[8..]).Trim()
            : "The tracker refused, and said nothing about why.";
    }
}
