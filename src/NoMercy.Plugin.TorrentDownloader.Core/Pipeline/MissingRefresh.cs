using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Works out, from the library alone, which episodes this plugin should be
/// keeping an eye on.
/// </summary>
/// <remarks>
/// <para>
/// The whole rule, and there is no more to it: every show in every television
/// and anime library, every episode without a file, missing once it has aired.
/// No follow list, no subscription, no opt-in, no status check and no cut-off —
/// an episode that aired two years ago counts exactly as much as last night's,
/// because filling gaps backwards is what the plugin is for.
/// </para>
/// <para>
/// It derives and returns; it stores nothing and remembers nothing. What it
/// produces is compared against what is already stored by the repository, which
/// is where the plugin's own bookkeeping is kept.
/// </para>
/// </remarks>
public sealed class MissingRefresh(ILibrary library, TimeProvider time)
{
    /// <summary>
    /// Every episode that should have a row, with the state the library says it
    /// is in.
    /// </summary>
    /// <remarks>
    /// State comes from the library every time and is never carried over from
    /// what was stored. That is what makes <c>Unavailable</c> temporary: an
    /// episode that had been given up on is derived as missing again and gets
    /// another turn. 0.3.4 preserved the state instead, and an episode that
    /// went unavailable once was invisible for ever.
    /// </remarks>
    public async Task<IReadOnlyList<TrackedEpisode>> DeriveAsync(Profile profile, CancellationToken ct)
    {
        // The broadcast day, in the same shape as an air date. Comparing a date
        // against a moment would make an episode that aired this morning look
        // as though it had not aired yet.
        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        List<TrackedEpisode> tracked = [];

        foreach (Show show in await library.GetShowsAsync(ct))
        {
            IReadOnlyList<Episode> episodes = await library.GetEpisodesAsync(show.Id, ct);

            // Built from the list already fetched, so it costs no extra call —
            // and built from all of it, before anything is filtered out, because
            // a season already on disk still counts towards the next one's
            // offset. Television has no absolute numbering; giving it one would
            // put a number on a page that no release anywhere uses.
            IReadOnlyDictionary<EpisodeKey, int> absolute = show.Kind == LibraryKind.Anime
                ? AbsoluteNumbering.Build(episodes)
                : new Dictionary<EpisodeKey, int>();

            foreach (Episode episode in episodes)
            {
                if (episode.Key.IsSpecial && !profile.IncludeSpecials)
                {
                    continue;
                }

                // An episode the library has is not tracked at all: presence is
                // the absence of a row, so there is no second opinion about it
                // to go stale or disagree.
                if (episode.HasFile)
                {
                    continue;
                }

                tracked.Add(new(
                    episode.Key,
                    show.Title,
                    show.Year,
                    show.Kind,
                    episode.Title,
                    episode.AirDate,
                    HasAired(episode.AirDate, today) ? EpisodeState.Missing : EpisodeState.NotAired,
                    absolute.TryGetValue(episode.Key, out int number) ? number : null));
            }
        }

        return tracked;
    }

    /// <remarks>
    /// No date is not aired: an episode the server has no date for might be
    /// next year's. Today counts as aired — the library holds a day, and
    /// holding an episode back for a whole cycle over the hours of it would be
    /// a delay nobody could see a reason for.
    /// </remarks>
    private static bool HasAired(DateOnly? airDate, DateOnly today)
    {
        return airDate is not null && airDate.Value <= today;
    }
}
