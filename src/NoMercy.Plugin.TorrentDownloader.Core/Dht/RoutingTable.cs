// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Dht;

/// <summary>
/// A 160-bit identifier. Nodes and torrents share one space, which is what lets
/// "who is nearest this info hash" be a question about identifiers rather than
/// about the network.
/// </summary>
public readonly struct NodeId(byte[] bytes) : IEquatable<NodeId>, IComparable<NodeId>
{
    public const int Length = 20;

    public static NodeId Zero { get; } = new(new byte[Length]);

    public byte[] Bytes { get; } = bytes.Length == Length
        ? bytes
        : throw new ArgumentException($"a node id is {Length} bytes, not {bytes.Length}", nameof(bytes));

    public static NodeId NewRandom()
    {
        byte[] bytes = new byte[Length];
        RandomNumberGenerator.Fill(bytes);
        return new NodeId(bytes);
    }

    /// <summary>
    /// Kademlia's distance: exclusive or, read as a big number. It is symmetric, which
    /// is the property the whole routing scheme leans on - if I am near you, you are
    /// near me, so the network converges from either end.
    /// </summary>
    public NodeId DistanceTo(NodeId other)
    {
        byte[] distance = new byte[Length];

        for (int index = 0; index < Length; index++)
            distance[index] = (byte)(Bytes[index] ^ other.Bytes[index]);

        return new NodeId(distance);
    }

    /// <summary>
    /// How many leading bits are zero, which is the bucket a distance belongs to.
    /// An off-by-one here files every node in the wrong place and the table still
    /// looks plausible.
    /// </summary>
    public int LeadingZeroBits
    {
        get
        {
            for (int index = 0; index < Length; index++)
            {
                if (Bytes[index] == 0)
                    continue;

                return index * 8 + System.Numerics.BitOperations.LeadingZeroCount((uint)Bytes[index]) - 24;
            }

            return Length * 8;
        }
    }

    public int CompareTo(NodeId other)
    {
        for (int index = 0; index < Length; index++)
        {
            int difference = Bytes[index].CompareTo(other.Bytes[index]);

            if (difference != 0)
                return difference;
        }

        return 0;
    }

    public bool Equals(NodeId other) => Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    public override int GetHashCode() => BitConverter.ToInt32(Bytes, 0);

    public override string ToString() => Convert.ToHexStringLower(Bytes);

    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);

    public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;

    public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;

    public static bool operator <=(NodeId left, NodeId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(NodeId left, NodeId right) => left.CompareTo(right) >= 0;
}

public sealed record DhtNode(NodeId Id, PeerEndPoint EndPoint);

/// <summary>
/// Kademlia's routing table: one bucket per distance band, eight nodes each.
///
/// <para>
/// The shape is the point. Far-away nodes share a bucket while near ones get a bucket
/// each, so the table knows the neighbourhood in detail and the rest of the world
/// roughly - which is what makes a lookup converge in a handful of hops rather than
/// needing to know everybody.
/// </para>
/// </summary>
public sealed class RoutingTable(NodeId self)
{
    /// <summary>Eight per bucket, as Kademlia specifies and every DHT implementation uses.</summary>
    public const int BucketSize = 8;

    private readonly List<DhtNode>[] _buckets = [.. Enumerable.Range(0, NodeId.Length * 8).Select(_ => new List<DhtNode>())];

    public int Count => _buckets.Sum(bucket => bucket.Count);

    public bool Add(DhtNode node)
    {
        if (node.Id == self)
            return false;

        List<DhtNode> bucket = _buckets[self.DistanceTo(node.Id).LeadingZeroBits];

        int existing = bucket.FindIndex(known => known.Id == node.Id);

        if (existing >= 0)
        {
            // Same node, new address. Replace rather than keep both, and move it to the
            // end because a node we just heard from is the freshest thing in the bucket.
            bucket.RemoveAt(existing);
            bucket.Add(node);
            return true;
        }

        if (bucket.Count >= BucketSize)
        {
            // Kademlia keeps the nodes it already knows answer. A full bucket refuses a
            // newcomer rather than evicting a live node, because uptime predicts uptime.
            return false;
        }

        bucket.Add(node);
        return true;
    }

    public IReadOnlyList<DhtNode> Closest(NodeId target, int count) =>
    [
        .. _buckets
            .SelectMany(bucket => bucket)
            .OrderBy(node => node.Id.DistanceTo(target))
            .Take(count),
    ];
}
