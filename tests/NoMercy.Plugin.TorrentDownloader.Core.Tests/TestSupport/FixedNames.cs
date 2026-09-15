using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The same release names for every episode, with no name source behind them.
/// </summary>
/// <remarks>
/// For a search cycle test about what happens to names once a run has them — judging, the indexers,
/// the decision. Where the names come from is <c>FeedNamesTests</c> and <c>BackfillTests</c>, against the
/// sites' own pages. Each name is one a source really published, as the test using it says.
/// </remarks>
public sealed class FixedNames(params (string Title, string Source)[] names) : IReleaseNames
{
    public Task<FeedNamesTaken> ReadFeedsAsync(IReadOnlyList<TrackedEpisode> episodes, CancellationToken ct)
    {
        return Task.FromResult(FeedNamesTaken.None);
    }

    public Task<IReadOnlyList<SourceName>> NamesForAsync(TrackedEpisode episode, FeedNamesTaken taken, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<SourceName>>([.. names.Select(name => new SourceName(name.Title, name.Source))]);
    }
}
