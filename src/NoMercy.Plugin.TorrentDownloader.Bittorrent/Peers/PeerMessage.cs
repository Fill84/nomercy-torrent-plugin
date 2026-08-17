using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>The messages of BEP 3, and BEP 10's extended one.</summary>
public enum PeerMessageId
{
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,

    /// <summary>The DHT port a peer is listening on, when it has one.</summary>
    Port = 9,

    /// <summary>BEP 10. What carries metadata and peer exchange.</summary>
    Extended = 20,
}

/// <summary>
/// One message on the wire.
/// </summary>
/// <remarks>
/// A keep-alive is a message of no length and no id at all, which is why the id
/// is nullable. A reader that treated it as a malformed message would drop
/// every peer that went quiet for two minutes — which is every peer.
/// </remarks>
/// <param name="Id">Which message, or null for a keep-alive.</param>
/// <param name="Payload">Everything after the id.</param>
public sealed record PeerMessage(PeerMessageId? Id, byte[] Payload)
{
    /// <summary>The keep-alive: four bytes of nought and nothing else.</summary>
    public static PeerMessage KeepAlive { get; } = new(null, []);

    /// <summary>How long a block is: sixteen kibibytes, as every client uses.</summary>
    public const int BlockLength = 16 * 1024;

    /// <summary>The bytes this message goes out as.</summary>
    public byte[] Write()
    {
        if (Id is null)
        {
            return [0, 0, 0, 0];
        }

        byte[] bytes = new byte[4 + 1 + Payload.Length];

        BinaryPrimitives.WriteInt32BigEndian(bytes, Payload.Length + 1);
        bytes[4] = (byte)Id;
        Payload.CopyTo(bytes.AsSpan(5));

        return bytes;
    }

    /// <summary>A message with no payload: choke, unchoke, interested, not interested.</summary>
    public static PeerMessage Of(PeerMessageId id)
    {
        return new(id, []);
    }

    /// <summary>I have this piece.</summary>
    public static PeerMessage Have(int piece)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, piece);

        return new(PeerMessageId.Have, payload);
    }

    /// <summary>Send me this block.</summary>
    public static PeerMessage Request(int piece, int offset, int length)
    {
        return new(PeerMessageId.Request, Triple(piece, offset, length));
    }

    /// <summary>Never mind that block.</summary>
    public static PeerMessage Cancel(int piece, int offset, int length)
    {
        return new(PeerMessageId.Cancel, Triple(piece, offset, length));
    }

    /// <summary>Here is that block.</summary>
    public static PeerMessage Block(int piece, int offset, ReadOnlySpan<byte> data)
    {
        byte[] payload = new byte[8 + data.Length];

        BinaryPrimitives.WriteInt32BigEndian(payload, piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        data.CopyTo(payload.AsSpan(8));

        return new(PeerMessageId.Piece, payload);
    }

    /// <summary>The piece, offset and length a request or a cancel names.</summary>
    /// <exception cref="PeerProtocolException">It is not one of those, or it is too short to be.</exception>
    public (int Piece, int Offset, int Length) AsRequest()
    {
        if (Id is not (PeerMessageId.Request or PeerMessageId.Cancel) || Payload.Length < 12)
        {
            throw new PeerProtocolException($"A {Id} of {Payload.Length} bytes is not a request.");
        }

        return (
            BinaryPrimitives.ReadInt32BigEndian(Payload),
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4)),
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8)));
    }

    /// <summary>The piece and offset a block carries, and the block itself.</summary>
    /// <exception cref="PeerProtocolException">It is not a block, or it is too short to be.</exception>
    public (int Piece, int Offset, byte[] Data) AsBlock()
    {
        if (Id != PeerMessageId.Piece || Payload.Length < 8)
        {
            throw new PeerProtocolException($"A {Id} of {Payload.Length} bytes is not a block.");
        }

        return (
            BinaryPrimitives.ReadInt32BigEndian(Payload),
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4)),
            Payload[8..]);
    }

    /// <summary>Which piece a <c>have</c> names.</summary>
    public int AsHave()
    {
        return Id == PeerMessageId.Have && Payload.Length >= 4
            ? BinaryPrimitives.ReadInt32BigEndian(Payload)
            : throw new PeerProtocolException($"A {Id} of {Payload.Length} bytes is not a have.");
    }

    private static byte[] Triple(int piece, int offset, int length)
    {
        byte[] payload = new byte[12];

        BinaryPrimitives.WriteInt32BigEndian(payload, piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);

        return payload;
    }
}

/// <summary>A peer that said something a client cannot use.</summary>
public sealed class PeerProtocolException(string message) : Exception(message);

/// <summary>
/// Messages out of a stream of bytes that arrives in whatever sizes it likes.
/// </summary>
/// <remarks>
/// TCP is a stream and not a sequence of messages: a bitfield of two thousand
/// bytes arrives in four reads, and two small messages arrive in one. A reader
/// that assumed one read was one message would work on a fast local network and
/// fail against every real peer.
/// </remarks>
public sealed class PeerMessageReader
{
    private readonly List<byte> _buffer = [];

    /// <summary>
    /// The largest message this client will accept.
    /// </summary>
    /// <remarks>
    /// A block is sixteen kibibytes and a bitfield is one bit per piece, so a
    /// megabyte is far more than anything legitimate. A peer claiming a
    /// two-gigabyte message is a peer trying to have this process allocate two
    /// gigabytes.
    /// </remarks>
    public const int LongestMessage = 1024 * 1024;

    /// <summary>Whether the handshake has been read off the front yet.</summary>
    public bool Introduced { get; private set; }

    /// <summary>Adds bytes as they arrive.</summary>
    public void Add(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes);
    }

    /// <summary>
    /// The peer's handshake, once all sixty-eight bytes of it have arrived.
    /// </summary>
    public PeerHandshake? Handshake()
    {
        if (Introduced || _buffer.Count < Bittorrent.Handshake.Length)
        {
            return null;
        }

        PeerHandshake? handshake = Bittorrent.Handshake.Read(CollectionsMarshal.AsSpan(_buffer));

        if (handshake is null)
        {
            throw new PeerProtocolException("That is not a handshake.");
        }

        _buffer.RemoveRange(0, Bittorrent.Handshake.Length);
        Introduced = true;

        return handshake;
    }

    /// <summary>
    /// The next whole message, or null when one has not all arrived.
    /// </summary>
    public PeerMessage? Next()
    {
        if (_buffer.Count < 4)
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(CollectionsMarshal.AsSpan(_buffer));

        if (length < 0 || length > LongestMessage)
        {
            throw new PeerProtocolException($"A peer claimed a message of {length} bytes.");
        }

        if (_buffer.Count < 4 + length)
        {
            return null;
        }

        if (length == 0)
        {
            // A keep-alive. Not a malformed message, and dropping a peer for
            // one would drop every peer that went quiet for two minutes.
            _buffer.RemoveRange(0, 4);

            return PeerMessage.KeepAlive;
        }

        PeerMessage message = new(
            (PeerMessageId)_buffer[4],
            [.. CollectionsMarshal.AsSpan(_buffer).Slice(5, length - 1)]);

        _buffer.RemoveRange(0, 4 + length);

        return message;
    }
}
