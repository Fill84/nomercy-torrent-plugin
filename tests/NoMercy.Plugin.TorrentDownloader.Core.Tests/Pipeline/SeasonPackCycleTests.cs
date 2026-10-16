using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline.SeasonPackNamesTests;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>A season with several gaps takes a pack, and a season with one takes the single episode.</summary>
/// <remarks>
/// <para>
/// GitHub issue #1, on the owner's word of 14 October 2026, which replaced the owner's rule of 15 September 2026
/// that a pack is not searched for (<c>docs/specs/release-names.md</c> § Season packs, <c>run.md</c>).
/// </para>
/// <para>
/// Everything is the sites' own answer of 14 October 2026: the four name sources asked <c>Adolescence S01</c>,
/// srrDB asked <c>Adolescence S01E01</c>, and LimeTorrents and The Pirate Bay asked FLUX's pack —
/// <c>89772EF1…</c> on both, fourteen seeders on The Pirate Bay — and SuccessfulCrab's S01E01, which only The
/// Pirate Bay lists (<c>09E01E5B…</c>, forty seeders). The feeds are those of 15 September 2026, which carry
/// nothing of Adolescence. Every other address answers nothing.
/// </para>
/// </remarks>
public sealed class SeasonPackCycleTests
{
    private const string PackHash = "89772EF137A0857860A9A8DFD213CDB008FBE7B5";
    private const string SingleHash = "09E01E5BE24A8CE4F867515FA6D57A9DCFD424EB";
    private const string Single = "Adolescence.S01E01.1080p.WEB.H264-SuccessfulCrab";

    /// <remarks>
    /// Three of the season's four episodes are searched for — the fourth has its file — so the pack is looked
    /// for first and wins: one grab, answering for the three, and not one of them searched for singly.
    /// </remarks>
    [Fact]
    public async Task ASeasonWithSeveralGapsTakesThePack()
    {
        FakeFetch fetch = Sites(packListed: true);
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine).RunAsync(
            [Adolescence(1), Adolescence(2), Adolescence(3)],
            Options,
            CancellationToken.None);

        EpisodeOutcome taken = Assert.Single(report.Outcomes, outcome => outcome.HandedOver);

        Assert.Equal(PackHash, taken.InfoHash);
        Assert.Equal(FluxPack, taken.Release);
        Assert.Equal([Adolescence(1).Key, Adolescence(2).Key, Adolescence(3).Key], taken.Covers);

        // And the client is told which episodes those are, so the fourth — which has its file — is not
        // downloaded again.
        Assert.Equal(taken.Covers, Assert.Single(engine.Taken).Episodes);
        Assert.Equal(3, report.Outcomes.Count);
        Assert.DoesNotContain(fetch.Asked, address => address.ToString().Contains("S01E0", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// <para>
    /// <strong>A season whose first episode is to be replaced takes its pack for the missing ones.</strong> An
    /// episode to be replaced is never answered for by a pack, but it was still the first of its season in the
    /// queue, so the pack was looked for when the run reached it: the pack's outcome was E01's, it covered E02
    /// and E03, it was written down as replacing E01's old delivery — and that grab never encoded, because E02
    /// and E03 have no old encode to recycle. E01 was not searched for at all that run.
    /// </para>
    /// <para>Sabotage: looking for the pack at the season's first queued episode, whatever its state.</para>
    /// </remarks>
    [Fact]
    public async Task ASeasonWhoseFirstEpisodeIsToBeReplacedTakesThePackForTheMissingOnes()
    {
        FakeFetch fetch = Sites(packListed: true);
        FakeTorrentEngine engine = new();
        TrackedEpisode replacing = Adolescence(1) with
        {
            State = EpisodeState.Replace,
            Replaces = new("7445E4910B7A4D7E1F3C35D5A4A9C2E1B0C0FFEE", "Adolescence S01E01 1080p HDTV x264-ViETNAM"),
        };

        CycleReport report = await Cycle(fetch, engine).RunAsync(
            [replacing, Adolescence(2), Adolescence(3)],
            Options,
            CancellationToken.None);

        EpisodeOutcome pack = Assert.Single(report.Outcomes, outcome => outcome.InfoHash == PackHash);

        Assert.Equal([Adolescence(2).Key, Adolescence(3).Key], pack.Covers);
        Assert.Contains(pack.Episode, pack.Covers);

        // And E01 was looked for on its own, by name.
        EpisodeOutcome own = Assert.Single(report.Outcomes, outcome => outcome.Episode == replacing.Key);

        Assert.NotEqual(PackHash, own.InfoHash);
        Assert.Contains(fetch.Asked, address => address.ToString().Contains("S01E01", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// One episode of the season is searched for, so no pack is looked for — the season's search is never
    /// asked — and the single episode is taken.
    /// </remarks>
    [Fact]
    public async Task ASeasonWithOneGapTakesTheSingleEpisode()
    {
        FakeFetch fetch = Sites(packListed: true);
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine).RunAsync([Adolescence(1)], Options, CancellationToken.None);

        EpisodeOutcome taken = Assert.Single(report.Outcomes);

        Assert.True(taken.HandedOver, taken.Detail);
        Assert.Equal(SingleHash, taken.InfoHash);
        Assert.Equal(Single, taken.Release);
        Assert.Equal([Adolescence(1).Key], taken.Covers);
        Assert.DoesNotContain(fetch.Asked, address => address.ToString() == SceneSourceSeason);

        // A single episode's release is downloaded as it always was: every video, the largest one staged.
        Assert.Null(Assert.Single(engine.Taken).Episodes);
    }

    /// <remarks>
    /// The pack is looked for and no indexer lists a torrent of it, so the season's episodes are searched for
    /// singly, as they were before: S01E01 is taken on its own, and S01E02 — which no source named that day —
    /// is not.
    /// </remarks>
    [Fact]
    public async Task WhenNoPackGivesATorrentTheEpisodesAreSearchedSingly()
    {
        FakeFetch fetch = Sites(packListed: false);
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine).RunAsync(
            [Adolescence(1), Adolescence(2)],
            Options,
            CancellationToken.None);

        EpisodeOutcome taken = Assert.Single(report.Outcomes, outcome => outcome.HandedOver);

        Assert.Equal(SingleHash, taken.InfoHash);
        Assert.Equal([Adolescence(1).Key], taken.Covers);
        Assert.False(report.Outcomes.Single(outcome => outcome.Episode == Adolescence(2).Key).HandedOver);
        Assert.Contains(fetch.Asked, address => address.ToString() == IndexerSites.Exact(ThePirateBay, FluxPack));
    }

    /// <remarks>
    /// The pack is still downloading and two of its episodes were let go of — narrowed out of it because their
    /// videos would not come — so they are gaps again. The pack is the one the indexers rank first, and taken
    /// again it answers for nothing new: the client already has it, the store keeps the open row as it is, and
    /// the two episodes are settled as taken by a torrent that will never deliver them, run after run. A
    /// torrent already open does not stand, so the run searches the two singly instead.
    /// </remarks>
    [Fact]
    public async Task TheEpisodesLetGoOfByAnOpenPackAreSearchedSinglyAndThePackIsNotTakenAgain()
    {
        FakeFetch fetch = Sites(packListed: true);
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine).RunAsync(
            [Adolescence(1), Adolescence(2)],
            Options with { OpenHashes = new HashSet<string>([PackHash], StringComparer.OrdinalIgnoreCase) },
            CancellationToken.None);

        Assert.DoesNotContain(report.Outcomes, outcome => outcome.InfoHash == PackHash);
        Assert.DoesNotContain(engine.Taken, taken => taken.Episodes is not null);

        EpisodeOutcome single = Assert.Single(report.Outcomes, outcome => outcome.HandedOver);

        Assert.Equal(SingleHash, single.InfoHash);
        Assert.Equal([Adolescence(1).Key], single.Covers);
        Assert.Contains(fetch.Asked, address => address.ToString() == IndexerSites.Exact(ThePirateBay, Single));
    }

    private static CycleOptions Options { get; } =
        new(
            SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", EnglishOnly = true, Searched = true }),
            Blacklist.None,
            DryRun: false,
            @"C:\downloads");

    private static readonly SourceDefinition LimeTorrents =
        new("LimeTorrents", "site", "https://www.limetorrents.fun/search/all/{query}/")
        {
            Priority = 35,
            FirstChoice = 3,
        };

    private static readonly SourceDefinition ThePirateBay = IndexerSites.ThePirateBay;

    /// <summary>The name sources and the two indexers as they answered, with the pack's listings or without.</summary>
    private static FakeFetch Sites(bool packListed)
    {
        FakeFetch fetch = new FakeFetch()
            .AnswersAnything(string.Empty)
            .AnsweringFeeds()
            .AnsweringAdolescenceSeason()
            .Answers(IndexerSites.Exact(ThePirateBay, Single), Capture.Fixture("pack-adolescence-s01e01-apibay-exact.json"))
            .Answers(IndexerSites.Words(ThePirateBay, Single), Capture.Fixture("pack-adolescence-s01e01-apibay-words.json"))
            .Answers(IndexerSites.Exact(LimeTorrents, Single), Capture.Fixture("pack-adolescence-s01e01-limetorrents-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, Single), Capture.Fixture("pack-adolescence-s01e01-limetorrents-words.html"));

        if (packListed)
        {
            fetch
                .Answers(IndexerSites.Exact(ThePirateBay, FluxPack), Capture.Fixture("pack-adolescence-s01-apibay-exact.json"))
                .Answers(IndexerSites.Words(ThePirateBay, FluxPack), Capture.Fixture("pack-adolescence-s01-apibay-words.json"))
                .Answers(IndexerSites.Exact(LimeTorrents, FluxPack), Capture.Fixture("pack-adolescence-s01-limetorrents-exact.html"))
                .Answers(IndexerSites.Words(LimeTorrents, FluxPack), Capture.Fixture("pack-adolescence-s01-limetorrents-words.html"));
        }

        return fetch;
    }

    private static SearchCycle Cycle(FakeFetch fetch, FakeTorrentEngine engine)
    {
        ActivityJournal journal = new();

        return new(
            NameSourceSites.Over(fetch, journal),
            IndexerSites.Finding(fetch, sources: [LimeTorrents, ThePirateBay], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }
}
