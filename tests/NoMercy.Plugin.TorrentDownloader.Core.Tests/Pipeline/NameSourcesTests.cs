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
    /// <c>run.md</c>: a name source whose feed or search fails gives no release names in that run, and the
    /// run carries on. One feed refuses and another throws something nobody planned for; the two left
    /// still give their names. The same for the searches.
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

        Assert.Equal(["PreDB", "PreDB.net"], names.Select(name => name.Source).Distinct().Order(StringComparer.Ordinal));

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed && entry.Subject == "PreDB" && entry.Detail!.Contains("did not answer", StringComparison.Ordinal));
        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed && entry.Subject == "PreDB.net" && entry.Detail!.Contains("nobody planned for", StringComparison.Ordinal));
        Assert.Empty(journal.Snapshot().InFlight);
    }
}
