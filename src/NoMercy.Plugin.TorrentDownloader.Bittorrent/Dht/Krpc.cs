using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>What a KRPC packet turned out to be.</summary>
public enum KrpcKind
{
    Query,
    Response,
    Error,
}

/// <summary>
/// One packet of BEP 5's remote procedure call, read.
/// </summary>
/// <param name="Kind">A question, an answer, or a refusal.</param>
/// <param name="Transaction">
/// The bytes that say which question this answers. Chosen by whoever asked and
/// echoed back — UDP has no connection, so this is the only thing tying an
/// answer to the question it belongs to.
/// </param>
/// <param name="Query">Which question, when it is one.</param>
/// <param name="Body">The arguments of a question or the values of an answer.</param>
/// <param name="ErrorCode">The number, when it is a refusal.</param>
/// <param name="ErrorMessage">What it said, when it is a refusal.</param>
public sealed record KrpcMessage(
    KrpcKind Kind,
    byte[] Transaction,
    string? Query,
    BencodeDictionary Body,
    int ErrorCode,
    string? ErrorMessage)
{
    /// <summary>The node that sent it, when it named itself.</summary>
    public NodeId? From => Body.Bytes("id") is byte[] id && id.Length == NodeId.Length ? new(id) : null;
}

/// <summary>
/// What a <c>get_peers</c> came back with.
/// </summary>
/// <param name="From">Which node answered.</param>
/// <param name="Token">
/// What has to be quoted back to announce to this node, or null when it gave
/// none. A router gives none: it holds nothing and nothing may be announced to
/// it, which is exactly what the captured answers show.
/// </param>
/// <param name="Nodes">Who it says is nearer, when it has no peers.</param>
/// <param name="Peers">Who is actually on the torrent, when it knows.</param>
public sealed record GetPeersAnswer(
    NodeId? From,
    byte[]? Token,
    IReadOnlyList<DhtContact> Nodes,
    IReadOnlyList<PeerAddress> Peers);

/// <summary>
/// BEP 5's four questions, and the answers to them.
/// </summary>
/// <remarks>
/// Bencode over UDP, with no connection and no ordering. Every question carries
/// a transaction id and every answer echoes it, because two answers can arrive
/// in the other order and a client that assumed otherwise would credit one
/// node's peers to another node's question.
/// </remarks>
public static class Krpc
{
    /// <summary>What a node asks with.</summary>
    public const string Ping = "ping";

    public const string FindNode = "find_node";

    public const string GetPeers = "get_peers";

    public const string AnnouncePeer = "announce_peer";

    /// <summary>Are you there.</summary>
    public static byte[] WritePing(ReadOnlySpan<byte> transaction, NodeId ours)
    {
        return Query(transaction, Ping, [Id(ours)]);
    }

    /// <summary>Who do you know nearest this id.</summary>
    public static byte[] WriteFindNode(ReadOnlySpan<byte> transaction, NodeId ours, NodeId target)
    {
        return Query(transaction, FindNode, [Id(ours), new("target"u8.ToArray(), new BencodeBytes(target.Bytes.ToArray()))]);
    }

    /// <summary>Who is on this torrent, or who is nearer it.</summary>
    public static byte[] WriteGetPeers(ReadOnlySpan<byte> transaction, NodeId ours, ReadOnlySpan<byte> infoHash)
    {
        return Query(transaction, GetPeers, [Id(ours), new("info_hash"u8.ToArray(), new BencodeBytes(infoHash.ToArray()))]);
    }

    /// <summary>
    /// Put us on this torrent's list.
    /// </summary>
    /// <param name="transaction">Which question this is.</param>
    /// <param name="ours">Who we are.</param>
    /// <param name="infoHash">Which torrent.</param>
    /// <param name="port">
    /// The port we listen on. Sent with <c>implied_port</c> unset, because the
    /// port a node sees a packet come from is the port a NAT gave it and not
    /// the one anybody can dial.
    /// </param>
    /// <param name="token">What that node handed out with its <c>get_peers</c> answer.</param>
    public static byte[] WriteAnnouncePeer(
        ReadOnlySpan<byte> transaction,
        NodeId ours,
        ReadOnlySpan<byte> infoHash,
        int port,
        ReadOnlySpan<byte> token)
    {
        return Query(
            transaction,
            AnnouncePeer,
            [
                Id(ours),
                new("implied_port"u8.ToArray(), new BencodeInteger(0)),
                new("info_hash"u8.ToArray(), new BencodeBytes(infoHash.ToArray())),
                new("port"u8.ToArray(), new BencodeInteger(port)),
                new("token"u8.ToArray(), new BencodeBytes(token.ToArray())),
            ]);
    }

    /// <summary>Reads a packet, whichever of the three it is.</summary>
    /// <exception cref="PeerProtocolException">It is not a KRPC packet.</exception>
    public static KrpcMessage Read(ReadOnlySpan<byte> packet)
    {
        if (Bencode.Read(packet).Root is not BencodeDictionary root)
        {
            throw new PeerProtocolException("A KRPC packet is a dictionary, and this one is not.");
        }

        byte[] transaction = root.Bytes("t")
                             ?? throw new PeerProtocolException("A KRPC packet carries a transaction id, and this one does not.");

        return root.Text("y") switch
        {
            "q" => new(
                KrpcKind.Query,
                transaction,
                root.Text("q") ?? throw new PeerProtocolException("A query says which one it is, and this one does not."),
                root["a"] as BencodeDictionary ?? Empty,
                0,
                null),
            "r" => new(KrpcKind.Response, transaction, null, root["r"] as BencodeDictionary ?? Empty, 0, null),
            "e" => Refusal(transaction, root),
            _ => throw new PeerProtocolException("A KRPC packet is a query, a response or an error, and this one is none."),
        };
    }

    /// <summary>Reads what a <c>get_peers</c> answered.</summary>
    public static GetPeersAnswer ReadGetPeers(KrpcMessage answer)
    {
        if (answer.Kind != KrpcKind.Response)
        {
            throw new PeerProtocolException("That is not an answer to anything.");
        }

        List<PeerAddress> peers = [];

        if (answer.Body["values"] is BencodeList values)
        {
            // Six bytes each, one bencoded string per peer — not one string of
            // all of them, which is what a tracker sends and is the trap here.
            foreach (BencodeBytes peer in values.Items.OfType<BencodeBytes>())
            {
                if (peer.Value.Length == 6)
                {
                    peers.Add(new(
                        new IPAddress(peer.Value.AsSpan(0, 4)),
                        BinaryPrimitives.ReadUInt16BigEndian(peer.Value.AsSpan(4, 2))));
                }
            }
        }

        return new(
            answer.From,
            answer.Body.Bytes("token"),
            DhtContact.Read(answer.Body.Bytes("nodes") ?? []),
            peers);
    }

    /// <summary>Reads the contacts out of a <c>find_node</c> answer.</summary>
    public static IReadOnlyList<DhtContact> ReadNodes(KrpcMessage answer)
    {
        return DhtContact.Read(answer.Body.Bytes("nodes") ?? []);
    }

    /// <summary>An answer of our own, echoing the transaction it belongs to.</summary>
    public static byte[] WriteResponse(ReadOnlySpan<byte> transaction, IEnumerable<BencodeEntry> values)
    {
        return Bencode.Write(new BencodeDictionary(
        [
            new("t"u8.ToArray(), new BencodeBytes(transaction.ToArray())),
            new("y"u8.ToArray(), new BencodeBytes("r"u8.ToArray())),
            new("r"u8.ToArray(), new BencodeDictionary([.. values])),
        ]));
    }

    private static KrpcMessage Refusal(byte[] transaction, BencodeDictionary root)
    {
        // A list of the code and the message, which is the one place BEP 5 uses
        // a list where everything else is a dictionary.
        BencodeList said = root["e"] as BencodeList ?? new([]);

        return new(
            KrpcKind.Error,
            transaction,
            null,
            Empty,
            said.Items.ElementAtOrDefault(0) is BencodeInteger code ? (int)code.Value : 0,
            said.Items.ElementAtOrDefault(1) is BencodeBytes message ? Encoding.UTF8.GetString(message.Value) : null);
    }

    private static BencodeEntry Id(NodeId ours)
    {
        return new("id"u8.ToArray(), new BencodeBytes(ours.Bytes.ToArray()));
    }

    private static byte[] Query(ReadOnlySpan<byte> transaction, string name, BencodeEntry[] arguments)
    {
        return Bencode.Write(new BencodeDictionary(
        [
            new("t"u8.ToArray(), new BencodeBytes(transaction.ToArray())),
            new("y"u8.ToArray(), new BencodeBytes("q"u8.ToArray())),
            new("q"u8.ToArray(), new BencodeBytes(Encoding.ASCII.GetBytes(name))),
            new("a"u8.ToArray(), new BencodeDictionary(arguments)),
        ]));
    }

    private static BencodeDictionary Empty { get; } = new([]);
}
