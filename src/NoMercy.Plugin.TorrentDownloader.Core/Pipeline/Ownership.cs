using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Whether a show in the library is one the owner actually has.
/// </summary>
/// <remarks>
/// <para>
/// It is here, alone, because two halves of the plugin ask it: the refresh
/// decides which shows are searched for, and the transfers tick decides which
/// grabs are cancelled for belonging to a show the owner does not have. While
/// the expression was written out in both, they could drift apart — and a
/// disagreement means the plugin grabs a show and cancels it on the next tick,
/// or keeps one it should never have started.
/// </para>
/// <para>
/// <strong>Membership is the rule, and having a file used to be.</strong>
/// Taking every show in the library was tried on 24 August 2026 and within the
/// hour the owner's plugin was on 479 grabs, Family Guy alone claiming 456
/// missing episodes — a show they have never watched, whose row the server kept
/// all the same. Nothing in such a row told it apart from a show they added: the
/// library's id, a folder, a full episode list, the same as the rest. So having
/// a file became the discriminator, and the cost was that a show just added was
/// invisible until something downloaded — the case most worth having, and the
/// one the rule could not reach.
/// </para>
/// <para>
/// <strong>Why it can change now.</strong> media-server #36 stopped
/// identification importing whole shows on a guess and #34 made a newly added
/// show visible; both closed on 30 August 2026. On the owner's server the next
/// day, the television library held fifty-five shows and <em>not one of them</em>
/// was without a file — so membership and having a file gave the same answer,
/// and the rows nobody asked for were gone. Membership is what the rule should
/// have said all along: a show is in scope on the day it is added.
/// </para>
/// <para>
/// <strong>What it costs, said out loud.</strong> A show added with nothing on
/// disk now has every episode missing, and the plugin will look for all of them.
/// That is the point of it and it is still a burst of searching that the owner
/// did not have before.
/// </para>
/// </remarks>
public static class Ownership
{
    /// <summary>
    /// True when this show is in one of the libraries the plugin watches.
    /// </summary>
    /// <param name="showId">The show a grab names, or one being considered.</param>
    /// <param name="shows">
    /// Every show the watched libraries hold. A show that is in none of them is
    /// one the owner has removed, or one this plugin was never for.
    /// </param>
    public static bool Theirs(int showId, IReadOnlyList<Show> shows)
    {
        return shows.Any(show => show.Id == showId);
    }
}
