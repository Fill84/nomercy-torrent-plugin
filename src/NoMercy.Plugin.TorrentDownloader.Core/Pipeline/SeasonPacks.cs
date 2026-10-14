using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>Which seasons of a run look for a pack first, and what a pack of one is held to.</summary>
/// <remarks>
/// <c>docs/specs/release-names.md</c> § Season packs — GitHub issue #1, on the owner's word of 14 October 2026,
/// which replaced the owner's rule of 15 September 2026 that a pack is not searched for. One rule for the feeds
/// and for the run, so a season whose feed pack was taken is the season the run looks for a pack of.
/// </remarks>
public static class SeasonPacks
{
    /// <summary>
    /// The seasons with two or more episodes being searched for, each as its episodes in the order given.
    /// </summary>
    /// <remarks>
    /// Two, because one gap is one episode and its own name says which file it is; a pack for it downloads a
    /// season to keep one file. Only what is searched for counts: an episode that has not aired, or that an open
    /// grab already answers for, is not in the list a run is handed.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<TrackedEpisode>> WithSeveralGaps(IEnumerable<TrackedEpisode> searched)
    {
        return
        [
            .. searched
                .Where(episode => episode.State == EpisodeState.Missing)
                .GroupBy(episode => (episode.Key.ShowId, episode.Key.Season))
                .Where(season => season.Count() >= 2)
                .Select(season => (IReadOnlyList<TrackedEpisode>)[.. season]),
        ];
    }

    /// <summary>The latest-aired episode of the episodes a pack would cover, which a pack name is dated against.</summary>
    /// <remarks>
    /// The same one-week rule as an episode's name (<see cref="SourceName.Before"/>), against the episode the pack
    /// has to hold last: a pack published more than a week before that episode aired cannot be holding it. An
    /// episode with no air date sorts first, so a season with any date is held to its latest.
    /// </remarks>
    public static TrackedEpisode Latest(IReadOnlyList<TrackedEpisode> season)
    {
        return season
            .OrderBy(episode => episode.AirDate ?? DateOnly.MinValue)
            .ThenBy(episode => episode.Key.Number)
            .Last();
    }

    /// <summary>What the Activity page and the log call a season: the show and the season tag.</summary>
    public static string Subject(TrackedEpisode episode)
    {
        return $"{episode.ShowTitle} S{episode.Key.Season:00}";
    }
}
