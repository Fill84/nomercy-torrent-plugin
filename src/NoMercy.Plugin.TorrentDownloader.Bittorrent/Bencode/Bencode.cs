using System.Text;

// The types live in the assembly's own namespace rather than a Bencode one:
// a class and the namespace above it cannot share a name, and the class is
// what every caller writes.
namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>Where the bytes stopped making sense, and how.</summary>
/// <param name="Offset">How far in. The end of the input is a valid offset: that is where a truncated file fails.</param>
/// <param name="Reason">What was wrong, in words.</param>
public sealed record BencodeError(int Offset, string Reason)
{
    public override string ToString()
    {
        return $"{Reason} (at byte {Offset})";
    }
}

/// <summary>
/// Bencode that could not be read.
/// </summary>
/// <remarks>
/// Carries the offset, because "this torrent is malformed" is not something
/// anybody can act on and "byte 200000 is the end of the file" is.
/// </remarks>
public sealed class BencodeFormatException(BencodeError error)
    : Exception(error.ToString())
{
    public BencodeError Error { get; } = error;
}

/// <summary>
/// What was read, and where the <c>info</c> dictionary was while reading it.
/// </summary>
/// <param name="Root">The value the bytes decoded to.</param>
/// <param name="InfoStart">
/// Where the top-level <c>info</c> dictionary began, or null when there was
/// none — a magnet's metadata, a tracker's answer and a peer's message are all
/// bencode with no info in them.
/// </param>
/// <param name="InfoLength">How long it was.</param>
public sealed record BencodeDocument(BencodeValue Root, int? InfoStart, int? InfoLength);

/// <summary>
/// One value read off the front of something longer.
/// </summary>
/// <param name="Root">The value.</param>
/// <param name="Length">How many bytes it took, so whatever follows can be found.</param>
public sealed record BencodePrefix(BencodeValue Root, int Length);

/// <summary>
/// BEP 3's encoding, read and written over bytes.
/// </summary>
/// <remarks>
/// <para>
/// The reader records the byte range of the top-level <c>info</c> dictionary
/// as it goes. The info hash is SHA-1 over <em>those bytes as they arrived</em>:
/// decoding and re-encoding gives a different hash whenever the file had
/// anything this reader did not know about, and every peer then refuses the
/// handshake.
/// </para>
/// <para>
/// Nothing here indexes without checking first. A reader that walks off the end
/// of a truncated download and throws from three frames down tells whoever is
/// watching nothing about which file was wrong or where.
/// </para>
/// </remarks>
public static class Bencode
{
    /// <summary>
    /// Reads one complete value, and nothing after it.
    /// </summary>
    /// <exception cref="BencodeFormatException">The bytes are not one bencoded value.</exception>
    public static BencodeDocument Read(ReadOnlySpan<byte> bytes)
    {
        return TryRead(bytes, out BencodeDocument? document, out BencodeError? error)
            ? document!
            : throw new BencodeFormatException(error!);
    }

    /// <summary>
    /// Reads one value off the front and says how many bytes it took.
    /// </summary>
    /// <remarks>
    /// For BEP 9, where a metadata piece is sixteen kibibytes of raw bytes
    /// following a bencoded dictionary. Bencode has nowhere to put them and the
    /// only way to find where they start is where the dictionary ended, so this
    /// is the one reader that is allowed to leave bytes behind it.
    /// </remarks>
    /// <exception cref="BencodeFormatException">The bytes do not start with one.</exception>
    public static BencodePrefix ReadPrefix(ReadOnlySpan<byte> bytes)
    {
        Cursor cursor = new(bytes);

        if (!ReadValue(ref cursor, depth: 0, top: true, out BencodeValue? root))
        {
            throw new BencodeFormatException(cursor.Error!);
        }

        return new(root!, cursor.At);
    }

    /// <summary>Reads one complete value, or says where it stopped.</summary>
    public static bool TryRead(
        ReadOnlySpan<byte> bytes,
        out BencodeDocument? document,
        out BencodeError? error)
    {
        document = null;
        error = null;

        Cursor cursor = new(bytes);

        if (!ReadValue(ref cursor, depth: 0, top: true, out BencodeValue? root))
        {
            error = cursor.Error;

            return false;
        }

        if (cursor.At != bytes.Length)
        {
            // A torrent with something appended is not a torrent that happens
            // to start with one.
            error = new(cursor.At, "There are bytes after the value.");

            return false;
        }

        document = new(root!, cursor.InfoStart, cursor.InfoLength);

        return true;
    }

    /// <summary>Writes a value back out.</summary>
    /// <remarks>
    /// In the order it was read, so a document that came off a disk goes back
    /// byte for byte.
    /// </remarks>
    public static byte[] Write(BencodeValue value)
    {
        MemoryStream into = new();

        Write(value, into);

        return into.ToArray();
    }

    /// <summary>
    /// How deep a document may nest.
    /// </summary>
    /// <remarks>
    /// A depth nothing legitimate reaches — a torrent is four deep at most —
    /// and a bound that stops a hostile peer sending ten thousand open lists
    /// and taking the stack with it.
    /// </remarks>
    private const int DeepEnough = 64;

    private static void Write(BencodeValue value, Stream into)
    {
        switch (value)
        {
            case BencodeInteger number:
                Ascii(into, $"i{number.Value}e");
                break;

            case BencodeBytes bytes:
                Ascii(into, $"{bytes.Value.Length}:");
                into.Write(bytes.Value);
                break;

            case BencodeList list:
                into.WriteByte((byte)'l');

                foreach (BencodeValue item in list.Items)
                {
                    Write(item, into);
                }

                into.WriteByte((byte)'e');
                break;

            case BencodeDictionary dictionary:
                into.WriteByte((byte)'d');

                foreach (BencodeEntry entry in dictionary.Entries)
                {
                    Ascii(into, $"{entry.Key.Length}:");
                    into.Write(entry.Key);
                    Write(entry.Value, into);
                }

                into.WriteByte((byte)'e');
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a bencoded value.");
        }
    }

    private static void Ascii(Stream into, string text)
    {
        into.Write(Encoding.ASCII.GetBytes(text));
    }

    private static bool ReadValue(ref Cursor cursor, int depth, bool top, out BencodeValue? value)
    {
        value = null;

        if (depth > DeepEnough)
        {
            return cursor.Fail("The value is nested deeper than anything legitimate goes.");
        }

        if (!cursor.More)
        {
            return cursor.Fail("The value ended before it began.");
        }

        return cursor.Peek switch
        {
            (byte)'i' => ReadInteger(ref cursor, out value),
            (byte)'l' => ReadList(ref cursor, depth, out value),
            (byte)'d' => ReadDictionary(ref cursor, depth, top, out value),
            >= (byte)'0' and <= (byte)'9' => ReadBytes(ref cursor, out value),
            _ => cursor.Fail($"'{(char)cursor.Peek}' does not begin a bencoded value."),
        };
    }

    private static bool ReadInteger(ref Cursor cursor, out BencodeValue? value)
    {
        value = null;

        int start = cursor.At;
        cursor.Skip();

        int end = cursor.IndexOf((byte)'e');

        if (end < 0)
        {
            return cursor.FailAt(cursor.Length, "An integer has no end.");
        }

        ReadOnlySpan<byte> digits = cursor.Slice(cursor.At, end - cursor.At);

        if (digits.Length == 0)
        {
            return cursor.Fail("An integer has no digits.");
        }

        // i-0e and leading zeros are refused by BEP 3, and a reader that takes
        // them will disagree with the peer that sent them about what the bytes
        // meant.
        bool negative = digits[0] == (byte)'-';
        ReadOnlySpan<byte> magnitude = negative ? digits[1..] : digits;

        if (magnitude.Length == 0
            || (magnitude.Length > 1 && magnitude[0] == (byte)'0')
            || (negative && magnitude[0] == (byte)'0'))
        {
            return cursor.FailAt(start, "An integer is written with no leading zeros and no negative nought.");
        }

        foreach (byte digit in magnitude)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                return cursor.FailAt(start, "An integer has something in it that is not a digit.");
            }
        }

        if (!long.TryParse(Encoding.ASCII.GetString(digits), out long number))
        {
            return cursor.FailAt(start, "The integer does not fit in a 64-bit number.");
        }

        cursor.MoveTo(end + 1);
        value = new BencodeInteger(number);

        return true;
    }

    private static bool ReadBytes(ref Cursor cursor, out BencodeValue? value)
    {
        value = null;

        int start = cursor.At;
        int colon = cursor.IndexOf((byte)':');

        if (colon < 0)
        {
            return cursor.FailAt(cursor.Length, "A byte string has no colon after its length.");
        }

        ReadOnlySpan<byte> digits = cursor.Slice(start, colon - start);

        foreach (byte digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                return cursor.FailAt(start, "A byte string's length is not a number.");
            }
        }

        if (!int.TryParse(Encoding.ASCII.GetString(digits), out int length) || length < 0)
        {
            return cursor.FailAt(start, "A byte string's length is not a number.");
        }

        if (colon + 1 + length > cursor.Length)
        {
            // The end of the input is the offset, because that is where a
            // truncated download stops being a torrent.
            return cursor.FailAt(cursor.Length, $"A byte string of {length} bytes runs past the end.");
        }

        value = new BencodeBytes(cursor.Slice(colon + 1, length).ToArray());
        cursor.MoveTo(colon + 1 + length);

        return true;
    }

    private static bool ReadList(ref Cursor cursor, int depth, out BencodeValue? value)
    {
        value = null;
        cursor.Skip();

        List<BencodeValue> items = [];

        while (true)
        {
            if (!cursor.More)
            {
                return cursor.FailAt(cursor.Length, "A list has no end.");
            }

            if (cursor.Peek == (byte)'e')
            {
                cursor.Skip();
                value = new BencodeList(items);

                return true;
            }

            if (!ReadValue(ref cursor, depth + 1, top: false, out BencodeValue? item))
            {
                return false;
            }

            items.Add(item!);
        }
    }

    private static bool ReadDictionary(ref Cursor cursor, int depth, bool top, out BencodeValue? value)
    {
        value = null;
        cursor.Skip();

        List<BencodeEntry> entries = [];

        while (true)
        {
            if (!cursor.More)
            {
                return cursor.FailAt(cursor.Length, "A dictionary has no end.");
            }

            if (cursor.Peek == (byte)'e')
            {
                cursor.Skip();
                value = new BencodeDictionary(entries);

                return true;
            }

            if (!ReadBytes(ref cursor, out BencodeValue? key))
            {
                return false;
            }

            byte[] name = ((BencodeBytes)key!).Value;

            // Where the info dictionary is, recorded while passing it. Only the
            // top-level one: a peer's metadata message has an "info" of its own
            // meaning something else entirely.
            int began = cursor.At;

            if (!ReadValue(ref cursor, depth + 1, top: false, out BencodeValue? entry))
            {
                return false;
            }

            if (top && name.AsSpan().SequenceEqual("info"u8))
            {
                cursor.RecordInfo(began, cursor.At - began);
            }

            entries.Add(new(name, entry!));
        }
    }

    /// <summary>
    /// Where the reader is, and what it has noticed.
    /// </summary>
    /// <remarks>
    /// A ref struct over the input so nothing is copied and nothing outlives
    /// the span it points into.
    /// </remarks>
    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;

        public int At { get; private set; }

        public int Length => _bytes.Length;

        public bool More => At < _bytes.Length;

        public byte Peek => _bytes[At];

        public BencodeError? Error { get; private set; }

        public int? InfoStart { get; private set; }

        public int? InfoLength { get; private set; }

        public void Skip()
        {
            At++;
        }

        public void MoveTo(int offset)
        {
            At = offset;
        }

        public readonly ReadOnlySpan<byte> Slice(int start, int length)
        {
            return _bytes.Slice(start, length);
        }

        /// <summary>Where the next of this byte is, counted from the whole input.</summary>
        public readonly int IndexOf(byte wanted)
        {
            int found = _bytes[At..].IndexOf(wanted);

            return found < 0 ? -1 : At + found;
        }

        public void RecordInfo(int start, int length)
        {
            InfoStart = start;
            InfoLength = length;
        }

        public bool Fail(string reason)
        {
            return FailAt(At, reason);
        }

        public bool FailAt(int offset, string reason)
        {
            // The first failure is the one that matters: everything after it is
            // a reader trying to make sense of bytes it has already lost track
            // of.
            Error ??= new(offset, reason);

            return false;
        }
    }
}
