using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// The order missing episodes are asked about in.
/// </summary>
/// <remarks>
/// One rule, used both by search and by the Queue page. Two would
/// make the page a guess about what the plugin is going to do rather than a
/// statement of it, and the page nobody can trust is the one nobody reads.
/// </remarks>
public static class QueueOrder
{
    /// <summary>
    /// The episodes that will be searched, soonest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Always from the top: show, season, episode.</strong> The owner's
    /// decision of 11 September 2026. This used to put whatever was searched
    /// longest ago first, so every episode a stopped run had reached went to
    /// the back — and the next Run carried on from where the stopped one had
    /// got to, which looked exactly like a pause. A run now begins where every
    /// run begins.
    /// </para>
    /// <para>
    /// Shows by title, because that is how the owner reads the Queue page; the
    /// id only breaks a tie between two shows of one name.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TrackedEpisode> Order(IEnumerable<TrackedEpisode> episodes)
    {
        return
        [
            .. episodes
                // Only what is being looked for. An unaired episode would be
                // asked about before it exists; an unavailable one has been
                // given up on until the next maintenance pass puts it back.
                .Where(episode => episode.State == EpisodeState.Missing)
                .OrderBy(episode => episode.ShowTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(episode => episode.Key.ShowId)
                .ThenBy(episode => episode.Key.Season)
                .ThenBy(episode => episode.Key.Number),
        ];
    }
}
