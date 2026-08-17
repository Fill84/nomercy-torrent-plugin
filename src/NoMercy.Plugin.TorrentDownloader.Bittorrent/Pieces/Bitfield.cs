namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// One bit per piece: which of them somebody has.
/// </summary>
/// <remarks>
/// The high bit of the first byte is piece nought, which is the opposite of
/// what a bit index usually means and is what BEP 3 says. A client that got it
/// backwards would ask every peer for pieces they do not have and refuse the
/// ones they do.
/// </remarks>
public sealed class Bitfield
{
    private readonly byte[] _bits;

    public Bitfield(int pieces)
    {
        Pieces = pieces;
        _bits = new byte[(pieces + 7) / 8];
    }

    /// <summary>How many pieces the torrent has.</summary>
    public int Pieces { get; }

    /// <summary>How many of them are set.</summary>
    public int Count
    {
        get
        {
            int count = 0;

            for (int piece = 0; piece < Pieces; piece++)
            {
                if (Has(piece))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Whether every piece is set.</summary>
    public bool All => Count == Pieces;

    /// <summary>
    /// Reads a peer's bitfield.
    /// </summary>
    /// <remarks>
    /// The length has to be exactly one bit per piece rounded up, and the
    /// spare bits at the end have to be nought. A peer whose bitfield is the
    /// wrong size is talking about a different torrent — BEP 3 says to drop it,
    /// and a client that padded it instead would believe it had pieces nobody
    /// has.
    /// </remarks>
    public static Bitfield Read(ReadOnlySpan<byte> bytes, int pieces)
    {
        if (bytes.Length != (pieces + 7) / 8)
        {
            throw new PeerProtocolException(
                $"A bitfield for {pieces} pieces is {(pieces + 7) / 8} bytes, and this one is {bytes.Length}.");
        }

        Bitfield field = new(pieces);
        bytes.CopyTo(field._bits);

        for (int spare = pieces; spare < field._bits.Length * 8; spare++)
        {
            if (field.Has(spare))
            {
                throw new PeerProtocolException("A bitfield has bits set past the end of the torrent.");
            }
        }

        return field;
    }

    /// <summary>The bytes this bitfield goes out as.</summary>
    public byte[] Write()
    {
        return [.. _bits];
    }

    /// <summary>Whether this piece is set.</summary>
    public bool Has(int piece)
    {
        return piece >= 0
               && piece < _bits.Length * 8
               && (_bits[piece / 8] & (0x80 >> (piece % 8))) != 0;
    }

    /// <summary>Sets one piece.</summary>
    public void Set(int piece)
    {
        if (piece < 0 || piece >= Pieces)
        {
            throw new PeerProtocolException($"Piece {piece} is not in a torrent of {Pieces}.");
        }

        _bits[piece / 8] |= (byte)(0x80 >> (piece % 8));
    }

    /// <summary>Every piece this one is missing that the other has.</summary>
    public IEnumerable<int> Wanted(Bitfield theirs)
    {
        for (int piece = 0; piece < Pieces; piece++)
        {
            if (!Has(piece) && theirs.Has(piece))
            {
                yield return piece;
            }
        }
    }
}
