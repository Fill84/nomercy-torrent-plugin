using System.Net;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Peer exchange and local discovery: the two ways peers arrive without anybody
/// being asked.
/// </summary>
/// <remarks>
/// Both are messages this client writes and reads, so both are round-tripped
/// through its own writer and reader — and the reader is put to the shapes real
/// clients send, which for BEP 14 means header names in whatever case the
/// sender felt like and lines that end the way the specification says rather
/// than the way a text file does.
/// </remarks>
public class PeerExchangeTests
{
    /// <remarks>
    /// Compact, six bytes a peer, and the flags are one byte each. A length
    /// that does not match is what makes a real client drop the message whole,
    /// so the flags are written even though this client knows nothing to put
    /// in them.
    /// </remarks>
    [Fact]
    public void AddedAndDroppedPeersAreWrittenCompactAndReadBack()
    {
        PeerMessage message = Pex.Write(
            theirId: 7,
            [Peer("203.0.113.4", 51413), Peer("198.51.100.9", 6881)],
            [Peer("192.0.2.7", 12345)]);

        Assert.Equal(PeerMessageId.Extended, message.Id);
        Assert.Equal(7, message.Payload[0]);

        PexUpdate update = Pex.Read(message);

        Assert.Equal(["203.0.113.4:51413", "198.51.100.9:6881"], update.Added.Select(one => one.ToString()));
        Assert.Equal(["192.0.2.7:12345"], update.Dropped.Select(one => one.ToString()));

        BencodeDictionary body = Assert.IsType<BencodeDictionary>(
            Bencode.ReadPrefix(message.Payload.AsSpan(1)).Root);

        Assert.Equal(12, body.Bytes("added")!.Length);
        Assert.Equal(2, body.Bytes("added.f")!.Length);
    }

    /// <remarks>
    /// Fifty at a time, from BEP 11. A client with a thousand peers that sent
    /// all of them would send fifty kilobytes to every peer it has, out of the
    /// bandwidth the download wanted.
    /// </remarks>
    [Fact]
    public void AtMostFiftyPeersGoInOneMessage()
    {
        PexUpdate update = Pex.Read(Pex.Write(
            theirId: 7,
            [.. Enumerable.Range(1, 200).Select(which => Peer($"203.0.113.{which % 255}", 6881 + which))],
            []));

        Assert.Equal(Pex.MostPerMessage, update.Added.Count);
    }

    /// <remarks>
    /// Differences, not the list. The second message to the same peer says who
    /// has arrived and who has gone since the first, which is why what was sent
    /// is remembered per peer.
    /// </remarks>
    [Fact]
    public void WhatGoesOutIsWhatChangedSinceThatPeerWasLastTold()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        PeerExchange exchange = new(Torrent(priv: false), clock);

        PeerAddress[] first = [Peer("203.0.113.1", 6881), Peer("203.0.113.2", 6881)];

        PexUpdate opening = Pex.Read(exchange.Offer("peer-a", 7, first)!);

        Assert.Equal(2, opening.Added.Count);
        Assert.Empty(opening.Dropped);

        clock.Advance(Pex.LeastInterval);

        // One has gone and one is new.
        PexUpdate second = Pex.Read(exchange.Offer("peer-a", 7, [first[0], Peer("203.0.113.3", 6881)])!);

        Assert.Equal(["203.0.113.3:6881"], second.Added.Select(one => one.ToString()));
        Assert.Equal(["203.0.113.2:6881"], second.Dropped.Select(one => one.ToString()));
    }

    /// <remarks>
    /// At most once a minute per peer, from docs/06-torrent-client.md. BEP 11
    /// calls a client that sends them faster misbehaving, and a peer that
    /// agrees disconnects — which costs exactly the peer the message was meant
    /// to be a kindness to.
    /// </remarks>
    [Fact]
    public void APeerIsNotToldTwiceWithinAMinute()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        PeerExchange exchange = new(Torrent(priv: false), clock);

        Assert.NotNull(exchange.Offer("peer-a", 7, [Peer("203.0.113.1", 6881)]));

        clock.Advance(TimeSpan.FromSeconds(59));

        Assert.Null(exchange.Offer("peer-a", 7, [Peer("203.0.113.9", 6881)]));

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(exchange.Offer("peer-a", 7, [Peer("203.0.113.9", 6881)]));
    }

    /// <remarks>
    /// Per peer, not globally: a peer that connected thirty seconds ago has
    /// been told nothing yet, and holding its first message back for another
    /// half minute is the one case where it is most worth having.
    /// </remarks>
    [Fact]
    public void TheMinuteIsPerPeerAndNotAcrossAllOfThem()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        PeerExchange exchange = new(Torrent(priv: false), clock);

        Assert.NotNull(exchange.Offer("peer-a", 7, [Peer("203.0.113.1", 6881)]));

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.NotNull(exchange.Offer("peer-b", 3, [Peer("203.0.113.1", 6881)]));
    }

    /// <remarks>
    /// Nothing has changed, so there is nothing to send. A message saying so is
    /// one the peer has to read for nothing.
    /// </remarks>
    [Fact]
    public void APeerWithNothingNewToHearIsNotSentAnything()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        PeerExchange exchange = new(Torrent(priv: false), clock);

        PeerAddress[] known = [Peer("203.0.113.1", 6881)];

        Assert.NotNull(exchange.Offer("peer-a", 7, known));

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(exchange.Offer("peer-a", 7, known));
    }

    /// <remarks>
    /// Neither offered nor read. This is the half of BEP 27 that would really
    /// be seen: the client being told is somebody else's, and what it is being
    /// told is who is on a private torrent.
    /// </remarks>
    [Fact]
    public void APrivateTorrentOffersNothingAndReadsNothing()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        PeerExchange exchange = new(Torrent(priv: true), clock);

        Assert.False(exchange.Allowed);
        Assert.Null(exchange.Offer("peer-a", 7, [Peer("203.0.113.1", 6881), Peer("203.0.113.2", 6881)]));

        // And a peer that sends one anyway is not listened to: a peer list
        // arriving is the same leak as one leaving.
        PexUpdate ignored = exchange.Read(Pex.Write(7, [Peer("203.0.113.3", 6881)], []));

        Assert.Empty(ignored.Added);
        Assert.Empty(ignored.Dropped);
    }

    /// <remarks>
    /// Something under the extended id that is not this. A client that read it
    /// anyway would take somebody else's extension for a peer list.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotAPexMessageIsRefused()
    {
        Assert.Throws<PeerProtocolException>(() => Pex.Read(PeerMessage.Of(PeerMessageId.Unchoke)));
        Assert.Throws<BencodeFormatException>(() => Pex.Read(new(PeerMessageId.Extended, [7, 0x20, 0x20])));
    }

    /// <remarks>
    /// The message is HTTP-shaped and is not HTTP. Every line ends in a
    /// carriage return and a newline and the whole thing in two more; a client
    /// that wrote it the way a text file ends its lines is one nothing on the
    /// network will understand.
    /// </remarks>
    [Fact]
    public void AnAnnounceIsWrittenTheWayBep14SpellsIt()
    {
        string message = System.Text.Encoding.ASCII.GetString(
            LocalDiscovery.Write(51413, ["d160b8d8ea35a5b4e52837468fc8f03d55cef1f7"], "NM0400abc"));

        Assert.StartsWith("BT-SEARCH * HTTP/1.1\r\n", message, StringComparison.Ordinal);
        Assert.Contains("Host: 239.192.152.143:6771\r\n", message, StringComparison.Ordinal);
        Assert.Contains("Port: 51413\r\n", message, StringComparison.Ordinal);

        // Upper case, because that is how the hash is written everywhere else
        // in this client and a peer matching on the text would otherwise miss.
        Assert.Contains("Infohash: D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7\r\n", message, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n\r\n", message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Read back, and read the way real clients send it: header names in
    /// whatever case they please.
    /// </remarks>
    [Fact]
    public void AnAnnounceIsReadBackWhateverCaseTheHeadersAreIn()
    {
        LsdAnnounce? ours = LocalDiscovery.Read(
            LocalDiscovery.Write(51413, [Ubuntu], "somebody-else"),
            ours: "NM0400abc");

        Assert.NotNull(ours);
        Assert.Equal(51413, ours!.Port);
        Assert.Equal([Ubuntu], ours.InfoHashes);

        LsdAnnounce? shouted = LocalDiscovery.Read(
            System.Text.Encoding.ASCII.GetBytes(
                $"BT-SEARCH * HTTP/1.1\r\nHOST: 239.192.152.143:6771\r\nPORT: 6881\r\nINFOHASH: {Ubuntu}\r\nCOOKIE: xyz\r\n\r\n\r\n"),
            ours: "NM0400abc");

        Assert.NotNull(shouted);
        Assert.Equal(6881, shouted!.Port);
        Assert.Equal([Ubuntu], shouted.InfoHashes);
    }

    /// <remarks>
    /// Several torrents in one packet, which is how a client with a list
    /// announces them.
    /// </remarks>
    [Fact]
    public void OnePacketCanCarrySeveralTorrents()
    {
        LsdAnnounce? announce = LocalDiscovery.Read(
            LocalDiscovery.Write(51413, [Ubuntu, Archive], "somebody-else"),
            ours: "NM0400abc");

        Assert.Equal([Ubuntu, Archive], announce!.InfoHashes);
    }

    /// <remarks>
    /// Every packet this client sends comes straight back to it off the group.
    /// A client that took its own announce would spend the afternoon connecting
    /// to itself.
    /// </remarks>
    [Fact]
    public void OurOwnAnnounceComingBackRoundIsIgnored()
    {
        Assert.Null(LocalDiscovery.Read(
            LocalDiscovery.Write(51413, [Ubuntu], "NM0400abc"),
            ours: "NM0400abc"));
    }

    /// <remarks>
    /// The group carries plenty that is not this, and a packet without a port
    /// or without a hash says nothing worth acting on.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotAnAnnounceIsIgnored()
    {
        Assert.Null(LocalDiscovery.Read("GET / HTTP/1.1\r\n\r\n"u8, ours: "NM0400abc"));
        Assert.Null(LocalDiscovery.Read(""u8, ours: "NM0400abc"));

        Assert.Null(LocalDiscovery.Read(
            System.Text.Encoding.ASCII.GetBytes($"BT-SEARCH * HTTP/1.1\r\nInfohash: {Ubuntu}\r\n\r\n\r\n"),
            ours: "NM0400abc"));

        Assert.Null(LocalDiscovery.Read(
            "BT-SEARCH * HTTP/1.1\r\nPort: 51413\r\n\r\n\r\n"u8,
            ours: "NM0400abc"));

        // And a hash that is not one: forty hex characters or it is not an info
        // hash, and connecting on the strength of it would be connecting for a
        // torrent nobody named.
        Assert.Null(LocalDiscovery.Read(
            "BT-SEARCH * HTTP/1.1\r\nPort: 51413\r\nInfohash: not-a-hash\r\n\r\n\r\n"u8,
            ours: "NM0400abc"));
    }

    /// <remarks>
    /// The packet says the info hash in the clear to everybody on the network,
    /// which is exactly what a private tracker's members are forbidden to do.
    /// </remarks>
    [Fact]
    public void APrivateTorrentIsNeverAnnouncedOnTheGroup()
    {
        IReadOnlyList<string> announceable = LocalDiscovery.Announceable(
            [Torrent(priv: false), Torrent(priv: true)]);

        Assert.Equal([Torrent(priv: false).InfoHash], announceable);
    }

    private const string Ubuntu = "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7";

    private const string Archive = "E2720161FF77B42E61D15F4958134DEBAE8D0A96";

    private static PeerAddress Peer(string address, int port)
    {
        return new(IPAddress.Parse(address), port);
    }

    private static TorrentMetadata Torrent(bool priv)
    {
        return new(priv ? Archive : Ubuntu, "something", 262144, [], [], 0, [], priv);
    }

    /// <remarks>
    /// <para>
    /// <strong>A peer only sends what it was told we speak.</strong> The
    /// extension handshake is the whole of that conversation: an extension left
    /// out of it is one that never arrives, however well this client can read
    /// it.
    /// </para>
    /// <para>
    /// <c>ut_pex</c> was left out, so no peer ever offered one and every peer
    /// this client had came from a tracker's own list — fifty addresses of
    /// which most are stale. On 26 August 2026 a swarm that other clients see
    /// hundreds of seeds in gave this one a single peer, which is what asking
    /// nobody for more looks like.
    /// </para>
    /// </remarks>
    [Fact]
    public void OurHandshakeAsksForPeerExchangeAsWellAsMetadata()
    {
        PeerMessage handshake = Extensions.Handshake("NoMercy");

        ExtensionHandshake ours = Extensions.Read(handshake);

        Assert.Equal(Extensions.OurMetadataId, ours.Messages[Extensions.Metadata]);
        Assert.Equal(Extensions.OurExchangeId, ours.Messages[Extensions.PeerExchange]);
    }
}
