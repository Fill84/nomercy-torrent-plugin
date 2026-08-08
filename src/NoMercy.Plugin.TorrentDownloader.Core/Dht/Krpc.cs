// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Dht;

public sealed class DhtException(string message) : Exception(message);

/// <summary>
/// What a node answered. A get_peers reply carries either the peers it knows or the
/// nodes it thinks are closer - never both, and the lookup treats the second as a
/// step rather than a failure.
/// </summary>
public sealed record KrpcResponse(
    NodeId NodeId,
    IReadOnlyList<PeerEndPoint> Peers,
    IReadOnlyList<DhtNode> Nodes,
    byte[]? Token);

/// <summary>
/// BEP 5's message format: bencode over UDP. Queries name a method and their
/// arguments; replies carry a transaction id that ties them to the question.
/// </summary>
public static class Krpc
{
    /// <summary>Twenty bytes of id, four of address, two of port.</summary>
    private const int CompactNodeLength = 26;

    private const int CompactPeerLength = 6;

    public static byte[] Ping(NodeId self, out byte[] transaction) =>
        Query("ping", new Dictionary<string, BValue> { ["id"] = new BBytes(self.Bytes) }, out transaction);

    public static byte[] FindNode(NodeId self, NodeId target, out byte[] transaction) =>
        Query("find_node", new Dictionary<string, BValue>
        {
            ["id"] = new BBytes(self.Bytes),
            ["target"] = new BBytes(target.Bytes),
        }, out transaction);

    public static byte[] GetPeers(NodeId self, byte[] infoHash, out byte[] transaction) =>
        Query("get_peers", new Dictionary<string, BValue>
        {
            ["id"] = new BBytes(self.Bytes),
            ["info_hash"] = new BBytes(infoHash),
        }, out transaction);

    public static byte[] AnnouncePeer(NodeId self, byte[] infoHash, int port, byte[] token, out byte[] transaction) =>
        Query("announce_peer", new Dictionary<string, BValue>
        {
            ["id"] = new BBytes(self.Bytes),
            ["info_hash"] = new BBytes(infoHash),
            ["port"] = new BInteger(port),
            ["token"] = new BBytes(token),
        }, out transaction);

    public static KrpcResponse ParseResponse(ReadOnlySpan<byte> message)
    {
        if (BencodeReader.Parse(message) is not BDictionary envelope)
            throw new DhtException("a KRPC message must be a dictionary");

        string kind = envelope.Entries.TryGetValue("y", out BValue? y) && y is BBytes type ? type.AsText() : "";

        if (kind == "e")
            throw new DhtException($"the node answered with an error: {ErrorText(envelope)}");

        if (kind != "r" || !envelope.Entries.TryGetValue("r", out BValue? body) || body is not BDictionary reply)
            throw new DhtException($"'{kind}' is not a response");

        if (!reply.Entries.TryGetValue("id", out BValue? id) || id is not BBytes nodeId || nodeId.Value.Length != NodeId.Length)
            throw new DhtException("the response does not say which node sent it");

        byte[]? token = reply.Entries.TryGetValue("token", out BValue? t) && t is BBytes bytes ? bytes.Value : null;

        return new KrpcResponse(new NodeId(nodeId.Value), ReadPeers(reply), ReadNodes(reply), token);
    }

    private static IReadOnlyList<PeerEndPoint> ReadPeers(BDictionary reply)
    {
        if (!reply.Entries.TryGetValue("values", out BValue? values) || values is not BList list)
            return [];

        List<PeerEndPoint> peers = [];

        foreach (BValue entry in list.Items)
        {
            if (entry is not BBytes compact || compact.Value.Length != CompactPeerLength)
                continue;

            int port = BinaryPrimitives.ReadUInt16BigEndian(compact.Value.AsSpan(4));

            if (port > 0)
                peers.Add(new PeerEndPoint(new IPAddress(compact.Value.AsSpan(0, 4)), port));
        }

        return peers;
    }

    private static IReadOnlyList<DhtNode> ReadNodes(BDictionary reply)
    {
        if (!reply.Entries.TryGetValue("nodes", out BValue? nodes) || nodes is not BBytes compact)
            return [];

        // A list that does not divide evenly is a node that cannot count. Take nothing
        // rather than guess where the entries begin.
        if (compact.Value.Length == 0 || compact.Value.Length % CompactNodeLength != 0)
            return [];

        List<DhtNode> found = [];

        for (int offset = 0; offset + CompactNodeLength <= compact.Value.Length; offset += CompactNodeLength)
        {
            NodeId id = new(compact.Value[offset..(offset + NodeId.Length)]);
            int port = BinaryPrimitives.ReadUInt16BigEndian(compact.Value.AsSpan(offset + 24, 2));

            if (port > 0)
                found.Add(new DhtNode(id, new PeerEndPoint(new IPAddress(compact.Value.AsSpan(offset + 20, 4)), port)));
        }

        return found;
    }

    private static string ErrorText(BDictionary envelope) =>
        envelope.Entries.TryGetValue("e", out BValue? error) && error is BList parts
            ? string.Join(' ', parts.Items.Select(part => part switch
            {
                BBytes text => text.AsText(),
                BInteger number => number.Value.ToString(),
                _ => "",
            }))
            : "no reason given";

    private static byte[] Query(string method, Dictionary<string, BValue> arguments, out byte[] transaction)
    {
        // Two bytes is what every implementation uses and is plenty: it only has to be
        // unique among the queries still outstanding, not across the network.
        transaction = new byte[2];
        RandomNumberGenerator.Fill(transaction);

        return BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["t"] = new BBytes(transaction),
            ["y"] = new BBytes(Encoding.ASCII.GetBytes("q")),
            ["q"] = new BBytes(Encoding.ASCII.GetBytes(method)),
            ["a"] = new BDictionary(arguments),
        }));
    }
}
