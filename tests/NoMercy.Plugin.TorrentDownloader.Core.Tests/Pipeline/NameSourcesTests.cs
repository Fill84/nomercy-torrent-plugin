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
    /// <para>
    /// <strong>A name published before an episode aired is not a name for it.</strong> Asked about Dark Matter
    /// S02E04, PreDB.net answers with every Dark Matter S02E04 it has: the 2015 programme's, published between
    /// 2016 and 2023. The owner's Dark Matter is the 2024 one, whose S02E04 aired on 17 September 2026 — and
    /// on that day <c>Dark.Matter.S02E04.1080p.WEB.x264-FaiLED</c>, from July 2016, was grabbed for it.
    /// </para>
    /// <para>
    /// Every name the sources date is dated here, and a name more than a week older than the episode is left
    /// out. The same answer, for the 2015 programme's S02E04 of 22 July 2016, keeps FaiLED's name.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANamePublishedBeforeTheEpisodeAiredIsNotANameForIt()
    {
        const string asked = "https://api.predb.net/feed/?q=Dark+Matter+S02E04";

        FakeFetch fetch = new FakeFetch()
            .AnswersAnything(string.Empty)
            .Answers(asked, Capture.Fixture("names-predbnet-search-dark-matter-s02e04.xml"));

        TrackedEpisode theirs = Episode(196322, "Dark Matter", 2, 4, year: 2024) with { AirDate = new DateOnly(2026, 9, 17) };
        TrackedEpisode older = Episode(61889, "Dark Matter", 2, 4, year: 2015) with { AirDate = new DateOnly(2016, 7, 22) };

        NameSources sources = Over(fetch);
        FeedNamesTaken taken = await sources.ReadFeedsAsync([], CancellationToken.None);

        Assert.DoesNotContain(
            await sources.NamesForAsync(theirs, taken, CancellationToken.None),
            name => name.Title.StartsWith("Dark.Matter.S02E04", StringComparison.Ordinal));

        Assert.Contains(
            await Over(fetch).NamesForAsync(older, taken, CancellationToken.None),
            name => name.Title == "Dark.Matter.S02E04.1080p.WEB.x264-FaiLED");
    }

    /// <remarks>
    /// <para>
    /// <strong>A name any source dates too old is left out, whichever source gave it.</strong> On
    /// 17 September 2026 the 2015 Dark Matter's <c>Dark.Matter.S02E04.1080p.WEB.x264-FaiLED</c> was grabbed
    /// twice more for the 2024 programme's S02E04, after the rule was in: PreDB.net dated it 2016 and its copy
    /// was left out, and a copy from a source that writes no date — PreDB's RSS has none — was kept.
    /// </para>
    /// <para>
    /// A name is published once. Any source that says when is saying it for all of them. Here Silo S02E01 is
    /// taken to have aired in 2027, so every dated name for it is too old, and PreDB's undated copy of a name
    /// PreDB.net dated goes too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANameAnySourceDatesTooOldIsLeftOutWhereverItCameFrom()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnsweringSiloSearches();
        NameSources sources = Over(fetch);
        TrackedEpisode later = Episode(41, "Silo", 2, 1, year: 2023) with { AirDate = new DateOnly(2027, 6, 1) };

        FeedNamesTaken taken = await sources.ReadFeedsAsync([later], CancellationToken.None);
        IReadOnlyList<SourceName> names = await sources.NamesForAsync(later, taken, CancellationToken.None);

        Assert.Contains("Silo.S02E01.720p.WEB.H264-SYLiX", Capture.Rows("names-predb-search-silo-s02e01.xml", "rss"));
        Assert.DoesNotContain(names, name => name.Title == "Silo.S02E01.720p.WEB.H264-SYLiX");
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
