using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// The whole chain, over a library and sources that answer with real pages.
/// </summary>
/// <remarks>
/// <strong>H1.</strong> The real profile, the real filter, the real decider and
/// the real readers throughout. The only things stood in for are the wire and
/// the torrent client, and neither of those decides anything.
/// </remarks>
public class SearchCycleTests
{
    /// <remarks>
    /// One decision per episode, with the release, the site, the seeder count
    /// and a reason in words. The thing 0.3.4 could not answer was "what
    /// happened to this episode", and every fault it shipped hid behind that.
    /// </remarks>
    [Fact]
    public async Task EveryEpisodeGetsOneDecisionWithTheReleaseTheSiteAndTheCount()
    {
        FakeFetch fetch = Answering();
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine).RunAsync(
            [Silo(6), Silo(7)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Equal(2, report.Outcomes.Count);
        Assert.All(report.Outcomes, outcome => Assert.NotEqual(string.Empty, outcome.Detail));

        EpisodeOutcome taken = report.Outcomes.Single(outcome => outcome.Episode == Silo(6).Key);

        // The title the site announced, not the one that was searched for:
        // LimeTorrents prints this release with spaces where the scene name
        // has dots, and the copy is what it said it was.
        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES", taken.Release);
        Assert.Equal("LimeTorrents", taken.Source);
        Assert.NotNull(taken.Seeders);
        Assert.True(taken.HandedOver);

        // The magnet built from the hash the listing carried: this site
        // publishes a hashed .torrent link and no magnet at all.
        Assert.Contains(
            "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            Assert.Single(engine.Taken).Source,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(Folder, engine.Taken[0].DownloadFolder);

        // And the episode nothing has a name for says exactly that, rather than
        // disappearing from the report.
        EpisodeOutcome missing = report.Outcomes.Single(outcome => outcome.Episode == Silo(7).Key);

        Assert.Null(missing.Release);
        Assert.False(missing.HandedOver);
        Assert.Contains("name", missing.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// Every stage appears in the journal, which is what fills the dashboard's
    /// <em>Now</em>. A stage that cannot be seen does not ship, and an episode
    /// that stops moving has to be traceable to the step it stopped at.
    /// </remarks>
    [Fact]
    public async Task EveryStageOfTheChainAppearsInTheJournal()
    {
        ActivityJournal journal = new();

        await Cycle(Answering(), new(), journal).RunAsync(
            [Silo(6)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        ActivityStage[] seen = [.. journal.Snapshot().History.Select(entry => entry.Stage).Distinct()];

        Assert.Contains(ActivityStage.Names, seen);
        Assert.Contains(ActivityStage.Find, seen);
        Assert.Contains(ActivityStage.Decide, seen);
        Assert.Contains(ActivityStage.Grab, seen);

        // And nothing is left looking as though it were still running.
        Assert.Empty(journal.Snapshot().InFlight);
    }

    /// <remarks>
    /// Forty-two episodes across six seasons cost six questions per name
    /// database, end to end and not only in the resolver's own test. The pool
    /// answers the rest, and an episode nobody has a name for costs no indexer
    /// anything at all. The year form is left out of this one: a one-word title
    /// with a year is asked under both, which doubles the count honestly and is
    /// the resolver's own rule rather than this stage's.
    /// </remarks>
    [Fact]
    public async Task FortyTwoEpisodesAcrossSixSeasonsCostSixQuestionsPerNameDatabase()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        TrackedEpisode[] episodes =
        [
            .. Enumerable.Range(1, 6).SelectMany(season =>
                Enumerable.Range(1, 7).Select(number => Episode(season, number) with { ShowYear = null })),
        ];

        Assert.Equal(42, episodes.Length);

        CycleReport report = await Cycle(fetch, new()).RunAsync(
            episodes,
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Equal(42, report.Outcomes.Count);

        Assert.Equal(6, fetch.Asked.Count(address => address.Host == "api.srrdb.com"));
        Assert.Equal(6, fetch.Asked.Count(address => address.Host == "predb.me"));

        // And not one indexer was asked, because no episode had a name to ask
        // about. A search for nothing is the request 0.3.4 made forty times a
        // cycle.
        Assert.DoesNotContain(fetch.Asked, address => address.Host == "www.limetorrents.lol");
    }

    /// <remarks>
    /// With dry run on, everything is decided and nothing is handed over — and
    /// the report says what it would have taken, which is the whole point of
    /// the switch.
    /// </remarks>
    [Fact]
    public async Task WithDryRunOnNothingIsHandedOverAndTheReportSaysWhatItWouldTake()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(Answering(), engine).RunAsync(
            [Silo(6)],
            new(Wanted, Blacklist.None, DryRun: true, Folder),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES", outcome.Release);
        Assert.False(outcome.HandedOver);
        Assert.Contains("dry run", outcome.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(engine.Taken);
    }

    /// <remarks>
    /// And with no torrent client at all — which is every build until Sprint 5
    /// finishes one — it says that instead. Silence there would read as a
    /// decision nobody made.
    /// </remarks>
    [Fact]
    public async Task WithNoTorrentClientItSaysSoRatherThanSayingNothing()
    {
        CycleReport report = await Cycle(Answering(), engine: null).RunAsync(
            [Silo(6)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.False(outcome.HandedOver);
        Assert.Contains("no torrent client", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// An episode has many spellings of its release in the pool, and they are
    /// tried in turn until one produces a copy worth taking — but never more
    /// than the owner's own <c>MaxSearchAttempts</c>. Twenty spellings times
    /// seventeen indexers is a cycle that gets the plugin banned from every
    /// site it asks.
    /// </remarks>
    [Fact]
    public async Task NoMoreNamesAreSearchedForThanTheOwnerAllowsAttempts()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        // The one indexer is down, so every name costs one request and none of
        // them produces a copy.
        fetch.FailsHost("www.limetorrents.lol", FetchOutcome.Unreachable, "nothing answered");

        CycleReport report = await Cycle(fetch, new()).RunAsync(
            [Silo(6)],
            new(new() { MaximumResolution = "1080p", MaxSearchAttempts = 2 }, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Equal(2, fetch.Asked.Count(address => address.Host == "www.limetorrents.lol"));

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);
        Assert.False(outcome.HandedOver);
    }

    /// <remarks>
    /// A pack taken for one gap answers for the rest of its season, and none of
    /// them is searched for again this cycle. Without that the plugin asks
    /// every indexer for episodes already on their way, and grabs the same
    /// season once per gap.
    /// </remarks>
    [Fact]
    public async Task APackTakenForOneGapSettlesTheRestOfItsSeason()
    {
        // One real page throughout: the name databases read its titles, and
        // Nyaa — which is a real indexer answering in the same XML — reads its
        // items for the hashes.
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-diacritic.xml"));

        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine, sources: WithNyaa).RunAsync(
            [Pokemon(1), Pokemon(2), Pokemon(3)],
            new(new() { MaximumResolution = "1080p", EnglishOnly = false, SeasonPackThreshold = 3 }, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        TorrentRequest taken = Assert.Single(engine.Taken);
        Assert.StartsWith("magnet:?", taken.Source, StringComparison.Ordinal);

        Assert.Equal(
            2,
            report.Outcomes.Count(outcome =>
                outcome.Detail.Contains("settled", StringComparison.OrdinalIgnoreCase)));
    }

    /// <remarks>
    /// Every refusal from the whole chain arrives in one list, which is what
    /// the Skipped page renders.
    /// </remarks>
    [Fact]
    public async Task EveryRefusalOfTheCycleIsReportedTogether()
    {
        CycleReport report = await Cycle(Answering(), new()).RunAsync(
            [Silo(6)],
            // Wanting 2160p refuses every name the capture carries for it.
            new(new() { MaximumResolution = "2160p" }, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.NotEmpty(report.Skipped);
        Assert.All(report.Skipped, skipped => Assert.Equal(Silo(6).Key, skipped.Episode));
        Assert.All(report.Skipped, skipped => Assert.NotEqual(string.Empty, skipped.Reason));
    }

    /// <summary>Where a download would land, if anything were downloading.</summary>
    private const string Folder = @"C:\downloads";

    /// <summary>What the owner wants, at its documented defaults.</summary>
    private static Profile Wanted => new() { MaximumResolution = "1080p" };

    /// <summary>
    /// The name databases answer with a real srrDB search, and the one indexer
    /// with a real LimeTorrents listing.
    /// </summary>
    private static FakeFetch Answering()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));
        fetch.Answers(
            "https://www.limetorrents.lol/search/all/Silo.S03E06.1080p.WEB.H264-CAKES/",
            Capture.Fixture("limetorrents.html"));

        return fetch;
    }

    private static readonly SourceDefinition[] Sources =
    [
        new("srrDB search", "srrdb", "https://api.srrdb.com/v1/search/{query}") { Query = QueryStyles.Slug },
        new("PreDB", "rss", "https://predb.me/?rss=1") { SearchUrl = "https://predb.me/?search={query}&rss=1" },
        new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/")
        {
            Query = QueryStyles.Verbatim,
            Priority = 35,
        },
    ];

    /// <summary>The same sources, with a real anime indexer among them.</summary>
    private static readonly SourceDefinition[] WithNyaa =
    [
        .. Sources,
        new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}") { Priority = 30 },
    ];

    private static SearchCycle Cycle(
        FakeFetch fetch,
        FakeTorrentEngine? engine,
        ActivityJournal? journal = null,
        SourceDefinition[]? sources = null)
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(sources ?? Sources, [], []);
        ActivityJournal writing = journal ?? new ActivityJournal();
        Readers readers = Readers.Shipped();

        return new(
            new(catalogue, fetch, readers, new FakePool(), writing, TimeProvider.System),
            new(catalogue, fetch, readers, writing),
            writing,
            engine);
    }

    /// <summary>A gap in the season the captured pack covers.</summary>
    private static TrackedEpisode Pokemon(int number)
    {
        return new(
            new(2201, 5, number),
            "Pokemon Master Quest",
            null,
            LibraryKind.Anime,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing);
    }

    private static TrackedEpisode Silo(int number)
    {
        return Episode(3, number);
    }

    private static TrackedEpisode Episode(int season, int number)
    {
        return new(
            new(1399, season, number),
            "Silo",
            2023,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing);
    }
}
