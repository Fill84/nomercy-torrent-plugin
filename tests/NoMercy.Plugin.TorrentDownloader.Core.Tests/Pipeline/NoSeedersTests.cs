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
/// nobody seeding. No air date is given, so each row is held to its title alone.
/// </para>
/// <para>
/// A show's rows reach the gate only through its release names (<c>release-names.md</c> § Release names first),
/// so each test puts the names of the releases it is about to the cycle, as each indexer titles them, and each
/// indexer answers a name with its page for the episode. A row counts only when its title is the name asked for,
/// so the wider page gives exactly the rows the name's own would have, with the counts they had that day. What is
/// held here is what happens to a winner nobody seeds, which is the same whichever search found it.
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

        string[] names = [Fgt, NtbWebRip, NtbWebDl];

        CycleReport report = await Cycle(SugarS02E01(names), engine, journal, names).RunAsync(
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

        CycleReport report = await Cycle(SugarS02E01(Topkek), engine, new ActivityJournal(), Topkek).RunAsync(
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
        FakeFetch fetch = SugarS02E08(Convoy);
        FakeTorrentEngine engine = new();
        ActivityJournal journal = new();
        SearchCycle cycle = Cycle(fetch, engine, journal, Convoy);

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

    /// <summary>FGT's 1080p upload, as LimeTorrents titles it.</summary>
    private const string Fgt = "Queen Sugar S02E01 1080p WEB-DL DD5 1 H264-FGT";

    /// <summary>NTb's 1080p WEBRip, as LimeTorrents titles it: nought seeders there, and not on The Pirate Bay.</summary>
    private const string NtbWebRip = "Queen Sugar S02E01 After The Winter 1080p AMZN WEBRip DD5 1 x264 NTb rarbg";

    /// <summary>NTb's 1080p WEB-DL, as The Pirate Bay titles it: twenty-five seeders.</summary>
    private const string NtbWebDl = "Queen Sugar S02E01 After The Winter 1080p AMZN WEB-DL DDP5 1 H 264-NTb";

    /// <summary>TOPKEK's 720p upload, as The Pirate Bay titles it.</summary>
    private const string Topkek = "Queen.Sugar.S02E01.After.The.Winter.720p.WEBRiP.H.264-TOPKEK";

    /// <summary>CONVOY's 720p release of S02E08, which LimeTorrents lists twice.</summary>
    private const string Convoy = "Queen Sugar S02E08 720p WEB H264-CONVOY";

    private static FakeFetch SugarS02E01(params string[] names)
    {
        return Answering("Queen Sugar S02E01", names, "sugar1-limetorrents.html", "sugar1-apibay.json");
    }

    private static FakeFetch SugarS02E08(params string[] names)
    {
        return Answering("Queen Sugar S02E08", names, "sugar-limetorrents.html", "sugar-apibay.json");
    }

    /// <summary>
    /// Both indexers answering the episode, and each of <paramref name="names"/>, with their captured pages for the
    /// episode, and every other page empty.
    /// </summary>
    private static FakeFetch Answering(string episode, string[] names, string lime, string apibay)
    {
        FakeFetch fetch = new FakeFetch().AnswersAnything(string.Empty);

        foreach (string question in (string[])[episode, .. names])
        {
            fetch
                .Answers(IndexerSites.Exact(LimeTorrents, question), Capture.Fixture(lime))
                .Answers(IndexerSites.Words(LimeTorrents, question), Capture.Fixture(lime))
                .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, question), Capture.Fixture(apibay))
                .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, question), Capture.Fixture(apibay));
        }

        return fetch;
    }

    private static SearchCycle Cycle(FakeFetch fetch, FakeTorrentEngine engine, ActivityJournal journal, params string[] names)
    {
        return new(
            new FixedNames([.. names.Select(name => (name, "an indexer's own title"))]),
            IndexerSites.Finding(fetch, sources: [LimeTorrents, IndexerSites.ThePirateBay], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }
}
