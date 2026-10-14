using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>A copy nobody seeds is not started.</summary>
/// <remarks>
/// <para>
/// <c>docs/specs/indexer-search.md</c> § The winner, and GitHub issue #2 of 13 October 2026, which replaced the
/// owner's rule of 12 September 2026 that there is no seeder gate: a merged torrent every one of whose indexer
/// results reports nought seeders is not offered to the client, and the next one in winning order is. A result
/// with no seeder count is not nought.
/// </para>
/// <para>
/// The rows are LimeTorrents' and The Pirate Bay's own answers, captured on 22 August 2026 for <c>Sugar S02E01</c>
/// and <c>Sugar S02E08</c>. Both list Queen Sugar's releases of those episodes beside Sugar's, many of them with
/// nobody seeding, and those are the rows that count when Queen Sugar's episode is asked for. No name source is
/// behind them, so the episode itself is what the indexers are asked; no air date either, so each row is held to
/// its title alone.
/// </para>
/// </remarks>
public sealed class NoSeedersTests
{
    /// <summary>
    /// Queen Sugar S02E01 at 1080p: LimeTorrents' FGT upload has seven seeders, its NTb upload nought, and The
    /// Pirate Bay's NTb WEB-DL twenty-five. FGT is refused here as a release the client failed, which leaves the
    /// NTb upload nobody seeds the winner — and it is passed over for The Pirate Bay's.
    /// </summary>
    [Fact]
    public async Task ACopyEveryIndexerSaysNobodySeedsIsNotStartedAndTheNextOneIs()
    {
        const string fgt = "E6E08A374067D327F6A20E8A3E15773D29825A5C";
        const string unseeded = "E3B3E080C7558B8A777491B680534B08B3D589AC";
        const string seeded = "C85354D278014A82D5463B411082AC6BC4CA80FD";

        FakeTorrentEngine engine = new();
        ActivityJournal journal = new();

        // What the Activity page showed while the episode was being decided: its notes are cleared once it is.
        List<string> shown = [];
        journal.Recorded += () => shown.AddRange(journal.Snapshot().Notes.Select(note => note.Line));

        CycleReport report = await Cycle(SugarS02E01(), engine, journal).RunAsync(
            [QueenSugar(1)],
            new(At1080p, Blacklist.Of(fgt), DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(seeded, outcome.InfoHash);
        Assert.DoesNotContain(engine.Taken, one => one.Source.Contains(unseeded, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            shown,
            line => line.Contains("no seeders", StringComparison.Ordinal) && line.Contains("NTb rarbg", StringComparison.Ordinal));
    }

    /// <summary>
    /// Queen Sugar S02E01 at 720p: TOPKEK's upload is on both indexers, two seeders on LimeTorrents and nought on
    /// The Pirate Bay. One indexer seeing seeders is enough, and it is started.
    /// </summary>
    [Fact]
    public async Task ACopyOneIndexerSeesSeedersOnIsStarted()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(SugarS02E01(), engine, new ActivityJournal()).RunAsync(
            [QueenSugar(1)],
            new(At720p, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("66AC98D6AE35C2DCD26A82D0EDE320F36DBC85F6", outcome.InfoHash);
    }

    /// <summary>
    /// Queen Sugar S02E08 at 720p: CONVOY's two uploads, both on LimeTorrents alone and both with nought seeders.
    /// Nothing is started, the decision says "no seeders" on the Activity page and — carried with the release it
    /// would have been — on the History page, and the next run asks the indexers all over again: an episode that
    /// has only just aired gets its second chance that way.
    /// </summary>
    [Fact]
    public async Task WithNobodySeedingAnyCopyNothingIsStartedAndTheNextRunAsksAgain()
    {
        FakeFetch fetch = SugarS02E08();
        FakeTorrentEngine engine = new();
        ActivityJournal journal = new();
        SearchCycle cycle = Cycle(fetch, engine, journal);

        CycleReport first = await cycle.RunAsync([QueenSugar(8)], new(At720p, Blacklist.None, DryRun: false, @"C:\downloads"), CancellationToken.None);
        int asked = fetch.Asked.Count;

        CycleReport second = await cycle.RunAsync([QueenSugar(8)], new(At720p, Blacklist.None, DryRun: false, @"C:\downloads"), CancellationToken.None);

        Assert.Empty(engine.Taken);

        foreach (CycleReport report in (CycleReport[])[first, second])
        {
            EpisodeOutcome outcome = Assert.Single(report.Outcomes);

            Assert.False(outcome.HandedOver);
            Assert.True(outcome.Searched);
            Assert.NotNull(outcome.Release);
            Assert.Contains("no seeders", outcome.Detail, StringComparison.Ordinal);
        }

        Assert.True(asked > 0);
        Assert.Equal(asked * 2, fetch.Asked.Count);
        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Decide && entry.Detail is not null && entry.Detail.Contains("no seeders", StringComparison.Ordinal));
    }

    /// <summary>
    /// A result with no seeder count is not nought. LimeTorrents reports nought for CONVOY's 720p upload of Queen
    /// Sugar S02E08, and on its own that is a torrent nobody seeds. Listed as well by an indexer whose listing
    /// prints no count — EZTV's prints none for any of the six rows in <c>eztv.html</c> — it is not: that indexer
    /// has said nothing about the swarm.
    /// </summary>
    [Fact]
    public async Task AResultWithNoSeederCountIsNotNought()
    {
        const string convoy = "759CD7A75425E894DE6910C3EB5824A7F7D591E6";

        ReleaseCopy[] rows = await IndexerSites.Finding(SugarS02E08(), [LimeTorrents])
            .AskAsync(LimeTorrents, new("Queen Sugar S02E08", true), "Queen Sugar S02E08", CancellationToken.None);

        ReleaseCopy onLimeTorrents = Assert.Single(rows, row => row.InfoHash == convoy);
        ReleaseCopy uncounted = onLimeTorrents with { Source = "EZTV", Seeders = null };

        Assert.Equal(0, onLimeTorrents.Seeders);
        Assert.True(Assert.Single(Merging.ByHash([new(onLimeTorrents, onLimeTorrents.Title, 0)])).NobodySeeds);
        Assert.False(
            Assert.Single(Merging.ByHash([new(onLimeTorrents, onLimeTorrents.Title, 0), new(uncounted, onLimeTorrents.Title, Merging.PerIndexer)]))
                .NobodySeeds);
    }

    private static readonly SourceDefinition LimeTorrents =
        new("LimeTorrents", "site", "https://www.limetorrents.fun/search/all/{query}/")
        {
            Priority = 35,
            FirstChoice = 3,
        };

    private static SettingsByShow At1080p { get; } =
        SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", EnglishOnly = true, Searched = true });

    private static SettingsByShow At720p { get; } =
        SettingsByShow.Every(new() { Quality = "720p", Codec = "h264", EnglishOnly = true, Searched = true });

    private static TrackedEpisode QueenSugar(int episode)
    {
        return new(new(66292, 2, episode), "Queen Sugar", 2016, LibraryKind.Television, null, null, EpisodeState.Missing);
    }

    private static FakeFetch SugarS02E01()
    {
        return Answering("Queen Sugar S02E01", "sugar1-limetorrents.html", "sugar1-apibay.json");
    }

    private static FakeFetch SugarS02E08()
    {
        return Answering("Queen Sugar S02E08", "sugar-limetorrents.html", "sugar-apibay.json");
    }

    /// <summary>Both indexers answering the episode with their captured pages, and every other page empty.</summary>
    private static FakeFetch Answering(string episode, string lime, string apibay)
    {
        return new FakeFetch()
            .AnswersAnything(string.Empty)
            .Answers(IndexerSites.Exact(LimeTorrents, episode), Capture.Fixture(lime))
            .Answers(IndexerSites.Words(LimeTorrents, episode), Capture.Fixture(lime))
            .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, episode), Capture.Fixture(apibay))
            .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, episode), Capture.Fixture(apibay));
    }

    private static SearchCycle Cycle(FakeFetch fetch, FakeTorrentEngine engine, ActivityJournal journal)
    {
        return new(
            new FixedNames(),
            IndexerSites.Finding(fetch, sources: [LimeTorrents, IndexerSites.ThePirateBay], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }
}
