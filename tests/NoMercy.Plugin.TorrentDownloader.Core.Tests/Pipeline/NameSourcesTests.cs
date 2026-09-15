using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.NameSourceSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>Which sources give release names, and what a failing one gives.</summary>
public sealed class NameSourcesTests
{
    /// <remarks>
    /// <c>release-names.md</c>: the name sources are the same four for a show and for an anime, and Nyaa
    /// is an indexer and not a name source. With Nyaa in the catalogue, scoped to anime as it ships, an
    /// anime episode's feeds and searches still reach none of it.
    /// </remarks>
    [Fact]
    public async Task NyaaIsNotANameSourceForAnAnime()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnswersAnything(Capture.Fixture("nyaa-subsplease.xml"));
        NameSources sources = Over(fetch, sources: [.. Shipped, Nyaa]);
        TrackedEpisode anime = Episode(77, "Rilakkuma", 2, 8, year: 2019, kind: LibraryKind.Anime, absolute: 20);

        FeedNamesTaken taken = await sources.ReadFeedsAsync([anime], CancellationToken.None);

        await sources.NamesForAsync(anime, taken, CancellationToken.None);

        Assert.DoesNotContain(fetch.Asked, address => address.Host == "nyaa.si");

        // And the four were asked, so this is about which sources and not about asking none.
        Assert.Equal(
            ["api.predb.net", "api.srrdb.com", "predb.me", "www.scnsrc.me", "www.srrdb.com"],
            fetch.Asked.Select(address => address.Host).Distinct().Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// <c>run.md</c>: a site that still gives no answer after its second attempt is left out of the rest of the
    /// run. srrDB's search does not answer about Silo S02E01, so it is not asked about Silo S02E02 either;
    /// the other three are.
    /// </remarks>
    [Fact]
    public async Task ASearchThatDoesNotAnswerIsNotAskedAgainThisRun()
    {
        FakeFetch fetch = new FakeFetch()
            .AnsweringFeeds()
            .AnswersAnything(Capture.Fixture("names-srrdb-search-silo-s02e01.json"))
            .Fails(SrrDbSearch, FetchOutcome.Unreachable, "api.srrdb.com did not answer");

        NameSources sources = Over(fetch);
        TrackedEpisode first = Episode(41, "Silo", 2, 1, year: 2023);
        TrackedEpisode second = Episode(41, "Silo", 2, 2, year: 2023);

        FeedNamesTaken taken = await sources.ReadFeedsAsync([first, second], CancellationToken.None);

        await sources.NamesForAsync(first, taken, CancellationToken.None);
        await sources.NamesForAsync(second, taken, CancellationToken.None);

        Assert.Equal(1, fetch.Asked.Count(address => address.Host == "api.srrdb.com"));
        // PreDB.net's feed and both its searches share a host: three.
        Assert.Equal(3, fetch.Asked.Count(address => address.Host == "api.predb.net"));
    }

    /// <remarks>
    /// <c>run.md</c>: a name source whose feed or search fails gives no release names in that run, and the
    /// run carries on. One feed refuses and another throws something nobody planned for; the two left still
    /// give their names. Those two sources are then not searched at all, and the two whose searches fail
    /// give nothing either — the run carries on with no names for the episode and no exception.
    /// </remarks>
    [Fact]
    public async Task ASourceWhoseFeedFailsGivesNothingAndTheRunCarriesOn()
    {
        FakeFetch fetch = new FakeFetch()
            .AnsweringFeeds()
            .AnsweringSiloSearches()
            .Fails(PreDbFeed, FetchOutcome.Unreachable, "predb.me did not answer")
            .Throws(PreDbNetFeed, new InvalidOperationException("something nobody planned for"))
            .Fails(SrrDbSearch, FetchOutcome.Unreachable, "api.srrdb.com did not answer")
            .Throws(SceneSourceSearch, new InvalidOperationException("nor this"));

        ActivityJournal journal = new();
        NameSources sources = Over(fetch, journal);

        TrackedEpisode khloeFive = Episode(1, "The Girls: A Khloé Kardashian Project", 1, 5, year: 2026);
        TrackedEpisode silo = Episode(41, "Silo", 2, 1, year: 2023);

        FeedNamesTaken taken = await sources.ReadFeedsAsync([khloeFive, silo], CancellationToken.None);

        Assert.Equal(["srrDB"], taken.For(khloeFive.Key).Select(name => name.Source));

        IReadOnlyList<SourceName> names = await sources.NamesForAsync(silo, taken, CancellationToken.None);

        Assert.Empty(names);
        Assert.DoesNotContain(fetch.Asked, address => address.ToString() is PreDbSearch or PreDbNetSearch);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed && entry.Subject == "PreDB" && entry.Detail!.Contains("did not answer", StringComparison.Ordinal));
        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed && entry.Subject == "PreDB.net" && entry.Detail!.Contains("nobody planned for", StringComparison.Ordinal));
        Assert.Empty(journal.Snapshot().InFlight);
    }
}
