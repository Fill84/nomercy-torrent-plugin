using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The distributed hash table.
/// </summary>
/// <remarks>
/// <para>
/// The packets are real. A DHT node answers a UDP packet from anybody — nothing
/// has to accept a connection — so unlike the peer wire, these could be
/// captured: <c>tests/fixtures/dht-ping.bin</c>, <c>dht-find-node.bin</c> and
/// <c>dht-get-peers.bin</c> are what <c>dht.transmissionbt.com:6881</c> really
/// answered, and <c>dht-values.bin</c> is what a node thirteen hops into the
/// Ubuntu swarm answered, with the peers it knew in it.
/// </para>
/// <para>
/// Every address in them was replaced with TEST-NET-1 before saving, keeping
/// the ports and the node ids — including the <c>ip</c> field, which is where a
/// node tells us our own public address. Everything asserted below is
/// structure, and none of it depends on the addresses.
/// </para>
/// </remarks>
public class DhtTests
{
    /// <remarks>
    /// Twenty bytes, and distance is exclusive-or: an id is its own distance
    /// from nowhere, and the nearest id to a target is the target.
    /// </remarks>
    [Fact]
    public void DistanceIsExclusiveOrAndAnIdIsNearestItself()
    {
        NodeId one = Id("00000000000000000000000000000000000000FF");
        NodeId other = Id("00000000000000000000000000000000000000F0");

        Assert.Equal(0x0F, one.Distance(other)[^1]);
        Assert.Equal(new byte[20], one.Distance(one));

        Assert.True(one.Nearer(other, one));
        Assert.False(other.Nearer(one, one));
    }

    /// <remarks>
    /// Which bucket a node goes in is how many leading bits it shares with our
    /// own id. Nought is as far away as the space allows.
    /// </remarks>
    [Theory]
    [InlineData("8000000000000000000000000000000000000000", 0)]
    [InlineData("4000000000000000000000000000000000000000", 1)]
    [InlineData("0100000000000000000000000000000000000000", 7)]
    [InlineData("0080000000000000000000000000000000000000", 8)]
    [InlineData("0000000000000000000000000000000000000001", 159)]
    [InlineData("0000000000000000000000000000000000000000", 160)]
    public void TheBucketIsHowManyLeadingBitsAreShared(string other, int prefix)
    {
        Assert.Equal(prefix, Id("0000000000000000000000000000000000000000").Prefix(Id(other)));
    }

    /// <remarks>
    /// Eight to a bucket, from BEP 5, and the ninth is refused: the eight in
    /// there have answered and the newcomer has not. Nodes at different
    /// distances go in different buckets, which is what stops one crowd of
    /// neighbours filling the whole table.
    /// </remarks>
    [Fact]
    public void ABucketHoldsEightAndTheNinthIsRefused()
    {
        RoutingTable table = new(Zero);

        for (int which = 0; which < RoutingTable.Kademlia; which++)
        {
            // Every one of these shares nought leading bits with our id, so
            // they all belong in the same bucket.
            Assert.True(table.Add(Contact($"8{which:X}00000000000000000000000000000000000000", which)));
        }

        Assert.False(table.Add(Contact("89FF000000000000000000000000000000000000", 99)));
        Assert.Equal(RoutingTable.Kademlia, table.Count);
        Assert.Equal(1, table.Buckets);

        // And one at another distance is a different bucket, with room of its
        // own.
        Assert.True(table.Add(Contact("4000000000000000000000000000000000000000", 100)));
        Assert.Equal(2, table.Buckets);
    }

    /// <remarks>
    /// A node heard from again is moved to the back rather than added twice.
    /// The front of a bucket is the least recently heard from and is what goes
    /// when something has to.
    /// </remarks>
    [Fact]
    public void ANodeHeardFromAgainIsNotAddedTwice()
    {
        RoutingTable table = new(Zero);

        Assert.True(table.Add(Contact("8000000000000000000000000000000000000000", 1)));
        Assert.True(table.Add(Contact("8000000000000000000000000000000000000000", 1)));

        Assert.Equal(1, table.Count);
        Assert.True(table.Knows(Id("8000000000000000000000000000000000000000")));
    }

    /// <remarks>
    /// Our own id is never in our own table: every bootstrap answer eventually
    /// contains it, and a table with itself in it sends packets to its own
    /// socket.
    /// </remarks>
    [Fact]
    public void OurOwnIdIsNeverInOurOwnTable()
    {
        RoutingTable table = new(Zero);

        Assert.False(table.Add(Contact("0000000000000000000000000000000000000000", 1)));
        Assert.Equal(0, table.Count);
    }

    /// <remarks>
    /// Nearest first, and over the whole table rather than one bucket — the
    /// bucket a target falls in is very often empty, and the answer is still
    /// whoever is nearest.
    /// </remarks>
    [Fact]
    public void TheClosestNodesComeBackNearestFirst()
    {
        RoutingTable table = new(Zero);

        table.Add(Contact("F000000000000000000000000000000000000000", 1));
        table.Add(Contact("8000000000000000000000000000000000000000", 2));
        table.Add(Contact("C000000000000000000000000000000000000000", 3));

        IReadOnlyList<DhtContact> closest = table.Closest(Id("FF00000000000000000000000000000000000000"), 3);

        Assert.Equal(
            ["F000000000000000000000000000000000000000", "C000000000000000000000000000000000000000", "8000000000000000000000000000000000000000"],
            closest.Select(one => one.Id.ToString()));

        Assert.Equal(2, table.Closest(Id("FF00000000000000000000000000000000000000"), 2).Count);
    }

    /// <remarks>
    /// A real answer to a real <c>ping</c>. It names the node and echoes the
    /// transaction id the question carried, which is the only thing tying the
    /// two together over UDP.
    /// </remarks>
    [Fact]
    public void ARealPingAnswerIsRead()
    {
        KrpcMessage answer = Krpc.Read(Fixture("dht-ping.bin"));

        Assert.Equal(KrpcKind.Response, answer.Kind);
        Assert.Equal("aa"u8.ToArray(), answer.Transaction);
        Assert.NotNull(answer.From);
        Assert.Equal(NodeId.Length, answer.From!.Bytes.Length);

        // And it named nobody else, which is what a ping answer is.
        Assert.Empty(Krpc.ReadNodes(answer));
    }

    /// <remarks>
    /// Two hundred and eight bytes of contacts is eight of them, twenty-six
    /// bytes each: twenty of node id, four of address, two of port. A client
    /// that read them as six-byte peers — which is what a tracker sends — would
    /// get rubbish that looked plausible.
    /// </remarks>
    [Fact]
    public void ARealFindNodeAnswerIsEightContacts()
    {
        IReadOnlyList<DhtContact> nodes = Krpc.ReadNodes(Krpc.Read(Fixture("dht-find-node.bin")));

        Assert.Equal(8, nodes.Count);
        Assert.All(nodes, one => Assert.Equal(NodeId.Length, one.Id.Bytes.Length));

        // Eight addresses, which is what says the stride is twenty-six: a
        // reader that had it wrong would run the id of one into the address of
        // the next and come out with eight of something, all plausible.
        Assert.Equal(8, nodes.Select(one => one.Address.ToString()).Distinct().Count());

        // And all eight are the same node id on the same port. That is what a
        // router really is — one logical node behind several addresses — and it
        // is worth pinning, because a reader whose stride was wrong would show
        // eight different ids here and look more correct than it was.
        Assert.Single(nodes.Select(one => one.Id.ToString()).Distinct());
        Assert.Single(nodes.Select(one => one.Address.Port).Distinct());
    }

    /// <remarks>
    /// A router answers <c>get_peers</c> with nodes and no token at all: it
    /// holds nothing, so nothing may be announced to it. A client that assumed
    /// a token was always there would throw on the very first answer it got.
    /// </remarks>
    [Fact]
    public void ARealGetPeersAnswerFromARouterHasNodesAndNoToken()
    {
        GetPeersAnswer answer = Krpc.ReadGetPeers(Krpc.Read(Fixture("dht-get-peers.bin")));

        Assert.Equal(8, answer.Nodes.Count);
        Assert.Empty(answer.Peers);
        Assert.Null(answer.Token);
    }

    /// <remarks>
    /// And a real node, thirteen hops in, answers with peers, a token and more
    /// nodes at once. The peers are a <em>list of six-byte strings</em>, not
    /// one string of all of them the way a tracker sends them — reading it the
    /// tracker's way is the trap, and it produces peers that look real.
    /// </remarks>
    [Fact]
    public void ARealAnswerWithPeersInItIsRead()
    {
        GetPeersAnswer answer = Krpc.ReadGetPeers(Krpc.Read(Fixture("dht-values.bin")));

        Assert.Equal(2, answer.Peers.Count);
        Assert.Equal(8, answer.Nodes.Count);
        Assert.NotNull(answer.Token);
        Assert.Equal(4, answer.Token!.Length);

        Assert.Equal(["192.0.2.1:40715", "192.0.2.2:37928"], answer.Peers.Select(one => one.ToString()));

        // Eight different nodes this time, and every one of them nearer the
        // torrent than a random id would be: they all share at least twelve
        // leading bits with it. That is Kademlia doing what it is for, in an
        // answer from a real node, and it is the property the walk relies on.
        Assert.Equal(8, answer.Nodes.Select(one => one.Id.ToString()).Distinct().Count());

        NodeId ubuntu = new(Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7"));

        Assert.All(answer.Nodes, one => Assert.InRange(one.Id.Prefix(ubuntu), 12, NodeId.Bits));
    }

    /// <remarks>
    /// Every question this client asks, read back by its own reader. The
    /// arguments are what BEP 5 names them: a client that spelled one of them
    /// differently would be answered with an error by every node in the world.
    /// </remarks>
    [Fact]
    public void EveryQuestionIsWrittenAsBep5NamesIt()
    {
        NodeId ours = NodeId.Random();
        NodeId target = NodeId.Random();
        byte[] hash = RandomNumberGenerator.GetBytes(20);

        KrpcMessage ping = Krpc.Read(Krpc.WritePing("t1"u8, ours));

        Assert.Equal(KrpcKind.Query, ping.Kind);
        Assert.Equal(Krpc.Ping, ping.Query);
        Assert.Equal(ours, ping.From);

        KrpcMessage find = Krpc.Read(Krpc.WriteFindNode("t2"u8, ours, target));

        Assert.Equal(Krpc.FindNode, find.Query);
        Assert.Equal(target.Bytes.ToArray(), find.Body.Bytes("target"));

        KrpcMessage peers = Krpc.Read(Krpc.WriteGetPeers("t3"u8, ours, hash));

        Assert.Equal(Krpc.GetPeers, peers.Query);
        Assert.Equal(hash, peers.Body.Bytes("info_hash"));

        KrpcMessage announce = Krpc.Read(Krpc.WriteAnnouncePeer("t4"u8, ours, hash, 51413, "tok!"u8));

        Assert.Equal(Krpc.AnnouncePeer, announce.Query);
        Assert.Equal(hash, announce.Body.Bytes("info_hash"));
        Assert.Equal(51413, announce.Body.Number("port"));
        Assert.Equal("tok!"u8.ToArray(), announce.Body.Bytes("token"));

        // implied_port is nought and said so: the port a node sees a packet
        // come from is the one a NAT gave it, not the one anybody can dial.
        Assert.Equal(0, announce.Body.Number("implied_port"));
    }

    /// <remarks>
    /// A node that refuses says so with a code and a reason, in the one place
    /// BEP 5 uses a list. A client that read it as an answer would take the
    /// refusal for an empty result.
    /// </remarks>
    [Fact]
    public void ARefusalIsReadAsARefusal()
    {
        byte[] packet = Bencode.Write(new BencodeDictionary(
        [
            new("t"u8.ToArray(), new BencodeBytes("aa"u8.ToArray())),
            new("y"u8.ToArray(), new BencodeBytes("e"u8.ToArray())),
            new("e"u8.ToArray(), new BencodeList(
            [
                new BencodeInteger(201),
                new BencodeBytes("A Generic Error Ocurred"u8.ToArray()),
            ])),
        ]));

        KrpcMessage refusal = Krpc.Read(packet);

        Assert.Equal(KrpcKind.Error, refusal.Kind);
        Assert.Equal(201, refusal.ErrorCode);
        Assert.Equal("A Generic Error Ocurred", refusal.ErrorMessage);
    }

    /// <remarks>
    /// Something that is not a KRPC packet at all — a stray UDP packet on the
    /// same port, which is what happens when the client shares its port with
    /// the peer wire.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotAKrpcPacketIsRefused()
    {
        Assert.Throws<BencodeFormatException>(() => Krpc.Read("not bencode at all"u8));
        Assert.Throws<PeerProtocolException>(() => Krpc.Read(Bencode.Write(new BencodeInteger(3))));
        Assert.Throws<PeerProtocolException>(() => Krpc.Read(Bencode.Write(new BencodeDictionary([]))));
    }

    /// <remarks>
    /// The walk: each round asks the nearest nodes known and they name nearer
    /// ones, until nobody can name anybody nearer. The swarm here is a thousand
    /// nodes that answer honestly, and the one holding the peers is the one
    /// nearest the hash — so a search that did not converge would never reach
    /// it.
    /// </remarks>
    [Fact]
    public async Task AGetPeersWalkConvergesOnTheNodesNearestTheHashAndCollectsThePeers()
    {
        byte[] hash = RandomNumberGenerator.GetBytes(20);
        FakeSwarm swarm = new(hash, nodes: 1000);

        RoutingTable table = new(NodeId.Random());

        // One node to start from, and a distant one at that.
        table.Add(swarm.Furthest);

        Dht dht = new(table.Ours, table, swarm);

        PeerSearch found = await dht.PeersAsync(Torrent(hash, priv: false), wanted: 50, CancellationToken.None);

        Assert.Equal(swarm.Peers, found.Peers.Select(one => one.ToString()).Order());

        // It got there in a handful of rounds, having asked a small fraction of
        // the swarm: that is the whole point of Kademlia, and a walk that
        // wandered would show up here as hundreds asked.
        Assert.InRange(found.Rounds, 1, Dht.MostRounds);
        Assert.InRange(found.Asked, 1, 200);

        // And nobody was asked twice. A node sent the same question every round
        // treats it as a flood and stops answering — the packets are wasted and
        // the client earns itself a reputation with the nodes it most needs.
        Assert.Equal(swarm.Asked.Count, swarm.Asked.Distinct().Count());

        // And it came away able to announce: the nodes nearest the hash, each
        // with the token it handed out.
        Assert.NotEmpty(found.Closest);
        Assert.All(found.Closest, one => Assert.NotEmpty(one.Token));

        Assert.Equal(
            swarm.Nearest.Id.ToString(),
            found.Closest[0].Node.Id.ToString());
    }

    /// <remarks>
    /// An announce goes only to nodes that handed out a token, quoting theirs.
    /// A node refuses an announce with anybody else's, which is what stops one
    /// client filling another node's table with peers that are not there.
    /// </remarks>
    [Fact]
    public async Task AnAnnounceQuotesEachNodesOwnToken()
    {
        byte[] hash = RandomNumberGenerator.GetBytes(20);
        FakeSwarm swarm = new(hash, nodes: 200);

        RoutingTable table = new(NodeId.Random());

        table.Add(swarm.Furthest);

        Dht dht = new(table.Ours, table, swarm);

        PeerSearch found = await dht.PeersAsync(Torrent(hash, priv: false), wanted: 50, CancellationToken.None);
        int taken = await dht.AnnounceAsync(Torrent(hash, priv: false), 51413, found, CancellationToken.None);

        Assert.Equal(found.Closest.Count, taken);
        Assert.All(swarm.Announced, one => Assert.Equal(51413, one));
    }

    /// <remarks>
    /// <strong>Not a packet.</strong> A private tracker's whole point is that it
    /// knows every peer on the torrent; a client that quietly found more
    /// elsewhere would have its owner's account closed, and "we only listened"
    /// is not a defence when the listening is a UDP packet with the info hash
    /// in it.
    /// </remarks>
    [Fact]
    public async Task APrivateTorrentNeverSendsASinglePacket()
    {
        byte[] hash = RandomNumberGenerator.GetBytes(20);
        FakeSwarm swarm = new(hash, nodes: 50);

        RoutingTable table = new(NodeId.Random());

        table.Add(swarm.Furthest);

        Dht dht = new(table.Ours, table, swarm);

        PeerSearch found = await dht.PeersAsync(Torrent(hash, priv: true), wanted: 50, CancellationToken.None);

        Assert.Empty(found.Peers);
        Assert.Equal(0, found.Asked);
        Assert.Empty(swarm.Asked);

        // Nor does the announce, which is the half that would really be seen.
        Assert.Equal(0, await dht.AnnounceAsync(Torrent(hash, priv: true), 51413, Everything(swarm), CancellationToken.None));
        Assert.Empty(swarm.Asked);
    }

    /// <remarks>
    /// The table survives a restart, and so does the id. A client that
    /// bootstrapped from nothing every time would lean on two routers for its
    /// first minutes; one whose id changed would be a stranger to every table
    /// that knew it, and every announce it had made would be lost with it.
    /// </remarks>
    [Fact]
    public void TheTableAndTheIdSurviveARestart()
    {
        RoutingTable table = new(NodeId.Random());

        for (int which = 0; which < 40; which++)
        {
            table.Add(Contact(Convert.ToHexString(RandomNumberGenerator.GetBytes(20)), which));
        }

        RoutingTable reloaded = DhtStore.Read(DhtStore.Write(table));

        Assert.Equal(table.Ours.ToString(), reloaded.Ours.ToString());
        Assert.Equal(table.Count, reloaded.Count);

        Assert.Equal(
            table.All.Select(one => $"{one.Id}@{one.Address}").Order(),
            reloaded.All.Select(one => $"{one.Id}@{one.Address}").Order());
    }

    /// <remarks>
    /// A file that will not parse is not worth stopping for — the table is a
    /// cache of who was up last time — but the client must still come up with
    /// an id and an empty table rather than throwing on the way in.
    /// </remarks>
    [Fact]
    public void ATableFileThatWillNotParseGivesAFreshTable()
    {
        RoutingTable table = DhtStore.Read("this is not bencode"u8);

        Assert.Equal(0, table.Count);
        Assert.Equal(NodeId.Length, table.Ours.Bytes.Length);
    }

    /// <remarks>
    /// Two routers, measured on the day this was written. The two everybody
    /// quotes did not answer at all, which is why the list is what it is.
    /// </remarks>
    [Fact]
    public void ThereIsAShippedNodeListToStartFrom()
    {
        Assert.NotEmpty(Dht.BootstrapNodes);
        Assert.All(Dht.BootstrapNodes, one => Assert.Contains(":", one, StringComparison.Ordinal));
    }

    private static NodeId Zero => Id("0000000000000000000000000000000000000000");

    private static NodeId Id(string hex)
    {
        return new(Convert.FromHexString(hex));
    }

    private static DhtContact Contact(string hex, int which)
    {
        return new(Id(hex), new(IPAddress.Parse($"192.0.2.{which % 255}"), 6881 + which));
    }

    private static TorrentMetadata Torrent(byte[] hash, bool priv)
    {
        return new(Convert.ToHexString(hash), "something", 262144, [], [], 0, [], priv);
    }

    /// <summary>Something to announce to, for the test that nothing is announced.</summary>
    private static PeerSearch Everything(FakeSwarm swarm)
    {
        return new([], [(swarm.Furthest, "tok!"u8.ToArray())], 1, 1);
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }

    /// <summary>
    /// A swarm of nodes that answer honestly, over no network at all.
    /// </summary>
    /// <remarks>
    /// Each one answers with the eight nodes it considers nearest the target,
    /// out of everybody — which is a friendlier network than the real one and
    /// is exactly the case a walk has to converge in. The peers live on the one
    /// node nearest the hash, so a search that stopped early finds nothing.
    /// </remarks>
    private sealed class FakeSwarm : IDhtTransport
    {
        private readonly Dictionary<string, DhtContact> _byAddress = new(StringComparer.Ordinal);
        private readonly List<DhtContact> _nodes = [];
        private readonly NodeId _target;

        public FakeSwarm(byte[] infoHash, int nodes)
        {
            _target = new(infoHash);

            for (int which = 0; which < nodes; which++)
            {
                DhtContact contact = new(
                    NodeId.Random(),
                    new(new IPAddress([10, (byte)(which >> 16), (byte)(which >> 8), (byte)which]), 6881));

                _nodes.Add(contact);
                _byAddress[contact.Address.ToString()] = contact;
            }

            Nearest = _nodes.OrderBy(one => one.Id.Distance(_target), Comparer<byte[]>.Create(
                (left, right) => left.AsSpan().SequenceCompareTo(right))).First();

            Furthest = _nodes.OrderByDescending(one => one.Id.Distance(_target), Comparer<byte[]>.Create(
                (left, right) => left.AsSpan().SequenceCompareTo(right))).First();
        }

        /// <summary>The node holding the peers.</summary>
        public DhtContact Nearest { get; }

        /// <summary>The one a search is started from, as far away as the swarm goes.</summary>
        public DhtContact Furthest { get; }

        /// <summary>Every node that was asked anything.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>Every port that was announced.</summary>
        public List<long> Announced { get; } = [];

        /// <summary>The peers the nearest node knows, as they will read back.</summary>
        public string[] Peers { get; } = ["203.0.113.10:51413", "203.0.113.11:6881"];

        public Task<KrpcMessage?> AskAsync(IPEndPoint node, byte[] query, CancellationToken ct)
        {
            Asked.Add(node.ToString());

            KrpcMessage question = Krpc.Read(query);

            if (!_byAddress.TryGetValue(node.ToString(), out DhtContact? answering))
            {
                return Task.FromResult<KrpcMessage?>(null);
            }

            if (question.Query == Krpc.AnnouncePeer)
            {
                Announced.Add(question.Body.Number("port") ?? 0);

                return Task.FromResult<KrpcMessage?>(Krpc.Read(Krpc.WriteResponse(
                    question.Transaction,
                    [new("id"u8.ToArray(), new BencodeBytes(answering.Id.Bytes.ToArray()))])));
            }

            List<BencodeEntry> values =
            [
                new("id"u8.ToArray(), new BencodeBytes(answering.Id.Bytes.ToArray())),
                new("token"u8.ToArray(), new BencodeBytes([.. answering.Id.Bytes[..4]])),
                new("nodes"u8.ToArray(), new BencodeBytes(DhtContact.Write(
                    _nodes
                        .OrderBy(one => one.Id.Distance(_target), Comparer<byte[]>.Create(
                            (left, right) => left.AsSpan().SequenceCompareTo(right)))
                        .Take(RoutingTable.Kademlia)))),
            ];

            if (answering.Id.Equals(Nearest.Id))
            {
                values.Add(new("values"u8.ToArray(), new BencodeList(
                [
                    .. Peers.Select(one => new BencodeBytes(Compact(one))),
                ])));
            }

            return Task.FromResult<KrpcMessage?>(Krpc.Read(Krpc.WriteResponse(question.Transaction, values)));
        }

        private static byte[] Compact(string peer)
        {
            string[] parts = peer.Split(':');
            int port = int.Parse(parts[1]);

            return [.. IPAddress.Parse(parts[0]).GetAddressBytes(), (byte)(port >> 8), (byte)port];
        }
    }
}
