namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Who this client knows, kept the way Kademlia keeps them.
/// </summary>
/// <remarks>
/// <para>
/// A bucket per shared prefix bit with our own id, up to a hundred and sixty of
/// them, eight nodes each. That is what the split-on-insert tree settles into:
/// a bucket splits when it is full and holds our own id, and the half that does
/// not hold it never splits again — so the table ends up knowing almost
/// everybody near us and a handful of nodes at each distance further out. It is
/// what makes a search take about seventeen rounds instead of a million.
/// </para>
/// <para>
/// A full bucket keeps what it has. The eight it holds have answered; the new
/// one has not, and Kademlia's own argument is that a node which has been up a
/// long time is likelier to stay up than a node that just appeared.
/// </para>
/// </remarks>
public sealed class RoutingTable(NodeId ours, int perBucket = RoutingTable.Kademlia)
{
    private readonly Dictionary<int, List<DhtContact>> _buckets = [];

    /// <summary>How many nodes a bucket holds. BEP 5 says eight.</summary>
    public const int Kademlia = 8;

    /// <summary>Our own id, which every distance is measured from.</summary>
    public NodeId Ours => ours;

    /// <summary>How many nodes are known.</summary>
    public int Count => _buckets.Values.Sum(bucket => bucket.Count);

    /// <summary>How many buckets have anything in them.</summary>
    public int Buckets => _buckets.Count;

    /// <summary>Everybody, in no particular order.</summary>
    public IEnumerable<DhtContact> All => _buckets.Values.SelectMany(bucket => bucket);

    /// <summary>
    /// Notes a node, and says whether there was room for it.
    /// </summary>
    /// <remarks>
    /// A node already known is moved to the back of its bucket rather than
    /// added twice: the back is the most recently heard from, and it is the
    /// front that gets dropped when a bucket is full and something has to go.
    /// </remarks>
    public bool Add(DhtContact contact)
    {
        if (contact.Id.Equals(ours))
        {
            // Ourselves, which every bootstrap answer eventually contains. A
            // table with itself in it would send packets to its own socket.
            return false;
        }

        List<DhtContact> bucket = Bucket(ours.Prefix(contact.Id));
        int already = bucket.FindIndex(one => one.Id.Equals(contact.Id));

        if (already >= 0)
        {
            bucket.RemoveAt(already);
            bucket.Add(contact);

            return true;
        }

        if (bucket.Count >= perBucket)
        {
            return false;
        }

        bucket.Add(contact);

        return true;
    }

    /// <summary>Forgets a node that has stopped answering.</summary>
    public bool Remove(NodeId id)
    {
        return Bucket(ours.Prefix(id)).RemoveAll(one => one.Id.Equals(id)) > 0;
    }

    /// <summary>Whether this node is in the table.</summary>
    public bool Knows(NodeId id)
    {
        return Bucket(ours.Prefix(id)).Any(one => one.Id.Equals(id));
    }

    /// <summary>
    /// The nodes nearest a target, nearest first.
    /// </summary>
    /// <remarks>
    /// Over the whole table rather than one bucket: the bucket a target falls
    /// in may be empty, and the answer is still whoever is nearest.
    /// </remarks>
    public IReadOnlyList<DhtContact> Closest(NodeId target, int wanted = Kademlia)
    {
        return
        [
            .. All
                .OrderBy(one => one.Id.Distance(target), ByteOrder.Instance)
                .Take(wanted),
        ];
    }

    private List<DhtContact> Bucket(int prefix)
    {
        if (!_buckets.TryGetValue(prefix, out List<DhtContact>? bucket))
        {
            bucket = [];
            _buckets[prefix] = bucket;
        }

        return bucket;
    }

    /// <summary>Distances compared as numbers, which is what nearer means.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
