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
    /// <para>
    /// <strong>None at all: the owner's decision of 11 September 2026.</strong>
    /// They watched ten Chrome processes and two hundred megabytes sitting on
    /// their server with nothing running, and asked for it to go the moment the
    /// last page has been read.
    /// </para>
    /// <para>
    /// <strong>It was a quarter of an hour, for a reason that turned out to be
    /// the wrong one.</strong> On 26 August 2026 stopping it with its last tab
    /// was measured breaking gated sources — TorrentBay cleared, 1337x and EZTV
    /// answered "this address is behind a challenge and the browser could not
    /// get past it" — and the browser being taken down was blamed. It was not
    /// the browser. <c>CloudflareChallenge</c> counted
    /// <c>challenge-platform</c> a marker, and Cloudflare leaves that script on
    /// an ordinary page for the session that has just solved a challenge, so
    /// the solver rejected the very page it had been waiting for. That is
    /// fixed, and with it the reason for keeping a browser warm.
    /// </para>
    /// <para>
    /// What is not lost by closing: the clearance cookie is read into the
    /// clearance store the moment it is issued, and the browser's own profile
    /// is a folder on disk that outlives the process. A cold start costs the
    /// seconds it takes to start Chrome.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan After = TimeSpan.Zero;

    /// <summary>
    /// How often the idle check runs anyway.
    /// </summary>
    /// <remarks>
    /// The close is done by the last tab as it goes, so this is a backstop and
    /// nothing more: a tab abandoned without being disposed would otherwise
    /// leave a browser nobody closes. A timer cannot have a period of nothing,
    /// and there is no case that needs it sooner than this.
    /// </remarks>
    public static readonly TimeSpan Backstop = TimeSpan.FromMinutes(1);

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
