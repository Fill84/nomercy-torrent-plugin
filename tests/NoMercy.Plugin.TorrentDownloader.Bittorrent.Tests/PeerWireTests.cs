using System.Buffers.Binary;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The peer wire.
/// </summary>
/// <remarks>
/// <para>
/// The handshake is tested against a real one: <c>tests/fixtures/peer-handshake.bin</c>
/// is what a Transmission 4.1.3 peer in the Ubuntu swarm really sent this
/// machine when it was dialled by <c>tools/Capture --peer</c>.
/// </para>
/// <para>
/// The messages are not, and the reason is recorded rather than glossed: no
/// peer on this network would send any. Fifty were dialled over several
/// announces; almost none accepted a connection at all, and the one that did
/// shook hands and said nothing further. The bytes asserted below are BEP 3's
/// own layout, stated here, and every one of them is round-tripped through the
/// writer and the reader. When a peer will talk, the capture tool is already
/// written and these become assertions about a real conversation.
/// </para>
/// </remarks>
public class PeerWireTests
{
    /// <remarks>
    /// Sixty-eight bytes: the length, the name, eight reserved, the hash, the
    /// id. A peer reads the reserved bytes to decide what it will offer, and
    /// gets its own framing wrong if the length is wrong.
    /// </remarks>
    [Fact]
    public void OurHandshakeIsExactAndCarriesTheExtensionAndDhtBits()
    {
        byte[] bytes = Handshake.Write(InfoHash, PeerId);

        Assert.Equal(68, bytes.Length);
        Assert.Equal(19, bytes[0]);
        Assert.True(bytes.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8));

        // Byte five of the reserved eight carries BEP 10, byte seven the DHT.
        Assert.Equal(0x10, bytes[25]);
        Assert.Equal(0x01, bytes[27]);

        // And the rest of the reserved bytes are nought: a bit set by accident
        // is a promise this client cannot keep.
        foreach (int at in (int[])[20, 21, 22, 23, 24, 26])
        {
            Assert.Equal(0, bytes[at]);
        }

        Assert.True(bytes.AsSpan(28, 20).SequenceEqual(InfoHash));
        Assert.True(bytes.AsSpan(48, 20).SequenceEqual(PeerId));
    }

    /// <remarks>
    /// A real peer's handshake, from the Ubuntu swarm. It advertises the
    /// extension protocol and the DHT, which is what makes a magnet resolvable
    /// at all — and it is the only proof that this client reads the reserved
    /// bytes at the offsets a real client writes them.
    /// </remarks>
    [Fact]
    public void ARealPeersHandshakeIsReadForItsHashAndItsBits()
    {
        PeerHandshake peer = Assert.IsType<PeerHandshake>(Handshake.Read(Fixture("peer-handshake.bin")));

        Assert.Equal(InfoHash, peer.InfoHash);
        Assert.StartsWith("-TR", peer.Client, StringComparison.Ordinal);

        Assert.True(peer.Extensions);
        Assert.True(peer.Dht);

        Assert.True(Handshake.IsFor(peer, InfoHash));
    }

    /// <remarks>
    /// A peer answering with another torrent's hash is not confused, it is
    /// another torrent — and whatever it sent after that would be written into
    /// the wrong file.
    /// </remarks>
    [Fact]
    public void APeerWithTheWrongInfoHashIsNotForThisTorrent()
    {
        PeerHandshake peer = Assert.IsType<PeerHandshake>(Handshake.Read(Fixture("peer-handshake.bin")));

        Assert.False(Handshake.IsFor(peer, Convert.FromHexString("E2720161FF77B42E61D15F4958134DEBAE8D0A96")));
    }

    /// <remarks>
    /// And something that is not a handshake at all is nobody: a stranger
    /// connecting to this port and sending rubbish is an ordinary event on the
    /// internet, not a fault to report.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotAHandshakeIsRefusedQuietly()
    {
        Assert.Null(Handshake.Read("this is not a handshake at all, not even close to it, no"u8));
        Assert.Null(Handshake.Read("short"u8));
    }

    /// <remarks>
    /// Every message: written, read back, and written again to the same bytes.
    /// The ids are BEP 3's own numbers, which is the part a client can be
    /// quietly wrong about — a <c>have</c> read as a <c>bitfield</c> is a peer
    /// that appears to have the whole torrent.
    /// </remarks>
    [Theory]
    [InlineData(PeerMessageId.Choke, 0)]
    [InlineData(PeerMessageId.Unchoke, 1)]
    [InlineData(PeerMessageId.Interested, 2)]
    [InlineData(PeerMessageId.NotInterested, 3)]
    public void AMessageWithNoPayloadIsFiveBytesAndItsOwnId(PeerMessageId id, byte expected)
    {
        byte[] bytes = PeerMessage.Of(id).Write();

        Assert.Equal(5, bytes.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.Equal(expected, bytes[4]);

        Assert.Equal(id, Read(bytes).Id);
    }

    /// <remarks>
    /// A request is the piece, the offset and the length, in that order, and a
    /// block is the piece, the offset and the bytes. Getting the order wrong
    /// asks for the right amount of the wrong thing.
    /// </remarks>
    [Fact]
    public void ARequestAndABlockRoundTripThroughTheirNumbers()
    {
        PeerMessage request = PeerMessage.Request(7, 2 * PeerMessage.BlockLength, PeerMessage.BlockLength);

        Assert.Equal(17, request.Write().Length);
        Assert.Equal(6, request.Write()[4]);
        Assert.Equal((7, 32768, 16384), Read(request.Write()).AsRequest());

        PeerMessage block = PeerMessage.Block(7, 32768, "the bytes themselves"u8);

        Assert.Equal(7, block.Write()[4]);

        (int piece, int offset, byte[] data) = Read(block.Write()).AsBlock();

        Assert.Equal(7, piece);
        Assert.Equal(32768, offset);
        Assert.True(data.AsSpan().SequenceEqual("the bytes themselves"u8));
    }

    /// <remarks>
    /// A have names one piece, and a cancel names the same three numbers a
    /// request does.
    /// </remarks>
    [Fact]
    public void AHaveAndACancelRoundTrip()
    {
        Assert.Equal(4, PeerMessage.Have(24207).Write()[4]);
        Assert.Equal(24207, Read(PeerMessage.Have(24207).Write()).AsHave());

        Assert.Equal(8, PeerMessage.Cancel(1, 2, 3).Write()[4]);
        Assert.Equal((1, 2, 3), Read(PeerMessage.Cancel(1, 2, 3).Write()).AsRequest());
    }

    /// <remarks>
    /// A keep-alive is four bytes of nought and no id at all. A reader that
    /// took it for a malformed message would drop every peer that went quiet
    /// for two minutes, which is every peer.
    /// </remarks>
    [Fact]
    public void AKeepAliveIsFourBytesOfNoughtAndNotAFault()
    {
        byte[] bytes = PeerMessage.KeepAlive.Write();

        Assert.Equal([0, 0, 0, 0], bytes);

        PeerMessageReader reader = Introduced();
        reader.Add(bytes);

        PeerMessage message = Assert.IsType<PeerMessage>(reader.Next());

        Assert.Null(message.Id);
    }

    /// <remarks>
    /// TCP is a stream and not a sequence of messages. A bitfield of two
    /// thousand bytes arrives in several reads and two small messages arrive in
    /// one; a reader that took one read for one message would work on a fast
    /// local network and fail against every real peer.
    /// </remarks>
    [Fact]
    public void MessagesSplitAcrossReadsAreReassembled()
    {
        byte[] stream =
        [
            .. PeerMessage.Of(PeerMessageId.Unchoke).Write(),
            .. new PeerMessage(PeerMessageId.Bitfield, new byte[3026]).Write(),
            .. PeerMessage.Have(1).Write(),
        ];

        PeerMessageReader reader = Introduced();
        List<PeerMessage> read = [];

        // Seven bytes at a time: no message begins or ends on that boundary.
        for (int at = 0; at < stream.Length; at += 7)
        {
            reader.Add(stream.AsSpan(at, Math.Min(7, stream.Length - at)));

            while (reader.Next() is PeerMessage message)
            {
                read.Add(message);
            }
        }

        Assert.Equal(
            [PeerMessageId.Unchoke, PeerMessageId.Bitfield, PeerMessageId.Have],
            read.Select(message => message.Id));

        Assert.Equal(3026, read[1].Payload.Length);
        Assert.Equal(1, read[2].AsHave());
    }

    /// <remarks>
    /// And two messages arriving in one read are two messages.
    /// </remarks>
    [Fact]
    public void TwoMessagesInOneReadAreTwoMessages()
    {
        PeerMessageReader reader = Introduced();

        reader.Add([.. PeerMessage.Of(PeerMessageId.Choke).Write(), .. PeerMessage.Have(9).Write()]);

        Assert.Equal(PeerMessageId.Choke, reader.Next()!.Id);
        Assert.Equal(PeerMessageId.Have, reader.Next()!.Id);
        Assert.Null(reader.Next());
    }

    /// <remarks>
    /// The handshake comes off the front of the same stream, and the messages
    /// after it are read as normal — which is how a real conversation arrives.
    /// </remarks>
    [Fact]
    public void TheHandshakeIsReadOffTheFrontOfTheStream()
    {
        PeerMessageReader reader = new();

        reader.Add([.. Fixture("peer-handshake.bin"), .. PeerMessage.Of(PeerMessageId.Unchoke).Write()]);

        PeerHandshake handshake = Assert.IsType<PeerHandshake>(reader.Handshake());

        Assert.Equal(InfoHash, handshake.InfoHash);
        Assert.Equal(PeerMessageId.Unchoke, reader.Next()!.Id);
    }

    /// <remarks>
    /// A peer claiming an enormous message is a peer trying to have this
    /// process allocate it. Refused by the number it claimed.
    /// </remarks>
    [Fact]
    public void AMessageLongerThanAnythingLegitimateIsRefused()
    {
        PeerMessageReader reader = Introduced();

        byte[] absurd = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(absurd, int.MaxValue);

        reader.Add(absurd);

        Assert.Throws<PeerProtocolException>(() => reader.Next());
    }

    /// <remarks>
    /// A block nobody asked for is not a gift: it is memory this process did
    /// not plan to spend, at an offset nothing is expecting. The peer sending
    /// it is broken or trying something, and either way it goes.
    /// </remarks>
    [Fact]
    public void ABlockNobodyRequestedIsRefused()
    {
        RequestLedger ledger = new();

        ledger.Asked(7, 0, PeerMessage.BlockLength);

        Assert.True(ledger.Accept(7, 0, PeerMessage.BlockLength));

        // The same block twice: the second one was not asked for either.
        Assert.False(ledger.Accept(7, 0, PeerMessage.BlockLength));

        Assert.False(ledger.Accept(9, 0, PeerMessage.BlockLength));
    }

    /// <remarks>
    /// And a block of the wrong length is the same fault wearing a different
    /// hat: a peer answering a sixteen-kibibyte request with a megabyte is
    /// still sending what nobody asked for.
    /// </remarks>
    [Fact]
    public void ABlockOfTheWrongLengthIsRefusedToo()
    {
        RequestLedger ledger = new();

        ledger.Asked(7, 0, PeerMessage.BlockLength);

        Assert.False(ledger.Accept(7, 0, PeerMessage.BlockLength * 4));
        Assert.Equal(1, ledger.InFlight);
    }

    /// <remarks>
    /// A choke ends every request that was in flight. Keeping them would have
    /// this client waiting on blocks nobody is going to send.
    /// </remarks>
    [Fact]
    public void AChokeForgetsEverythingInFlight()
    {
        RequestLedger ledger = new();

        ledger.Asked(1, 0, PeerMessage.BlockLength);
        ledger.Asked(1, PeerMessage.BlockLength, PeerMessage.BlockLength);

        Assert.Equal(2, ledger.InFlight);

        ledger.Clear();

        Assert.Equal(0, ledger.InFlight);
        Assert.False(ledger.Accept(1, 0, PeerMessage.BlockLength));
    }

    private static readonly byte[] InfoHash = Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7");

    private static readonly byte[] PeerId = "-NM0400-abcdefghijkl"u8.ToArray();

    /// <summary>A reader that has already had its handshake.</summary>
    private static PeerMessageReader Introduced()
    {
        PeerMessageReader reader = new();

        reader.Add(Fixture("peer-handshake.bin"));
        reader.Handshake();

        return reader;
    }

    private static PeerMessage Read(byte[] bytes)
    {
        PeerMessageReader reader = Introduced();

        reader.Add(bytes);

        return Assert.IsType<PeerMessage>(reader.Next());
    }

    /// <remarks>
    /// <para>
    /// An encrypted dial sends this client's handshake inside the encryption
    /// negotiation, so by the time there is a connection to make, ours has gone
    /// and theirs has already been read off the wire — and whatever they sent
    /// after it has been read too.
    /// </para>
    /// <para>
    /// Those extra bytes are the peer's first real message, most often its
    /// bitfield. Thrown away, the client would believe a peer that has the whole
    /// torrent has nothing, and would never ask it for a piece.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AHandshakeAlreadyReadIsUsedAndWhatFollowedItIsNotLost()
    {
        byte[] theirs = Fixture("peer-handshake.bin");
        byte[] infoHash = theirs[28..48];
        PeerMessage bitfield = new(PeerMessageId.Bitfield, new byte[] { 0xFF });

        // Their handshake and their first message, in one read, which is how a
        // peer that answers in a single round trip arrives.
        byte[] already = [.. theirs, .. bitfield.Write()];

        using MemoryStream wire = new();

        PeerConnection connection = Assert.IsType<PeerConnection>(
            await PeerConnection.IntroducedAsync(wire, infoHash, pieces: 8, already, CancellationToken.None));

        using (connection)
        {
            PeerMessage first = Assert.IsType<PeerMessage>(await connection.NextAsync(CancellationToken.None));

            Assert.Equal(PeerMessageId.Bitfield, first.Id);
            Assert.True(connection.Has.All, "the peer said it has everything and was not believed");
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>A magnet has no piece count, and a peer still sends a
    /// bitfield.</strong> A client that took a magnet on knows the info hash
    /// and nothing else: it dials with nought pieces, because the metadata that
    /// says how many there are is the very thing it is dialling for.
    /// </para>
    /// <para>
    /// Nearly every client sends its bitfield the moment the handshake is done,
    /// and a bitfield for nought pieces is nought bytes — so every one of them
    /// was read as a protocol violation and the peer was dropped. The
    /// conversation swallowed the exception, the peer went with it, and the
    /// metadata it was dialled for never had a chance to arrive. On 26 August
    /// 2026 a swarm of 1206 seeders answered 9 of 175 dials and every one of
    /// the nine was destroyed on its first message: the page said no peers, no
    /// seeds and no error, for hours.
    /// </para>
    /// <para>
    /// There is nothing to check a bitfield against until the metadata says how
    /// long it should be, so it is taken as it comes and checked when there is
    /// something to check it with.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABitfieldBeforeTheMetadataIsTakenRatherThanRefused()
    {
        byte[] theirs = Fixture("peer-handshake.bin");
        byte[] infoHash = theirs[28..48];

        // A real peer's bitfield: far more than the nought bytes a torrent of
        // nought pieces would have.
        PeerMessage bitfield = new(PeerMessageId.Bitfield, [.. Enumerable.Repeat((byte)0xFF, 188)]);

        byte[] already = [.. theirs, .. bitfield.Write()];

        using MemoryStream wire = new();

        PeerConnection connection = Assert.IsType<PeerConnection>(
            await PeerConnection.IntroducedAsync(wire, infoHash, pieces: 0, already, CancellationToken.None));

        using (connection)
        {
            PeerMessage first = Assert.IsType<PeerMessage>(await connection.NextAsync(CancellationToken.None));

            Assert.Equal(PeerMessageId.Bitfield, first.Id);
        }
    }

    /// <remarks>
    /// A peer offering another torrent is a different swarm, not a confused
    /// peer. BEP 3 says to drop it, and writing its blocks into these files
    /// would be writing somebody else's bytes.
    /// </remarks>
    [Fact]
    public async Task AHandshakeAlreadyReadForAnotherTorrentIsRefused()
    {
        byte[] theirs = Fixture("peer-handshake.bin");

        using MemoryStream wire = new();

        Assert.Null(await PeerConnection.IntroducedAsync(
            wire,
            [.. Enumerable.Range(0, 20).Select(one => (byte)one)],
            pieces: 8,
            theirs,
            CancellationToken.None));
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }
}
