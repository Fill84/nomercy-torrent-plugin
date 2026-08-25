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
/// <strong>Why having a file is the rule.</strong> Taking every show in the
/// library instead was tried on 24 August 2026, because a show just added has
/// nothing on disk and is exactly the case worth having. Within the hour the
/// owner's plugin was on 479 grabs, and Family Guy alone claimed 456 missing
/// episodes — a show they have never watched, whose row the server keeps all
/// the same. Nothing in such a row tells it apart from a show they added: it
/// carries the library's id, a folder and a full episode list, the same as the
/// rest. Having a file is the only thing that does.
/// </para>
/// <para>
/// <strong>What replaces it.</strong> When media-server #36 stops
/// identification importing shows on a guess, and #34 makes a newly added show
/// visible, library membership becomes the discriminator and this body becomes
/// that question instead. A show just added is then in scope on the day it is
/// added, which is what the rule should have said all along. Until then this is
/// the rule, and a show just added is still not searched for — known and
/// unsolved rather than overlooked.
/// </para>
/// </remarks>
public static class Ownership
{
    /// <summary>
    /// True when the show these episodes belong to is one the owner has.
    /// </summary>
    /// <param name="episodes">
    /// Every episode of one show, as the library gives them. An empty list is
    /// not the owner's: a show with nothing at all is the clearest case of the
    /// row nobody asked for.
    /// </param>
    public static bool Theirs(IReadOnlyList<Episode> episodes)
    {
        return episodes.Any(episode => episode.HasFile);
    }
}
