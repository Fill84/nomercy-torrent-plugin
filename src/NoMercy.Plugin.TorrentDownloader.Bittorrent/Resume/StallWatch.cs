namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Whether a torrent has stopped getting anywhere.
/// </summary>
/// <remarks>
/// <para>
/// No progress <strong>and</strong> no peers, for <c>StallMinutes</c>. Both
/// halves matter and the document is emphatic about it: a torrent downloading
/// steadily from one peer that the tracker has stopped listing is not stalled,
/// and a torrent with forty peers that has not moved for a minute is not
/// either — it is waiting for a piece, which is what the endgame looks like
/// from outside.
/// </para>
/// <para>
/// Calling a healthy torrent stalled is expensive: the hash gets blacklisted
/// and the episode goes back to missing, so the plugin then refuses the release
/// it was already downloading.
/// </para>
/// </remarks>
public sealed class StallWatch(TimeSpan limit, TimeProvider time)
{
    private long _bytes = -1;
    private DateTimeOffset _since;

    /// <summary>Since when nothing at all has happened, or null.</summary>
    public DateTimeOffset? StuckSince => _since == default ? null : _since;

    /// <summary>
    /// Takes a reading, and says whether this torrent is now stalled.
    /// </summary>
    /// <param name="bytesDone">How much is verified on disk.</param>
    /// <param name="peers">How many peers are connected.</param>
    public bool Observe(long bytesDone, int peers)
    {
        DateTimeOffset now = time.GetUtcNow();

        // Either half is enough to say it is alive. Progress cannot be judged
        // on the very first reading — there is nothing to compare it with — but
        // having no peers can, so a torrent that has had none since the client
        // came up starts its clock at that first reading rather than the
        // second. Otherwise a restart quietly buys a dead torrent one more
        // interval, every time.
        bool alive = (_bytes >= 0 && bytesDone != _bytes) || peers > 0;

        _bytes = bytesDone;

        if (alive)
        {
            _since = default;

            return false;
        }

        if (_since == default)
        {
            _since = now;

            return false;
        }

        return now - _since >= limit;
    }
}
