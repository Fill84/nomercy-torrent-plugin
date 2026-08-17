using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Fetching a torrent's metadata from a peer, which is all a magnet leaves to do.
/// </summary>
/// <remarks>
/// <para>
/// The metadata asserted here is real: the info dictionary of
/// <c>tests/fixtures/ubuntu-desktop.torrent</c>, 484 kilobytes of it, taken out
/// of the file as raw bytes and handed back a piece at a time exactly as a peer
/// would. It has to reassemble to Ubuntu's own published info hash — a hash
/// that was known before any of this code existed — and to the same file list,
/// piece length and piece count the whole-file reader produces. Nothing here
/// can pass by agreeing with itself.
/// </para>
/// <para>
/// The framing around those bytes is BEP 9's and BEP 10's, stated here for the
/// same reason it was stated in <see cref="PeerWireTests"/>: no peer on this
/// network will hold a conversation. Every message is round-tripped through
/// this client's writer and its reader, and the extension handshake is read
/// back out of real bencode.
/// </para>
/// </remarks>
public class MetadataFromPeersTests
{
    /// <remarks>
    /// A peer cannot send us metadata without being told what to call the
    /// message. Ours goes out in the handshake and is ours alone to choose.
    /// </remarks>
    [Fact]
    public void OurHandshakeOffersUtMetadata()
    {
        ExtensionHandshake ours = Extensions.Read(Extensions.Handshake("NoMercy 0.4.0"));

        Assert.Equal(Extensions.OurMetadataId, ours.MetadataId);
        Assert.Equal("NoMercy 0.4.0", ours.Client);
    }

    /// <remarks>
    /// Nought is the handshake's own id in both directions. Sending it under
    /// anything else is a message the peer will not recognise.
    /// </remarks>
    [Fact]
    public void TheHandshakeGoesOutUnderIdNought()
    {
        PeerMessage message = Extensions.Handshake("NoMercy 0.4.0");

        Assert.Equal(PeerMessageId.Extended, message.Id);
        Assert.Equal(Extensions.HandshakeId, message.Payload[0]);
    }

    /// <remarks>
    /// A magnet has no size in it. Without the peer saying how many bytes the
    /// info dictionary is there is no way to know how many pieces to ask for,
    /// so ours does not claim one it has not got.
    /// </remarks>
    [Fact]
    public void OurHandshakeClaimsNoSizeUntilThereIsOne()
    {
        Assert.Null(Extensions.Read(Extensions.Handshake("NoMercy 0.4.0")).MetadataSize);
        Assert.Equal(484261, Extensions.Read(Extensions.Handshake("NoMercy 0.4.0", 484261)).MetadataSize);
    }

    /// <remarks>
    /// The id is the <em>peer's</em> choice and differs per peer. A client that
    /// sent metadata requests under its own number would be sending them to
    /// whatever that peer happens to call something else.
    /// </remarks>
    [Fact]
    public void APeersHandshakeNamesItsOwnIdAndTheSizeOfTheMetadata()
    {
        ExtensionHandshake theirs = Extensions.Read(Peer(metadataId: 3, size: 484261, client: "Transmission 4.1.3"));

        Assert.Equal(3, theirs.MetadataId);
        Assert.Equal(484261, theirs.MetadataSize);
        Assert.Equal("Transmission 4.1.3", theirs.Client);
        Assert.Equal(7, theirs.Messages[Extensions.PeerExchange]);
    }

    /// <remarks>
    /// Not every peer speaks it, and one that does not is no use for a magnet.
    /// </remarks>
    [Fact]
    public void APeerThatDoesNotSpeakItHasNoId()
    {
        Assert.Null(Extensions.Read(Peer(metadataId: null, size: null, client: "qBittorrent 5.0")).MetadataId);
    }

    /// <remarks>
    /// BEP 10: an id of nought in a second handshake means the peer has dropped
    /// that extension. Taking it at face value would have this client send
    /// requests under id nought, which is the handshake's own number.
    /// </remarks>
    [Fact]
    public void AnIdOfNoughtMeansThePeerHasDroppedIt()
    {
        Assert.Null(Extensions.Read(Peer(metadataId: 0, size: 484261, client: "qBittorrent 5.0")).MetadataId);
    }

    /// <remarks>
    /// Anything else under the extended id is some other extension's traffic,
    /// and reading it as a handshake would take its ids for the peer's.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotTheHandshakeIsRefused()
    {
        Assert.Throws<PeerProtocolException>(
            () => Extensions.Read(MetadataTransfer.Request(theirId: 3, piece: 0)));

        Assert.Throws<PeerProtocolException>(
            () => Extensions.Read(PeerMessage.Of(PeerMessageId.Unchoke)));
    }

    /// <remarks>
    /// Sixteen kibibytes each, and the last one is short. A client that asked
    /// for a round number of pieces would wait forever for one the peer will
    /// never send.
    /// </remarks>
    [Fact]
    public void TheMetadataIsCountedInSixteenKibibytePiecesWithAShortLastOne()
    {
        Assert.Equal(30, MetadataTransfer.Pieces(484261));
        Assert.Equal(1, MetadataTransfer.Pieces(1));
        Assert.Equal(1, MetadataTransfer.Pieces(MetadataTransfer.PieceLength));
        Assert.Equal(2, MetadataTransfer.Pieces(MetadataTransfer.PieceLength + 1));
    }

    /// <remarks>
    /// The real thing, in pieces, through the writer and back through the
    /// reader: Ubuntu's own info dictionary reassembles to Ubuntu's own info
    /// hash, and to the same torrent the whole-file reader produces.
    /// </remarks>
    [Fact]
    public void TheRealInfoDictionaryComesBackInPiecesAndIsWhatTheTorrentSaid()
    {
        byte[] info = UbuntuInfo();

        MetadataFetch fetch = new(Ubuntu, info.Length);

        Assert.Equal(30, fetch.Pieces);

        foreach (int piece in fetch.Wanted().ToArray())
        {
            // Written by us as a peer would write it, framed as a peer-wire
            // message, and read back the way one arrives.
            MetadataPart part = MetadataTransfer.Read(
                Rewritten(MetadataTransfer.Data(theirId: 3, piece, info.Length, Slice(info, piece))));

            Assert.Equal(MetadataMessage.Data, part.Kind);
            Assert.Equal(info.Length, part.TotalSize);

            fetch.Add(part.Piece, part.Data, "203.0.113.7:51413");
        }

        Assert.True(fetch.Complete);
        Assert.True(fetch.Verified);

        TorrentMetadata torrent = fetch.Read(["udp://tracker.example:1337/announce"]);

        Assert.Equal("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7", torrent.InfoHash);
        Assert.Equal("ubuntu-24.04.3-desktop-amd64.iso", torrent.Name);
        Assert.Equal(262144, torrent.PieceLength);
        Assert.Equal(24208, torrent.PieceCount);
        Assert.Equal(6345887744, torrent.TotalLength);
        Assert.Equal("udp://tracker.example:1337/announce", Assert.Single(torrent.Trackers));
    }

    /// <remarks>
    /// A magnet carries the trackers; the info dictionary has none. A client
    /// that took the metadata's word for it would announce to nobody.
    /// </remarks>
    [Fact]
    public void TheTrackersComeFromTheMagnetBecauseTheInfoDictionaryHasNone()
    {
        MetadataFetch fetch = Filled(UbuntuInfo());

        Assert.Empty(fetch.Read([]).Trackers);
        Assert.Equal(2, fetch.Read(["udp://one.example:80", "udp://two.example:80"]).Trackers.Count);
    }

    /// <remarks>
    /// Only the pieces that have not arrived, so a client with three peers does
    /// not ask all three for all thirty.
    /// </remarks>
    [Fact]
    public void OnlyThePiecesThatHaveNotArrivedAreAskedFor()
    {
        byte[] info = UbuntuInfo();
        MetadataFetch fetch = new(Ubuntu, info.Length);

        fetch.Add(0, Slice(info, 0), "203.0.113.7:51413");
        fetch.Add(29, Slice(info, 29), "203.0.113.7:51413");

        Assert.Equal(28, fetch.Wanted().Count());
        Assert.DoesNotContain(0, fetch.Wanted());
        Assert.DoesNotContain(29, fetch.Wanted());
        Assert.False(fetch.Complete);
    }

    /// <remarks>
    /// One byte of 484 kilobytes, and the hash says so. This is the whole
    /// reason the info hash exists: a peer can be lying and there is no other
    /// way to tell.
    /// </remarks>
    [Fact]
    public void MetadataThatDoesNotHashToTheTorrentsHashIsRefused()
    {
        byte[] info = UbuntuInfo();

        info[200000] ^= 0x01;

        MetadataFetch fetch = Filled(info);

        Assert.True(fetch.Complete);
        Assert.False(fetch.Verified);
        Assert.Throws<TorrentFormatException>(() => fetch.Read([]));
    }

    /// <remarks>
    /// Every peer that sent part of it is dropped, and the fetch starts again
    /// from nothing. Unlike a piece of the download there is no per-piece hash
    /// to say which peer lied, and keeping any of it would have the next
    /// attempt reassemble the same wrong bytes.
    /// </remarks>
    [Fact]
    public void EveryPeerThatContributedToWrongMetadataIsDroppedAndTheFetchStartsAgain()
    {
        byte[] info = UbuntuInfo();

        info[200000] ^= 0x01;

        MetadataFetch fetch = new(Ubuntu, info.Length);

        for (int piece = 0; piece < fetch.Pieces; piece++)
        {
            fetch.Add(piece, Slice(info, piece), piece < 15 ? "203.0.113.7:51413" : "198.51.100.9:6881");
        }

        Assert.False(fetch.Verified);

        IReadOnlyCollection<string> dropped = fetch.Discard();

        Assert.Equal(2, dropped.Count);
        Assert.Contains("203.0.113.7:51413", dropped);
        Assert.Contains("198.51.100.9:6881", dropped);

        Assert.False(fetch.Complete);
        Assert.Equal(30, fetch.Wanted().Count());
        Assert.Empty(fetch.Contributors);
    }

    /// <remarks>
    /// A peer sending a piece that is not the length that piece is has either
    /// got a different torrent or is filling the buffer with something. Either
    /// way the assembled bytes would be wrong with nothing to say why.
    /// </remarks>
    [Fact]
    public void APieceOfTheWrongLengthOrTheWrongNumberIsRefused()
    {
        byte[] info = UbuntuInfo();
        MetadataFetch fetch = new(Ubuntu, info.Length);

        Assert.Throws<PeerProtocolException>(() => fetch.Add(0, new byte[MetadataTransfer.PieceLength - 1], "peer"));
        Assert.Throws<PeerProtocolException>(() => fetch.Add(29, Slice(info, 0), "peer"));
        Assert.Throws<PeerProtocolException>(() => fetch.Add(30, Slice(info, 29), "peer"));
        Assert.Throws<PeerProtocolException>(() => fetch.Add(-1, Slice(info, 0), "peer"));
    }

    /// <remarks>
    /// A request, a piece and a refusal all arrive under the same id and are
    /// told apart by <c>msg_type</c> alone.
    /// </remarks>
    [Fact]
    public void ARequestAndARejectAreToldApartFromAPiece()
    {
        MetadataPart request = MetadataTransfer.Read(Rewritten(MetadataTransfer.Request(theirId: 3, piece: 11)));

        Assert.Equal(MetadataMessage.Request, request.Kind);
        Assert.Equal(11, request.Piece);
        Assert.Empty(request.Data);

        MetadataPart reject = MetadataTransfer.Read(Rewritten(MetadataTransfer.Reject(theirId: 3, piece: 11)));

        Assert.Equal(MetadataMessage.Reject, reject.Kind);
        Assert.Equal(11, reject.Piece);
    }

    /// <remarks>
    /// It goes out under the number that peer asked for, not ours. This is the
    /// one thing the extension handshake exists to settle.
    /// </remarks>
    [Fact]
    public void AMetadataMessageGoesOutUnderTheIdThatPeerAskedFor()
    {
        Assert.Equal(3, MetadataTransfer.Request(theirId: 3, piece: 0).Payload[0]);
        Assert.Equal(7, MetadataTransfer.Request(theirId: 7, piece: 0).Payload[0]);
    }

    /// <remarks>
    /// The data follows the bencoded dictionary and is not inside it: bencode
    /// has no place to put sixteen kibibytes of binary that a peer could read
    /// back out unchanged.
    /// </remarks>
    [Fact]
    public void ThePieceBytesFollowTheDictionaryRatherThanSittingInIt()
    {
        byte[] info = UbuntuInfo();
        PeerMessage message = MetadataTransfer.Data(theirId: 3, piece: 0, info.Length, Slice(info, 0));

        Assert.Equal(
            MetadataTransfer.PieceLength,
            message.Payload.Length - 1 - Bencode.ReadPrefix(message.Payload.AsSpan(1)).Length);
    }

    /// <remarks>
    /// Anything under the metadata id that is not one of the three is a message
    /// from a newer BEP or a peer with a fault, and guessing at it would put
    /// bytes nobody vouched for into the info dictionary.
    /// </remarks>
    [Fact]
    public void AnUnknownMessageTypeIsRefused()
    {
        PeerMessage strange = Extensions.Extended(
            3,
            new BencodeDictionary(
            [
                new("msg_type"u8.ToArray(), new BencodeInteger(9)),
                new("piece"u8.ToArray(), new BencodeInteger(0)),
            ]));

        Assert.Throws<PeerProtocolException>(() => MetadataTransfer.Read(strange));
    }

    /// <remarks>
    /// Five minutes by default, from <c>MetadataTimeoutMinutes</c>. A magnet
    /// nobody in the swarm will serve the metadata for otherwise sits in the
    /// list forever saying "fetching metadata", which is <strong>exactly</strong>
    /// what 0.3.4 did.
    /// </remarks>
    [Fact]
    public void AFetchThatHasNotFinishedWithinTheLimitHasExpired()
    {
        DateTimeOffset started = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        MetadataFetch fetch = new(Ubuntu, UbuntuInfo().Length, started);

        Assert.False(fetch.Expired(started.AddMinutes(4.9), TimeSpan.FromMinutes(5)));
        Assert.True(fetch.Expired(started.AddMinutes(5), TimeSpan.FromMinutes(5)));
    }

    /// <remarks>
    /// The clock is only against the fetching. Metadata that arrived inside the
    /// limit is not failed by a tick that happens after it.
    /// </remarks>
    [Fact]
    public void MetadataThatArrivedInTimeIsNotExpiredByALaterTick()
    {
        DateTimeOffset started = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        MetadataFetch fetch = Filled(UbuntuInfo(), started);

        Assert.True(fetch.Verified);
        Assert.False(fetch.Expired(started.AddHours(9), TimeSpan.FromMinutes(5)));
    }

    /// <summary>Ubuntu's own info hash, which is what all of this has to come back to.</summary>
    private static byte[] Ubuntu =>
        Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7");

    /// <summary>The raw info dictionary out of the real torrent.</summary>
    private static byte[] UbuntuInfo()
    {
        byte[] torrent = Fixture("ubuntu-desktop.torrent");
        BencodeDocument document = Bencode.Read(torrent);

        return torrent[document.InfoStart!.Value..(document.InfoStart.Value + document.InfoLength!.Value)];
    }

    /// <summary>A fetch with every piece of these bytes in it, from one peer.</summary>
    private static MetadataFetch Filled(byte[] info, DateTimeOffset? started = null)
    {
        MetadataFetch fetch = new(Ubuntu, info.Length, started ?? DateTimeOffset.UnixEpoch);

        for (int piece = 0; piece < fetch.Pieces; piece++)
        {
            fetch.Add(piece, Slice(info, piece), "203.0.113.7:51413");
        }

        return fetch;
    }

    /// <summary>One sixteen-kibibyte piece of the metadata, the last one short.</summary>
    private static byte[] Slice(byte[] info, int piece)
    {
        int at = piece * MetadataTransfer.PieceLength;

        return info[at..Math.Min(at + MetadataTransfer.PieceLength, info.Length)];
    }

    /// <summary>
    /// A message put on the wire and read back off it, so nothing here asserts
    /// against an object that never went through the framing.
    /// </summary>
    private static PeerMessage Rewritten(PeerMessage message)
    {
        PeerMessageReader reader = new();

        reader.Add(message.Write());

        return reader.Next() ?? throw new InvalidOperationException("The message did not survive the wire.");
    }

    /// <summary>
    /// A peer's extension handshake, bencoded the way one arrives. BEP 10's
    /// layout, stated here — see the note on this class.
    /// </summary>
    private static PeerMessage Peer(int? metadataId, int? size, string client)
    {
        List<BencodeEntry> offered = [new(Encoding.ASCII.GetBytes(Extensions.PeerExchange), new BencodeInteger(7))];

        if (metadataId is int id)
        {
            offered.Add(new(Encoding.ASCII.GetBytes(Extensions.Metadata), new BencodeInteger(id)));
        }

        List<BencodeEntry> root =
        [
            new("m"u8.ToArray(), new BencodeDictionary(offered)),
            new("v"u8.ToArray(), new BencodeBytes(Encoding.UTF8.GetBytes(client))),
        ];

        if (size is int bytes)
        {
            root.Add(new("metadata_size"u8.ToArray(), new BencodeInteger(bytes)));
        }

        return Extensions.Extended(Extensions.HandshakeId, new BencodeDictionary(root));
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
}
