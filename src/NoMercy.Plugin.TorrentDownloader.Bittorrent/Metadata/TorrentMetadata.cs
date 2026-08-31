using System.Security.Cryptography;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// One file of a torrent, and where it sits in the stream.
/// </summary>
/// <remarks>
/// A multi-file torrent is one byte stream laid across several files, and every
/// piece is a range of that stream. <see cref="Offset"/> is where this file
/// begins in it.
/// </remarks>
/// <param name="Path">Its path under the torrent's name, with separators as this machine writes them.</param>
/// <param name="Length">How many bytes it is.</param>
/// <param name="Offset">Where it starts in the whole stream.</param>
public sealed record TorrentFileEntry(string Path, long Length, long Offset);

/// <summary>
/// A run of bytes that belongs to one file.
/// </summary>
/// <param name="File">Which file.</param>
/// <param name="OffsetInFile">How far into it the run starts.</param>
/// <param name="Length">How long the run is.</param>
public sealed record TorrentSlice(TorrentFileEntry File, long OffsetInFile, long Length);

/// <summary>
/// What a <c>.torrent</c> says.
/// </summary>
/// <param name="InfoHash">Forty hex characters, upper case: SHA-1 over the raw info bytes.</param>
/// <param name="Name">The file's name, or the folder a multi-file torrent goes in.</param>
/// <param name="PieceLength">How long every piece but the last is.</param>
/// <param name="Pieces">The SHA-1 of each piece, twenty bytes each, in order.</param>
/// <param name="Files">Every file, in the order the torrent lists them.</param>
/// <param name="TotalLength">Every file's length added up.</param>
/// <param name="Trackers">Every tracker it names, announce and announce-list together.</param>
/// <param name="Private">
/// BEP 27. A private torrent never touches the DHT, peer exchange or local
/// discovery, and announces only to its own trackers.
/// </param>
public sealed record TorrentMetadata(
    string InfoHash,
    string Name,
    long PieceLength,
    IReadOnlyList<byte[]> Pieces,
    IReadOnlyList<TorrentFileEntry> Files,
    long TotalLength,
    IReadOnlyList<string> Trackers,
    bool Private)
{
    /// <summary>How many pieces the torrent has.</summary>
    public int PieceCount => Pieces.Count;

    /// <summary>
    /// How long one piece is.
    /// </summary>
    /// <remarks>
    /// The last one is short unless the total divides exactly, and a client
    /// that assumed otherwise asks a peer for bytes that do not exist — which
    /// is a request every peer answers by disconnecting.
    /// </remarks>
    public long LengthOfPiece(int index)
    {
        long start = (long)index * PieceLength;

        return Math.Min(PieceLength, TotalLength - start);
    }

    /// <summary>
    /// Which files a range of the stream falls in, and where in each.
    /// </summary>
    /// <remarks>
    /// A piece pays no attention to where one file ends and the next begins:
    /// the first piece of an ordinary multi-file torrent covers the end of a
    /// thumbnail and the start of the thing you actually wanted, and a client
    /// that wrote it to one file would corrupt both.
    /// </remarks>
    /// <summary>
    /// Where this file sits under the folder a torrent was downloaded into.
    /// </summary>
    /// <remarks>
    /// A torrent of one file puts it straight in the folder; a torrent of
    /// several puts them in a folder of its own name, and the paths inside the
    /// metadata are relative to that folder rather than to the download folder.
    /// <c>TorrentDisk</c> has always known this and nothing else did, so
    /// staging looked for the episode straight in the download folder while the
    /// file was in the torrent's own folder inside it. Every scene release
    /// failed that way: they ship with a Sample folder beside the episode and
    /// are therefore never single-file torrents.
    /// </remarks>
    public string PathUnderFolder(TorrentFileEntry file)
    {
        return Files.Count > 1 ? System.IO.Path.Combine(Name, file.Path) : file.Path;
    }

    /// <summary>
    /// Which pieces hold any part of these files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mask a session downloads by. The owner's rule is that only video
    /// files are downloaded, and a torrent addresses bytes rather than files —
    /// so "only these files" has to become "only these pieces" before the
    /// picker can act on it.
    /// </para>
    /// <para>
    /// A piece that straddles a wanted file and an unwanted one is wanted, and
    /// there is no finer answer available: a piece is the smallest thing a
    /// swarm hands over and its hash covers all of it. It costs at most one
    /// piece at each end of each wanted file, and the fragment of the neighbour
    /// that comes with it is never staged and goes with the download folder.
    /// </para>
    /// </remarks>
    public Bitfield PiecesOf(IEnumerable<TorrentFileEntry> files)
    {
        Bitfield wanted = new(PieceCount);

        foreach (TorrentFileEntry file in files)
        {
            if (file.Length <= 0)
            {
                // A zero-length file sits at an offset without occupying it.
                // Asking for the piece it points at would fetch a neighbour
                // nobody wanted.
                continue;
            }

            int first = (int)(file.Offset / PieceLength);
            int last = (int)((file.Offset + file.Length - 1) / PieceLength);

            for (int piece = first; piece <= last && piece < PieceCount; piece++)
            {
                wanted.Set(piece);
            }
        }

        return wanted;
    }

    public IEnumerable<TorrentSlice> Slice(long offset, long length)
    {
        foreach (TorrentFileEntry file in Files)
        {
            if (length <= 0)
            {
                yield break;
            }

            long end = file.Offset + file.Length;

            if (offset >= end)
            {
                continue;
            }

            long within = offset - file.Offset;
            long take = Math.Min(length, file.Length - within);

            yield return new(file, within, take);

            offset += take;
            length -= take;
        }
    }

    /// <summary>Reads a <c>.torrent</c>.</summary>
    /// <exception cref="BencodeFormatException">The bytes are not bencode.</exception>
    /// <exception cref="TorrentFormatException">They are bencode, and not a torrent.</exception>
    public static TorrentMetadata Read(ReadOnlySpan<byte> torrent)
    {
        BencodeDocument document = Bencode.Read(torrent);

        if (document.Root is not BencodeDictionary root
            || root["info"] is not BencodeDictionary info
            || document.InfoStart is not int start
            || document.InfoLength is not int length)
        {
            throw new TorrentFormatException("There is no info dictionary in it.");
        }

        return Of(info, torrent.Slice(start, length), TrackersOf(root));
    }

    /// <summary>
    /// Reads the info dictionary on its own, as it arrives from a peer.
    /// </summary>
    /// <remarks>
    /// A magnet's metadata is the info dictionary and nothing else — no
    /// <c>announce</c>, no <c>announce-list</c>, no creation date. The trackers
    /// come from the magnet and from the owner's own list, which is why they
    /// are a parameter here and were read out of the file in <see cref="Read"/>.
    /// </remarks>
    /// <exception cref="BencodeFormatException">The bytes are not bencode.</exception>
    /// <exception cref="TorrentFormatException">They are bencode, and not an info dictionary.</exception>
    public static TorrentMetadata FromInfo(ReadOnlySpan<byte> info, IReadOnlyList<string> trackers)
    {
        if (Bencode.Read(info).Root is not BencodeDictionary dictionary)
        {
            throw new TorrentFormatException("The metadata is not an info dictionary.");
        }

        return Of(dictionary, info, trackers);
    }

    /// <summary>One torrent, out of its info dictionary and the raw bytes of it.</summary>
    private static TorrentMetadata Of(
        BencodeDictionary info,
        ReadOnlySpan<byte> raw,
        IReadOnlyList<string> trackers)
    {
        // Over the bytes as they arrived. This reader keeps every entry it did
        // not recognise and writes them back in the order they were read, so a
        // re-encode of a real torrent comes out byte for byte the same — which
        // BencodeTests asserts, and which is the only reason the two are the
        // same thing today. A file whose keys are out of order would not be:
        // it would hash one way through a re-encode and another through what
        // arrived, and what arrived is the one every peer checks.
        string hash = Convert.ToHexString(SHA1.HashData(raw));

        string name = info.Text("name")
            ?? throw new TorrentFormatException("The info dictionary has no name.");

        long pieceLength = info.Number("piece length")
            ?? throw new TorrentFormatException("The info dictionary has no piece length.");

        byte[] pieces = info.Bytes("pieces")
            ?? throw new TorrentFormatException("The info dictionary has no piece hashes.");

        if (pieces.Length % 20 != 0)
        {
            throw new TorrentFormatException($"There are {pieces.Length} bytes of piece hashes, which is not a whole number of them.");
        }

        List<byte[]> hashes = [];

        for (int at = 0; at < pieces.Length; at += 20)
        {
            hashes.Add(pieces[at..(at + 20)]);
        }

        return new(
            hash,
            name,
            pieceLength,
            hashes,
            FilesOf(info, name, out long total),
            total,
            trackers,
            info.Number("private") == 1);
    }

    /// <summary>
    /// Every file, with where each starts in the stream.
    /// </summary>
    /// <remarks>
    /// A single-file torrent has a length and no file list, and its one file is
    /// the torrent's own name. A multi-file torrent has the list and the name
    /// is the folder they go in.
    /// </remarks>
    private static IReadOnlyList<TorrentFileEntry> FilesOf(BencodeDictionary info, string name, out long total)
    {
        List<TorrentFileEntry> files = [];
        long at = 0;

        if (info["files"] is BencodeList listed)
        {
            foreach (BencodeValue item in listed.Items)
            {
                if (item is not BencodeDictionary file
                    || file.Number("length") is not long length
                    || file["path"] is not BencodeList path)
                {
                    throw new TorrentFormatException("A file in the list has no length or no path.");
                }

                files.Add(new(
                    string.Join(
                        System.IO.Path.DirectorySeparatorChar,
                        path.Items.OfType<BencodeBytes>().Select(part => part.Text)),
                    length,
                    at));

                at += length;
            }
        }
        else
        {
            long length = info.Number("length")
                ?? throw new TorrentFormatException("The info dictionary has neither a length nor a file list.");

            files.Add(new(name, length, 0));
            at = length;
        }

        total = at;

        return files;
    }

    /// <summary>
    /// Every tracker, the announce and the announce-list together.
    /// </summary>
    /// <remarks>
    /// In the order they were given and without duplicates: the announce is
    /// usually the first entry of the list as well, and announcing to it twice
    /// is a tracker seeing two peers where there is one.
    /// </remarks>
    private static IReadOnlyList<string> TrackersOf(BencodeDictionary root)
    {
        List<string> trackers = [];

        void Add(string? tracker)
        {
            if (tracker is not null
                && tracker.Length > 0
                && !trackers.Contains(tracker, StringComparer.OrdinalIgnoreCase))
            {
                trackers.Add(tracker);
            }
        }

        Add(root.Text("announce"));

        if (root["announce-list"] is BencodeList tiers)
        {
            foreach (BencodeValue tier in tiers.Items)
            {
                foreach (BencodeBytes tracker in (tier as BencodeList)?.Items.OfType<BencodeBytes>() ?? [])
                {
                    Add(tracker.Text);
                }
            }
        }

        return trackers;
    }
}

/// <summary>Bencode that is not a torrent.</summary>
/// <remarks>
/// Its own type rather than the bencode one: "this is not a torrent" and "these
/// are not bencode" are different sentences, and only one of them means the
/// download was truncated.
/// </remarks>
public sealed class TorrentFormatException(string message) : Exception(message);

/// <summary>
/// A magnet link.
/// </summary>
/// <param name="InfoHash">Forty hex characters, upper case, whichever way it was written.</param>
/// <param name="DisplayName">What it calls itself, or null. For people; the protocol runs on the hash.</param>
/// <param name="Trackers">Every <c>tr</c>, decoded.</param>
public sealed record Magnet(string InfoHash, string? DisplayName, IReadOnlyList<string> Trackers)
{
    /// <summary>
    /// Reads a magnet, or answers null when it is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than a magnet with an empty hash: a torrent handed to the
    /// client under forty characters of something else never finds a peer and
    /// never says why.
    /// </remarks>
    public static Magnet? Parse(string? text)
    {
        if (text is null || !text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? hash = null;
        string? name = null;
        List<string> trackers = [];

        foreach (string pair in text["magnet:?".Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0)
            {
                continue;
            }

            string key = pair[..equals];
            string value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            switch (key.ToLowerInvariant())
            {
                case "xt" when value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase):
                    hash ??= HashOf(value["urn:btih:".Length..]);
                    break;

                case "dn":
                    name ??= value.Length > 0 ? value : null;
                    break;

                case "tr" when Announceable(value) && !trackers.Contains(value, StringComparer.OrdinalIgnoreCase):
                    trackers.Add(value);
                    break;

                default:
                    break;
            }
        }

        return hash is null ? null : new(hash, name, trackers);
    }

    /// <summary>Whether a <c>tr</c> is an address this client could announce to.</summary>
    /// <remarks>
    /// A magnet arrives however the owner pasted it, and one can arrive cut in
    /// half. On 31 August 2026 a hand-added magnet was stored at exactly a
    /// thousand and twenty-four characters, ending mid-parameter, and the last
    /// tracker on it read <c>udp://tracke</c>. That is not a host: it was asked
    /// on every announce for as long as the torrent ran, and it sat in the
    /// owner's log beside the real refusals as though it were one.
    ///
    /// A scheme this client speaks and a host with a dot or a colon in it — a
    /// name, an address, or an IPv6 literal. Everything a real tracker has, and
    /// nothing half a word has.
    /// </remarks>
    private static bool Announceable(string tracker)
    {
        return Uri.TryCreate(tracker, UriKind.Absolute, out Uri? address)
               && address.Scheme is "udp" or "http" or "https"
               && (address.Host.Contains('.', StringComparison.Ordinal)
                   || address.Host.Contains(':', StringComparison.Ordinal));
    }

    /// <summary>
    /// Forty hex characters, from either spelling.
    /// </summary>
    /// <remarks>
    /// BEP 9 allows base32 as well, and plenty of sites use it. Both have to
    /// come out the same or one torrent is taken twice under two names.
    /// </remarks>
    private static string? HashOf(string written)
    {
        if (written.Length == 40)
        {
            return written.All(Uri.IsHexDigit) ? written.ToUpperInvariant() : null;
        }

        if (written.Length != 32)
        {
            return null;
        }

        byte[] bytes = new byte[20];
        int bits = 0;
        int buffer = 0;
        int at = 0;

        foreach (char character in written.ToUpperInvariant())
        {
            int value = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1,
            };

            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes[at++] = (byte)((buffer >> bits) & 0xFF);
            }
        }

        return at == 20 ? Convert.ToHexString(bytes) : null;
    }

    /// <summary>The magnet this torrent would be offered under.</summary>
    public override string ToString()
    {
        StringBuilder text = new($"magnet:?xt=urn:btih:{InfoHash}");

        if (DisplayName is not null)
        {
            text.Append("&dn=").Append(Uri.EscapeDataString(DisplayName));
        }

        foreach (string tracker in Trackers)
        {
            text.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        return text.ToString();
    }
}
