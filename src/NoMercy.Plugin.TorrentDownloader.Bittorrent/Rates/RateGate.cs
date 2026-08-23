namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// How fast bytes are allowed to move, across every torrent at once.
/// </summary>
/// <remarks>
/// <para>
/// <c>MaxDownloadRate</c> and <c>MaxUploadRate</c> from settings. Both were on
/// the Settings page and read by nothing at all, so a client told to take at
/// most a megabyte a second took whatever the line would give — which on a
/// server the household also watches television through is the whole of it.
/// </para>
/// <para>
/// One gate for each direction and shared between torrents, because the limit
/// the owner set is on the line and not on a torrent. Nought is nobody asking
/// for a limit and costs nothing at all: no lock is taken and nothing waits.
/// </para>
/// <para>
/// A bucket that fills at the rate and holds one second of it. Holding a
/// second's worth is what lets a burst of blocks through unharmed while the
/// average still comes out at the number the owner typed; without any burst at
/// all every single block would wait its own turn and the overhead would be the
/// download.
/// </para>
/// </remarks>
public sealed class RateGate(long bytesPerSecond, TimeProvider time)
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private double _allowance;
    private DateTimeOffset _last;

    /// <summary>What the owner asked for, in bytes a second. Nought is no limit.</summary>
    public long BytesPerSecond => bytesPerSecond;

    /// <summary>
    /// Waits until these bytes may move, and then lets them.
    /// </summary>
    /// <param name="bytes">How many are about to move.</param>
    /// <param name="ct">The caller's own lifetime.</param>
    public async Task PassAsync(int bytes, CancellationToken ct)
    {
        if (bytesPerSecond <= 0 || bytes <= 0)
        {
            return;
        }

        await _turn.WaitAsync(ct).ConfigureAwait(false);

        TimeSpan waiting;

        try
        {
            DateTimeOffset now = time.GetUtcNow();

            if (_last == default)
            {
                // The bucket starts full. Starting it empty makes the very
                // first block of the very first torrent wait a whole second
                // for permission to exist, which is a limit applied to a line
                // that has not been used yet.
                _last = now;
                _allowance = bytesPerSecond;
            }

            _allowance = Math.Min(bytesPerSecond, _allowance + ((now - _last).TotalSeconds * bytesPerSecond));
            _last = now;
            _allowance -= bytes;

            waiting = _allowance >= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(-_allowance / bytesPerSecond);

            if (waiting > TimeSpan.Zero)
            {
                // Paid for in advance: the allowance goes back to nought and the
                // wait happens after the lock is given up, so one slow caller
                // does not hold every other caller behind it as well as itself.
                _allowance = 0;
                _last = now + waiting;
            }
        }
        finally
        {
            _turn.Release();
        }

        if (waiting > TimeSpan.Zero)
        {
            await Task.Delay(waiting, time, ct).ConfigureAwait(false);
        }
    }
}
