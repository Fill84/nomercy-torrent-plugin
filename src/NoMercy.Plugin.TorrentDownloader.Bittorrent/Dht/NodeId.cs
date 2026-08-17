using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Twenty bytes naming a node, or a torrent, in the one space they share.
/// </summary>
/// <remarks>
/// The whole of Kademlia is that a node id and an info hash are the same kind
/// of thing: "who is nearest this torrent" is the same question as "who is
/// nearest this id", and distance is exclusive-or. That is why the routing
/// table can be one structure for every torrent at once.
/// </remarks>
public sealed class NodeId : IEquatable<NodeId>
{
    private readonly byte[] _bytes;

    public NodeId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new PeerProtocolException($"A node id is {Length} bytes, and this one is {bytes.Length}.");
        }

        _bytes = bytes.ToArray();
    }

    /// <summary>How long one is.</summary>
    public const int Length = 20;

    /// <summary>How many bits, which is how many buckets there can be.</summary>
    public const int Bits = Length * 8;

    /// <summary>The bytes themselves.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>A fresh random id, which is what a client's own is.</summary>
    public static NodeId Random()
    {
        return new(RandomNumberGenerator.GetBytes(Length));
    }

    /// <summary>The distance between two ids, which is exclusive-or.</summary>
    public byte[] Distance(NodeId other)
    {
        byte[] distance = new byte[Length];

        for (int at = 0; at < Length; at++)
        {
            distance[at] = (byte)(_bytes[at] ^ other._bytes[at]);
        }

        return distance;
    }

    /// <summary>
    /// How many leading bits two ids share, which is the bucket one goes in.
    /// </summary>
    /// <remarks>
    /// Nought means they differ in the very first bit — as far away as the
    /// space allows — and a hundred and sixty means they are the same id. It is
    /// the depth in the tree the split-on-insert bucket structure settles at.
    /// </remarks>
    public int Prefix(NodeId other)
    {
        byte[] distance = Distance(other);

        for (int at = 0; at < Length; at++)
        {
            if (distance[at] != 0)
            {
                return (at * 8) + BitOperations.LeadingZeroCountOfByte(distance[at]);
            }
        }

        return Bits;
    }

    /// <summary>Whether this id is nearer the target than the other one is.</summary>
    public bool Nearer(NodeId than, NodeId target)
    {
        return Distance(target).AsSpan().SequenceCompareTo(than.Distance(target)) < 0;
    }

    public bool Equals(NodeId? other)
    {
        return other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    public override bool Equals(object? other)
    {
        return Equals(other as NodeId);
    }

    public override int GetHashCode()
    {
        return BinaryPrimitives.ReadInt32BigEndian(_bytes);
    }

    public override string ToString()
    {
        return Convert.ToHexString(_bytes);
    }

    private static class BitOperations
    {
        /// <summary>How many nought bits a byte starts with.</summary>
        public static int LeadingZeroCountOfByte(byte value)
        {
            int count = 0;

            for (int bit = 7; bit >= 0 && (value & (1 << bit)) == 0; bit--)
            {
                count++;
            }

            return count;
        }
    }
}

/// <summary>
/// One node: who it is and where it is.
/// </summary>
/// <param name="Id">Its twenty bytes.</param>
/// <param name="Address">Where to send a packet.</param>
public sealed record DhtContact(NodeId Id, IPEndPoint Address)
{
    /// <summary>How long one is in the compact form: twenty of id, four of address, two of port.</summary>
    public const int CompactLength = NodeId.Length + 6;

    /// <summary>The contacts in a <c>nodes</c> string.</summary>
    /// <remarks>
    /// A short tail is ignored rather than thrown at: a node that padded its
    /// answer is still telling the truth about the contacts in front of it, and
    /// dropping the lot would lose a whole round of a search.
    /// </remarks>
    public static IReadOnlyList<DhtContact> Read(ReadOnlySpan<byte> compact)
    {
        List<DhtContact> contacts = [];

        for (int at = 0; at + CompactLength <= compact.Length; at += CompactLength)
        {
            contacts.Add(new(
                new(compact.Slice(at, NodeId.Length)),
                new(
                    new IPAddress(compact.Slice(at + NodeId.Length, 4)),
                    BinaryPrimitives.ReadUInt16BigEndian(compact.Slice(at + NodeId.Length + 4, 2)))));
        }

        return contacts;
    }

    /// <summary>These contacts as one <c>nodes</c> string.</summary>
    public static byte[] Write(IEnumerable<DhtContact> contacts)
    {
        List<byte> compact = [];

        foreach (DhtContact contact in contacts)
        {
            compact.AddRange(contact.Id.Bytes);
            compact.AddRange(contact.Address.Address.GetAddressBytes());
            compact.AddRange([(byte)(contact.Address.Port >> 8), (byte)contact.Address.Port]);
        }

        return [.. compact];
    }
}
