// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Dht;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Dht;

public class KrpcTests
{
    private static readonly NodeId Us = new(Enumerable.Repeat((byte)0x11, 20).ToArray());
    private static readonly NodeId Them = new(Enumerable.Repeat((byte)0x22, 20).ToArray());
    private static readonly byte[] InfoHash = Enumerable.Repeat((byte)0x33, 20).ToArray();

    private static BDictionary Decode(byte[] message) => (BDictionary)BencodeReader.Parse(message);

    [Fact]
    public void GetPeers_IsAQueryNamingTheTorrentAndOurself()
    {
        byte[] message = Krpc.GetPeers(Us, InfoHash, out byte[] transaction);

        BDictionary decoded = Decode(message);
        ((BBytes)decoded.Entries["y"]).AsText().Should().Be("q");
        ((BBytes)decoded.Entries["q"]).AsText().Should().Be("get_peers");
        ((BBytes)decoded.Entries["t"]).Value.Should().Equal(transaction);

        BDictionary arguments = (BDictionary)decoded.Entries["a"];
        ((BBytes)arguments.Entries["id"]).Value.Should().Equal(Us.Bytes);
        ((BBytes)arguments.Entries["info_hash"]).Value.Should().Equal(InfoHash);
    }

    [Fact]
    public void FindNode_NamesTheTargetItIsLookingFor()
    {
        BDictionary decoded = Decode(Krpc.FindNode(Us, Them, out _));

        ((BBytes)decoded.Entries["q"]).AsText().Should().Be("find_node");
        ((BBytes)((BDictionary)decoded.Entries["a"]).Entries["target"]).Value.Should().Equal(Them.Bytes);
    }

    [Fact]
    public void Ping_CarriesNothingButOurIdentity()
    {
        BDictionary decoded = Decode(Krpc.Ping(Us, out _));

        ((BBytes)decoded.Entries["q"]).AsText().Should().Be("ping");
        ((BDictionary)decoded.Entries["a"]).Entries.Should().ContainKey("id");
    }

    [Fact]
    public void EveryQuery_GetsItsOwnTransactionId()
    {
        Krpc.Ping(Us, out byte[] first);
        Krpc.Ping(Us, out byte[] second);

        // The transaction id is what ties a UDP answer to the question that asked it.
        first.Should().NotEqual(second);
    }

    [Fact]
    public void ParseResponse_ReadsPeersFromAGetPeersAnswer()
    {
        byte[] response = Response(new Dictionary<string, BValue>
        {
            ["id"] = new BBytes(Them.Bytes),
            ["token"] = new BBytes("opaque"u8.ToArray()),
            ["values"] = new BList(
            [
                new BBytes(Compact("192.168.2.50", 6881)),
                new BBytes(Compact("10.0.0.7", 51413)),
            ]),
        });

        KrpcResponse parsed = Krpc.ParseResponse(response);

        parsed.Peers.Should().HaveCount(2);
        parsed.Peers[0].Address.ToString().Should().Be("192.168.2.50");
        parsed.Peers[1].Port.Should().Be(51413);
        parsed.Token.Should().NotBeNull();
        parsed.NodeId.Should().Be(Them);
    }

    [Fact]
    public void ParseResponse_ReadsCloserNodesWhenThereAreNoPeersYet()
    {
        byte[] compact = [.. Them.Bytes, .. Compact("10.0.0.9", 1337)];

        KrpcResponse parsed = Krpc.ParseResponse(Response(new Dictionary<string, BValue>
        {
            ["id"] = new BBytes(Us.Bytes),
            ["nodes"] = new BBytes(compact),
        }));

        parsed.Peers.Should().BeEmpty();
        parsed.Nodes.Should().ContainSingle();
        parsed.Nodes[0].Id.Should().Be(Them);
        parsed.Nodes[0].EndPoint.Port.Should().Be(1337);
    }

    [Fact]
    public void ParseResponse_IgnoresANodeListThatIsNotAMultipleOfTwentySix()
    {
        KrpcResponse parsed = Krpc.ParseResponse(Response(new Dictionary<string, BValue>
        {
            ["id"] = new BBytes(Us.Bytes),
            ["nodes"] = new BBytes([1, 2, 3]),
        }));

        parsed.Nodes.Should().BeEmpty();
    }

    [Fact]
    public void ParseResponse_SurfacesAnErrorReply()
    {
        byte[] error = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["t"] = new BBytes([0x01, 0x02]),
            ["y"] = new BBytes("e"u8.ToArray()),
            ["e"] = new BList([new BInteger(201), new BBytes("Generic Error"u8.ToArray())]),
        }));

        Action parse = () => Krpc.ParseResponse(error);

        parse.Should().Throw<DhtException>().WithMessage("*Generic Error*");
    }

    [Fact]
    public void ParseResponse_RefusesSomethingThatIsNotAResponse()
    {
        Action parse = () => Krpc.ParseResponse(Krpc.Ping(Us, out _));

        parse.Should().Throw<DhtException>();
    }

    [Fact]
    public void ParseResponse_RefusesAnAnswerWithNoNodeIdentity()
    {
        Action parse = () => Krpc.ParseResponse(Response(new Dictionary<string, BValue>
        {
            ["token"] = new BBytes("x"u8.ToArray()),
        }));

        parse.Should().Throw<DhtException>();
    }

    private static byte[] Compact(string address, int port) =>
        [.. IPAddress.Parse(address).GetAddressBytes(), (byte)(port >> 8), (byte)(port & 0xFF)];

    private static byte[] Response(Dictionary<string, BValue> values) =>
        BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["t"] = new BBytes([0x01, 0x02]),
            ["y"] = new BBytes("r"u8.ToArray()),
            ["r"] = new BDictionary(values),
        }));
}
