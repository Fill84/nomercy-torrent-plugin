using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// The order missing episodes are asked about in.
/// </summary>
/// <remarks>
/// One rule, used both by the search cadence and by the Queue page. Two would
/// make the page a guess about what the plugin is going to do rather than a
/// statement of it, and the page nobody can trust is the one nobody reads.
/// </remarks>
public static class QueueOrder
{
    /// <summary>
    /// The episodes that will be searched, soonest first.
    /// </summary>
    /// <remarks>
    /// Never searched first, then longest waiting. Anything else lets one
    /// episode be asked about over and over while an unlucky one is never
    /// reached at all.
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
                .OrderBy(episode => episode.LastSearchAt ?? DateTimeOffset.MinValue)
                // Everything searched in one cycle shares a moment, so without
                // a tie-break the page reshuffles itself between two renders of
                // the same data and looks like it is doing something.
                .ThenBy(episode => episode.Key.ShowId)
                .ThenBy(episode => episode.Key.Season)
                .ThenBy(episode => episode.Key.Number),
        ];
    }
}
