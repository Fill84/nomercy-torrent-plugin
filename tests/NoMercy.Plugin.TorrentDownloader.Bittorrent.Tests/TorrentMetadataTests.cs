using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// What is inside a <c>.torrent</c>, from two real ones.
/// </summary>
/// <remarks>
/// Ubuntu's desktop image is a single-file torrent; the Internet Archive's copy
/// of <em>The Principle of Relativity</em> is a multi-file one with
/// twenty-three files in it. Every number asserted here was read out of those
/// files by a second implementation.
/// </remarks>
public class TorrentMetadataTests
{
    /// <remarks>
    /// The info hash is SHA-1 over the raw bytes of the info dictionary as they
    /// arrived. Anything else — decoding and re-encoding, sorting keys,
    /// dropping a field nobody recognised — gives a hash no peer will shake
    /// hands on.
    /// </remarks>
    [Theory]
    [InlineData("ubuntu-desktop.torrent", "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7")]
    [InlineData("archive-multifile.torrent", "E2720161FF77B42E61D15F4958134DEBAE8D0A96")]
    public void TheInfoHashIsTakenOverTheRawInfoBytes(string file, string hash)
    {
        Assert.Equal(hash, Read(file).InfoHash);
    }

    /// <remarks>
    /// One file, one length, and the name is the file's own.
    /// </remarks>
    [Fact]
    public void ASingleFileTorrentYieldsOneFileAndItsSize()
    {
        TorrentMetadata torrent = Read("ubuntu-desktop.torrent");

        Assert.Equal("ubuntu-24.04.3-desktop-amd64.iso", torrent.Name);
        Assert.Equal(262144, torrent.PieceLength);
        Assert.Equal(6345887744, torrent.TotalLength);

        TorrentFileEntry only = Assert.Single(torrent.Files);

        Assert.Equal("ubuntu-24.04.3-desktop-amd64.iso", only.Path);
        Assert.Equal(6345887744, only.Length);
        Assert.Equal(0, only.Offset);

        Assert.Equal(24208, torrent.PieceCount);
    }

    /// <remarks>
    /// Twenty-three files, each with its path, its length and where it starts
    /// in the one byte stream the torrent really is. The name is the folder
    /// they go in rather than a file.
    /// </remarks>
    [Fact]
    public void AMultiFileTorrentYieldsEveryFileItsSizeAndItsPlaceInTheStream()
    {
        TorrentMetadata torrent = Read("archive-multifile.torrent");

        Assert.Equal("principleofrelat00eins", torrent.Name);
        Assert.Equal(524288, torrent.PieceLength);
        Assert.Equal(23, torrent.Files.Count);
        Assert.Equal(198588270, torrent.TotalLength);
        Assert.Equal(379, torrent.PieceCount);

        Assert.Equal("__ia_thumb.jpg", torrent.Files[0].Path);
        Assert.Equal(11534, torrent.Files[0].Length);
        Assert.Equal(0, torrent.Files[0].Offset);

        Assert.Equal("principleofrelat00eins.djvu", torrent.Files[1].Path);
        Assert.Equal(11534, torrent.Files[1].Offset);

        Assert.Equal(9450690, torrent.Files[2].Offset);

        // The last piece is short, and a client that assumed otherwise asks a
        // peer for bytes that do not exist.
        Assert.Equal(407406, torrent.LengthOfPiece(torrent.PieceCount - 1));
        Assert.Equal(524288, torrent.LengthOfPiece(0));
    }

    /// <remarks>
    /// A piece is a range of the whole stream and pays no attention to where
    /// one file ends and the next begins. The first piece of this torrent
    /// covers the end of a thumbnail and the start of a nine-megabyte scan, and
    /// a client that wrote it to one file would corrupt both.
    /// </remarks>
    [Fact]
    public void APieceThatStraddlesTwoFilesMapsToBoth()
    {
        TorrentMetadata torrent = Read("archive-multifile.torrent");

        TorrentSlice[] runs = [.. torrent.Slice(0, torrent.PieceLength)];

        Assert.Equal(2, runs.Length);

        Assert.Equal("__ia_thumb.jpg", runs[0].File.Path);
        Assert.Equal(0, runs[0].OffsetInFile);
        Assert.Equal(11534, runs[0].Length);

        Assert.Equal("principleofrelat00eins.djvu", runs[1].File.Path);
        Assert.Equal(0, runs[1].OffsetInFile);
        Assert.Equal(524288 - 11534, runs[1].Length);

        // And every byte of the piece is accounted for exactly once.
        Assert.Equal(torrent.PieceLength, runs.Sum(run => run.Length));
    }

    /// <remarks>
    /// And a piece that sits inside one file is one run. Nothing is split that
    /// does not need splitting.
    /// </remarks>
    [Fact]
    public void APieceInsideOneFileIsOneRun()
    {
        TorrentMetadata torrent = Read("archive-multifile.torrent");

        TorrentSlice only = Assert.Single(torrent.Slice(torrent.PieceLength, torrent.PieceLength));

        Assert.Equal("principleofrelat00eins.djvu", only.File.Path);
        Assert.Equal(524288 - 11534, only.OffsetInFile);
    }

    /// <remarks>
    /// Any offset in the stream, to the file it is in and how far into it. The
    /// answer below was worked out by the second implementation, at a byte
    /// nineteen megabytes in and five files along.
    /// </remarks>
    [Fact]
    public void AnyOffsetInTheStreamLandsInTheRightFile()
    {
        TorrentMetadata torrent = Read("archive-multifile.torrent");

        TorrentSlice found = Assert.Single(torrent.Slice(20_000_000, 1));

        Assert.Equal("principleofrelat00eins.pdf", found.File.Path);
        Assert.Equal(2785646, found.OffsetInFile);
    }

    /// <remarks>
    /// Every tracker the file names, the announce and the announce-list
    /// together, without duplicates and in the order they were given.
    /// </remarks>
    [Fact]
    public void EveryTrackerTheTorrentNamesIsRead()
    {
        Assert.Equal(
            ["https://torrent.ubuntu.com/announce", "https://ipv6.torrent.ubuntu.com/announce"],
            Read("ubuntu-desktop.torrent").Trackers);

        Assert.Equal(
            ["http://bt1.archive.org:6969/announce"],
            [Read("archive-multifile.torrent").Trackers[0]]);
    }

    /// <remarks>
    /// Neither of these is private, and a client that thought otherwise would
    /// switch off the DHT, peer exchange and local discovery for a torrent that
    /// wants all three.
    /// </remarks>
    [Fact]
    public void ARealPublicTorrentIsNotPrivate()
    {
        Assert.False(Read("ubuntu-desktop.torrent").Private);
        Assert.False(Read("archive-multifile.torrent").Private);
    }

    /// <remarks>
    /// And <c>info.private</c> is read where it is set. Written by hand: a
    /// private torrent comes from a private tracker with a passkey in it, which
    /// is not a thing to fetch into a repository — and BEP 27 is one field, so
    /// the bencode is stated rather than captured.
    /// </remarks>
    [Fact]
    public void APrivateTorrentSaysSo()
    {
        byte[] torrent = Encoding.ASCII.GetBytes(
            "d8:announce19:http://tracker/test4:infod6:lengthi1e4:name4:file12:piece lengthi16384e"
            + "6:pieces20:aaaaaaaaaaaaaaaaaaaa7:privatei1eee");

        Assert.True(TorrentMetadata.Read(torrent).Private);
    }

    private static TorrentMetadata Read(string file)
    {
        return TorrentMetadata.Read(File.ReadAllBytes(Fixture(file)));
    }

    private static string Fixture(string file)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "fixtures", file);
    }
}
