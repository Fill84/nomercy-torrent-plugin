namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>One block of one piece.</summary>
public readonly record struct BlockRequest(int Piece, int Offset, int Length);

/// <summary>
/// Which piece to ask for next.
/// </summary>
/// <remarks>
/// <para>
/// Rarest first, from what the peers have said they have. A client that took
/// pieces in order finishes last: the common pieces stay common, the rare ones
/// disappear with the peer holding them, and the swarm as a whole is worse off.
/// </para>
/// <para>
/// The picker holds no locks and no sockets. What it knows is what it has been
/// told — every bitfield and every <c>have</c> — and what it answers is a piece
/// number.
/// </para>
/// </remarks>
public sealed class PiecePicker(
    int pieces,
    int endgamePieces = PiecePicker.DefaultEndgamePieces,
    Bitfield? only = null)
{
    private readonly int[] _availability = new int[pieces];

    /// <summary>
    /// How few pieces are left before the endgame starts.
    /// </summary>
    /// <remarks>
    /// No document gives a number, so this is the one this client uses: with
    /// fewer than this outstanding, the last blocks are asked of everybody at
    /// once. It matters because the tail of a download is otherwise spent
    /// waiting on the slowest peer holding the last piece.
    /// </remarks>
    public const int DefaultEndgamePieces = 8;

    /// <summary>
    /// How many pieces are picked at random before rarest-first takes over.
    /// </summary>
    /// <remarks>
    /// From docs/06-torrent-client.md: the first four, so that something can be
    /// verified early. Rarest-first at the very start sends every peer after
    /// the same rare piece, and nothing completes until it does.
    /// </remarks>
    public const int RandomFirst = 4;

    /// <summary>How many pieces the torrent has.</summary>
    public int Pieces => pieces;

    /// <summary>Notes what a peer said it has.</summary>
    public void Saw(Bitfield theirs)
    {
        for (int piece = 0; piece < pieces; piece++)
        {
            if (theirs.Has(piece))
            {
                _availability[piece]++;
            }
        }
    }

    /// <summary>Notes one piece a peer has just announced.</summary>
    public void Saw(int piece)
    {
        if (piece >= 0 && piece < pieces)
        {
            _availability[piece]++;
        }
    }

    /// <summary>Forgets what a peer had, because it has gone.</summary>
    public void Left(Bitfield theirs)
    {
        for (int piece = 0; piece < pieces; piece++)
        {
            if (theirs.Has(piece) && _availability[piece] > 0)
            {
                _availability[piece]--;
            }
        }
    }

    /// <summary>How many peers have this piece.</summary>
    public int Availability(int piece)
    {
        return _availability[piece];
    }

    /// <summary>How many pieces this picker will ever ask for.</summary>
    /// <remarks>
    /// Every piece unless the caller named a mask, in which case only the ones
    /// inside it — the owner downloads video files and nothing else, so most
    /// torrents have pieces this client will never want.
    /// </remarks>
    public int Wanted => only?.Count ?? pieces;

    /// <summary>Whether this piece is one of the ones being downloaded.</summary>
    public bool Wants(int piece)
    {
        return only is null || only.Has(piece);
    }

    /// <summary>How many wanted pieces are still missing.</summary>
    public int Missing(Bitfield mine)
    {
        if (only is null)
        {
            return pieces - mine.Count;
        }

        int missing = 0;

        for (int piece = 0; piece < pieces; piece++)
        {
            if (only.Has(piece) && !mine.Has(piece))
            {
                missing++;
            }
        }

        return missing;
    }

    /// <summary>Whether the tail of the download has been reached.</summary>
    /// <remarks>
    /// Counted over the wanted pieces alone. Against the whole torrent a
    /// download of one file out of twenty would look like the endgame from its
    /// first message and ask every peer for everything at once.
    /// </remarks>
    public bool Endgame(Bitfield mine)
    {
        return Missing(mine) <= endgamePieces;
    }

    /// <summary>
    /// The next piece to ask this peer for, or null when it has nothing wanted.
    /// </summary>
    /// <param name="mine">What is already verified here.</param>
    /// <param name="theirs">What the peer says it has.</param>
    /// <param name="inFlight">Pieces already being asked of somebody.</param>
    /// <param name="random">
    /// Where the randomness comes from, so a test can hand in a known one. The
    /// first four pieces are picked with it.
    /// </param>
    public int? Next(Bitfield mine, Bitfield theirs, IReadOnlySet<int> inFlight, Random random)
    {
        // In the endgame every outstanding piece is asked of everybody, so a
        // piece already in flight is still worth asking this peer for.
        bool endgame = Endgame(mine);

        int[] wanted =
        [
            .. mine.Wanted(theirs).Where(piece => Wants(piece) && (endgame || !inFlight.Contains(piece))),
        ];

        if (wanted.Length == 0)
        {
            return null;
        }

        if (mine.Count < RandomFirst)
        {
            // At random, so that several peers do not all begin on the same
            // rare piece and leave this client with nothing verified for
            // minutes.
            return wanted[random.Next(wanted.Length)];
        }

        int rarest = wanted.Min(piece => _availability[piece]);

        // Among equally rare pieces, at random rather than the lowest number:
        // every client in the swarm has the same availability table, and taking
        // the lowest would have them all ask for the same one.
        int[] tied = [.. wanted.Where(piece => _availability[piece] == rarest)];

        return tied[random.Next(tied.Length)];
    }

    /// <summary>
    /// The blocks one piece is made of.
    /// </summary>
    /// <remarks>
    /// Sixteen kibibytes each and in order, so the piece completes and can be
    /// verified rather than being a scatter of holes across the whole torrent.
    /// </remarks>
    public static IEnumerable<BlockRequest> Blocks(int piece, long pieceLength)
    {
        for (long at = 0; at < pieceLength; at += PeerMessage.BlockLength)
        {
            yield return new(piece, (int)at, (int)Math.Min(PeerMessage.BlockLength, pieceLength - at));
        }
    }
}
