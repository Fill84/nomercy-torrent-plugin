namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Where each episode sits counted from the start of the series, rather than
/// from the start of its season.
/// </summary>
/// <remarks>
/// <para>
/// Anime releases are usually numbered this way: episode 13 of season 2 is
/// released as <c>- 37</c>. The library numbers by season, so both forms have
/// to be searchable or half of what exists is invisible to the plugin.
/// </para>
/// <para>
/// Built from the episode list the pipeline already fetched, so it costs no
/// extra call to the library — one map per show per cycle.
/// </para>
/// </remarks>
public static class AbsoluteNumbering
{
    /// <summary>
    /// The absolute number of every episode in <paramref name="episodes"/>,
    /// which are expected to be one show's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Season 0 is neither counted nor numbered. Specials are not in the
    /// absolute sequence, and if they counted, every episode after them would
    /// be out by however many specials the show happened to have — an error
    /// that differs per show and reads as bad luck rather than as a rule.
    /// </para>
    /// <para>
    /// A season with a hole in it is counted from the episodes that are there.
    /// It is the only honest answer available: nothing here can tell an episode
    /// that exists and was never imported from one that never existed, and
    /// assuming the larger number would shift every later season by one.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<EpisodeKey, int> Build(IReadOnlyList<Episode> episodes)
    {
        Episode[] numbered = [.. episodes.Where(episode => !episode.Key.IsSpecial)];

        // How long each season is, which is all the earlier seasons contribute.
        // Whether an episode is already on disk has nothing to do with where it
        // sits in the series, so every episode counts: numbering only what is
        // missing would renumber the show each time something downloaded.
        Dictionary<int, int> lengths = numbered
            .GroupBy(episode => episode.Season)
            .ToDictionary(season => season.Key, season => season.Count());

        Dictionary<EpisodeKey, int> absolute = [];

        foreach (Episode episode in numbered)
        {
            // The episode's own number plus everything before its season — not
            // its position in the list. Those are the same only while the list
            // is complete, and they part company exactly when the library is
            // missing rows, which is the case this plugin exists for.
            int before = lengths
                .Where(season => season.Key < episode.Season)
                .Sum(season => season.Value);

            absolute[episode.Key] = episode.Number + before;
        }

        return absolute;
    }
}
