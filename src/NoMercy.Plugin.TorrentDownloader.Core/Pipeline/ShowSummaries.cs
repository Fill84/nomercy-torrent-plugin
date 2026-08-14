using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Rolls tracked episodes up into one line per show.
/// </summary>
public static class ShowSummaries
{
    /// <summary>
    /// One summary per show that has anything outstanding, by title.
    /// </summary>
    /// <remarks>
    /// Counted from the rows themselves, every time. Keeping a total anywhere
    /// else would be a second number that could disagree with the list under
    /// it — which is exactly how 0.3.4 came to show "0 downloads" while two
    /// were running.
    ///
    /// A show with nothing outstanding has no rows at all and so no line here.
    /// That is not an omission: a show whose every episode is on disk is a show
    /// this plugin has nothing to say about.
    /// </remarks>
    public static IReadOnlyList<ShowSummary> Summarise(IEnumerable<TrackedEpisode> episodes)
    {
        return
        [
            .. episodes
                .GroupBy(episode => episode.Key.ShowId)
                .Select(show => new ShowSummary(
                    show.Key,
                    show.First().ShowTitle,
                    show.First().ShowYear,
                    show.First().Kind,
                    show.Count(episode => episode.State == EpisodeState.Missing),
                    show.Count(episode => episode.State == EpisodeState.NotAired),
                    show.Count(episode => episode.State == EpisodeState.Unavailable)))
                .OrderBy(show => show.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(show => show.ShowId),
        ];
    }
}
