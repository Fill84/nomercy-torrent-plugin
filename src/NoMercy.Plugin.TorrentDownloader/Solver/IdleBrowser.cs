namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// When a browser nobody is using is worth closing.
/// </summary>
/// <remarks>
/// <para>
/// The browser is kept between solves on purpose: a gated source hands out its
/// clearance to a browser session, and taking that session down loses it, so
/// the next search pays for a fresh challenge. Keeping it was the fix for a
/// gated indexer answering nothing.
/// </para>
/// <para>
/// <strong>What it cost.</strong> Kept for the life of the server, that is ten
/// Chrome processes and about two hundred megabytes held for a machine that may
/// not search again until morning. The owner saw exactly that and asked why it
/// was running while nothing was happening.
/// </para>
/// <para>
/// So it is kept for as long as it is worth keeping and no longer. A search
/// cycle solves several sources within a few minutes and they share one
/// browser and one clearance; an evening with nothing to look for gets its
/// memory back. Losing a clearance costs one challenge on the next gated
/// search, which is what it cost before this browser was ever kept.
/// </para>
/// </remarks>
public static class IdleBrowser
{
    /// <summary>How long a browser with nothing open is kept before it is closed.</summary>
    /// <remarks>
    /// Long enough that every source of one search cycle shares the browser
    /// that the first of them started, and that a cycle a quarter of an hour
    /// later still finds its clearance. Short enough that a server left alone
    /// is not holding a browser at midnight for a search it made at nine.
    /// </remarks>
    public static readonly TimeSpan After = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether a browser should be closed now.
    /// </summary>
    /// <param name="open">How many tabs are open. One is a solve in flight.</param>
    /// <param name="lastClosed">
    /// When the last tab closed, or null where none ever opened. Null keeps the
    /// browser: something started it and has not asked for a tab yet, and
    /// closing it from underneath is how a solve fails before it begins.
    /// </param>
    /// <param name="now">The time.</param>
    /// <param name="after">How long idle is long enough.</param>
    public static bool Due(int open, DateTimeOffset? lastClosed, DateTimeOffset now, TimeSpan after)
    {
        if (open > 0 || lastClosed is not DateTimeOffset since)
        {
            return false;
        }

        return now - since >= after;
    }
}
