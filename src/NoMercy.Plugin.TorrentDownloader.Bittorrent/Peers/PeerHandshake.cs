using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// What a peer said when it introduced itself.
/// </summary>
/// <param name="InfoHash">Which torrent it thinks this is.</param>
/// <param name="PeerId">Twenty bytes naming its client.</param>
/// <param name="Extensions">Whether it speaks BEP 10, which is how metadata and peer exchange arrive.</param>
/// <param name="Dht">Whether it has a DHT node worth asking about.</param>
public sealed record PeerHandshake(byte[] InfoHash, byte[] PeerId, bool Extensions, bool Dht)
{
    /// <summary>What the client calls itself, when the bytes are readable.</summary>
    /// <remarks>
    /// For the page and the journal only. A peer id is twenty arbitrary bytes
    /// and nothing is decided on what they spell.
    /// </remarks>
    public string Client => Encoding.ASCII.GetString(PeerId).Trim('\0');
}

/// <summary>
/// The sixty-eight bytes two peers begin with.
/// </summary>
/// <remarks>
/// <c>19</c>, <c>BitTorrent protocol</c>, eight reserved bytes, the info hash,
/// the peer id. Exactly that, in that order: a peer reads the reserved bytes to
/// decide what it will offer, and gets the length wrong at its own end if ours
/// is wrong.
/// </remarks>
public static class Handshake
{
    /// <summary>The name every handshake carries.</summary>
    public static ReadOnlySpan<byte> Protocol => "BitTorrent protocol"u8;

    /// <summary>How long a handshake is: one, nineteen, eight, twenty, twenty.</summary>
    public const int Length = 68;

    /// <summary>
    /// The extension-protocol bit, on the sixth reserved byte.
    /// </summary>
    /// <remarks>
    /// BEP 10. Without it a peer will not offer <c>ut_metadata</c>, and a
    /// magnet then has no way of ever becoming a torrent.
    /// </remarks>
    public const int ExtensionByte = 5;

    public const byte ExtensionBit = 0x10;

    /// <summary>The DHT bit, on the eighth reserved byte.</summary>
    public const int DhtByte = 7;

    public const byte DhtBit = 0x01;

    /// <summary>Our own handshake for this torrent.</summary>
    public static byte[] Write(byte[] infoHash, byte[] peerId)
    {
        if (infoHash.Length != 20 || peerId.Length != 20)
        {
            throw new ArgumentException("An info hash and a peer id are twenty bytes each.");
        }

        byte[] bytes = new byte[Length];

        bytes[0] = (byte)Protocol.Length;
        Protocol.CopyTo(bytes.AsSpan(1));

        // The two bits this client can honour. Advertising one it cannot serve
        // is worse than not advertising it: the peer asks, and nothing answers.
        bytes[20 + ExtensionByte] = ExtensionBit;
        bytes[20 + DhtByte] = DhtBit;

        infoHash.CopyTo(bytes.AsSpan(28));
        peerId.CopyTo(bytes.AsSpan(48));

        return bytes;
    }

    /// <summary>
    /// Reads a peer's handshake, or answers null when it is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: a stranger connecting to this port and
    /// sending rubbish is an ordinary event on the internet, not a fault to
    /// report.
    /// </remarks>
    public static PeerHandshake? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length
            || bytes[0] != Protocol.Length
            || !bytes.Slice(1, Protocol.Length).SequenceEqual(Protocol))
        {
            return null;
        }

        return new(
            bytes.Slice(28, 20).ToArray(),
            bytes.Slice(48, 20).ToArray(),
            (bytes[20 + ExtensionByte] & ExtensionBit) != 0,
            (bytes[20 + DhtByte] & DhtBit) != 0);
    }

    /// <summary>
    /// Whether a peer that has introduced itself is worth talking to.
    /// </summary>
    /// <remarks>
    /// The info hash has to be the one asked for. A peer that answers with
    /// another torrent's hash is not confused, it is another torrent — and
    /// whatever it sends after that would be written into the wrong file.
    /// </remarks>
    public static bool IsFor(PeerHandshake handshake, byte[] infoHash)
    {
        return handshake.InfoHash.AsSpan().SequenceEqual(infoHash);
    }
}
