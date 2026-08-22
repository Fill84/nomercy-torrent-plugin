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

        // The name a name database published, not the site's rendering of it.
        // LimeTorrents prints this release with spaces where the scene name has
        // dots; it is one release with one name, and the name is srrDB's. It is
        // written against the grab and is what staging matches a finished file
        // by, so a site's spelling of it is a name nothing answers to.
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", taken.Release);
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

        // And the episode nobody is serving says exactly that, rather than
        // disappearing from the report. It was asked about: the indexer was put
        // the question and answered with a page carrying nothing for it, which
        // is a different thing from never having been asked.
        EpisodeOutcome missing = report.Outcomes.Single(outcome => outcome.Episode == Silo(7).Key);

        Assert.Null(missing.Release);
        Assert.False(missing.HandedOver);
        Assert.True(missing.Searched);
        Assert.Contains("nothing anybody is serving", missing.Detail, StringComparison.OrdinalIgnoreCase);
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
    /// answers the rest. The year form is left out of this one: a one-word
    /// title with a year is asked under both, which doubles the count honestly
    /// and is the resolver's own rule rather than this stage's.
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

        // And the indexer was asked about every one of them, because an episode
        // nothing has a name for is still an episode a search engine can be
        // asked about by number. While it was not, five gaps of the owner's own
        // Silo season three were never put to a single site: two of them had no
        // name in the pool at all, and every indexer was carrying the release.
        Assert.Equal(42, fetch.Asked.Count(address =>
            address.Host == "www.limetorrents.lol"
            && address.AbsolutePath.Contains('E', StringComparison.Ordinal)));
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

        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", outcome.Release);
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

    /// <remarks>
    /// The store has to be able to find the torrent again after a restart and
    /// to know which episodes to put back if it fails, and neither is anywhere
    /// in "taken from Nyaa". A season pack that fails puts back every gap it
    /// answered for, which is the whole reason the coverage travels with it.
    /// </remarks>
    [Fact]
    public async Task AGrabCarriesTheHashTheMagnetAndEveryEpisodeItAnswersFor()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-diacritic.xml"));

        CycleReport report = await Cycle(fetch, new(), sources: WithNyaa).RunAsync(
            [Pokemon(1), Pokemon(2), Pokemon(3)],
            new(new() { MaximumResolution = "1080p", EnglishOnly = false, SeasonPackThreshold = 3 }, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        EpisodeOutcome taken = Assert.Single(report.Outcomes, outcome => outcome.HandedOver);

        Assert.NotNull(taken.InfoHash);
        Assert.StartsWith("magnet:?", taken.Magnet!, StringComparison.Ordinal);

        // All three gaps, because it is a pack and the season had three.
        Assert.Equal(3, taken.Covers.Count);
        Assert.Contains(Pokemon(2).Key, taken.Covers);
    }

    /// <remarks>
    /// The room check is in <c>Grab</c> and the cycle went round it, straight to
    /// the client. A torrent that fills the disk takes the media server with it,
    /// since the same disk holds the library and the database — so the cycle has
    /// to hand over through the thing that checks, not beside it.
    /// </remarks>
    [Fact]
    public async Task ACycleWithNoRoomLeftHandsNothingOverAndSaysBothNumbers()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(Answering(), engine, free: 1024).RunAsync(
            [Silo(6)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Empty(engine.Taken);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.False(outcome.HandedOver);

        // Both numbers, because "not enough space" tells the owner nothing they
        // can act on and these two say exactly what to clear.
        Assert.Contains("needs", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("free", outcome.Detail, StringComparison.Ordinal);
    }


    /// <remarks>
    /// <strong>A3 was wrong, and this is the test that proves it.</strong> The
    /// rule said an indexer is asked the full release name and nothing else. On
    /// 22 August 2026 the real library asked apibay for
    /// <c>Silo S03E08 1080p WEB H264 CAKES</c> and it answered
    /// <em>No results returned</em>; the same site answers
    /// <c>Silo S03E08</c> with twelve rows, the first of them seeded by six
    /// thousand. Both captures are in tests/fixtures. A search engine is asked
    /// what it can answer, and what comes back is judged by the profile — which
    /// is the protection A3 was really asking for and which 0.3.4 did not have.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeIsAskedForByItsOwnNumberSoASiteCanAnswerIt()
    {
        FakeFetch fetch = new();

        // Nothing has a name for it: the pool is empty and the name databases
        // answer with a feed carrying nothing at all.
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        fetch.Answers(
            "https://apibay.org/q.php?q=Silo+S03E08&cat=",
            Capture.Fixture("the-pirate-bay-episode.json"));

        CycleReport report = await Cycle(fetch, new(), sources: WithPirateBay).RunAsync(
            [Silo(8)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("Silo S03E08 1080p HEVC x265-MeGusta", outcome.Release);
        Assert.Equal(6372, outcome.Seeders);
    }

    /// <remarks>
    /// A site asked about one gap answers with the whole programme, and the
    /// other gaps of this cycle are in that answer. Throwing them away is what
    /// the owner saw on 22 August 2026: four 1080p copies of Silo S03E04 to
    /// S03E07 came back from a search for S03E08, every one of them an episode
    /// the library was missing, and every one recorded as refused for not being
    /// S03E08.
    /// </remarks>
    [Fact]
    public async Task ACopyThatAnswersAnotherGapOfThisCycleIsGivenToIt()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        // The whole programme, which is what this site answers when it is asked
        // for one: a hundred rows from S01E01 to S03E08, with hashes and
        // seeders on all of them.
        fetch.Answers(
            "https://apibay.org/q.php?q=Silo+S03E04&cat=",
            Capture.Fixture("the-pirate-bay-show.json"));

        CycleReport report = await Cycle(fetch, new(), sources: WithPirateBay).RunAsync(
            [Silo(4), Silo(5), Silo(6), Silo(7)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Equal(4, report.Outcomes.Count);
        Assert.All(report.Outcomes, outcome => Assert.True(outcome.HandedOver, outcome.Detail));

        // Every gap is still asked about - what an earlier search turned up is
        // a candidate and never an answer, which is
        // WhatAnEarlierSearchTurnedUpDoesNotStopThisOneBeingMade. Only S03E04's
        // address is scripted here, so the three that follow are answered with
        // a page carrying nothing for them, and they are taken anyway: out of
        // what the first search brought back and would otherwise have thrown
        // away.
        Assert.Equal(4, fetch.Asked.Count(address => address.Host == "apibay.org"));
    }

    /// <remarks>
    /// A row that came back for another episode is not a refusal. It was never
    /// offered for this one — a search engine answered broadly — and recording
    /// it as refused is what filled the Skipped page with
    /// "'Silo S03E04 …' is not S03E08" and buried the reasons the page exists
    /// for.
    /// </remarks>
    [Fact]
    public async Task ARowForAnotherEpisodeIsNotRecordedAsARefusal()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));
        fetch.Answers(
            "https://apibay.org/q.php?q=Silo+S03E08&cat=",
            Capture.Fixture("the-pirate-bay-show.json"));

        CycleReport report = await Cycle(fetch, new(), sources: WithPirateBay).RunAsync(
            [Silo(8)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.DoesNotContain(
            report.Skipped,
            skipped => skipped.Reason.Contains("is not S03E08", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The best copy is the one with the most seeders, and the site with the
    /// most seeders is the one whose magnet is hardest to get: TorrentBay's
    /// comes from a signed request this plugin does not make, so every row it
    /// answers with is unreachable. On 22 August 2026 it outranked everything
    /// for Silo S03E08, the cycle followed it, found no torrent and stopped —
    /// with a copy of the same episode from another site sitting unexamined.
    /// </remarks>
    [Fact]
    public async Task WhenTheBestCopyNamesNoTorrentTheNextOneIsTaken()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        // TorrentGalaxy publishes neither a magnet nor a hash, and its rows'
        // own pages are unreachable in this test — so every copy it offers is
        // a dead end, and it is asked first because it answers with the higher
        // count.
        fetch.Answers(
            "https://torrentgalaxy.one/get-posts/keywords:Silo%20S03E07/",
            Capture.Fixture("torrentgalaxy.html"));

        fetch.Answers(
            "https://apibay.org/q.php?q=Silo+S03E07&cat=",
            Capture.Fixture("the-pirate-bay-show.json"));

        CycleReport report = await Cycle(fetch, new(), sources: WithGalaxyAndPirateBay).RunAsync(
            [Silo(7)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("The Pirate Bay", outcome.Source);
    }

    /// <remarks>
    /// <strong>What an earlier search happened to turn up is a candidate, never
    /// an answer.</strong> The cycle kept every copy it had seen and tried that
    /// stack before searching, and took the first acceptable thing in it — so
    /// an episode could be settled by a leftover from another episode's search
    /// without one indexer being asked about it.
    ///
    /// The owner watched it happen on 22 August 2026. Sugar S02E08 was settled
    /// from the stack by a FLUX release, and
    /// <c>Sugar 2024 S02E08 1080p WEB H264-CAKES</c> — the top row on both
    /// TorrentBay and The Pirate Bay, at 483 and 458 seeders — was never
    /// fetched at all, because nothing ever asked for that episode.
    ///
    /// The stack now adds to what a search brings back and decides nothing on
    /// its own.
    /// </remarks>
    [Fact]
    public async Task WhatAnEarlierSearchTurnedUpDoesNotStopThisOneBeingMade()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        // The whole programme, which is what the first gap's search brings
        // back: a hundred rows covering every episode of it.
        fetch.Answers(
            "https://apibay.org/q.php?q=Silo+S03E04&cat=",
            Capture.Fixture("the-pirate-bay-show.json"));

        // And S03E08's own answer, which carries the copy that is actually
        // best seeded — six thousand of them.
        fetch.Answers(
            "https://apibay.org/q.php?q=Silo+S03E08&cat=",
            Capture.Fixture("the-pirate-bay-episode.json"));

        CycleReport report = await Cycle(fetch, new(), sources: WithPirateBay).RunAsync(
            [Silo(4), Silo(8)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        EpisodeOutcome last = report.Outcomes.Single(outcome => outcome.Episode == Silo(8).Key);

        // Asked, rather than settled out of what the first gap's search left
        // lying about.
        Assert.Contains(fetch.Asked, address => address.Query.Contains("Silo+S03E08", StringComparison.Ordinal));

        // And the copy taken is the best-seeded one anybody offered, which is
        // only in that answer.
        Assert.True(last.HandedOver, last.Detail);
        Assert.Equal(6372, last.Seeders);
    }

    /// <remarks>
    /// <strong>One term is asked once.</strong> The programme's own name is a
    /// term every gap of that programme falls through to, so eight gaps asked
    /// every indexer the identical question eight times — and apibay, which
    /// rate-limits hard, answered 429 to the ninth. The answer to a question
    /// already asked this cycle is the answer already in hand.
    ///
    /// Not a short cut: it saves the <em>request</em> and never the decision.
    /// Every gap is still decided over everything, which is
    /// <see cref="WhatAnEarlierSearchTurnedUpDoesNotStopThisOneBeingMade"/>.
    /// </remarks>
    [Fact]
    public async Task ATermAlreadyAskedThisCycleIsNotAskedAgain()
    {
        FakeFetch fetch = new();

        // The name databases have nothing, and neither has either episode's own
        // number - a real page with no rows on it.
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        // The programme's own name, which both gaps fall through to.
        fetch.Answers(
            "https://www.limetorrents.lol/search/all/Silo/",
            Capture.Fixture("limetorrents.html"));

        CycleReport report = await Cycle(fetch, new()).RunAsync(
            [Silo(6), Silo(7)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Equal(2, report.Outcomes.Count);

        Assert.Equal(
            1,
            fetch.Asked.Count(address =>
                address.Host == "www.limetorrents.lol" && address.AbsolutePath == "/search/all/Silo/"));
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

    /// <summary>
    /// One indexer, and it is the real one whose two captures prove what a
    /// search term does: The Pirate Bay's own JSON, hashes and seeders and all.
    /// </summary>
    private static readonly SourceDefinition[] WithPirateBay =
    [
        new("srrDB search", "srrdb", "https://api.srrdb.com/v1/search/{query}") { Query = QueryStyles.Slug },
        new("PreDB", "rss", "https://predb.me/?rss=1") { SearchUrl = "https://predb.me/?search={query}&rss=1" },
        new("The Pirate Bay", "apibay", "https://apibay.org/q.php?q={query}&cat=") { Priority = 45 },
    ];

    /// <summary>
    /// The same, with a site that publishes no route to a torrent at all in
    /// front of it - which is the arrangement that stopped every download on
    /// 22 August 2026.
    /// </summary>
    private static readonly SourceDefinition[] WithGalaxyAndPirateBay =
    [
        .. WithPirateBay,
        new("TorrentGalaxy", "site", "https://torrentgalaxy.one/get-posts/keywords:{query}/")
        {
            Reader = "torrentgalaxy",
            Query = QueryStyles.Spaced,
            Priority = 30,
        },
    ];

    private static SearchCycle Cycle(
        FakeFetch fetch,
        FakeTorrentEngine? engine,
        ActivityJournal? journal = null,
        SourceDefinition[]? sources = null,
        long? free = null)
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(sources ?? Sources, [], []);
        ActivityJournal writing = journal ?? new ActivityJournal();
        Readers readers = Readers.Shipped();

        return new(
            new(catalogue, fetch, readers, new FakePool(), writing, TimeProvider.System),
            new(catalogue, fetch, readers, writing),
            writing,

            // Through the grab, which is what checks there is room. A cycle that
            // called the client directly went round that check.
            engine is null ? null : new Grab(engine, new EndlessDisk(free), writing));
    }

    /// <summary>A disk with as much room as the test says, or as much as anyone could want.</summary>
    private sealed class EndlessDisk(long? free) : IStorageSpace
    {
        public long? FreeBytes(string folder)
        {
            return free ?? long.MaxValue;
        }
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
