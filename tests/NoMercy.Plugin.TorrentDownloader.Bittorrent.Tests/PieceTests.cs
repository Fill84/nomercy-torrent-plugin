using System.Security.Cryptography;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Choosing pieces, verifying them, and putting them on disk.
/// </summary>
/// <remarks>
/// The disk tests run against the real multi-file torrent in
/// <c>tests/fixtures/</c> — twenty-three files with the first piece straddling
/// two of them — and write real files into a temporary folder. A fake file
/// system would be a second implementation of the part that can be wrong.
/// </remarks>
public class PieceTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <remarks>
    /// The piece the fewest peers have. A client that took them in order
    /// finishes last: the common pieces stay common and the rare ones leave
    /// with the peer holding them.
    /// </remarks>
    [Fact]
    public void RarestFirstPicksThePieceTheFewestPeersHave()
    {
        PiecePicker picker = new(8);

        // Three peers have everything but piece five; one has piece five too.
        picker.Saw(Field(8, 0, 1, 2, 3, 4, 6, 7));
        picker.Saw(Field(8, 0, 1, 2, 3, 4, 6, 7));
        picker.Saw(Field(8, 0, 1, 2, 3, 4, 6, 7));
        picker.Saw(Field(8, 0, 1, 2, 3, 4, 5, 6, 7));

        // Past the first four, so rarest-first is in charge.
        Bitfield mine = Field(8, 0, 1, 2, 3);

        Assert.Equal(1, picker.Availability(5));
        Assert.Equal(5, picker.Next(mine, Field(8, 4, 5, 6, 7), new HashSet<int>(), new Random(1)));
    }

    /// <remarks>
    /// And a piece nobody has is never picked, however rare it is: rarest
    /// counts only among the pieces this peer actually has.
    /// </remarks>
    [Fact]
    public void APieceThePeerDoesNotHaveIsNeverPicked()
    {
        PiecePicker picker = new(4);
        picker.Saw(Field(4, 0, 1, 2, 3));

        Bitfield mine = Field(4, 0, 1, 2, 3);

        Assert.Null(picker.Next(mine, Field(4, 0, 1, 2, 3), new HashSet<int>(), new Random(1)));
        Assert.Null(picker.Next(new(4), Field(4), new HashSet<int>(), new Random(1)));
    }

    /// <remarks>
    /// The first four are picked at random, so something can be verified early.
    /// Rarest-first at the very start sends every peer after the same piece and
    /// nothing completes until it does.
    /// </remarks>
    [Fact]
    public void TheFirstFourPiecesArePickedAtRandom()
    {
        PiecePicker picker = new(64);
        picker.Saw(Field(64, [.. Enumerable.Range(0, 64)]));

        // One piece is rarest by a mile; while fewer than four are verified it
        // is not automatically the answer.
        picker.Saw(Field(64, [.. Enumerable.Range(1, 63)]));

        Bitfield everything = Field(64, [.. Enumerable.Range(0, 64)]);

        HashSet<int> picked = [];

        for (int seed = 0; seed < 40; seed++)
        {
            picked.Add(picker.Next(new(64), everything, new HashSet<int>(), new Random(seed))!.Value);
        }

        // Random means several different answers; rarest-first would give one.
        Assert.True(picked.Count > 5, $"Only {picked.Count} different pieces were ever picked.");

        // And once four are verified it settles on the rarest, every time.
        Bitfield four = Field(64, 1, 2, 3, 4);

        for (int seed = 0; seed < 10; seed++)
        {
            Assert.Equal(0, picker.Next(four, everything, new HashSet<int>(), new Random(seed)));
        }
    }

    /// <remarks>
    /// A piece already being asked of somebody is not asked again — until the
    /// endgame, when the last few are asked of everybody at once. The tail of a
    /// download is otherwise spent waiting on the slowest peer holding the last
    /// piece.
    /// </remarks>
    [Fact]
    public void InTheEndgameAPieceAlreadyInFlightIsAskedOfEverybody()
    {
        PiecePicker picker = new(64, endgamePieces: 2);
        picker.Saw(Field(64, [.. Enumerable.Range(0, 64)]));

        Bitfield everything = Field(64, [.. Enumerable.Range(0, 64)]);

        // Sixty of sixty-four verified: four to go, which is not the endgame.
        Bitfield most = Field(64, [.. Enumerable.Range(0, 60)]);

        Assert.False(picker.Endgame(most));

        // Three of the four are being asked of somebody already, so the answer
        // is the fourth — every time, whatever the randomness says.
        for (int seed = 0; seed < 20; seed++)
        {
            Assert.Equal(63, picker.Next(most, everything, new HashSet<int> { 60, 61, 62 }, new Random(seed)));
        }

        // Sixty-two: two to go, and both are asked of everybody.
        Bitfield nearlyAll = Field(64, [.. Enumerable.Range(0, 62)]);

        Assert.True(picker.Endgame(nearlyAll));

        // Both remaining pieces are in flight, and in the endgame that is no
        // reason not to ask this peer for one of them as well.
        int? again = picker.Next(nearlyAll, everything, new HashSet<int> { 62, 63 }, new Random(3));

        Assert.Contains(again, (int?[])[62, 63]);
    }

    /// <remarks>
    /// The blocks of a piece are sixteen kibibytes and in order, so the piece
    /// completes and can be verified rather than being a scatter of holes.
    /// </remarks>
    [Fact]
    public void APieceIsAskedForInSixteenKibibyteBlocksInOrder()
    {
        BlockRequest[] blocks = [.. PiecePicker.Blocks(3, 40_000)];

        Assert.Equal(3, blocks.Length);
        Assert.Equal(new(3, 0, 16384), blocks[0]);
        Assert.Equal(new(3, 16384, 16384), blocks[1]);

        // The last one is short, because the piece is.
        Assert.Equal(new(3, 32768, 7232), blocks[2]);
    }

    /// <remarks>
    /// A piece is verified against the twenty bytes the torrent named before
    /// anything of it reaches the disk. That check is the whole reason a
    /// torrent can be trusted at all.
    /// </remarks>
    [Fact]
    public void APieceThatHashesCorrectlyIsVerifiedAndOneThatDoesNotIsDiscarded()
    {
        byte[] wanted = RandomNumberGenerator.GetBytes(20_000);

        PieceAssembly good = new(0, wanted.Length, SHA1.HashData(wanted));

        Assert.Equal(PieceOutcome.Incomplete, good.Add(0, wanted.AsSpan(0, 16384), "one"));
        Assert.Equal(PieceOutcome.Verified, good.Add(16384, wanted.AsSpan(16384), "two"));
        Assert.True(good.Bytes.SequenceEqual(wanted));

        PieceAssembly bad = new(0, wanted.Length, SHA1.HashData(wanted));

        bad.Add(0, wanted.AsSpan(0, 16384), "one");

        byte[] ruined = wanted[16384..];
        ruined[0] ^= 0xFF;

        Assert.Equal(PieceOutcome.Failed, bad.Add(16384, ruined, "two"));
    }

    /// <remarks>
    /// Every peer that contributed to a failed piece is penalised, because
    /// there is no way to say which of them ruined it — and a peer present at
    /// two failures is the one they had in common.
    /// </remarks>
    [Fact]
    public void TwoFailedPiecesBanAPeerAndOneDoesNot()
    {
        PeerTrust trust = new();

        trust.Failed(["one", "two"]);

        Assert.Equal(1, trust.Failures("one"));
        Assert.False(trust.Banned("one"));
        Assert.False(trust.Banned("two"));

        trust.Failed(["one", "three"]);

        Assert.True(trust.Banned("one"));
        Assert.False(trust.Banned("two"));
        Assert.False(trust.Banned("three"));
    }

    /// <remarks>
    /// A block at an offset that is not a block boundary, or one that runs past
    /// the end of the piece, is a peer sending something nobody could have
    /// asked for.
    /// </remarks>
    [Fact]
    public void ABlockThatIsNotWhereABlockGoesIsRefused()
    {
        PieceAssembly piece = new(0, 20_000, new byte[20]);

        Assert.Throws<PeerProtocolException>(() => piece.Add(17, new byte[16], "one"));
        Assert.Throws<PeerProtocolException>(() => piece.Add(16384, new byte[16384], "one"));
    }

    /// <remarks>
    /// The first piece of the real Archive torrent covers the end of an
    /// eleven-kilobyte thumbnail and the start of a nine-megabyte scan. Writing
    /// it to one file would corrupt both.
    /// </remarks>
    [Fact]
    public void APieceStraddlingTwoFilesIsWrittenToBothAtTheRightOffsets()
    {
        TorrentMetadata torrent = Torrent();

        using TorrentDisk disk = new(torrent, _folder);
        disk.Create();

        byte[] piece = RandomNumberGenerator.GetBytes((int)torrent.PieceLength);
        disk.Write(0, piece);

        string thumbnail = disk.PathOf(torrent.Files[0]);
        string scan = disk.PathOf(torrent.Files[1]);

        // The whole of the first file is the front of the piece.
        Assert.True(Bytes(thumbnail).AsSpan().SequenceEqual(piece.AsSpan(0, (int)torrent.Files[0].Length)));

        // And the rest of the piece is the front of the second file.
        byte[] start = Bytes(scan).AsSpan(0, piece.Length - (int)torrent.Files[0].Length).ToArray();

        Assert.True(start.AsSpan().SequenceEqual(piece.AsSpan((int)torrent.Files[0].Length)));

        // And read back through the same map, it is the piece again.
        Assert.True(disk.Read(0, piece.Length).AsSpan().SequenceEqual(piece));
    }

    /// <remarks>
    /// And a piece that sits inside one file goes at its own offset in that
    /// file, not at the front of it. The second piece of this torrent begins
    /// half a megabyte into a nine-megabyte scan, and writing it to the start
    /// would destroy what the first piece put there.
    /// </remarks>
    [Fact]
    public void APieceInsideOneFileIsWrittenAtItsOffsetInThatFile()
    {
        TorrentMetadata torrent = Torrent();

        using TorrentDisk disk = new(torrent, _folder);
        disk.Create();

        byte[] piece = RandomNumberGenerator.GetBytes((int)torrent.PieceLength);
        disk.Write(1, piece);

        long at = torrent.PieceLength - torrent.Files[0].Length;
        byte[] file = Bytes(disk.PathOf(torrent.Files[1]));

        Assert.True(file.AsSpan((int)at, piece.Length).SequenceEqual(piece));

        // And nothing was put in front of it: that is the first piece's place.
        Assert.True(file.AsSpan(0, (int)at).ToArray().All(one => one == 0));
    }

    /// <remarks>
    /// Files are made at their full size and sparse. A torrent of two hundred
    /// megabytes should not spend two hundred megabytes of writing before the
    /// first block arrives, and a download that is cancelled should not have
    /// filled the disk in the meantime.
    /// </remarks>
    [Fact]
    public void FilesAreCreatedSparseAtTheirFullSize()
    {
        TorrentMetadata torrent = Torrent();

        using TorrentDisk disk = new(torrent, _folder);
        disk.Create();

        foreach (TorrentFileEntry file in torrent.Files)
        {
            FileInfo made = new(disk.PathOf(file));

            Assert.True(made.Exists, $"{file.Path} was not created.");
            Assert.Equal(file.Length, made.Length);

            if (OperatingSystem.IsWindows())
            {
                // NTFS reserves the whole length otherwise, and a six-gigabyte
                // torrent would cost six gigabytes before a peer answered.
                Assert.True(
                    made.Attributes.HasFlag(FileAttributes.SparseFile),
                    $"{file.Path} is not sparse.");
            }
        }
    }

    /// <remarks>
    /// Creating is idempotent, which is what a resumed torrent needs: the files
    /// are made again on every start, and what is already on disk stays there.
    /// </remarks>
    [Fact]
    public void CreatingTwiceKeepsWhatIsAlreadyThere()
    {
        TorrentMetadata torrent = Torrent();

        using TorrentDisk disk = new(torrent, _folder);
        disk.Create();
        disk.Write(0, RandomNumberGenerator.GetBytes((int)torrent.PieceLength));

        byte[] before = Bytes(disk.PathOf(torrent.Files[0]));

        disk.Create();

        Assert.True(Bytes(disk.PathOf(torrent.Files[0])).AsSpan().SequenceEqual(before));
    }

    /// <remarks>
    /// A bitfield is one bit per piece with the high bit of the first byte
    /// being piece nought — the opposite of what a bit index usually means, and
    /// a client that got it backwards would ask every peer for what they do not
    /// have.
    /// </remarks>
    [Fact]
    public void ABitfieldIsHighBitFirstAndRoundTrips()
    {
        Bitfield field = Field(12, 0, 7, 8, 11);

        Assert.Equal([0b1000_0001, 0b1001_0000], field.Write());

        Bitfield read = Bitfield.Read(field.Write(), 12);

        Assert.True(read.Has(0));
        Assert.True(read.Has(11));
        Assert.False(read.Has(1));
        Assert.Equal(4, read.Count);
    }

    /// <remarks>
    /// A bitfield of the wrong length, or with bits set past the end of the
    /// torrent, is a peer talking about something else. BEP 3 says to drop it.
    /// </remarks>
    [Fact]
    public void ABitfieldThatIsNotForThisTorrentIsRefused()
    {
        Assert.Throws<PeerProtocolException>(() => Bitfield.Read(new byte[3], 12));
        Assert.Throws<PeerProtocolException>(() => Bitfield.Read([0b0000_0000, 0b0000_0001], 12));
    }

    /// <summary>
    /// Reads a file the client still has open.
    /// </summary>
    /// <remarks>
    /// Sharing both ways, because the client holds every file of a torrent open
    /// while it downloads — which is the point of the class under test, and
    /// which <c>File.ReadAllBytes</c> refuses to read past.
    /// </remarks>
    private static byte[] Bytes(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[stream.Length];

        stream.ReadExactly(bytes);

        return bytes;
    }

    private static Bitfield Field(int pieces, params int[] has)
    {
        Bitfield field = new(pieces);

        foreach (int piece in has)
        {
            field.Set(piece);
        }

        return field;
    }

    private static TorrentMetadata Torrent()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return TorrentMetadata.Read(
            File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", "archive-multifile.torrent")));
    }
}
