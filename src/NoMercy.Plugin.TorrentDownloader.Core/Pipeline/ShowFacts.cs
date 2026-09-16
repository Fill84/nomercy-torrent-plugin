using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// What the overview says about one show: how many aired episodes have no file, and whether the owner
/// has the show at all.
/// </summary>
/// <param name="Missing">Aired episodes with no video file, specials counted only where the show takes them.</param>
/// <param name="Held">Whether the library holds one video file of it anywhere.</param>
/// <remarks>
/// <para>
/// <strong>Counted from the episodes, never from the server's own column.</strong> On the owner's library
/// on 16 September 2026 all 69 shows read <c>HaveEpisodes = 0</c> while 57 of them held 2,264 files
/// between them, so a page built on that column would have told the owner they own nothing.
/// </para>
/// <para>
/// <strong>Held is what keeps a show nobody added off the page.</strong> The server writes a row for every
/// show it ever identified, and twelve of the owner's sixty-nine — Brilliant Minds, Family Guy, GINTAMA,
/// The Simpsons among them — are shows they do not have, imported on a guess (media-server #36) and read
/// by the owner as recommendations. A show with one file of it anywhere is a show they have.
/// </para>
/// </remarks>
public readonly record struct ShowFacts(int Missing, bool Held)
{
    /// <summary>Counts one show's episodes, as the library hands them over.</summary>
    /// <param name="episodes">Every episode of the show, those with a file included.</param>
    /// <param name="today">The broadcast day: an episode airing today has aired.</param>
    /// <param name="specials">Whether this show's settings take season 0.</param>
    /// <param name="files">Every file the library holds of the show, whichever episode it is registered against.</param>
    public static ShowFacts Of(IReadOnlyList<Episode> episodes, DateOnly today, bool specials, IReadOnlyList<string>? files = null)
    {
        int missing = 0;
        bool held = false;

        foreach (Episode episode in episodes)
        {
            if (episode.HasFile)
            {
                // A file of a special still says the owner has the show, whether
                // or not their settings would ever search for one.
                held = true;

                continue;
            }

            if (episode.Key.IsSpecial && !specials)
            {
                continue;
            }

            // The same rules the run itself follows: a file in the library named
            // for the episode is the episode (MissingRefresh says why), no date
            // is not aired, and today counts as aired.
            if (episode.AirDate is DateOnly aired && aired <= today && !Landed.Wrote(episode.Key, files ?? []))
            {
                missing++;
            }
        }

        return new(missing, held);
    }
}
