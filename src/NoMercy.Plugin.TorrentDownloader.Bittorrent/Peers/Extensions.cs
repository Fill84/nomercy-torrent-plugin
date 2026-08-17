using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// What a peer said it can do, in BEP 10's extension handshake.
/// </summary>
/// <param name="Messages">
/// The extensions it speaks and the id it wants each one sent under. The ids
/// are the <em>peer's</em> choice and differ per peer, which is the whole point
/// of the handshake: sending <c>ut_metadata</c> under our own number reaches
/// whatever that peer happens to call it.
/// </param>
/// <param name="MetadataSize">
/// How many bytes the info dictionary is, when the peer says. Without it there
/// is no way to know how many pieces to ask for.
/// </param>
/// <param name="Client">What the peer calls itself, for the journal.</param>
public sealed record ExtensionHandshake(
    IReadOnlyDictionary<string, int> Messages,
    int? MetadataSize,
    string? Client)
{
    /// <summary>The id this peer wants <c>ut_metadata</c> sent under, or null.</summary>
    public int? MetadataId =>
        Messages.TryGetValue(Extensions.Metadata, out int id) && id > 0 ? id : null;
}

/// <summary>
/// BEP 10: the handshake that carries every other extension.
/// </summary>
public static class Extensions
{
    /// <summary>The extension a magnet needs, and the only one this client asks for.</summary>
    public const string Metadata = "ut_metadata";

    /// <summary>Peer exchange, which arrives the same way.</summary>
    public const string PeerExchange = "ut_pex";

    /// <summary>
    /// The id under which the extension handshake itself is sent.
    /// </summary>
    /// <remarks>
    /// Nought, always, in both directions. Every other id is chosen by the
    /// peer receiving the message.
    /// </remarks>
    public const int HandshakeId = 0;

    /// <summary>
    /// The id this client asks peers to send <c>ut_metadata</c> under.
    /// </summary>
    /// <remarks>
    /// Ours to choose and ours alone. A peer will use this number when it
    /// sends metadata to us, and its own number when it expects metadata from
    /// us — the two have nothing to do with each other.
    /// </remarks>
    public const int OurMetadataId = 1;

    /// <summary>Our own handshake: what we speak and what to send it under.</summary>
    public static PeerMessage Handshake(string client, int? metadataSize = null)
    {
        List<BencodeEntry> entries =
        [
            new(
                "m"u8.ToArray(),
                new BencodeDictionary([new(Encoding.ASCII.GetBytes(Metadata), new BencodeInteger(OurMetadataId))])),
            new("v"u8.ToArray(), new BencodeBytes(Encoding.UTF8.GetBytes(client))),
        ];

        if (metadataSize is int size)
        {
            // Only when it is known. Claiming a size we do not have would have
            // a peer ask us for pieces of nothing.
            entries.Add(new("metadata_size"u8.ToArray(), new BencodeInteger(size)));
        }

        return Extended(HandshakeId, new BencodeDictionary(entries));
    }

    /// <summary>Reads a peer's extension handshake.</summary>
    public static ExtensionHandshake Read(PeerMessage message)
    {
        if (message.Id != PeerMessageId.Extended || message.Payload.Length < 1 || message.Payload[0] != HandshakeId)
        {
            throw new PeerProtocolException("That is not an extension handshake.");
        }

        if (Bencode.Read(message.Payload.AsSpan(1)).Root is not BencodeDictionary root)
        {
            throw new PeerProtocolException("An extension handshake is a dictionary, and this one is not.");
        }

        Dictionary<string, int> messages = new(StringComparer.Ordinal);

        if (root["m"] is BencodeDictionary offered)
        {
            foreach (BencodeEntry entry in offered.Entries)
            {
                if (entry.Value is BencodeInteger id)
                {
                    messages[Encoding.UTF8.GetString(entry.Key)] = (int)id.Value;
                }
            }
        }

        return new(messages, (int?)root.Number("metadata_size"), root.Text("v"));
    }

    /// <summary>Wraps a bencoded body in an extended message under one id.</summary>
    public static PeerMessage Extended(int id, BencodeValue body, ReadOnlySpan<byte> trailing = default)
    {
        byte[] bencoded = Bencode.Write(body);
        byte[] payload = new byte[1 + bencoded.Length + trailing.Length];

        payload[0] = (byte)id;
        bencoded.CopyTo(payload.AsSpan(1));
        trailing.CopyTo(payload.AsSpan(1 + bencoded.Length));

        return new(PeerMessageId.Extended, payload);
    }
}
