using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// What a peer said it has before anybody knew how many pieces there are.
/// </summary>
/// <remarks>
/// <para>
/// A magnet is a hash. The connections that fetch its metadata are made with
/// nought pieces, and a peer sends its bitfield the moment the handshake is
/// done, so nearly every one of them has said what it has before this side can
/// check a bit of it. Those connections are the ones the download then runs on.
/// </para>
/// <para>
/// The bitfield was kept as it came and never read again. On 19 November 2026
/// the owner's Downloads page read "0 of 7" seeds beside "5 of 1" peers on an
/// episode arriving at seventy megabytes a second: the five were seeds, each
/// holding every piece out of a count rounded up to eight.
/// </para>
/// <para>
/// 379 pieces throughout, the archive fixture's count: forty-eight bytes with
/// five bits to spare. A count that is a multiple of eight hides all of this.
/// </para>
/// </remarks>
public class PeerConnectionSizedTests
{
    private const int Pieces = 379;

    private static readonly byte[] InfoHash = [.. Enumerable.Range(1, 20).Select(one => (byte)one)];

    [Fact]
    public async Task ASeedsEarlyBitfieldIsASeedsOnceTheCountIsKnown()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        await theirs.SendAsync(new(PeerMessageId.Bitfield, Whole().Write()), stopping.Token);
        await ours.NextAsync(stopping.Token);

        // As it came: 379 set out of the 384 bits its bytes have.
        Assert.False(ours.Seed);

        ours.Sized(Pieces);

        Assert.True(ours.Seed);
        Assert.Equal(Pieces, ours.Has.Pieces);
        Assert.Equal(Pieces, ours.Has.Count);
    }

    /// <remarks>
    /// Nought out of nought is every piece there is. A peer that had said
    /// nothing was the one kind this client did count as a seed.
    /// </remarks>
    [Fact]
    public async Task APeerThatSentNoBitfieldHasNothingAndIsNoSeed()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (_, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        ours.Sized(Pieces);

        Assert.False(ours.Seed);
        Assert.Equal(Pieces, ours.Has.Pieces);
        Assert.Equal(0, ours.Has.Count);
    }

    /// <remarks>
    /// A <c>have</c> that came early was set only when its number was below how
    /// many pieces the peer already had, so a peer with one piece that finished
    /// its three-hundredth was never asked for it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HavesSaidBeforeTheCountIsKnownAreKept(bool bitfieldFirst)
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        if (bitfieldFirst)
        {
            Bitfield one = new(Pieces);
            one.Set(0);

            await theirs.SendAsync(new(PeerMessageId.Bitfield, one.Write()), stopping.Token);
            await ours.NextAsync(stopping.Token);
        }

        foreach (int piece in new[] { 300, 378 })
        {
            await theirs.SendAsync(PeerMessage.Have(piece), stopping.Token);
            await ours.NextAsync(stopping.Token);
        }

        ours.Sized(Pieces);

        Assert.True(ours.Has.Has(300));
        Assert.True(ours.Has.Has(378));
        Assert.Equal(bitfieldFirst ? 3 : 2, ours.Has.Count);
        Assert.False(ours.Seed);
    }

    /// <remarks>
    /// After the count is known a connection made for a magnet is like any
    /// other: a <c>have</c> is set, and the last one makes a seed of a leecher.
    /// </remarks>
    [Fact]
    public async Task AfterTheCountIsKnownTheLastHaveMakesASeed()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        Bitfield allButTheLast = Whole();
        allButTheLast.Clear(Pieces - 1);

        await theirs.SendAsync(new(PeerMessageId.Bitfield, allButTheLast.Write()), stopping.Token);
        await ours.NextAsync(stopping.Token);

        ours.Sized(Pieces);

        Assert.False(ours.Seed);

        await theirs.SendAsync(PeerMessage.Have(Pieces - 1), stopping.Token);
        await ours.NextAsync(stopping.Token);

        Assert.True(ours.Seed);
    }

    /// <remarks>
    /// The rule every bitfield is read by, applied late and not waived: the
    /// wrong number of bytes, or a bit past the end, is a peer talking about
    /// another torrent.
    /// </remarks>
    [Theory]
    [InlineData(47, 0x00)]
    [InlineData(49, 0x00)]
    [InlineData(48, 0xFF)]
    public async Task AnEarlyBitfieldThatCannotBeThisTorrentsIsRefusedWhenTheCountIsKnown(int bytes, byte fill)
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        byte[] said = new byte[bytes];
        Array.Fill(said, fill);

        await theirs.SendAsync(new(PeerMessageId.Bitfield, said), stopping.Token);
        await ours.NextAsync(stopping.Token);

        Assert.Throws<PeerProtocolException>(() => ours.Sized(Pieces));
    }

    [Fact]
    public async Task AnEarlyHavePastTheEndIsRefusedWhenTheCountIsKnown()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        await theirs.SendAsync(PeerMessage.Have(Pieces), stopping.Token);
        await ours.NextAsync(stopping.Token);

        Assert.Throws<PeerProtocolException>(() => ours.Sized(Pieces));
    }

    /// <remarks>
    /// A peer can say anything before it can be checked. A list is kept for a
    /// handful of pieces finished while the metadata arrives, not for a flood.
    /// </remarks>
    [Fact]
    public async Task EarlyHavesAreKeptUpToALimitAndNoFurther()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsMagnetAsync(stopping.Token);

        // The limit of piece nought, then one that would be refused if kept.
        for (int said = 0; said < PeerConnection.MostEarlyHaves; said++)
        {
            await theirs.SendAsync(PeerMessage.Have(0), stopping.Token);
            await ours.NextAsync(stopping.Token);
        }

        await theirs.SendAsync(PeerMessage.Have(Pieces), stopping.Token);
        await ours.NextAsync(stopping.Token);

        ours.Sized(Pieces);

        Assert.Equal(1, ours.Has.Count);
    }

    /// <remarks>
    /// A connection made once the torrent was known checked its bitfield as it
    /// arrived. Telling it a count again changes nothing.
    /// </remarks>
    [Fact]
    public async Task AConnectionThatKnewTheCountIsLeftAsItIs()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));
        (PeerConnection theirs, PeerConnection ours) = await MetAsync(Pieces, stopping.Token);

        await theirs.SendAsync(new(PeerMessageId.Bitfield, Whole().Write()), stopping.Token);
        await ours.NextAsync(stopping.Token);

        Assert.True(ours.Seed);

        ours.Sized(Pieces + 8);

        Assert.True(ours.Seed);
        Assert.Equal(Pieces, ours.Has.Pieces);
    }

    private static Bitfield Whole()
    {
        Bitfield whole = new(Pieces);

        for (int piece = 0; piece < Pieces; piece++)
        {
            whole.Set(piece);
        }

        return whole;
    }

    /// <summary>Two ends of one conversation, ours made the way a magnet's are: with nought pieces.</summary>
    private static Task<(PeerConnection Theirs, PeerConnection Ours)> MetAsMagnetAsync(CancellationToken ct)
    {
        return MetAsync(0, ct);
    }

    private static async Task<(PeerConnection Theirs, PeerConnection Ours)> MetAsync(int pieces, CancellationToken ct)
    {
        PeerWire wire = new();

        PeerConnection?[] introduced = await Task.WhenAll(
            PeerConnection.IntroduceAsync(wire.Receiver, InfoHash, Id("THEIRS"), 0, dialling: false, ct),
            PeerConnection.IntroduceAsync(wire.Initiator, InfoHash, Id("NM0001"), pieces, dialling: true, ct));

        return (introduced[0]!, introduced[1]!);
    }

    private static byte[] Id(string name)
    {
        byte[] id = new byte[20];

        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(id, 0);

        return id;
    }
}
