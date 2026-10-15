using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// A delivery from before the plugin wrote down where its release came from, checked against the name
/// sources' search.
/// </summary>
/// <remarks>
/// The owner's rule of 15 October 2026. The four name sources answer as they did that day between 11:17 and
/// 11:21 UTC, when every one of them asked for <c>South Park S29E03</c> named CAKES's WEB release and none named
/// ViETNAM's HDTV capture, which the plugin had delivered at 03:07.
/// </remarks>
public class DeliveredCheckTests
{
    private static readonly TrackedEpisode SouthPark =
        new(new(2190, 29, 3), "South Park", 1997, LibraryKind.Television, null, new DateOnly(2026, 10, 15), EpisodeState.Missing);

    /// <remarks>
    /// <strong>The release no name source names is not named</strong>, and that is what marks the episode.
    /// Sabotage: answering "named" whatever the sources say keeps the ViETNAM episode for ever.
    /// </remarks>
    [Fact]
    public async Task TheViETNAMCaptureIsANameNoSourceGives()
    {
        bool? named = await Check(Searching()).NamedAsync(
            "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM",
            [SouthPark],
            CancellationToken.None);

        Assert.False(named);
    }

    /// <remarks>
    /// <strong>The release the sources name is named</strong>, spelled as an indexer titles it — LimeTorrents
    /// lists CAKES's upload as <c>South Park S29E03 1080p WEB H264 CAKES</c> in the same morning's capture: the
    /// same letters and digits in the same order, as a row counts for a name in a run. Sabotage: comparing the
    /// title letter for letter leaves the indexer's spelling unnamed, and a real release would be replaced.
    /// </remarks>
    [Fact]
    public async Task CakessReleaseIsANameTheSourcesGiveHoweverAnIndexerSpelledIt()
    {
        bool? named = await Check(Searching()).NamedAsync(
            "South Park S29E03 1080p WEB H264 CAKES",
            [SouthPark],
            CancellationToken.None);

        Assert.True(named);
    }

    /// <remarks>
    /// <strong>A name source that does not answer leaves nothing said.</strong> Its silence is not the release
    /// being unnamed, and taking it for that would replace an episode the owner has every reason to keep —
    /// so the delivery is checked again on the next run. Sabotage: ignoring the failure reads PreDB's
    /// silence as the release not being named.
    /// </remarks>
    [Fact]
    public async Task ANameSourceThatDoesNotAnswerLeavesItUnsaid()
    {
        FakeFetch fetch = Searching().Fails(Searches[0], FetchOutcome.Unreachable, "no such host");

        bool? named = await Check(fetch).NamedAsync(
            "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM",
            [SouthPark],
            CancellationToken.None);

        Assert.Null(named);
    }

    private static string[] Searches { get; } =
    [
        "https://predb.me/?search=South+Park+S29E03&rss=1",
        "https://api.srrdb.com/v1/search/south-park-s29e03",
        "https://api.predb.net/feed/?q=South+Park+S29E03",
        "https://www.scnsrc.me/feed/?s=South+Park+S29E03",
    ];

    private static FakeFetch Searching()
    {
        return new FakeFetch()
            .Answers(Searches[0], Capture.Fixture("names-predb-search-south-park-s29e03.xml"))
            .Answers(Searches[1], Capture.Fixture("names-srrdb-search-south-park-s29e03.json"))
            .Answers(Searches[2], Capture.Fixture("names-predbnet-search-south-park-s29e03.xml"))
            .Answers(Searches[3], Capture.Fixture("names-scenesource-search-south-park-s29e03.xml"));
    }

    private static DeliveredCheck Check(FakeFetch fetch)
    {
        return new(NameSourceSites.Over(fetch));
    }
}
