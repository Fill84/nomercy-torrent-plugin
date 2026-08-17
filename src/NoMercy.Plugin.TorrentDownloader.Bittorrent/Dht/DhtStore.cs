namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// The routing table, kept across a restart.
/// </summary>
/// <remarks>
/// <para>
/// A client that bootstrapped from nothing every time it started would spend
/// its first minutes asking two routers who exist, and would be a burden on
/// them for no reason. It would also lose its own id, and an id that changes is
/// a client nobody's table remembers — every announce it ever made is thrown
/// away with it.
/// </para>
/// <para>
/// Bencode rather than JSON, because a node id is twenty arbitrary bytes and
/// half of them are not text.
/// </para>
/// </remarks>
public static class DhtStore
{
    /// <summary>What the file is called in the data folder.</summary>
    public const string FileName = "dht.dat";

    /// <summary>Our id and everybody we know, as bytes to write.</summary>
    public static byte[] Write(RoutingTable table)
    {
        return Bencode.Write(new BencodeDictionary(
        [
            new("id"u8.ToArray(), new BencodeBytes(table.Ours.Bytes.ToArray())),
            new("nodes"u8.ToArray(), new BencodeBytes(DhtContact.Write(table.All))),
        ]));
    }

    /// <summary>
    /// The table back, or a fresh one when there is nothing to read.
    /// </summary>
    /// <remarks>
    /// A file that will not parse is not an error worth stopping for: the table
    /// is a cache of who was up last time, and the client bootstraps instead.
    /// The id is kept when it is readable, because that is the part that cannot
    /// be rebuilt.
    /// </remarks>
    public static RoutingTable Read(ReadOnlySpan<byte> stored)
    {
        BencodeDictionary? root = null;

        if (Bencode.TryRead(stored, out BencodeDocument? document, out BencodeError? _))
        {
            root = document!.Root as BencodeDictionary;
        }

        byte[]? id = root?.Bytes("id");

        RoutingTable table = new(id?.Length == NodeId.Length ? new(id) : NodeId.Random());

        foreach (DhtContact contact in DhtContact.Read(root?.Bytes("nodes") ?? []))
        {
            table.Add(contact);
        }

        return table;
    }
}
