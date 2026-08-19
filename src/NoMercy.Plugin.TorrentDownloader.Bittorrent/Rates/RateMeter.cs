namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// How fast something is moving now.
/// </summary>
/// <remarks>
/// <para>
/// Now, and never on average since it started. A torrent that took a gigabyte
/// in its first minute and has been stalled for an hour since is not moving at
/// seventeen megabytes a second, and a page saying so would have the owner
/// waiting on a download that had stopped. docs/08-ui.md: every number is real.
/// </para>
/// <para>
/// It measures between two readings, so what it answers depends on how often it
/// is read — which is what "measured" means. The transfers cadence reads it
/// once a minute and gets the rate over that minute.
/// </para>
/// </remarks>
public sealed class RateMeter(TimeProvider time)
{
    private readonly Lock _lock = new();
    private long _at;
    private long _total;
    private double _rate;
    private bool _started;

    /// <summary>
    /// How little time between two readings makes the answer noise.
    /// </summary>
    /// <remarks>
    /// One sixteen-kibibyte block arriving four milliseconds after the last
    /// reading is four megabytes a second, and a dashboard redrawing on every
    /// push would show that, then nought, then that again. Under this, the last
    /// real measurement stands.
    /// </remarks>
    public static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(250);

    /// <summary>Takes a reading and answers the rate in bytes a second.</summary>
    /// <param name="total">How much has moved in total, ever.</param>
    public double Measure(long total)
    {
        lock (_lock)
        {
            long now = time.GetTimestamp();

            if (!_started)
            {
                // Nothing to measure against. Nought rather than a number
                // invented out of one sample.
                _started = true;
                _at = now;
                _total = total;

                return _rate;
            }

            TimeSpan since = time.GetElapsedTime(_at, now);

            if (since < Shortest)
            {
                // Too soon to mean anything, including no time at all — which
                // would divide by nought and print something that is not a
                // number.
                return _rate;
            }

            _rate = Math.Max(0, total - _total) / since.TotalSeconds;
            _at = now;
            _total = total;

            return _rate;
        }
    }
}
