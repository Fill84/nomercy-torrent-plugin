using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Whether a delivery recorded before the plugin wrote down where its release came from is a name a name
/// source gives.
/// </summary>
/// <remarks>
/// The owner's rule of 15 October 2026: a grab from before this version is checked once against the name
/// sources' search, the same search a run asks, and a release they do not name marks its episode to be
/// replaced.
/// </remarks>
public sealed class DeliveredCheck(IReleaseNames names)
{
    /// <summary>
    /// Whether <paramref name="release"/> is a name the name sources give for what it covers, or null when a
    /// name source failed to answer and nothing can be said.
    /// </summary>
    /// <param name="release">What the delivery's release was called.</param>
    /// <param name="covered">The episodes it delivered: one, or a season's for a pack.</param>
    /// <param name="ct">The run's lifetime.</param>
    /// <remarks>
    /// <para>
    /// Asked of the name sources' searches, never of a feed: a delivery is days old and a feed is the last
    /// hours. A pack's release is asked the season's pack search, an episode's the episode's — exactly what a
    /// run asks for each.
    /// </para>
    /// <para>
    /// Compared as an indexer's row is compared with a name in a run (<see cref="IndexerRound.IsTheName"/>):
    /// the grab carries the title the torrent was found under, which can spell the name with other
    /// punctuation.
    /// </para>
    /// <para>
    /// <strong>A source that did not answer says nothing.</strong> Its silence is not the release being
    /// unnamed, and reading it so would replace an episode the owner has every reason to keep.
    /// </para>
    /// </remarks>
    public async Task<bool?> NamedAsync(string release, IReadOnlyList<TrackedEpisode> covered, CancellationToken ct)
    {
        FeedNamesTaken asked = FeedNamesTaken.None;

        IReadOnlyList<SourceName> found = covered.Count > 1
            ? await names.PackNamesForAsync(covered, asked, ct)
            : await names.NamesForAsync(covered[0], asked, ct);

        if (found.Any(name => IndexerRound.IsTheName(release, name.Title)))
        {
            return true;
        }

        return asked.AnyFailed ? null : false;
    }
}
