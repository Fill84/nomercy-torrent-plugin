// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Dht;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Dht;

public class NodeIdTests
{
    private static NodeId Id(params byte[] leading)
    {
        byte[] bytes = new byte[20];
        leading.CopyTo(bytes, 0);
        return new NodeId(bytes);
    }

    [Fact]
    public void Distance_IsZeroToItself()
    {
        NodeId id = Id(0x12, 0x34);

        id.DistanceTo(id).Should().Be(NodeId.Zero);
    }

    [Fact]
    public void Distance_IsSymmetric()
    {
        NodeId left = Id(0xF0);
        NodeId right = Id(0x0F);

        left.DistanceTo(right).Should().Be(right.DistanceTo(left));
    }

    [Fact]
    public void Distance_IsTheExclusiveOrOfTheBytes()
    {
        NodeId distance = Id(0b1010_1010).DistanceTo(Id(0b1100_1100));

        distance.Bytes[0].Should().Be(0b0110_0110);
    }

    [Fact]
    public void CompareTo_OrdersByHowFarApartTheyAre()
    {
        NodeId target = Id(0x00);
        NodeId near = Id(0x01);
        NodeId far = Id(0x80);

        target.DistanceTo(near).CompareTo(target.DistanceTo(far)).Should().BeNegative();
    }

    [Fact]
    public void LeadingZeroBits_CountsHowMuchOfThePrefixMatches()
    {
        // Which bucket a node belongs in is decided by this, so an off-by-one here
        // quietly files every node in the wrong place.
        Id(0b1000_0000).DistanceTo(Id(0)).LeadingZeroBits.Should().Be(0);
        Id(0b0100_0000).DistanceTo(Id(0)).LeadingZeroBits.Should().Be(1);
        Id(0b0000_0001).DistanceTo(Id(0)).LeadingZeroBits.Should().Be(7);
        Id(0, 0b1000_0000).DistanceTo(Id(0)).LeadingZeroBits.Should().Be(8);
        NodeId.Zero.LeadingZeroBits.Should().Be(160);
    }

    [Fact]
    public void Random_ProducesTwentyDistinctBytesEachTime()
    {
        NodeId first = NodeId.NewRandom();

        first.Bytes.Should().HaveCount(20);
        first.Should().NotBe(NodeId.NewRandom());
    }

    [Fact]
    public void Constructor_RejectsAnythingThatIsNotTwentyBytes()
    {
        Action wrong = () => _ = new NodeId(new byte[19]);

        wrong.Should().Throw<ArgumentException>();
    }
}

public class RoutingTableTests
{
    private static readonly NodeId Us = new(Enumerable.Repeat((byte)0x00, 20).ToArray());

    private static DhtNode Node(byte first, byte second = 0, int port = 6881)
    {
        byte[] bytes = new byte[20];
        bytes[0] = first;
        bytes[1] = second;

        return new DhtNode(new NodeId(bytes), new PeerEndPoint(IPAddress.Parse("10.0.0.1"), port));
    }

    [Fact]
    public void Add_KeepsANode()
    {
        RoutingTable table = new(Us);

        table.Add(Node(0x80)).Should().BeTrue();
        table.Count.Should().Be(1);
    }

    [Fact]
    public void Add_IgnoresOurself()
    {
        RoutingTable table = new(Us);

        table.Add(new DhtNode(Us, new PeerEndPoint(IPAddress.Loopback, 6881))).Should().BeFalse();
        table.Count.Should().Be(0);
    }

    [Fact]
    public void Add_ReplacesAnEntryForTheSameNodeRatherThanDuplicatingIt()
    {
        RoutingTable table = new(Us);

        table.Add(Node(0x80, port: 6881));
        table.Add(Node(0x80, port: 51413));

        table.Count.Should().Be(1);
        table.Closest(Us, 1)[0].EndPoint.Port.Should().Be(51413);
    }

    [Fact]
    public void Add_StopsAtEightNodesPerBucket()
    {
        RoutingTable table = new(Us);

        // Every one of these shares a first bit with the others, so they compete for
        // one bucket. Kademlia keeps eight per bucket and prefers the ones already
        // known to answer, so the ninth is refused rather than evicting a live node.
        for (int index = 0; index < 8; index++)
            table.Add(Node(0x80, (byte)index)).Should().BeTrue();

        table.Add(Node(0x80, 99)).Should().BeFalse();
        table.Count.Should().Be(8);
    }

    [Fact]
    public void Add_SplitsAcrossBucketsSoDistantNodesDoNotCrowdNearOnes()
    {
        RoutingTable table = new(Us);

        for (int index = 0; index < 8; index++)
            table.Add(Node(0x80, (byte)index));

        // A node with a different leading bit belongs in another bucket entirely, and
        // a full bucket elsewhere must not keep it out.
        table.Add(Node(0x40)).Should().BeTrue();
        table.Count.Should().Be(9);
    }

    [Fact]
    public void Closest_ReturnsNodesNearestTheTargetFirst()
    {
        RoutingTable table = new(Us);
        table.Add(Node(0xFF));
        table.Add(Node(0x01));
        table.Add(Node(0x40));

        NodeId target = new(new byte[20]);
        IReadOnlyList<DhtNode> closest = table.Closest(target, 3);

        closest.Should().HaveCount(3);
        closest[0].Id.Bytes[0].Should().Be(0x01);
        closest[2].Id.Bytes[0].Should().Be(0xFF);
    }

    [Fact]
    public void Closest_ReturnsNoMoreThanAsked()
    {
        RoutingTable table = new(Us);

        for (int index = 0; index < 8; index++)
            table.Add(Node((byte)(0x80 + index)));

        table.Closest(Us, 3).Should().HaveCount(3);
    }

    [Fact]
    public void Closest_IsEmptyOnAFreshTable()
    {
        new RoutingTable(Us).Closest(Us, 8).Should().BeEmpty();
    }
}
