namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Whose turn it is to run the transfers pass: never two at once, and never a pass asked for and lost.
/// </summary>
/// <param name="pass">One pass over everything the client holds. It does not throw: one that does ends the turn.</param>
/// <param name="refused">
/// What a caller that found a pass already running does before it returns. Nothing, on a server. A test
/// holds a refused caller here while the running pass finishes, which is the one interleaving in which a
/// wake-up can be lost, and the only way to put it to a test rather than hope for it.
/// </param>
public sealed class TransfersTurn(Func<CancellationToken, Task> pass, Func<Task>? refused = null)
{
    /// <summary>Keeps two passes from staging the same file twice.</summary>
    private readonly OneAtATime _running = new();

    /// <summary>A pass was asked for while one ran, so that one goes round again.</summary>
    private int _wanted;

    /// <summary>Runs the pass now, or has the one already running go round once more.</summary>
    /// <returns>Whether this call ran a pass, as opposed to leaving it to the one already running.</returns>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        // The wish first, and only then the guard. It used to be written after
        // the guard refused, and in that gap the running pass could read it,
        // give its turn back and read it once more, all before it was written:
        // the wish then landed with nobody left to act on it. GitHub issue #3, a
        // finished download that never reached the intake folder. Written
        // before the attempt, it is there for any pass whose turn the attempt
        // finds taken, because that pass has not yet given its turn back.
        Interlocked.Exchange(ref _wanted, 1);

        bool ran = false;

        while (Volatile.Read(ref _wanted) != 0 && !ct.IsCancellationRequested)
        {
            if (!_running.TryEnter())
            {
                if (refused is not null)
                {
                    await refused();
                }

                return ran;
            }

            ran = true;

            try
            {
                // Taken before each pass, so a wish made while it runs is one
                // the next round sees.
                while (Interlocked.Exchange(ref _wanted, 0) != 0 && !ct.IsCancellationRequested)
                {
                    await pass(ct);
                }
            }
            finally
            {
                _running.Leave();
            }

            // Giving the turn back is a plain write, and the read of the wish
            // below may otherwise be answered before it is seen: a caller then
            // finds the turn taken while this pass finds no wish, which is the
            // same lost wake-up the other way round.
            Interlocked.MemoryBarrier();
        }

        return ran;
    }
}
