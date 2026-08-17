using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Bencode, against a real <c>.torrent</c>'s bytes.
/// </summary>
/// <remarks>
/// The file is Ubuntu's own published torrent for its desktop image: a real
/// announce, a real announce-list of lists, and an info dictionary with four
/// hundred and eighty-four kilobytes of piece hashes in it. Every number in
/// this file was read out of it by a second implementation — a few lines of
/// Python — so nothing here is this parser agreeing with itself.
/// </remarks>
public class BencodeTests
{
    /// <remarks>
    /// All four types, from the file: integers, byte strings, a list of lists,
    /// and dictionaries inside dictionaries.
    /// </remarks>
    [Fact]
    public void ARealTorrentDecodesToItsFourTypes()
    {
        BencodeDictionary root = Root();

        Assert.Equal("https://torrent.ubuntu.com/announce", root.Text("announce"));

        BencodeList tiers = Assert.IsType<BencodeList>(root["announce-list"]);
        BencodeList first = Assert.IsType<BencodeList>(tiers.Items[0]);

        Assert.Equal(2, tiers.Items.Count);
        Assert.Equal("https://torrent.ubuntu.com/announce", Assert.IsType<BencodeBytes>(first.Items[0]).Text);

        BencodeDictionary info = Assert.IsType<BencodeDictionary>(root["info"]);

        Assert.Equal("ubuntu-24.04.3-desktop-amd64.iso", info.Text("name"));
        Assert.Equal(262144, info.Number("piece length"));
        Assert.Equal(6345887744, info.Number("length"));

        // Twenty bytes of SHA-1 per piece, and nothing left over.
        Assert.Equal(0, info.Bytes("pieces")!.Length % 20);
        Assert.Equal(484160, info.Bytes("pieces")!.Length);
    }

    /// <remarks>
    /// Bencode is byte-oriented and this is the byte string that proves it:
    /// the piece hashes are raw SHA-1s and are not valid UTF-8 at all. A reader
    /// that decoded byte strings into text would replace every byte it could
    /// not read, and every piece in the torrent would then fail verification.
    /// </remarks>
    [Fact]
    public void AByteStringThatIsNotUtf8SurvivesAsBytes()
    {
        byte[] pieces = Assert.IsType<BencodeDictionary>(Root()["info"]).Bytes("pieces")!;

        // Not valid UTF-8: proven here rather than asserted about, so this test
        // says something even if Ubuntu one day publishes hashes that happen to
        // decode.
        Assert.Throws<DecoderFallbackException>(
            () => new UTF8Encoding(false, true).GetString(pieces));

        // And the bytes are the file's own, exactly.
        byte[] file = File.ReadAllBytes(Torrent);
        int at = Find(file, "6:pieces484160:"u8) + "6:pieces484160:"u8.Length;

        Assert.True(pieces.AsSpan().SequenceEqual(file.AsSpan(at, pieces.Length)));
    }

    /// <remarks>
    /// Written back byte for byte. The info hash is SHA-1 over the raw bytes of
    /// the info dictionary as they arrived, so a reader that reorders keys, or
    /// drops a field it did not recognise, produces a torrent no peer will
    /// handshake on.
    /// </remarks>
    [Fact]
    public void EncodingADecodedTorrentReproducesItByteForByte()
    {
        byte[] file = File.ReadAllBytes(Torrent);

        Assert.True(Bencode.Write(Bencode.Read(file).Root).AsSpan().SequenceEqual(file));
    }

    /// <remarks>
    /// The reader records where the info dictionary was, because that range is
    /// the torrent's identity. The offsets and the hash below were read out of
    /// the file by a second implementation.
    /// </remarks>
    [Fact]
    public void TheReaderReportsTheByteRangeOfTheInfoDictionary()
    {
        byte[] file = File.ReadAllBytes(Torrent);

        BencodeDocument document = Bencode.Read(file);

        Assert.Equal(256, document.InfoStart);
        Assert.Equal(484261, document.InfoLength);

        Assert.Equal(
            "D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7",
            Convert.ToHexString(SHA1.HashData(file.AsSpan(document.InfoStart!.Value, document.InfoLength!.Value))));
    }

    /// <remarks>
    /// Only the <em>top-level</em> info dictionary. A peer sends bencode too,
    /// and a nested dictionary that happens to be keyed <c>info</c> is not the
    /// thing the info hash is taken over — recording it instead would hash the
    /// wrong bytes and every handshake would be refused. Written by hand
    /// because it is a statement about the grammar rather than about anything a
    /// site sent: no real torrent nests one, which is exactly why a reader that
    /// gets it wrong is never noticed.
    /// </remarks>
    [Fact]
    public void OnlyTheTopLevelInfoDictionaryIsRecorded()
    {
        byte[] nested = "d4:infod4:infod1:ai1eeee"u8.ToArray();

        BencodeDocument document = Bencode.Read(nested);

        // The outer info's value: everything from just after its key to the
        // document's own closing byte.
        Assert.Equal(7, document.InfoStart);
        Assert.Equal(nested.Length - 8, document.InfoLength);

        // And one that comes after it does not overwrite it either. The order
        // matters: the last dictionary read is the one a reader that has
        // forgotten which level it is on would leave behind.
        byte[] afterwards = "d4:infod1:ci2ee1:zd4:infod1:bi1eeee"u8.ToArray();

        Assert.Equal(7, Bencode.Read(afterwards).InfoStart);
        Assert.Equal(8, Bencode.Read(afterwards).InfoLength);
    }

    /// <remarks>
    /// Malformed input is refused with the offset it went wrong at, and never
    /// with an exception from deep inside a reader that walked off the end of
    /// its own buffer. The first two are the real file cut short, which is what
    /// a truncated download looks like; the rest are the grammar of BEP 3 said
    /// wrongly on purpose.
    /// </remarks>
    [Theory]
    [InlineData("i42")]
    [InlineData("i-0e")]
    [InlineData("i03e")]
    [InlineData("5:abc")]
    // One byte short, which is the off-by-one a bounds check is written for.
    [InlineData("5:abcd")]
    [InlineData("d3:onei1e")]
    [InlineData("l1:ai2e")]
    [InlineData("d1:a")]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("-1:a")]
    public void MalformedInputIsRefusedWithTheOffset(string malformed)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(malformed);

        Assert.False(Bencode.TryRead(bytes, out BencodeDocument? document, out BencodeError? error));

        Assert.Null(document);
        Assert.NotNull(error);
        Assert.InRange(error.Offset, 0, Math.Max(0, bytes.Length));
        Assert.NotEqual(string.Empty, error.Reason);
    }

    /// <remarks>
    /// Including the real file cut in half, which is what an interrupted
    /// download of one leaves behind.
    /// </remarks>
    [Fact]
    public void ATruncatedTorrentIsRefusedRatherThanHalfRead()
    {
        byte[] half = File.ReadAllBytes(Torrent).AsSpan(0, 200_000).ToArray();

        Assert.False(Bencode.TryRead(half, out BencodeDocument? document, out BencodeError? error));

        Assert.Null(document);
        Assert.Equal(200_000, error!.Offset);
    }

    /// <remarks>
    /// And trailing rubbish after a complete value is refused too. A torrent
    /// with something appended is not a torrent that happens to start with one.
    /// </remarks>
    [Fact]
    public void BytesAfterTheValueAreRefused()
    {
        byte[] extra = [.. File.ReadAllBytes(Torrent), .. "junk"u8];

        Assert.False(Bencode.TryRead(extra, out _, out BencodeError? error));
        Assert.Equal(484518, error!.Offset);
    }

    /// <remarks>
    /// Thrown only when the caller asked for it that way, and carrying the same
    /// offset. A stage that would rather not branch gets an exception it can
    /// report; nothing gets a surprise from inside the reader.
    /// </remarks>
    [Fact]
    public void ReadingMalformedBytesThrowsWithTheOffsetInIt()
    {
        BencodeFormatException thrown = Assert.Throws<BencodeFormatException>(
            () => Bencode.Read("d1:a"u8));

        Assert.Equal(4, thrown.Error.Offset);
        Assert.Contains("4", thrown.Message, StringComparison.Ordinal);
    }

    private static BencodeDictionary Root()
    {
        return Assert.IsType<BencodeDictionary>(Bencode.Read(File.ReadAllBytes(Torrent)).Root);
    }

    private static int Find(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.AsSpan().IndexOf(needle);
    }

    /// <remarks>
    /// <para>
    /// A real torrent, read and written back, byte for byte — including the
    /// entries this reader has no opinion about, and in the order they were in.
    /// </para>
    /// <para>
    /// It is here because of what it protects. The info hash is taken over the
    /// raw bytes of the info dictionary rather than over a re-encode of it, and
    /// a mutation that swapped one for the other survived every test in this
    /// suite: with a faithful writer the two really are the same bytes. This is
    /// what makes them the same, so a writer that changed how it spells an
    /// integer or a string fails here, next to the reason.
    /// </para>
    /// <para>
    /// What it cannot catch is a writer that sorts the keys, because a real
    /// torrent's are sorted already — bencode requires it. A file whose keys
    /// are out of order would hash differently through a re-encode and
    /// identically through the raw bytes, and no fixture here is one. That is
    /// the whole argument for hashing what arrived rather than what was
    /// rebuilt, and it is an argument rather than a test.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ubuntu-desktop.torrent")]
    [InlineData("archive-multifile.torrent")]
    public void ARealTorrentIsWrittenBackByteForByte(string file)
    {
        byte[] torrent = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Torrent)!, file));

        Assert.Equal(torrent, Bencode.Write(Bencode.Read(torrent).Root));
    }

    private static string Torrent
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "tests", "fixtures", "ubuntu-desktop.torrent");
        }
    }
}
