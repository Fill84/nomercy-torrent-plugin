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
