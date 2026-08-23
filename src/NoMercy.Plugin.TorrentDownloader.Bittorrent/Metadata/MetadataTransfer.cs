using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>BEP 9's three messages, which share one id and are told apart by <c>msg_type</c>.</summary>
public enum MetadataMessage
{
    /// <summary>Send me this piece.</summary>
    Request = 0,

    /// <summary>Here is this piece, with the bytes after the dictionary.</summary>
    Data = 1,

    /// <summary>I will not, or I have not got it.</summary>
    Reject = 2,
}

/// <summary>One <c>ut_metadata</c> message, read.</summary>
/// <param name="Kind">Which of the three.</param>
/// <param name="Piece">Which piece it is about.</param>
/// <param name="TotalSize">How big the whole info dictionary is, on a piece.</param>
/// <param name="Data">The bytes of that piece, on a piece, and empty otherwise.</param>
public sealed record MetadataPart(MetadataMessage Kind, int Piece, int TotalSize, byte[] Data);

/// <summary>
/// BEP 9: the info dictionary, in sixteen-kibibyte pieces, from peers.
/// </summary>
/// <remarks>
/// A magnet is a hash and a name. Everything else a download needs — the file
/// list, the piece length, the piece hashes — is inside the info dictionary,
/// and the only place to get it is a peer that already has it.
/// </remarks>
public static class MetadataTransfer
{
    /// <summary>
    /// How much of the metadata one message carries.
    /// </summary>
    /// <remarks>
    /// Sixteen kibibytes, the same as a block of the download, and every piece
    /// but the last is exactly this. It is not negotiated: a client that used
    /// its own number would ask for piece four and be sent somebody else's
    /// idea of where piece four starts.
    /// </remarks>
    public const int PieceLength = 16 * 1024;

    /// <summary>How many pieces an info dictionary of this size is.</summary>
    public static int Pieces(int size)
    {
        return (size + PieceLength - 1) / PieceLength;
    }

    /// <summary>Asks a peer for one piece, under the id that peer asked for.</summary>
    public static PeerMessage Request(int theirId, int piece)
    {
        return Message(theirId, MetadataMessage.Request, piece);
    }

    /// <summary>Refuses one piece.</summary>
    public static PeerMessage Reject(int theirId, int piece)
    {
        return Message(theirId, MetadataMessage.Reject, piece);
    }

    /// <summary>Sends one piece, with its bytes after the dictionary.</summary>
    public static PeerMessage Data(int theirId, int piece, int totalSize, ReadOnlySpan<byte> bytes)
    {
        return Extensions.Extended(
            theirId,
            new BencodeDictionary(
            [
                new("msg_type"u8.ToArray(), new BencodeInteger((int)MetadataMessage.Data)),
                new("piece"u8.ToArray(), new BencodeInteger(piece)),
                new("total_size"u8.ToArray(), new BencodeInteger(totalSize)),
            ]),
            bytes);
    }

    /// <summary>Reads one, whichever of the three it is.</summary>
    /// <exception cref="PeerProtocolException">It is not one of the three.</exception>
    public static MetadataPart Read(PeerMessage message)
    {
        if (message.Id != PeerMessageId.Extended || message.Payload.Length < 1)
        {
            throw new PeerProtocolException("That is not an extended message.");
        }

        // Where the dictionary ends is where the piece begins. Bencode has
        // nowhere to put sixteen kibibytes of binary, so BEP 9 puts it after.
        BencodePrefix prefix = Bencode.ReadPrefix(message.Payload.AsSpan(1));

        if (prefix.Root is not BencodeDictionary body || body.Number("msg_type") is not long kind)
        {
            throw new PeerProtocolException("A ut_metadata message says what type it is, and this one does not.");
        }

        if (!Enum.IsDefined((MetadataMessage)kind))
        {
            // A newer BEP, or a peer with a fault. Guessing would put bytes
            // nobody vouched for into the info dictionary.
            throw new PeerProtocolException($"ut_metadata message type {kind} is not one this client knows.");
        }

        int piece = (int)(body.Number("piece")
                          ?? throw new PeerProtocolException("A ut_metadata message names a piece, and this one does not."));

        return new(
            (MetadataMessage)kind,
            piece,
            (int)(body.Number("total_size") ?? 0),
            message.Payload[(1 + prefix.Length)..]);
    }

    private static PeerMessage Message(int theirId, MetadataMessage kind, int piece)
    {
        return Extensions.Extended(
            theirId,
            new BencodeDictionary(
            [
                new("msg_type"u8.ToArray(), new BencodeInteger((int)kind)),
                new("piece"u8.ToArray(), new BencodeInteger(piece)),
            ]));
    }
}

/// <summary>
/// One torrent's metadata being fetched from whoever will send it.
/// </summary>
/// <remarks>
/// <para>
/// The pieces are put together and the whole thing is hashed once, against the
/// hash out of the magnet. There is no per-piece hash the way the download has
/// one, so a fetch that fails says nothing about which peer lied: everybody who
/// contributed is dropped and it starts again from nothing.
/// </para>
/// <para>
/// It carries its own clock reading. A magnet nobody in the swarm will serve
/// the metadata for otherwise sits there saying "fetching metadata" for as long
/// as the server runs, which is what 0.3.4 did.
/// </para>
/// </remarks>
public sealed class MetadataFetch
{
    private readonly byte[] _infoHash;
    private readonly byte[] _bytes;
    private readonly HashSet<int> _have = [];
    private readonly HashSet<string> _contributors = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _started;

    public MetadataFetch(byte[] infoHash, int size, DateTimeOffset started = default)
    {
        if (size <= 0)
        {
            throw new PeerProtocolException($"A peer said the metadata is {size} bytes, which is not a size.");
        }

        _infoHash = infoHash;
        _bytes = new byte[size];
        _started = started;
    }

    /// <summary>How many pieces it is.</summary>
    public int Pieces => MetadataTransfer.Pieces(_bytes.Length);

    /// <summary>Whether every piece has arrived.</summary>
    public bool Complete => _have.Count == Pieces;

    /// <summary>Everybody who sent part of it.</summary>
    public IReadOnlyCollection<string> Contributors => _contributors;

    /// <summary>Whether it is complete and hashes to the hash the magnet named.</summary>
    public bool Verified => Complete && SHA1.HashData(_bytes).AsSpan().SequenceEqual(_infoHash);

    /// <summary>The pieces still to ask somebody for.</summary>
    public IEnumerable<int> Wanted()
    {
        for (int piece = 0; piece < Pieces; piece++)
        {
            if (!_have.Contains(piece))
            {
                yield return piece;
            }
        }
    }

    /// <summary>
    /// Takes one piece from one peer.
    /// </summary>
    /// <remarks>
    /// The length has to be exactly what that piece is: every piece but the
    /// last is sixteen kibibytes and the last is the remainder. A peer sending
    /// a different length has a different torrent in mind, and the bytes would
    /// be wrong with nothing to say why.
    /// </remarks>
    /// <exception cref="PeerProtocolException">The piece or its length is not one of this fetch's.</exception>
    public void Add(int piece, ReadOnlySpan<byte> data, string peer)
    {
        if (piece < 0 || piece >= Pieces)
        {
            throw new PeerProtocolException($"Piece {piece} is not part of metadata of {Pieces} pieces.");
        }

        int at = piece * MetadataTransfer.PieceLength;
        int length = Math.Min(MetadataTransfer.PieceLength, _bytes.Length - at);

        if (data.Length != length)
        {
            throw new PeerProtocolException(
                $"Metadata piece {piece} is {length} bytes, and this peer sent {data.Length}.");
        }

        data.CopyTo(_bytes.AsSpan(at));
        _have.Add(piece);
        _contributors.Add(peer);
    }

    /// <summary>
    /// Throws away metadata that did not verify, and answers who is to blame.
    /// </summary>
    /// <remarks>
    /// Everybody who contributed. One of them ruined it and there is no per-piece
    /// hash to say which, so the whole fetch and every peer in it go — keeping
    /// any of it would have the next attempt reassemble the same wrong bytes.
    /// </remarks>
    public IReadOnlyCollection<string> Discard()
    {
        string[] blamed = [.. _contributors];

        Array.Clear(_bytes);
        _have.Clear();
        _contributors.Clear();

        return blamed;
    }

    /// <summary>
    /// Whether it has been fetching for longer than it is allowed.
    /// </summary>
    /// <remarks>
    /// Only while it is still fetching. Metadata that arrived inside the limit
    /// is not failed by a tick that happens afterwards.
    /// </remarks>
    public bool Expired(DateTimeOffset now, TimeSpan limit)
    {
        return !Verified && now - _started >= limit;
    }

    /// <summary>
    /// The info dictionary itself, once it is whole and hashes to the hash the
    /// magnet named.
    /// </summary>
    /// <remarks>
    /// Kept so it can be written down: a client that has the metadata should
    /// never have to ask a swarm for it again, and a swarm that has gone quiet
    /// cannot answer.
    /// </remarks>
    public ReadOnlySpan<byte> Info => Verified ? _bytes : [];

    /// <summary>
    /// The torrent, once it is verified.
    /// </summary>
    /// <param name="trackers">
    /// From the magnet and the owner's list. The info dictionary has none of
    /// its own, and a client that took its word for it would announce nowhere.
    /// </param>
    /// <exception cref="TorrentFormatException">It is not complete, or it did not verify.</exception>
    public TorrentMetadata Read(IReadOnlyList<string> trackers)
    {
        if (!Verified)
        {
            throw new TorrentFormatException(
                Complete
                    ? "The metadata does not hash to the hash the magnet named."
                    : $"{_have.Count} of {Pieces} metadata pieces have arrived.");
        }

        return TorrentMetadata.FromInfo(_bytes, trackers);
    }
}
