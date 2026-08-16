using System.Text;

// The types live in the assembly's own namespace rather than a Bencode one:
// a class and the namespace above it cannot share a name, and the class is
// what every caller writes.
namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// One bencoded value: an integer, a byte string, a list or a dictionary.
/// </summary>
/// <remarks>
/// Bencode is <em>byte</em>-oriented, and this model keeps it that way. A name
/// can be in any encoding or none, and the piece hashes are raw SHA-1s — over
/// four hundred kilobytes of them in an ordinary torrent — which are not valid
/// text in any encoding at all. A reader that decoded byte strings would
/// replace what it could not read and every piece would then fail its check.
/// </remarks>
public abstract record BencodeValue;

/// <summary>A bencoded integer: <c>i42e</c>.</summary>
public sealed record BencodeInteger(long Value) : BencodeValue;

/// <summary>A bencoded byte string: <c>4:spam</c>.</summary>
public sealed record BencodeBytes(byte[] Value) : BencodeValue
{
    /// <summary>
    /// The bytes as text, for the fields that really are text.
    /// </summary>
    /// <remarks>
    /// Lossy by construction and never used for anything that is hashed,
    /// compared or written back — those all use <see cref="Value"/>.
    /// </remarks>
    public string Text => Encoding.UTF8.GetString(Value);
}

/// <summary>A bencoded list: <c>l…e</c>.</summary>
public sealed record BencodeList(IReadOnlyList<BencodeValue> Items) : BencodeValue;

/// <summary>One key and its value, in the order the file had them.</summary>
public sealed record BencodeEntry(byte[] Key, BencodeValue Value);

/// <summary>
/// A bencoded dictionary: <c>d…e</c>.
/// </summary>
/// <remarks>
/// The entries keep the order they arrived in rather than being sorted or
/// hashed. BEP 3 requires keys in sorted order and every real torrent has them
/// that way, but writing the file back byte for byte means writing what was
/// read — and the info hash is SHA-1 over those bytes exactly.
/// </remarks>
public sealed record BencodeDictionary(IReadOnlyList<BencodeEntry> Entries) : BencodeValue
{
    /// <summary>The value under this key, or null when the dictionary has none.</summary>
    public BencodeValue? this[string key]
    {
        get
        {
            byte[] wanted = Encoding.UTF8.GetBytes(key);

            foreach (BencodeEntry entry in Entries)
            {
                if (entry.Key.AsSpan().SequenceEqual(wanted))
                {
                    return entry.Value;
                }
            }

            return null;
        }
    }

    /// <summary>The bytes under this key, or null.</summary>
    public byte[]? Bytes(string key)
    {
        return this[key] is BencodeBytes bytes ? bytes.Value : null;
    }

    /// <summary>The text under this key, or null.</summary>
    public string? Text(string key)
    {
        return this[key] is BencodeBytes bytes ? bytes.Text : null;
    }

    /// <summary>The number under this key, or null.</summary>
    public long? Number(string key)
    {
        return this[key] is BencodeInteger number ? number.Value : null;
    }
}
