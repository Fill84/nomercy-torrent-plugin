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
/// <strong>H1.</strong> Real show settings, the real judge, the real decider and
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
            engine.Taken[0].Source,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(Folder, engine.Taken[0].DownloadFolder);

        // And only the winner: this page carries the release under a second
        // hash, and no other hash of it is started (indexer-search.md).
        Assert.Single(engine.Taken);

        // And the episode nobody is serving says exactly that, rather than
        // disappearing from the report. No name source gave a release name for
        // it, so no indexer was asked (release-names.md: no release, no search),
        // and it does not count as searched.
        EpisodeOutcome missing = report.Outcomes.Single(outcome => outcome.Episode == Silo(7).Key);

        Assert.Null(missing.Release);
        Assert.False(missing.HandedOver);
        Assert.False(missing.Searched);
        Assert.Equal("no name source gave a release name for it", missing.Detail);
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
    /// <para>
    /// Forty-two episodes are looked up one at a time, end to end and not only
    /// in the name sources' own test. No feed names any of them, so each is
    /// looked up in every name source's search, asked show and episode once
    /// (docs/specs/release-names.md § Backfill): forty-two questions a source.
    /// </para>
    /// <para>
    /// It was six: one question per season. That saving is what hid the release
    /// the owner wanted, because a source asked about a season answers with the
    /// season and the episode's own releases fall past the cut.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryEpisodeIsAskedAboutOnItsOwnAtEveryNameDatabase()
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

        Assert.Equal(42, fetch.Asked.Count(address => address.Host == "api.srrdb.com"));

        // And PreDB's feed once, at the start of the run, besides.
        Assert.Equal(43, fetch.Asked.Count(address => address.Host == "predb.me"));

        // And not one indexer was asked about any of them: no name source gave a
        // release name, and an indexer is searched only with one
        // (release-names.md § No release, no search).
        Assert.DoesNotContain(fetch.Asked, address => address.Host == "www.limetorrents.lol");
        Assert.All(report.Outcomes, outcome => Assert.Equal("no name source gave a release name for it", outcome.Detail));
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
    /// Every refusal from the whole chain arrives in one list, which is what
    /// the Skipped page renders.
    /// </remarks>
    [Fact]
    public async Task EveryRefusalOfTheCycleIsReportedTogether()
    {
        CycleReport report = await Cycle(Answering(), new()).RunAsync(
            [Silo(6)],
            // Wanting 2160p refuses every name the capture carries for it.
            new(AtQuality("2160p"), Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.NotEmpty(report.Skipped);
        Assert.All(report.Skipped, skipped => Assert.Equal(Silo(6).Key, skipped.Episode));
        Assert.All(report.Skipped, skipped => Assert.NotEqual(string.Empty, skipped.Reason));
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
            "https://apibay.org/q.php?q=Silo+S03E08+1080p&cat=",
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
    /// <para>
    /// <strong>Stage 3 of docs/03-architecture.md: the settings applied to
    /// names.</strong> It was never built. Every name the sources gave was put
    /// to every indexer and only the rows that came back were judged, so a name
    /// the owner could never accept cost a request at every site that carries
    /// the show and had all of it thrown away one step later.
    /// </para>
    /// <para>
    /// The name here is the owner's own, off their own dashboard on 2 September
    /// 2026, and PreDB really does publish it — their pool held 2,238 names in
    /// other languages. It is a correct scene release, the show here forbids
    /// the tag German, and no indexer should be asked about it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ANameTheShowsSettingsRefuseIsNeverPutToAnIndexer()
    {
        FixedNames pool = new(
            ("Silo.S03E06.German.DL.AC3D.1080p.BluRay.x264-JaJunge", "PreDB"),
            ("Silo.S03E06.1080p.WEB.H264-CAKES", "srrDB"));

        FakeFetch fetch = Answering();

        await Cycle(fetch, new(), names: pool).RunAsync(
            [Silo(6)],
            new(AtQuality("1080p", forbidden: ["German"]), Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        // Not one request anywhere carries it — not to an indexer, not to
        // anybody. Asking cost four sites a paced request each on the owner's
        // own server.
        Assert.DoesNotContain(
            fetch.Asked,
            address => address.OriginalString.Contains("JaJunge", StringComparison.OrdinalIgnoreCase)
                       || address.OriginalString.Contains("German", StringComparison.OrdinalIgnoreCase));

        // And the name that is wanted was put to the indexer, so this is a rule
        // about which names are asked for and not about asking for none.
        Assert.Contains(
            fetch.Asked,
            address => address.OriginalString.Contains("Silo.S03E06.1080p.WEB.H264-CAKES", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <c>docs/specs/release-names.md</c> § No release, no search: an episode for which no name source gave a
    /// release name that meets its show's settings is not searched on any indexer, and the plugin builds no
    /// search term of its own. The only name here is German and the show forbids the tag; not one indexer
    /// is asked anything, and the episode says why.
    /// </remarks>
    [Fact]
    public async Task NothingIsAskedOfAnIndexerWhenNoNameMeetsTheSettings()
    {
        FixedNames names = new(("Silo.S03E06.German.DL.AC3D.1080p.BluRay.x264-JaJunge", "PreDB"));

        FakeFetch fetch = Answering();

        CycleReport report = await Cycle(fetch, new(), names: names).RunAsync(
            [Silo(6)],
            new(AtQuality("1080p", forbidden: ["German"]), Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Empty(fetch.Asked);
        Assert.Contains("German", Assert.Single(report.Outcomes).Detail, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// The stage rows and the lines under each episode are what the run really
    /// did — the owner asked on 11 September 2026 for the dashboard to show
    /// literally that. Counted and noted as it happens: which source was asked
    /// and what it said, and every question put to every indexer with the rows
    /// it came back with.
    /// </para>
    /// <para>
    /// Collected while the run goes, because an episode's lines are cleared the
    /// moment it is decided.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryQuestionTheRunPutsIsCountedAndNoted()
    {
        FakeFetch fetch = Answering();
        ActivityJournal journal = new();

        List<string> noted = [];
        journal.Recorded += () =>
        {
            string[] lines = [.. journal.Snapshot().Notes.Select(note => note.Line)];

            lock (noted)
            {
                noted.AddRange(lines);
            }
        };

        journal.RunStarted();

        await Cycle(fetch, new(), journal).RunAsync(
            [Silo(6)],
            new(Wanted, Blacklist.None, DryRun: true, Folder),
            CancellationToken.None);

        SearchProgress run = journal.Snapshot().Run!;

        // Every search question the indexer was really sent, and no more.
        Assert.Equal(
            fetch.Asked.Count(address => address.AbsolutePath.StartsWith("/search/", StringComparison.Ordinal)),
            run.Count(RunCounter.Questions));
        Assert.True(run.Count(RunCounter.QuestionsAnswered) > 0);

        Assert.Equal(1, run.Count(RunCounter.Episodes));
        Assert.Equal(1, run.Count(RunCounter.EpisodesAsked));
        Assert.True(run.Count(RunCounter.NamesFound) > 0);
        Assert.Equal(1, run.Count(RunCounter.Decided));

        // What a source was asked, and the exact name put to an indexer.
        Assert.Contains(noted, line => line.StartsWith("srrDB search · Silo S03E06 · ", StringComparison.Ordinal));
        Assert.Contains(noted, line => line.StartsWith("LimeTorrents · Silo.S03E06.1080p.WEB.H264-CAKES · ", StringComparison.Ordinal));
    }

    /// <summary>Where the first request matching this appears, or -1.</summary>
    private static int Order(FakeFetch fetch, Func<Uri, bool> matches)
    {
        for (int at = 0; at < fetch.Asked.Count; at++)
        {
            if (matches(fetch.Asked[at]))
            {
                return at;
            }
        }

        return -1;
    }

    /// <remarks>
    /// <para>
    /// <strong>An episode that has been decided is handed to the client before
    /// the next one is asked about.</strong> The owner's rule of 11 September
    /// 2026: the run goes on over everything, but a torrent that has been found
    /// — merged across every site that has it, with all their trackers — is
    /// started at once and waits for nothing else.
    /// </para>
    /// <para>
    /// It waited for every other episode's name. The sources were asked about
    /// the whole queue before any episode was searched for, and once each
    /// source was asked per episode and paced itself, that was most of an hour
    /// on a cycle whose pool was empty before the first download could start.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EachEpisodesWinnerIsOfferedBeforeTheNextEpisodeIsWorkedOn()
    {
        FakeFetch fetch = Answering();
        FakeTorrentEngine engine = new();
        ActivityJournal journal = new();

        await Cycle(fetch, engine, journal).RunAsync(
            [Silo(6), Silo(7)],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        ActivityEvent[] history = [.. journal.Snapshot().History];

        int handedOver = Array.FindIndex(history, entry =>
            entry.Stage == ActivityStage.Grab
            && entry.Outcome == ActivityOutcome.Finished
            && entry.Subject == "Silo S03E06");

        int askedAboutTheNext = Array.FindIndex(history, entry =>
            entry.Stage == ActivityStage.Names
            && entry.Outcome == ActivityOutcome.Started
            && entry.Subject == "Silo S03E07");

        Assert.True(handedOver >= 0, "the first episode was never handed to the client");

        // Either the next episode was asked about after the first was handed
        // over, or the first episode's answer already named it and it was not
        // asked about at all. What must never happen is the next episode's
        // question holding the first episode's download back.
        Assert.True(
            askedAboutTheNext < 0 || askedAboutTheNext > handedOver,
            "the next episode's name was asked for before the first episode was handed over");
    }

    /// <remarks>
    /// <c>docs/specs/indexer-search.md</c> § Handing the winner over: the winning merged torrent is offered
    /// to the download client, and no other hash of the same release is started. Silo's name is listed as
    /// the TGx upload on six indexers and the EZTV upload on three; only the TGx torrent is started, and the
    /// decision says what it won ahead of.
    /// </remarks>
    [Fact]
    public async Task OnlyTheWinnerIsOfferedToTheClient()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Round(new FakeFetch().AnsweringSilo(), engine).RunAsync(
            [IndexerSites.SiloEpisode],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        TorrentRequest started = Assert.Single(engine.Taken);
        Assert.Contains(IndexerSites.TgxHash, started.Source, StringComparison.OrdinalIgnoreCase);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(IndexerSites.TgxHash, outcome.InfoHash);
        Assert.Equal(IndexerSites.Silo, outcome.Release);
        Assert.Equal([IndexerSites.SiloEpisode.Key], outcome.Covers);
        Assert.Contains($"{IndexerSites.EztvHash} (3 indexers)", outcome.Considered!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <c>docs/specs/indexer-search.md</c>: an indexer result is not judged against the show's settings — the
    /// release name was. The show forbids the tags TGx and EZTV; the name carries neither, the rows carry
    /// them as the sites' own tags, and the winner is still taken.
    /// </remarks>
    [Fact]
    public async Task ARowIsNotJudgedAgainstTheShowsSettings()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Round(new FakeFetch().AnsweringSilo(), engine).RunAsync(
            [IndexerSites.SiloEpisode],
            new(AtQuality("1080p", forbidden: ["TGx", "EZTV"]), Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(IndexerSites.TgxHash, outcome.InfoHash);
    }

    /// <remarks>
    /// <c>docs/specs/release-names.md</c>: the names carrying the most wishes are searched first, and when they
    /// produce no torrent on any indexer the names carrying one wish fewer are searched. The show wishes for
    /// MULTI; HiggsBoson's MULTI release is asked first of every indexer and nobody lists it, so the round
    /// hands over to the plain name, which wins.
    /// </remarks>
    [Fact]
    public async Task AnEmptyWishGroupHandsOverToTheGroupWithOneWishFewer()
    {
        const string multi = "Silo.S02E01.MULTI.1080p.WEB.H264-HiggsBoson";

        Assert.Contains(multi, Capture.Rows("names-predb-search-silo-s02e01.xml", "rss"));

        FakeFetch fetch = new FakeFetch().AnsweringSilo();
        FakeTorrentEngine engine = new();

        foreach (SourceDefinition indexer in IndexerSites.Shipped.Where(one => one.Serves(LibraryKind.Television)))
        {
            fetch.Answers(IndexerSites.Exact(indexer, multi), Capture.Fixture("round-solo-nyaa-exact.xml"));
            fetch.Answers(IndexerSites.Words(indexer, multi), Capture.Fixture("round-solo-nyaa-exact.xml"));
        }

        CycleReport report = await Round(fetch, engine, names: new FixedNames((IndexerSites.Silo, "PreDB"), (multi, "PreDB"))).RunAsync(
            [IndexerSites.SiloEpisode],
            new(SettingsByShow.Every(new() { Quality = "1080p", Wishes = ["MULTI"], Searched = true }), Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        string[] asked = [.. fetch.Asked.Select(address => address.ToString())];

        // Group by group: every indexer has been asked the name carrying the wish before any indexer is asked
        // the plain one. Asking both names of one indexer before the next would be one group, not two.
        int lastOfTheWish = IndexerSites.Shipped
            .Where(one => one.Serves(LibraryKind.Television))
            .Max(indexer => Array.IndexOf(asked, IndexerSites.Words(indexer, multi)));
        int firstOfThePlainName = Array.IndexOf(asked, IndexerSites.Exact(IndexerSites.TorrentBay, IndexerSites.Silo));

        Assert.True(lastOfTheWish >= 0 && lastOfTheWish < firstOfThePlainName, "The group carrying the wish was not asked in full first.");

        Assert.Equal(IndexerSites.TgxHash, Assert.Single(report.Outcomes).InfoHash);
    }

    /// <remarks>
    /// <c>docs/specs/run.md</c>: an episode for which no wish group produces a torrent is shown with its reason,
    /// and is searched for again on the next run. Nobody lists the name; the outcome says so, and a second run
    /// puts every question to every indexer again.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWithNoTorrentIsShownWithItsReasonAndComesBackNextRun()
    {
        FakeFetch fetch = new FakeFetch().AnswersAnything(Capture.Fixture("round-solo-nyaa-exact.xml"));
        ActivityJournal journal = new();
        SearchCycle cycle = Round(fetch, new(), journal);

        CycleReport first = await cycle.RunAsync([IndexerSites.SiloEpisode], new(Wanted, Blacklist.None, DryRun: false, Folder), CancellationToken.None);
        int asked = fetch.Asked.Count;

        CycleReport second = await cycle.RunAsync([IndexerSites.SiloEpisode], new(Wanted, Blacklist.None, DryRun: false, Folder), CancellationToken.None);

        foreach (CycleReport report in (CycleReport[])[first, second])
        {
            EpisodeOutcome outcome = Assert.Single(report.Outcomes);

            Assert.False(outcome.HandedOver);
            Assert.True(outcome.Searched);
            Assert.Equal("no indexer listed a torrent for any of its release names", outcome.Detail);
        }

        Assert.True(asked > 0);
        Assert.Equal(asked * 2, fetch.Asked.Count);
        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Decide && entry.Detail == "no indexer listed a torrent for any of its release names");
    }

    /// <remarks>
    /// <c>docs/specs/run.md</c>: a site that still gives no answer after its second attempt is left out of the
    /// rest of that run, and the Sources page shows why. Torrentz2 does not answer Silo's name; the round
    /// carries on without it, and it is asked nothing more — not the name as words, not the other name.
    /// </remarks>
    [Fact]
    public async Task ASiteThatFailsTwiceSitsOutTheRestOfTheRun()
    {
        const string multi = "Silo.S02E01.MULTI.1080p.WEB.H264-HiggsBoson";

        FakeFetch fetch = new FakeFetch()
            .AnsweringSilo()
            .AnswersAnything(Capture.Fixture("round-solo-nyaa-exact.xml"))
            .Fails(IndexerSites.Exact(IndexerSites.Torrentz2, IndexerSites.Silo), FetchOutcome.Unreachable, "torrentz2.nz did not answer");
        RecordingLedger ledger = new();

        CycleReport report = await Round(fetch, new(), names: new FixedNames((IndexerSites.Silo, "PreDB"), (multi, "PreDB")), ledger: ledger).RunAsync(
            [IndexerSites.SiloEpisode],
            new(Wanted, Blacklist.None, DryRun: false, Folder),
            CancellationToken.None);

        Assert.Equal(1, fetch.Asked.Count(address => address.Host == "torrentz2.nz"));
        Assert.True(Assert.Single(report.Outcomes).HandedOver);

        SourceAnswer said = Assert.Single(ledger.Answers, answer => answer.Name == "Torrentz2");
        Assert.Contains("left out of the rest of this run", said.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A cycle over every shipped indexer, with Silo's name handed over as a name source gave it.</summary>
    private static SearchCycle Round(FakeFetch fetch, FakeTorrentEngine engine, ActivityJournal? journal = null, IReleaseNames? names = null, RecordingLedger? ledger = null)
    {
        ActivityJournal writing = journal ?? new ActivityJournal();

        return new(
            names ?? new FixedNames((IndexerSites.Silo, "PreDB")),
            IndexerSites.Finding(fetch, journal: writing, ledger: ledger),
            writing,
            new Grab(engine, new EndlessDisk(null), writing));
    }

    private const string Folder = @"C:\downloads";

    /// <summary>What the owner wants, at its documented defaults.</summary>
    private static SettingsByShow Wanted => AtQuality("1080p");

    /// <summary>Every show at this quality, with codec any and no tags.</summary>
    private static SettingsByShow AtQuality(string quality, IReadOnlyList<string>? forbidden = null)
    {
        return SettingsByShow.Every(new() { Quality = quality, Forbidden = forbidden ?? [], Searched = true });
    }

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
        long? free = null,
        IReleaseNames? names = null)
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(sources ?? Sources, [], []);
        ActivityJournal writing = journal ?? new ActivityJournal();
        Readers readers = Readers.Shipped();

        return new(
            names ?? new NameSources(catalogue, fetch, readers, writing, TimeProvider.System),
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

    /// <summary>A gap in the season The Pirate Bay's captured answer is about.</summary>
    private static TrackedEpisode Sugar(int number)
    {
        return new(
            new(157741, 2, number),
            "Sugar",
            2024,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing);
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
