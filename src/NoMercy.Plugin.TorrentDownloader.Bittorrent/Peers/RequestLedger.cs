namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// What this client has asked one peer for, and what it may therefore send.
/// </summary>
/// <remarks>
/// <para>
/// A block nobody asked for is not a gift. It is memory this process did not
/// plan to spend, written at an offset nothing is expecting, and the peer
/// sending it is either broken or trying something — so it is dropped rather
/// than accommodated.
/// </para>
/// <para>
/// One of these per peer. Two peers may hold a request for the same block
/// during endgame, and each has to be judged against its own.
/// </para>
/// </remarks>
public sealed class RequestLedger
{
    private readonly HashSet<(int Piece, int Offset, int Length)> _outstanding = [];

    /// <summary>How many are in flight to this peer.</summary>
    public int InFlight => _outstanding.Count;

    /// <summary>Records a request as it goes out.</summary>
    public void Asked(int piece, int offset, int length)
    {
        _outstanding.Add((piece, offset, length));
    }

    /// <summary>Forgets one, as a cancel does.</summary>
    public void Cancelled(int piece, int offset, int length)
    {
        _outstanding.Remove((piece, offset, length));
    }

    /// <summary>
    /// Whether a block that has arrived was asked for, and forgets it if so.
    /// </summary>
    /// <remarks>
    /// The length has to match as well as the place: a peer answering a
    /// sixteen-kibibyte request with a megabyte is the same fault wearing a
    /// different hat.
    /// </remarks>
    public bool Accept(int piece, int offset, int length)
    {
        return _outstanding.Remove((piece, offset, length));
    }

    /// <summary>Forgets everything, as a choke does.</summary>
    /// <remarks>
    /// A choked peer will not answer what was asked before it choked, and
    /// keeping those would have this client waiting on blocks nobody is going
    /// to send.
    /// </remarks>
    public void Clear()
    {
        _outstanding.Clear();
    }
}
