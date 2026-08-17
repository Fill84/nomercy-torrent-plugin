using System.Net;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// How a question reaches a node and an answer comes back.
/// </summary>
/// <remarks>
/// An interface because the walk is the interesting part and a socket is not.
/// A test puts a swarm of nodes behind this and watches the search converge on
/// the right ones; the real one is a UDP socket on the client's own port.
/// </remarks>
public interface IDhtTransport
{
    /// <summary>Asks one node, and answers null when it says nothing in time.</summary>
    Task<KrpcMessage?> AskAsync(IPEndPoint node, byte[] query, CancellationToken ct);
}

/// <summary>What a search found.</summary>
/// <param name="Peers">Who is on the torrent, without repeats.</param>
/// <param name="Closest">
/// The nodes nearest the hash that answered, with the token each handed out.
/// These are the ones an announce goes to: a node only accepts an announce
/// quoting the token it gave.
/// </param>
/// <param name="Rounds">How many rounds of asking it took.</param>
/// <param name="Asked">How many nodes were asked.</param>
public sealed record PeerSearch(
    IReadOnlyList<PeerAddress> Peers,
    IReadOnlyList<(DhtContact Node, byte[] Token)> Closest,
    int Rounds,
    int Asked);

/// <summary>
/// The distributed hash table: finding peers without asking a tracker.
/// </summary>
/// <remarks>
/// <para>
/// A search walks towards the hash. Each round asks the nearest nodes it knows
/// and they answer with noder ones, until nobody can name anybody nearer — about
/// seventeen rounds across the whole network, and rather fewer once the table
/// has been running a while.
/// </para>
/// <para>
/// A private torrent never touches any of it. Not a packet: BEP 27 exists
/// because a private tracker's whole point is that it knows every peer, and a
/// client that quietly found peers elsewhere would have its owner's account
/// closed.
/// </para>
/// </remarks>
public sealed class Dht(NodeId ours, RoutingTable table, IDhtTransport transport)
{
    /// <summary>
    /// Where a client with an empty table starts.
    /// </summary>
    /// <remarks>
    /// Measured rather than copied from a list: on 18 August 2026 these two
    /// answered a <c>ping</c> from this machine, and
    /// <c>router.bittorrent.com:6881</c> and <c>router.utorrent.com:6881</c> —
    /// the two everybody quotes — did not answer at all. The captured fixtures
    /// under tests/fixtures came from the first of these.
    /// </remarks>
    public static IReadOnlyList<string> BootstrapNodes { get; } =
    [
        "dht.transmissionbt.com:6881",
        "dht.libtorrent.org:25401",
    ];

    /// <summary>How many rounds a search will run before giving up.</summary>
    /// <remarks>
    /// Each round halves the distance, so a hundred and sixty bits is reached
    /// in about seventeen. Twenty is room to spare and a bound: without one, a
    /// ring of nodes naming each other would have a search run for ever.
    /// </remarks>
    public const int MostRounds = 20;

    /// <summary>Our own id.</summary>
    public NodeId Id => ours;

    /// <summary>Who this client knows.</summary>
    public RoutingTable Table => table;

    /// <summary>Asks the nodes we start with who they know, and fills the table.</summary>
    public async Task BootstrapAsync(IEnumerable<IPEndPoint> nodes, CancellationToken ct)
    {
        foreach (IPEndPoint node in nodes)
        {
            KrpcMessage? answer = await transport
                .AskAsync(node, Krpc.WriteFindNode(Transaction(), ours, ours), ct)
                .ConfigureAwait(false);

            if (answer is null || answer.Kind != KrpcKind.Response)
            {
                continue;
            }

            // A bootstrap node's own id is not added: a router is not a node
            // that holds anything, and putting it in the table would have every
            // search start by asking it again.
            foreach (DhtContact contact in Krpc.ReadNodes(answer))
            {
                table.Add(contact);
            }
        }
    }

    /// <summary>
    /// Finds peers for a torrent, walking towards its hash.
    /// </summary>
    /// <remarks>
    /// A private torrent gets an empty answer and no packet is sent.
    /// </remarks>
    public Task<PeerSearch> PeersAsync(TorrentMetadata torrent, int wanted, CancellationToken ct)
    {
        return torrent.Private
            ? Task.FromResult(new PeerSearch([], [], 0, 0))
            : SearchAsync(Convert.FromHexString(torrent.InfoHash), wanted, ct);
    }

    /// <summary>
    /// Puts this client on a torrent's list, at the nodes nearest its hash.
    /// </summary>
    /// <param name="torrent">Which torrent.</param>
    /// <param name="port">The port this client listens on.</param>
    /// <param name="found">What the search that preceded it came back with.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many nodes took it.</returns>
    public async Task<int> AnnounceAsync(TorrentMetadata torrent, int port, PeerSearch found, CancellationToken ct)
    {
        if (torrent.Private)
        {
            return 0;
        }

        byte[] infoHash = Convert.FromHexString(torrent.InfoHash);
        int taken = 0;

        foreach ((DhtContact node, byte[] token) in found.Closest)
        {
            KrpcMessage? answer = await transport
                .AskAsync(node.Address, Krpc.WriteAnnouncePeer(Transaction(), ours, infoHash, port, token), ct)
                .ConfigureAwait(false);

            if (answer?.Kind == KrpcKind.Response)
            {
                taken++;
            }
        }

        return taken;
    }

    /// <summary>The walk itself, for a hash whose torrent is known not to be private.</summary>
    private async Task<PeerSearch> SearchAsync(byte[] infoHash, int wanted, CancellationToken ct)
    {
        NodeId target = new(infoHash);

        Dictionary<string, PeerAddress> peers = new(StringComparer.Ordinal);
        Dictionary<string, (DhtContact Node, byte[] Token)> tokens = new(StringComparer.Ordinal);
        HashSet<string> asked = new(StringComparer.Ordinal);
        List<DhtContact> shortlist = [.. table.Closest(target)];
        int rounds = 0;

        while (rounds < MostRounds && peers.Count < wanted)
        {
            DhtContact[] asking =
            [
                .. shortlist
                    .Where(one => !asked.Contains(one.Address.ToString()))
                    .OrderBy(one => one.Id.Distance(target), Nearest.Instance)
                    .Take(RoutingTable.Kademlia),
            ];

            if (asking.Length == 0)
            {
                // Everybody near enough to be worth asking has been asked. That
                // is convergence, not failure, and it is where a search stops.
                break;
            }

            rounds++;

            foreach (DhtContact node in asking)
            {
                asked.Add(node.Address.ToString());

                KrpcMessage? answer = await transport
                    .AskAsync(node.Address, Krpc.WriteGetPeers(Transaction(), ours, infoHash), ct)
                    .ConfigureAwait(false);

                if (answer is null || answer.Kind != KrpcKind.Response)
                {
                    // A node that does not answer is the normal case out here,
                    // and it is dropped rather than asked again next round.
                    table.Remove(node.Id);

                    continue;
                }

                GetPeersAnswer said = Krpc.ReadGetPeers(answer);

                table.Add(node);

                if (said.Token is byte[] token)
                {
                    tokens[node.Address.ToString()] = (node, token);
                }

                foreach (PeerAddress peer in said.Peers)
                {
                    peers[peer.ToString()] = peer;
                }

                foreach (DhtContact nearer in said.Nodes)
                {
                    table.Add(nearer);

                    if (!asked.Contains(nearer.Address.ToString()))
                    {
                        shortlist.Add(nearer);
                    }
                }
            }
        }

        return new(
            [.. peers.Values],
            [
                .. tokens.Values
                    .OrderBy(one => one.Node.Id.Distance(target), Nearest.Instance)
                    .Take(RoutingTable.Kademlia),
            ],
            rounds,
            asked.Count);
    }

    /// <summary>
    /// Two bytes that say which question an answer belongs to.
    /// </summary>
    /// <remarks>
    /// Random rather than counted up. UDP has no connection: an answer to a
    /// question this client gave up on ten seconds ago arrives eventually, and
    /// a counter that had wrapped round would take it for the answer to a
    /// different one.
    /// </remarks>
    private static byte[] Transaction()
    {
        return RandomNumberGenerator.GetBytes(2);
    }

    private sealed class Nearest : IComparer<byte[]>
    {
        public static Nearest Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
