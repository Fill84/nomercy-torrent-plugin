using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Asking every indexer who is serving one release.
/// </summary>
/// <remarks>
/// Every page here is one a site really sent, detail pages included: those were
/// captured through the same tool, at the address a row on the listing really
/// carried.
/// </remarks>
public class FindTests
{
    /// <remarks>
    /// <strong>A3.</strong> An indexer is asked the full release name and
    /// nothing else. 0.3.4 searched them for <c>Silo S03E06</c>, which
    /// sometimes worked — and the times it did hid the times it did not.
    /// </remarks>
    [Fact]
    public async Task EveryIndexerIsAskedTheFullReleaseName()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("limetorrents.html"));

        await Finding(fetch).SearchAsync("Silo.S03E06.1080p.WEB.H264-CAKES", CancellationToken.None);

        Assert.NotEmpty(fetch.Asked);

        // The parts only the full name has, whichever way a site wants its
        // terms written: one joins them with plus signs, one with dashes.
        foreach (string part in (string[])["1080p", "cakes"])
        {
            Assert.All(
                fetch.Asked,
                address => Assert.Contains(part, Uri.UnescapeDataString(address.ToString()), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <remarks>
    /// Every indexer at once. Asked one after another, a search costs the sum
    /// of the slowest sites and there are one per episode of a cycle.
    /// </remarks>
    [Fact]
    public async Task EveryIndexerIsAskedAtOnceRatherThanOneAfterAnother()
    {
        FakeTimeProvider clock = new();
        FakeFetch fetch = new(clock);

        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"), TimeSpan.FromSeconds(5));
        fetch.Answers(Address("TorrentFunk"), Capture.Fixture("torrentfunk.html"), TimeSpan.FromSeconds(3));
        fetch.Answers(Address("TorrentDownloads"), Capture.Fixture("torrentdownloads.html"), TimeSpan.FromSeconds(1));

        Task run = Finding(fetch, clock).SearchAsync(Name, CancellationToken.None);

        await Task.WhenAny(fetch.AllInFlight, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            fetch.AllInFlight.IsCompletedSuccessfully,
            $"Only {fetch.InFlight} of {fetch.Expected} indexers were in flight, so they were asked one at a time.");

        clock.Advance(TimeSpan.FromSeconds(5));

        await run;
    }

    /// <remarks>
    /// The same torrent on five sites is one torrent with five sets of
    /// trackers. More trackers is a faster download, which is the whole reason
    /// every indexer is asked rather than the first one that answers.
    /// </remarks>
    [Fact]
    public void TheSameHashFromEverySiteIsOneReleaseWithEveryTrackerAndTheBestCount()
    {
        const string Hash = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

        ReleaseCopy[] copies =
        [
            new(Name, "LimeTorrents", 35, Hash, $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Fone.example%3A80", null, 4, 1_000),
            new(Name, "The Pirate Bay", 45, Hash, $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Ftwo.example%3A80", null, 40, 1_000),
            new(Name, "TorrentFunk", 25, Hash, $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Fone.example%3A80", null, 12, 1_000),
        ];

        ReleaseCopy merged = Assert.Single(Find.Merge(copies));

        Assert.Equal(40, merged.Seeders);
        Assert.Equal("The Pirate Bay", merged.Source);
        Assert.Equal(["udp://one.example:80", "udp://two.example:80"], merged.Trackers.Order());
    }

    /// <remarks>
    /// A copy with no hash is not merged into anything. Nothing says two rows
    /// with the same title are the same torrent, and taking them as one would
    /// hand the trackers of one file to another.
    /// </remarks>
    [Fact]
    public void CopiesWithNoHashAreLeftAsTheyAre()
    {
        ReleaseCopy[] copies =
        [
            new(Name, "TorrentGalaxy", 30, null, null, new("https://torrentgalaxy.one/1"), 9),
            new(Name, "Torrentz2", 25, null, null, new("https://torrentz2.nz/2"), 9),
        ];

        Assert.Equal(2, Find.Merge(copies).Count);
    }

    /// <remarks>
    /// <strong>C3.</strong> No shipped indexer publishes a magnet on its
    /// listing, so the row's own page is the only route to one — and 0.3.4
    /// wrote that address and read it nowhere, so TorrentBay produced rows for
    /// weeks and zero downloads. The page here is a real TorrentFunk detail
    /// page, which carries no magnet at all and prints the bare hash.
    /// </remarks>
    [Fact]
    public async Task ARowWithNoMagnetIsFollowedToItsOwnPage()
    {
        FakeFetch fetch = new();
        fetch.Answers(Detail, Capture.Fixture("torrentfunk-detail.html"));

        ReleaseCopy chosen = new(Name, "TorrentFunk", 25, null, null, new(Detail), 9);

        ReleaseCopy followed = await Finding(fetch).FollowAsync(chosen, CancellationToken.None);

        Assert.Equal("60207FB3AE7877C8C76DDD27A07C385E5047783C", followed.InfoHash);
        Assert.StartsWith(
            "magnet:?xt=urn:btih:60207FB3AE7877C8C76DDD27A07C385E5047783C",
            followed.Magnet,
            StringComparison.Ordinal);

        Assert.Single(fetch.Asked);
    }

    /// <remarks>
    /// And a detail page that does publish a magnet is read for it, trackers
    /// and all. This one is a real LimeTorrents page, at the address its own
    /// listing row carried.
    /// </remarks>
    [Fact]
    public async Task ADetailPageThatPublishesAMagnetIsReadForIt()
    {
        const string Page = "https://www.limetorrents.lol/Silo-S03E06-1080p-WEB-H264-CAKES-torrent-19877003.html";

        FakeFetch fetch = new();
        fetch.Answers(Page, Capture.Fixture("limetorrents-detail.html"));

        ReleaseCopy followed = await Finding(fetch).FollowAsync(
            new(Name, "LimeTorrents", 35, null, null, new(Page), 12),
            CancellationToken.None);

        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", followed.InfoHash);
        Assert.NotEmpty(followed.Trackers);
    }

    /// <remarks>
    /// Once, and only for the release that was chosen. Following every row of
    /// every answer is a request per row per episode, which is the shape of
    /// thing that gets a plugin banned from a site.
    /// </remarks>
    [Fact]
    public async Task ACopyThatAlreadyHasAMagnetIsNotFollowedAtAll()
    {
        FakeFetch fetch = new();

        ReleaseCopy already = new(
            Name,
            "LimeTorrents",
            35,
            "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            new(Detail),
            12);

        Assert.Same(already, await Finding(fetch).FollowAsync(already, CancellationToken.None));
        Assert.Empty(fetch.Asked);
    }

    /// <remarks>
    /// <strong>B4.</strong> Between two acceptable copies the higher-rated
    /// indexer wins, asserted with priorities that differ. 0.3.4 sorted the
    /// other way and took the worst-rated site every time two copies were level
    /// on seeders, which is most of the time — and a test enshrined it.
    /// </remarks>
    [Fact]
    public async Task BetweenTwoAcceptableCopiesTheHigherRatedIndexerWins()
    {
        FakeFetch fetch = new();
        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.Answers(Address("TorrentDownloads"), Capture.Fixture("torrentdownloads.html"));

        IReadOnlyList<ReleaseCopy> copies = await Finding(fetch).SearchAsync(Name, CancellationToken.None);

        // Both sites answered, and the ranking is the decider's own.
        Assert.Contains(copies, copy => copy.Source == "LimeTorrents");
        Assert.Contains(copies, copy => copy.Source == "TorrentDownloads");

        ReleaseCopy[] level =
        [
            .. copies
                .Where(copy => copy.Source is "LimeTorrents" or "TorrentDownloads")
                .Take(1)
                .Select(copy => copy with { Seeders = 10, Source = "TorrentDownloads", Priority = 25 }),
            .. copies
                .Where(copy => copy.Source is "LimeTorrents" or "TorrentDownloads")
                .Take(1)
                .Select(copy => copy with { Seeders = 10, Source = "LimeTorrents", Priority = 35 }),
        ];

        Decision decision = new ReleaseDecider(new() { MinimumSeeders = 2 }).Decide(level, Blacklist.None);

        Assert.Equal("LimeTorrents", decision.Chosen!.Source);
    }

    /// <remarks>
    /// One indexer being down is one indexer being down. Every other site still
    /// answers, and the episode is not lost because a site was.
    /// </remarks>
    [Fact]
    public async Task OneIndexerThatFailsDoesNotTakeTheSearchDown()
    {
        FakeFetch fetch = new();
        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.FailsHost("www.torrentfunk.com", FetchOutcome.RateLimited, "www.torrentfunk.com answered 429");
        fetch.Answers(Address("TorrentDownloads"), Capture.Fixture("torrentdownloads.html"));

        ActivityJournal journal = new();

        IReadOnlyList<ReleaseCopy> copies = await Finding(fetch, journal: journal)
            .SearchAsync(Name, CancellationToken.None);

        Assert.NotEmpty(copies);
        Assert.DoesNotContain(copies, copy => copy.Source == "TorrentFunk");

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Find
                     && entry.Outcome == ActivityOutcome.Failed
                     && entry.Subject.Contains("TorrentFunk", StringComparison.Ordinal));

        Assert.Empty(journal.Snapshot().InFlight);
    }

    private const string Name = "Silo.S03E06.1080p.WEB.H264-CAKES";

    private const string Detail = "https://www.torrentfunk.com/torrent/50533062/silo-s03e06.html";

    /// <summary>Three real indexers, as the catalogue has them.</summary>
    private static readonly SourceDefinition[] Indexers =
    [
        new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/") { Priority = 35 },
        new("TorrentFunk", "site", "https://www.torrentfunk.com/all/torrents/{query}.html")
        {
            Reader = "torrentfunk",
            Query = QueryStyles.Slug,
            Priority = 25,
        },
        new("TorrentDownloads", "site", "https://www.torrentdownloads.pro/search/?search={query}")
        {
            Reader = "torrentdownloads",
            Priority = 25,
        },
    ];

    private static string Address(string source)
    {
        SourceDefinition indexer = Indexers.Single(one => one.Name == source);

        return Query.Write(indexer.SearchAddress!, Name, indexer.Query);
    }

    private static Find Finding(FakeFetch fetch, TimeProvider? clock = null, ActivityJournal? journal = null)
    {
        _ = clock;

        return new(
            SourceCatalogue.Build(Indexers, [], []),
            fetch,
            Readers.Shipped(),
            journal ?? new ActivityJournal());
    }
}
