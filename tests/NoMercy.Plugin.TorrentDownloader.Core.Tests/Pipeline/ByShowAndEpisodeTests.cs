using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// When no release name gives a torrent, the indexers are asked for the show and episode.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/indexer-search.md</c> § When no release name gives a torrent — the owner's rule of
/// 16 September 2026, after South Park S15E12 could not be found on their server. PreDB.net names
/// <c>South.Park.S15E12.1080p.BluRay.x264-FilmHD</c>, the one scene name that meets 1080p, h264 and English
/// only, and no indexer has a torrent for it: it is from 2012. What the indexers do have is
/// <c>CtrlHD</c>'s 1080p WEB-DL, a P2P release no scene database knows.
/// </para>
/// <para>
/// Every page here was captured on that day through the capture tool: PreDB.net's answer to
/// <c>South Park S15E12</c>, LimeTorrents' answer to the FilmHD name (nothing) and to
/// <c>South Park S15E12</c>.
/// </para>
/// </remarks>
public class ByShowAndEpisodeTests
{
    private const string FilmHd = "South.Park.S15E12.1080p.BluRay.x264-FilmHD";

    private const string CtrlHdHash = "C4F76C30869F05E52FE90527354EBE5252844DB5";

    private static readonly SourceDefinition LimeTorrents =
        new("LimeTorrents", "site", "https://www.limetorrents.fun/search/all/{query}/")
        {
            Priority = 35,
            FirstChoice = 3,
        };

    /// <remarks>
    /// The name meets the settings and no indexer lists it, so the episode is asked for as
    /// <c>South Park S15E12</c>, and the one result that names S15E12 and meets 1080p, h264 and English only
    /// is handed over — under its own title, which is the name it is downloaded under.
    /// </remarks>
    [Fact]
    public async Task WhenNoReleaseNameGivesATorrentTheEpisodeIsAskedForAndTheBestResultTaken()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(Answering(), engine, NameFromPreDbNet()).RunAsync(
            [SouthPark],
            new(Settings, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(CtrlHdHash, outcome.InfoHash);
        Assert.Equal("South Park S15E12 1 1080p HMAX WEB-DL DD5 1 H 264-CtrlHD[TGx]", outcome.Release);
        Assert.Contains(CtrlHdHash, Assert.Single(engine.Taken).Source, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A result counts only when it names this episode and meets the show's settings. The same page carries
    /// S15E12 at 1080p from CtrlHD, at 720p from HDTV in x264 (ORENJI) and from WEB in x265 (MiNX), at 480p,
    /// and from HDTV with no resolution at all. A show wanting 720p in h265 takes MiNX's and nothing else.
    /// </remarks>
    [Fact]
    public async Task OnlyAResultThatMeetsTheShowsSettingsIsTaken()
    {
        FakeTorrentEngine engine = new();

        SettingsByShow h265At720 = SettingsByShow.Every(new() { Quality = "720p", Codec = "h265", EnglishOnly = true, Searched = true });

        CycleReport report = await Cycle(Answering(), engine, NameFromPreDbNet()).RunAsync(
            [SouthPark],
            new(h265At720, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("2AB3DF9814AF51A4E3E9375750CD968CCD3E9C8C", outcome.InfoHash);
    }

    /// <remarks>
    /// And where nothing on any indexer meets the settings, nothing is taken — and the episode still counts as
    /// searched, because the indexers were asked.
    /// </remarks>
    [Fact]
    public async Task WhereNoResultMeetsTheSettingsNothingIsTakenAndTheEpisodeWasSearched()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(Answering(), engine, NameFromPreDbNet()).RunAsync(
            [SouthPark],
            new(SettingsByShow.Every(new() { Quality = "2160p", Searched = true }), Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.False(outcome.HandedOver);
        Assert.Empty(engine.Taken);
        Assert.True(outcome.Searched);
    }

    /// <remarks>
    /// A name that gives a torrent ends it there: the episode is not asked for as well. Asking anyway is a
    /// request to every indexer for an answer that would never be used.
    /// </remarks>
    [Fact]
    public async Task AReleaseNameThatGivesATorrentIsNotFollowedByTheEpisodeSearch()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();

        await new SearchCycle(
                new FixedNames((IndexerSites.Silo, "PreDB")),
                IndexerSites.Finding(fetch),
                new ActivityJournal(),
                new Grab(new FakeTorrentEngine(), new EndlessDisk(null), new ActivityJournal()))
            .RunAsync(
                [IndexerSites.SiloEpisode],
                new(SettingsByShow.Every(new() { Quality = "1080p", Searched = true }), Blacklist.None, DryRun: false, @"C:\downloads"),
                CancellationToken.None);

        Assert.DoesNotContain(
            fetch.Asked,
            address => Uri.UnescapeDataString(address.ToString()).Replace('+', ' ').Contains("Silo S02E01/", StringComparison.OrdinalIgnoreCase)
                       || Uri.UnescapeDataString(address.ToString()).Replace('+', ' ').EndsWith("q=Silo S02E01", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// <para>
    /// <strong>A result uploaded long before the episode aired is not a result for it.</strong> Two programmes
    /// are called Dark Matter, one from 2015 and the owner's from 2024, and their S02E04s share every word of
    /// an indexer's title. On 17 September 2026, the day the 2024 programme's S02E04 aired, the episode search
    /// took <c>Dark Matter S02E04 We Were Family 1080p TrueHD 5 1 AVC REMUX-FraMeSToR</c> for it: the 2015
    /// programme's Blu-ray remux.
    /// </para>
    /// <para>
    /// The Pirate Bay's answer to <c>Dark Matter S02E04</c>, captured that day, dates that row
    /// 6 February 2026, seven months before the episode aired. It is the one result at 1080p in h264, and it is
    /// not taken.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResultUploadedLongBeforeTheEpisodeAiredIsNotTakenForIt()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await DarkMatterCycle(engine).RunAsync(
            [DarkMatter2024],
            new(Settings, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.False(outcome.HandedOver, outcome.Release);
        Assert.Empty(engine.Taken);
    }

    /// <remarks>
    /// The same answer for the 2015 programme's S02E04, which aired on 22 July 2016: MeGusta's 720p x265,
    /// uploaded the next day, is taken for it — and not for the 2024 programme's S02E04, ten years later.
    /// </remarks>
    [Fact]
    public async Task AResultUploadedAsTheEpisodeAiredIsTakenForIt()
    {
        SettingsByShow h265At720 = SettingsByShow.Every(new() { Quality = "720p", Codec = "h265", EnglishOnly = true, Searched = true });

        FakeTorrentEngine older = new();

        CycleReport olderReport = await DarkMatterCycle(older).RunAsync(
            [DarkMatter2015],
            new(h265At720, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome taken = Assert.Single(olderReport.Outcomes);

        Assert.True(taken.HandedOver, taken.Detail);
        Assert.Equal("FE037FFB0951D1ED1442B0CCF6197902C642FEA1", taken.InfoHash);
        Assert.Equal("Dark.Matter.S02E04.720p.HEVC.x265-MeGusta", taken.Release);

        FakeTorrentEngine theirs = new();

        CycleReport theirReport = await DarkMatterCycle(theirs).RunAsync(
            [DarkMatter2024],
            new(h265At720, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        Assert.False(Assert.Single(theirReport.Outcomes).HandedOver);
        Assert.Empty(theirs.Taken);
    }

    /// <remarks>
    /// <para>
    /// <strong>A release name's torrents are held to the episode's air date too.</strong> A name no source
    /// dated reached the indexers as <c>Dark.Matter.S02E04.1080p.WEB.x264-FaiLED</c>, and Torrentz2 listed it —
    /// uploaded in January 2019, years before the 2024 programme's S02E04 aired. It was taken, twice, on
    /// 17 September 2026. The date was on the page; only the episode search looked at it.
    /// </para>
    /// <para>
    /// Left out, it is not even read further: no detail page is asked for a torrent that will not be taken.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReleaseNamesTorrentUploadedLongBeforeTheEpisodeAiredIsNotTaken()
    {
        const string failed = "Dark.Matter.S02E04.1080p.WEB.x264-FaiLED";

        FakeFetch fetch = new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.Torrentz2, failed), Capture.Fixture("round-dark-matter-torrentz2-failed.html"))
            .Answers(IndexerSites.Words(IndexerSites.Torrentz2, failed), Capture.Fixture("round-dark-matter-torrentz2-failed.html"));

        IReadOnlyList<RankedTorrent> ranked = await IndexerSites.Round(fetch, [IndexerSites.Torrentz2])
            .AskAsync([failed], DarkMatter2024, Blacklist.None, new(), CancellationToken.None);

        Assert.Empty(ranked);
        Assert.All(fetch.Asked, address => Assert.Contains("/search", address.AbsolutePath, StringComparison.Ordinal));
    }

    private static readonly TrackedEpisode DarkMatter2024 =
        new(new(196322, 2, 4), "Dark Matter", 2024, LibraryKind.Television, null, new DateOnly(2026, 9, 17), EpisodeState.Missing);

    private static readonly TrackedEpisode DarkMatter2015 =
        new(new(61889, 2, 4), "Dark Matter", 2015, LibraryKind.Television, null, new DateOnly(2016, 7, 22), EpisodeState.Missing);

    /// <summary>No name source, and The Pirate Bay answering <c>Dark Matter S02E04</c> as it did on the day.</summary>
    /// <remarks>
    /// Captured with the spaces written as <c>%20</c>, the letter-for-letter ask. The words ask writes them as
    /// <c>+</c>, which a query string reads as the same spaces, so it is the same search and the same answer.
    /// </remarks>
    private static SearchCycle DarkMatterCycle(FakeTorrentEngine engine)
    {
        FakeFetch fetch = new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, "Dark Matter S02E04"), Capture.Fixture("fallback-apibay-dark-matter-s02e04.json"))
            .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, "Dark Matter S02E04"), Capture.Fixture("fallback-apibay-dark-matter-s02e04.json"));

        ActivityJournal journal = new();

        return new(
            new FixedNames(),
            IndexerSites.Finding(fetch, sources: [IndexerSites.ThePirateBay], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }

    private static readonly TrackedEpisode SouthPark =
        new(new(2190, 15, 12), "South Park", 1997, LibraryKind.Television, "1%", new DateOnly(2011, 11, 16), EpisodeState.Missing);

    private static SettingsByShow Settings { get; } =
        SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", EnglishOnly = true, Searched = true });

    /// <summary>The one name PreDB.net gave that meets the settings, checked against its captured answer.</summary>
    private static FixedNames NameFromPreDbNet()
    {
        Assert.Contains(FilmHd, Capture.Rows("fallback-predbnet-south-park-s15e12.xml", "rss"));

        return new FixedNames((FilmHd, "PreDB.net"));
    }

    private static FakeFetch Answering()
    {
        return new FakeFetch()
            .Answers(IndexerSites.Exact(LimeTorrents, FilmHd), Capture.Fixture("fallback-limetorrents-filmhd.html"))
            .Answers(IndexerSites.Words(LimeTorrents, FilmHd), Capture.Fixture("fallback-limetorrents-filmhd.html"))
            .Answers(IndexerSites.Exact(LimeTorrents, "South Park S15E12"), Capture.Fixture("fallback-limetorrents-south-park-s15e12.html"))
            .Answers(IndexerSites.Words(LimeTorrents, "South Park S15E12"), Capture.Fixture("fallback-limetorrents-south-park-s15e12.html"));
    }

    private static SearchCycle Cycle(FakeFetch fetch, FakeTorrentEngine engine, IReleaseNames names)
    {
        ActivityJournal journal = new();

        return new(
            names,
            IndexerSites.Finding(fetch, sources: [LimeTorrents], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }
}
