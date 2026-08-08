// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Dht;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Dht;

public class DhtPeerSourceTests
{
    private static readonly byte[] InfoHash = Enumerable.Repeat((byte)0x00, 20).ToArray();
    private static readonly NodeId Us = new(Enumerable.Repeat((byte)0xEE, 20).ToArray());

    private static NodeId IdStartingWith(byte first)
    {
        byte[] bytes = new byte[20];
        bytes[0] = first;
        return new NodeId(bytes);
    }

    private static DhtNode Node(byte first, int port) =>
        new(IdStartingWith(first), new PeerEndPoint(IPAddress.Parse("10.0.0." + port % 250), port));

    [Fact]
    public async Task FindPeersAsync_FollowsCloserNodesUntilItFindsPeers()
    {
        DhtNode far = Node(0x80, 1001);
        DhtNode nearer = Node(0x20, 1002);
        DhtNode nearest = Node(0x01, 1003);

        FakeNetwork network = new();

        // Each node knows somebody closer to the target, and only the last one holds
        // peers. That chain is what a real lookup walks.
        network.Answers[far.EndPoint.Port] = FakeNetwork.WithNodes(far.Id, nearer);
        network.Answers[nearer.EndPoint.Port] = FakeNetwork.WithNodes(nearer.Id, nearest);
        network.Answers[nearest.EndPoint.Port] = FakeNetwork.WithPeers(nearest.Id, ("192.168.2.50", 6881));

        RoutingTable table = new(Us);
        table.Add(far);

        DhtPeerSource source = new(Us, table, network);

        IReadOnlyList<PeerEndPoint> peers = await source.FindPeersAsync(InfoHash, CancellationToken.None);

        peers.Should().ContainSingle();
        peers[0].Address.ToString().Should().Be("192.168.2.50");
        peers[0].Port.Should().Be(6881);
    }

    [Fact]
    public async Task FindPeersAsync_LearnsTheNodesItMeets()
    {
        DhtNode bootstrap = Node(0x80, 1001);
        DhtNode discovered = Node(0x10, 1002);

        FakeNetwork network = new();
        network.Answers[bootstrap.EndPoint.Port] = FakeNetwork.WithNodes(bootstrap.Id, discovered);
        network.Answers[discovered.EndPoint.Port] = FakeNetwork.WithPeers(discovered.Id, ("192.168.2.50", 6881));

        RoutingTable table = new(Us);
        table.Add(bootstrap);

        await new DhtPeerSource(Us, table, network).FindPeersAsync(InfoHash, CancellationToken.None);

        // A lookup that does not remember who it met has to start from nothing again.
        table.Count.Should().Be(2);
    }

    [Fact]
    public async Task FindPeersAsync_ReturnsNothingWhenItKnowsNobodyToAsk()
    {
        DhtPeerSource source = new(Us, new RoutingTable(Us), new FakeNetwork());

        (await source.FindPeersAsync(InfoHash, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task FindPeersAsync_KeepsGoingWhenANodeDoesNotAnswer()
    {
        DhtNode silent = Node(0x80, 1001);
        DhtNode helpful = Node(0x40, 1002);

        FakeNetwork network = new();
        network.Answers[helpful.EndPoint.Port] = FakeNetwork.WithPeers(helpful.Id, ("192.168.2.51", 51413));

        RoutingTable table = new(Us);
        table.Add(silent);
        table.Add(helpful);

        // A node that never answers is the steady state on a DHT, not a reason to stop.
        IReadOnlyList<PeerEndPoint> peers = await new DhtPeerSource(Us, table, network)
            .FindPeersAsync(InfoHash, CancellationToken.None);

        peers.Should().ContainSingle().Which.Port.Should().Be(51413);
    }

    [Fact]
    public async Task FindPeersAsync_AsksNobodyTwice()
    {
        DhtNode first = Node(0x80, 1001);
        DhtNode second = Node(0x40, 1002);

        FakeNetwork network = new();

        // Both point back at each other. Without a memory of who was asked, a lookup
        // ping-pongs between them until something times out.
        network.Answers[first.EndPoint.Port] = FakeNetwork.WithNodes(first.Id, second);
        network.Answers[second.EndPoint.Port] = FakeNetwork.WithNodes(second.Id, first);

        RoutingTable table = new(Us);
        table.Add(first);

        await new DhtPeerSource(Us, table, network).FindPeersAsync(InfoHash, CancellationToken.None);

        network.Asked.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FindPeersAsync_MergesPeersFromSeveralNodesWithoutRepeatingThem()
    {
        DhtNode one = Node(0x40, 1001);
        DhtNode two = Node(0x20, 1002);

        FakeNetwork network = new();
        network.Answers[one.EndPoint.Port] = FakeNetwork.WithPeers(one.Id, ("192.168.2.50", 6881), ("192.168.2.51", 6881));
        network.Answers[two.EndPoint.Port] = FakeNetwork.WithPeers(two.Id, ("192.168.2.51", 6881), ("192.168.2.52", 6881));

        RoutingTable table = new(Us);
        table.Add(one);
        table.Add(two);

        IReadOnlyList<PeerEndPoint> peers = await new DhtPeerSource(Us, table, network)
            .FindPeersAsync(InfoHash, CancellationToken.None);

        peers.Should().HaveCount(3).And.OnlyHaveUniqueItems();
    }

    private sealed class FakeNetwork : IUdpTransport
    {
        public Dictionary<int, byte[]> Answers { get; } = [];

        public List<int> Asked { get; } = [];

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] request, CancellationToken ct)
        {
            Asked.Add(port);

            if (!Answers.TryGetValue(port, out byte[]? answer))
                throw new IOException($"nothing is listening on {port}");

            // A real reply echoes the transaction id from the question.
            BDictionary query = (BDictionary)BencodeReader.Parse(request);
            BDictionary reply = (BDictionary)BencodeReader.Parse(answer);

            Dictionary<string, BValue> echoed = reply.Entries.ToDictionary(entry => entry.Key, entry => entry.Value);
            echoed["t"] = query.Entries["t"];

            return Task.FromResult(BencodeWriter.Write(new BDictionary(echoed)));
        }

        public static byte[] WithNodes(NodeId from, params DhtNode[] nodes)
        {
            List<byte> compact = [];

            foreach (DhtNode node in nodes)
            {
                compact.AddRange(node.Id.Bytes);
                compact.AddRange(node.EndPoint.Address.GetAddressBytes());
                compact.Add((byte)(node.EndPoint.Port >> 8));
                compact.Add((byte)(node.EndPoint.Port & 0xFF));
            }

            return Reply(new Dictionary<string, BValue>
            {
                ["id"] = new BBytes(from.Bytes),
                ["nodes"] = new BBytes([.. compact]),
            });
        }

        public static byte[] WithPeers(NodeId from, params (string Address, int Port)[] peers) =>
            Reply(new Dictionary<string, BValue>
            {
                ["id"] = new BBytes(from.Bytes),
                ["token"] = new BBytes("opaque"u8.ToArray()),
                ["values"] = new BList([.. peers.Select(peer => (BValue)new BBytes(
                [
                    .. IPAddress.Parse(peer.Address).GetAddressBytes(),
                    (byte)(peer.Port >> 8),
                    (byte)(peer.Port & 0xFF),
                ]))]),
            });

        private static byte[] Reply(Dictionary<string, BValue> values) =>
            BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
            {
                ["t"] = new BBytes([0, 0]),
                ["y"] = new BBytes("r"u8.ToArray()),
                ["r"] = new BDictionary(values),
            }));
    }
}
