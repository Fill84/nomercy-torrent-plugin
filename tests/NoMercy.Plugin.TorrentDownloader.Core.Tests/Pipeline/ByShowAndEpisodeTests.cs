using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// When no release name gives a torrent: a show downloads nothing, and an anime is asked for by show and
/// episode.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/indexer-search.md</c> § When no release name gives a torrent. The search by show and episode
/// is the owner's rule of 16 September 2026, after South Park S15E12 could not be found: PreDB.net names
/// <c>South.Park.S15E12.1080p.BluRay.x264-FilmHD</c>, the one scene name that meets 1080p, h264 and English
/// only, and no indexer has a torrent for it; what the indexers do have is <c>CtrlHD</c>'s 1080p WEB-DL, a P2P
/// release no scene database knows. On 15 October 2026 the owner kept that search for an anime alone: for a
/// show it had taken South Park S29E03's ViETNAM capture, which no name source lists
/// (<c>OnlyANamedReleaseTests</c>).
/// </para>
/// <para>
/// South Park's pages were captured on 16 September 2026 through the capture tool: PreDB.net's answer to
/// <c>South Park S15E12</c>, LimeTorrents' answer to the FilmHD name (nothing) and to
/// <c>South Park S15E12</c>. Solo Leveling's were captured on 15 October 2026: Nyaa's and LimeTorrents'
/// answers to srrDB's <c>Solo.Leveling.S02E01.1080p.WEB.H264-SKYANiME</c> (nothing) and to
/// <c>Solo Leveling S02E01</c>.
/// </para>
/// </remarks>
public class ByShowAndEpisodeTests
{
    private const string FilmHd = "South.Park.S15E12.1080p.BluRay.x264-FilmHD";

    private const string CtrlHdHash = "C4F76C30869F05E52FE90527354EBE5252844DB5";

    private const string Skyanime = "Solo.Leveling.S02E01.1080p.WEB.H264-SKYANiME";

    private static readonly SourceDefinition LimeTorrents =
        new("LimeTorrents", "site", "https://www.limetorrents.fun/search/all/{query}/")
        {
            Priority = 35,
            FirstChoice = 3,
        };

    /// <remarks>
    /// <para>
    /// <strong>A show whose release names give no torrent downloads nothing.</strong> FilmHD's name meets the
    /// settings and no indexer lists it. The episode is not asked for as <c>South Park S15E12</c>, CtrlHD's
    /// upload — which the search by show and episode took on 16 September 2026 — is not handed over, and the
    /// Activity page says why. The next run asks again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenAShowsReleaseNamesGiveNoTorrentNothingIsDownloadedAndTheActivityPageSaysWhy()
    {
        FakeFetch fetch = Answering();
        ActivityJournal journal = new();
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, engine, NameFromPreDbNet(), journal).RunAsync(
            [SouthPark],
            new(Settings, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.False(outcome.HandedOver, outcome.Release);
        Assert.Empty(engine.Taken);
        Assert.True(outcome.Searched);
        Assert.Equal("no indexer listed a torrent for any of its release names", outcome.Detail);
        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Decide && entry.Detail == outcome.Detail);

        Assert.Contains(fetch.Asked, address => address.ToString() == IndexerSites.Exact(LimeTorrents, FilmHd));
        Assert.DoesNotContain(
            fetch.Asked,
            address => address.ToString() == IndexerSites.Exact(LimeTorrents, "South Park S15E12")
                       || address.ToString() == IndexerSites.Words(LimeTorrents, "South Park S15E12"));
    }

    /// <remarks>
    /// <para>
    /// <strong>An anime in the same position is still asked for by show and episode.</strong> srrDB names
    /// SKYANiME's 1080p release of Solo Leveling S02E01, which meets 1080p and h264, and neither Nyaa nor
    /// LimeTorrents lists it. So the indexers are asked for <c>Solo Leveling S02E01</c>, Nyaa first, and the
    /// best result that names the episode and meets the settings is taken: ToonsHub's 1080p H.264 upload,
    /// found first on Nyaa.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAnimeWhoseReleaseNamesGiveNoTorrentIsStillAskedForByShowAndEpisode()
    {
        FakeFetch fetch = AnsweringSkyanimeAndTheEpisode();
        FakeTorrentEngine engine = new();

        CycleReport report = await AnimeCycle(fetch, engine, new FixedNames((Skyanime, "srrDB"))).RunAsync(
            [SoloLeveling],
            new(AnimeSettings, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("D8FA5AC1CE27DAB1F07F0C93111C5763908182E8", outcome.InfoHash);
        Assert.Equal("Nyaa", outcome.Source);

        string[] asked = [.. fetch.Asked.Select(address => address.ToString())];

        Assert.Equal(IndexerSites.Exact(IndexerSites.Nyaa, Skyanime), asked[0]);
        Assert.Contains(IndexerSites.Exact(IndexerSites.Nyaa, "Solo Leveling S02E01"), asked);
    }

    /// <remarks>
    /// A name that gives a torrent ends it there: an anime's episode is not asked for as well. Asking anyway is
    /// a request to every indexer for an answer that would never be used. PreDB.net names AMB3R's MULTi 1080p
    /// release of Solo Leveling S02E01, and LimeTorrents lists its EZTV upload; Nyaa has nothing for it.
    /// </remarks>
    [Fact]
    public async Task AReleaseNameThatGivesATorrentIsNotFollowedByTheEpisodeSearch()
    {
        const string amb3r = "Solo.Leveling.S02E01.MULTi.1080p.WEB.x264-AMB3R";

        FakeFetch fetch = new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.Nyaa, amb3r), Capture.Fixture("anime-nyaa-amb3r-exact.xml"))
            .Answers(IndexerSites.Words(IndexerSites.Nyaa, amb3r), Capture.Fixture("anime-nyaa-amb3r-words.xml"))
            .Answers(IndexerSites.Exact(LimeTorrents, amb3r), Capture.Fixture("anime-limetorrents-amb3r-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, amb3r), Capture.Fixture("anime-limetorrents-amb3r-words.html"));

        FakeTorrentEngine engine = new();

        CycleReport report = await AnimeCycle(fetch, engine, new FixedNames((amb3r, "PreDB.net"))).RunAsync(
            [SoloLeveling],
            new(AnimeSettings, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("0882B923D90EDDDEAAC17130EC032BCE8418B51D", outcome.InfoHash);
        Assert.All(fetch.Asked, address => Assert.Contains("AMB3R", address.ToString(), StringComparison.Ordinal));
    }

    /// <remarks>
    /// <para>
    /// <strong>A result uploaded long before the episode aired is not a result for it.</strong> Two programmes
    /// are called Dark Matter, one from 2015 and one from 2024, and their S02E04s share every word of an
    /// indexer's title. On 17 September 2026, the day the 2024 programme's S02E04 aired, the search by show and
    /// episode took <c>Dark Matter S02E04 We Were Family 1080p TrueHD 5 1 AVC REMUX-FraMeSToR</c> for it: the
    /// 2015 programme's Blu-ray remux. The Pirate Bay's answer to <c>Dark Matter S02E04</c>, captured that day,
    /// dates that row 6 February 2026, seven months before the episode aired.
    /// </para>
    /// <para>
    /// The cycle puts this question for an anime only; the round's date rules are held here on the pages where
    /// they were seen, which are a show's. MeGusta's 720p x265, uploaded the day after the 2015 programme's
    /// S02E04 aired, wins the round for it — and takes no part in the 2024 programme's S02E04, ten years later.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AResultUploadedAsTheEpisodeAiredWinsTheRoundForIt()
    {
        SettingsByShow h265At720 = SettingsByShow.Every(new() { Quality = "720p", Codec = "h265", EnglishOnly = true, Searched = true });

        IReadOnlyList<RankedTorrent> older = await DarkMatterRound(DarkMatter2015, h265At720);

        Assert.Equal("FE037FFB0951D1ED1442B0CCF6197902C642FEA1", older[0].Torrent.InfoHash);
        Assert.Equal("Dark.Matter.S02E04.720p.HEVC.x265-MeGusta", older[0].Torrent.Title);

        Assert.Empty(await DarkMatterRound(DarkMatter2024, h265At720));
    }

    /// <summary>The Pirate Bay's answer to <c>Dark Matter S02E04</c>, as the episode search puts it to the round.</summary>
    private static Task<IReadOnlyList<RankedTorrent>> DarkMatterRound(TrackedEpisode episode, SettingsByShow settings)
    {
        FakeFetch fetch = new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, "Dark Matter S02E04"), Capture.Fixture("fallback-apibay-dark-matter-s02e04.json"))
            .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, "Dark Matter S02E04"), Capture.Fixture("fallback-apibay-dark-matter-s02e04.json"));

        Decisions decisions = new(settings, Blacklist.None);

        return IndexerSites.Round(fetch, [IndexerSites.ThePirateBay]).AskForEpisodeAsync(
            episode,
            title => decisions.JudgeName(ReleaseName.Parse(title), episode).Accepted,
            Blacklist.None,
            new(),
            CancellationToken.None);
    }

    /// <remarks>
    /// <para>
    /// <strong>A release name's torrents are held to the episode's air date too.</strong> A name no source
    /// dated reached the indexers as <c>Dark.Matter.S02E04.1080p.WEB.x264-FaiLED</c>, and Torrentz2 listed it —
    /// uploaded in January 2019, years before the 2024 programme's S02E04 aired. It was taken, twice, on
    /// 17 September 2026. The date was on the page; only the episode search looked at it.
    /// </para>
    /// <para>
    /// Torrentz2 left the catalogue on 15 October 2026, when its domain stopped existing, and no indexer that
    /// still ships answered that name on a page captured here. So the rule is held on 1337x's real answer for
    /// Silo's name, which dates the TGx upload 15 November 2024, put for an episode recorded as airing a year
    /// later: the page is as 1337x sent it, the air date is this test's own. The same page for the episode
    /// the upload was made for is taken, so the date is the only thing that leaves it out.
    /// </para>
    /// <para>
    /// Left out, it is not even read further: no detail page is asked for a torrent that will not be taken.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReleaseNamesTorrentUploadedLongBeforeTheEpisodeAiredIsNotTaken()
    {
        TrackedEpisode aYearLater = IndexerSites.SiloEpisode with { AirDate = new DateOnly(2025, 11, 15) };

        FakeFetch fetch = new FakeFetch().AnsweringSilo();

        IReadOnlyList<RankedTorrent> ranked = await IndexerSites.Round(fetch, [IndexerSites.X1337])
            .AskAsync([IndexerSites.Silo], aYearLater, Blacklist.None, new(), CancellationToken.None);

        Assert.Empty(ranked);
        Assert.DoesNotContain(fetch.Asked, address => address.ToString() == IndexerSites.X1337Detail);

        FakeFetch onTheDay = new FakeFetch().AnsweringSilo();

        IReadOnlyList<RankedTorrent> taken = await IndexerSites.Round(onTheDay, [IndexerSites.X1337])
            .AskAsync([IndexerSites.Silo], IndexerSites.SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        Assert.Equal(IndexerSites.TgxHash, Assert.Single(taken).Torrent.InfoHash);
        Assert.Contains(onTheDay.Asked, address => address.ToString() == IndexerSites.X1337Detail);
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent any indexer dates too old is left out wherever it is listed.</strong> On 17 September
    /// 2026, with 0.6.2 in, LimeTorrents listed the 2015 Dark Matter's FraMeSToR remux with no date, and it was
    /// grabbed for the 2024 programme's S02E04 a third time. The Pirate Bay lists the same torrent — the same
    /// info hash, <c>FD77A7E3…</c> — dated 6 February 2026. A torrent is uploaded once: any indexer that dates
    /// it is dating it for all of them.
    /// </para>
    /// <para>
    /// <strong>And where another programme of the same name is known, an undated result is not taken.</strong>
    /// The same LimeTorrents page lists <c>Dark Matter S02E04 1080p WEB x264-FaiLED[PRiME]</c>, another 2015
    /// release, dated by no indexer at all. Asked with only dated results counting — the cycle's answer when the
    /// name sources gave nothing but an older programme's names — it takes no part; asked without, it does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentAnyIndexerDatesTooOldIsLeftOutWhereverItIsListed()
    {
        const string frameStor = "FD77A7E3EE956D3D1DE9B3BBF0DE99D7047F6A24";
        const string failed = "6EF5E9C36302F8D23787CC0E7CCA06C2DAC562B4";

        IReadOnlyList<RankedTorrent> datedOnly = await DarkMatterOnBoth(datedOnly: true);
        IReadOnlyList<RankedTorrent> undatedCount = await DarkMatterOnBoth(datedOnly: false);

        Assert.DoesNotContain(datedOnly, torrent => torrent.Torrent.InfoHash == frameStor || torrent.Torrent.InfoHash == failed);

        Assert.DoesNotContain(undatedCount, torrent => torrent.Torrent.InfoHash == frameStor);
        Assert.Contains(undatedCount, torrent => torrent.Torrent.InfoHash == failed);
    }

    /// <summary>LimeTorrents and The Pirate Bay answering <c>Dark Matter S02E04</c>, put for the 2024 programme.</summary>
    private static Task<IReadOnlyList<RankedTorrent>> DarkMatterOnBoth(bool datedOnly)
    {
        FakeFetch fetch = new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, "Dark Matter S02E04"), Capture.Fixture("fallback-apibay-dark-matter-s02e04.json"))
            .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, "Dark Matter S02E04"), Capture.Fixture("fallback-apibay-dark-matter-s02e04.json"))
            .Answers(IndexerSites.Exact(LimeTorrents, "Dark Matter S02E04"), Capture.Fixture("fallback-limetorrents-dark-matter-s02e04.html"))
            .Answers(IndexerSites.Words(LimeTorrents, "Dark Matter S02E04"), Capture.Fixture("fallback-limetorrents-dark-matter-s02e04.html"));

        Decisions decisions = new(Settings, Blacklist.None);

        return IndexerSites.Round(fetch, [LimeTorrents, IndexerSites.ThePirateBay]).AskForEpisodeAsync(
            DarkMatter2024,
            title => decisions.JudgeName(ReleaseName.Parse(title), DarkMatter2024).Accepted,
            Blacklist.None,
            new(),
            CancellationToken.None,
            datedOnly);
    }

    /// <remarks>
    /// The other half: with no sign of another programme of the same name, an undated result counts as its
    /// title says. LimeTorrents writes no dates, and South Park S15E12's CtrlHD wins its answer.
    /// </remarks>
    [Fact]
    public async Task WithNoOtherProgrammeOfTheNameAnUndatedResultStillCounts()
    {
        Decisions decisions = new(Settings, Blacklist.None);

        IReadOnlyList<RankedTorrent> ranked = await IndexerSites.Round(Answering(), [LimeTorrents]).AskForEpisodeAsync(
            SouthPark,
            title => decisions.JudgeName(ReleaseName.Parse(title), SouthPark).Accepted,
            Blacklist.None,
            new(),
            CancellationToken.None);

        Assert.Equal(CtrlHdHash, ranked[0].Torrent.InfoHash);
    }

    private static readonly TrackedEpisode DarkMatter2024 =
        new(new(196322, 2, 4), "Dark Matter", 2024, LibraryKind.Television, null, new DateOnly(2026, 9, 17), EpisodeState.Missing);

    private static readonly TrackedEpisode DarkMatter2015 =
        new(new(61889, 2, 4), "Dark Matter", 2015, LibraryKind.Television, null, new DateOnly(2016, 7, 22), EpisodeState.Missing);

    private static readonly TrackedEpisode SouthPark =
        new(new(2190, 15, 12), "South Park", 1997, LibraryKind.Television, "1%", new DateOnly(2011, 11, 16), EpisodeState.Missing);

    private static readonly TrackedEpisode SoloLeveling =
        new(new(88, 2, 1), "Solo Leveling", 2024, LibraryKind.Anime, null, new DateOnly(2025, 1, 5), EpisodeState.Missing, 13);

    private static SettingsByShow Settings { get; } =
        SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", EnglishOnly = true, Searched = true });

    private static SettingsByShow AnimeSettings { get; } =
        SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", Searched = true });

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

    /// <summary>Nyaa and LimeTorrents answering SKYANiME's name and the episode as they did on 15 October 2026.</summary>
    private static FakeFetch AnsweringSkyanimeAndTheEpisode()
    {
        return new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.Nyaa, Skyanime), Capture.Fixture("anime-nyaa-skyanime-exact.xml"))
            .Answers(IndexerSites.Words(IndexerSites.Nyaa, Skyanime), Capture.Fixture("anime-nyaa-skyanime-words.xml"))
            .Answers(IndexerSites.Exact(LimeTorrents, Skyanime), Capture.Fixture("anime-limetorrents-skyanime-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, Skyanime), Capture.Fixture("anime-limetorrents-skyanime-words.html"))
            .Answers(IndexerSites.Exact(IndexerSites.Nyaa, "Solo Leveling S02E01"), Capture.Fixture("anime-nyaa-solo-leveling-s02e01-exact.xml"))
            .Answers(IndexerSites.Words(IndexerSites.Nyaa, "Solo Leveling S02E01"), Capture.Fixture("anime-nyaa-solo-leveling-s02e01-words.xml"))
            .Answers(IndexerSites.Exact(LimeTorrents, "Solo Leveling S02E01"), Capture.Fixture("anime-limetorrents-solo-leveling-s02e01-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, "Solo Leveling S02E01"), Capture.Fixture("anime-limetorrents-solo-leveling-s02e01-words.html"));
    }

    private static SearchCycle Cycle(FakeFetch fetch, FakeTorrentEngine engine, IReleaseNames names, ActivityJournal journal)
    {
        return new(
            names,
            IndexerSites.Finding(fetch, sources: [LimeTorrents], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }

    private static SearchCycle AnimeCycle(FakeFetch fetch, FakeTorrentEngine engine, IReleaseNames names)
    {
        ActivityJournal journal = new();

        return new(
            names,
            IndexerSites.Finding(fetch, sources: [LimeTorrents, IndexerSites.Nyaa], journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }
}
