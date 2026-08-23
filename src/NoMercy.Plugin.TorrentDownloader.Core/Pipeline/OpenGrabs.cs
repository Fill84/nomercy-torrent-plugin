using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Episodes something is already downloading are not looked for again.
/// </summary>
/// <remarks>
/// <para>
/// An episode stays <see cref="EpisodeState.Missing"/> until a file for it is
/// in the library, which is right: it is missing until it arrives. But the
/// cycle read that as work to do, so every pass searched for it again and
/// grabbed the same release again.
/// </para>
/// <para>
/// On the owner's server on 23 August 2026 three episodes of Sugar had four
/// identical grabs each, one per cycle, all carrying the same info hash. The
/// client recognised the hash and took it once; the store did not, so the
/// Downloads page showed each of them four times and a failure had to put four
/// rows back.
/// </para>
/// <para>
/// A grab that fails puts its episodes back to missing, and a grab that
/// finishes gives the library a file — so nothing here has to expire. What is
/// open is the whole of what is excluded.
/// </para>
/// </remarks>
public static class OpenGrabs
{
    /// <summary>The gaps still worth searching for.</summary>
    /// <param name="tracked">Every episode the library is missing.</param>
    /// <param name="grabbing">
    /// Every episode an open grab answers for, its packs included: a season
    /// pack settles the whole season, and searching for the rest of it while
    /// the pack is downloading grabs the season a second time.
    /// </param>
    public static IReadOnlyList<TrackedEpisode> Excluding(
        IReadOnlyList<TrackedEpisode> tracked,
        IReadOnlyCollection<EpisodeKey> grabbing)
    {
        if (grabbing.Count == 0)
        {
            return tracked;
        }

        HashSet<EpisodeKey> already = [.. grabbing];

        return [.. tracked.Where(episode => !already.Contains(episode.Key))];
    }
}
