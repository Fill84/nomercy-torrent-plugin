namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Lets one thing run at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>F3.</strong> 0.3.4 had no overlap protection: a thirty-minute cycle
/// against a five-minute cron is six searches running at once, each asking
/// every site the same questions and each able to grab the release the other
/// five have just taken. What one cycle has decided is state the next cannot
/// see, which is why they cannot simply be allowed to race.
/// </para>
/// <para>
/// <strong>F2</strong> is why this takes no cancellation token. The run lock
/// was once taken with the caller's, and a zero wait cannot block — so it
/// bought nothing and killed the run on the way in. There is nothing here to
/// wait for: a tick that cannot get in is dropped, not queued.
/// </para>
/// </remarks>
public sealed class OneAtATime
{
    private int _running;

    /// <summary>Whether something is running now.</summary>
    public bool Busy => Volatile.Read(ref _running) != 0;

    /// <summary>Takes the turn, or answers that somebody else has it.</summary>
    /// <remarks>
    /// One of everything that arrives together gets in, whatever the load: a
    /// guard that let two through under contention is one that passes every
    /// test and fails on the machine it was written for.
    /// </remarks>
    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref _running, 1, 0) == 0;
    }

    /// <summary>Gives the turn back.</summary>
    /// <remarks>
    /// In a <c>finally</c>, always. A guard that never opened again would be a
    /// plugin that ran one cycle and then nothing for as long as the server was
    /// up, which is the same silence as no cycles at all and harder to see.
    /// </remarks>
    public void Leave()
    {
        Volatile.Write(ref _running, 0);
    }
}
