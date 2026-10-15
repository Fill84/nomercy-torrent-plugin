using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// A show downloads only a release a name source names; an anime is still searched for by show and episode.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/release-names.md</c> § Release names first — the owner's rule of 15 October 2026. South Park
/// S29E03 aired at 02:00 UTC that day. At 03:05 the search by show and episode took
/// <c>South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM</c>, a capture with the intro missing that no name
/// source lists, and the night before it had taken five TorrentGalaxy torrents titled as MeGusta's, CAKES's
/// and FLUX's WEB releases that held no video. The first real release,
/// <c>South.Park.S29E03.1080p.WEB.H264-CAKES</c>, reached the name sources at 06:00.
/// </para>
/// <para>
/// Every page here was captured on 15 October 2026 between 11:17 and 11:21 UTC through the capture tool:
/// the four name sources' searches for <c>South Park S29E03</c>, which name CAKES, and for
/// <c>South Park S29E04</c>, which has not aired and of which none of them names anything; The Pirate Bay's,
/// LimeTorrents' and TorrentGalaxy's answers to <c>South Park S29E03</c>, which list ViETNAM's upload — on
/// the first two as torrent <c>7445E491…</c> — and the CAKES-titled ones; the two indexers' answers to the
/// CAKES name; and Nyaa's and LimeTorrents' answers to <c>Solo Leveling S02E01</c>. The feeds are the ones
/// captured on 15 September 2026, which carry no South Park at all.
/// </para>
/// </remarks>
public class OnlyANamedReleaseTests
{
    private const string Cakes = "South.Park.S29E03.1080p.WEB.H264-CAKES";

    /// <summary>The real CAKES upload: on The Pirate Bay and on LimeTorrents.</summary>
    private const string CakesHash = "70AFDB530DD7CD04D1C36E142CC523A56E738DE8";

    private static readonly SourceDefinition LimeTorrents =
        new("LimeTorrents", "site", "https://www.limetorrents.fun/search/all/{query}/")
        {
            Priority = 35,
            FirstChoice = 3,
        };

    private static readonly TrackedEpisode SouthPark =
        new(new(2190, 29, 3), "South Park", 1997, LibraryKind.Television, null, new DateOnly(2026, 10, 15), EpisodeState.Missing);

    private static readonly TrackedEpisode SoloLeveling =
        new(new(88, 2, 1), "Solo Leveling", 2024, LibraryKind.Anime, null, new DateOnly(2025, 1, 5), EpisodeState.Missing, 13);

    private static SettingsByShow Settings { get; } =
        SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", EnglishOnly = true, Searched = true });

    /// <remarks>
    /// <para>
    /// <strong>Before 06:00 no name source names a release of S29E03, and nothing is downloaded.</strong> The
    /// indexers list ViETNAM's torrent and the CAKES-titled uploads all the same; none of them is asked, the
    /// client is handed nothing, and the Activity page says why. The episode is searched for again on the next
    /// run.
    /// </para>
    /// <para>
    /// Put back for a show, the search by show and episode takes ViETNAM's torrent from these very pages: it
    /// is on both indexers, as the real CAKES upload is, and LimeTorrents — asked first — lists it first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BeforeAnyNameSourceNamesAReleaseNothingIsDownloadedForAShow()
    {
        FakeFetch fetch = Indexers(AnsweringSearches(new FakeFetch().AnsweringFeeds(), "s29e04"));
        ActivityJournal journal = new();
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, journal, engine, [LimeTorrents, IndexerSites.ThePirateBay, IndexerSites.TorrentGalaxy])
            .RunAsync([SouthPark], new(Settings, Blacklist.None, DryRun: false, @"C:\downloads"), CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.False(outcome.HandedOver, outcome.Release);
        Assert.Null(outcome.Release);
        Assert.Empty(engine.Taken);

        // Every name source was asked, and no indexer was: there was nothing to ask one for.
        Assert.All(Searches("s29e03"), search => Assert.Contains(fetch.Asked, address => address.ToString() == search));
        Assert.DoesNotContain(
            fetch.Asked,
            address => address.Host is "www.limetorrents.fun" or "apibay.org" or "torrentgalaxy.one");
        Assert.False(outcome.Searched);

        Assert.Equal("no name source gave a release name for it, so nothing is downloaded until one does", outcome.Detail);
        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Decide && entry.Detail == outcome.Detail);
    }

    /// <remarks>
    /// <strong>From 06:00 the name sources name CAKES's release, and that is what is downloaded.</strong> The
    /// name is put to the indexers letter for letter, and the real upload — on both of them — is handed over
    /// under the name the sources gave it. The show and episode alone are never asked.
    /// </remarks>
    [Fact]
    public async Task OnceTheNameSourcesNameAReleaseThatReleaseIsSearchedAndTaken()
    {
        FakeFetch fetch = Indexers(AnsweringSearches(new FakeFetch().AnsweringFeeds(), "s29e03"));
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(fetch, new ActivityJournal(), engine, [LimeTorrents, IndexerSites.ThePirateBay])
            .RunAsync([SouthPark], new(Settings, Blacklist.None, DryRun: false, @"C:\downloads"), CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(CakesHash, outcome.InfoHash);
        Assert.Equal(Cakes, outcome.Release);
        Assert.Contains(CakesHash, Assert.Single(engine.Taken).Source, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(fetch.Asked, address => address.ToString() == IndexerSites.Exact(LimeTorrents, Cakes));
        Assert.Contains(fetch.Asked, address => address.ToString() == IndexerSites.Exact(IndexerSites.ThePirateBay, Cakes));
        Assert.DoesNotContain(
            fetch.Asked,
            address => address.ToString() == IndexerSites.Exact(LimeTorrents, "South Park S29E03")
                       || address.ToString() == IndexerSites.Exact(IndexerSites.ThePirateBay, "South Park S29E03"));
    }

    /// <remarks>
    /// <para>
    /// <strong>An anime is still searched for by show and episode.</strong> Fansubs are its real releases and
    /// no name source lists them, so with no name at all the indexers are asked for
    /// <c>Solo Leveling S02E01</c>, Nyaa first, and the best result that names the episode and meets the
    /// show's settings is taken.
    /// </para>
    /// <para>
    /// Nyaa's answer, captured that morning, lists ToonsHub's and VARYG's 1080p H.264 uploads, and
    /// LimeTorrents' lists iNSPiRE's. The one torrent on both is Tenrai-Sensei's HEVC, which the show's h264
    /// refuses, so every torrent left is on one indexer and the one found first wins: ToonsHub's newest
    /// upload, <c>D8FA5AC1…</c>, on Nyaa, which is asked before LimeTorrents.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAnimeNoNameSourceNamesIsSearchedByShowAndEpisodeNyaaFirst()
    {
        FakeFetch fetch = new FakeFetch()
            .Answers(IndexerSites.Exact(IndexerSites.Nyaa, "Solo Leveling S02E01"), Capture.Fixture("anime-nyaa-solo-leveling-s02e01-exact.xml"))
            .Answers(IndexerSites.Words(IndexerSites.Nyaa, "Solo Leveling S02E01"), Capture.Fixture("anime-nyaa-solo-leveling-s02e01-words.xml"))
            .Answers(IndexerSites.Exact(LimeTorrents, "Solo Leveling S02E01"), Capture.Fixture("anime-limetorrents-solo-leveling-s02e01-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, "Solo Leveling S02E01"), Capture.Fixture("anime-limetorrents-solo-leveling-s02e01-words.html"));

        ActivityJournal journal = new();
        FakeTorrentEngine engine = new();

        CycleReport report = await new SearchCycle(
                new FixedNames(),
                IndexerSites.Finding(fetch, sources: [LimeTorrents, IndexerSites.Nyaa], journal: journal),
                journal,
                new Grab(engine, new EndlessDisk(null), journal))
            .RunAsync(
                [SoloLeveling],
                new(SettingsByShow.Every(new() { Quality = "1080p", Codec = "h264", Searched = true }), Blacklist.None, DryRun: false, @"C:\downloads"),
                CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.Equal("nyaa.si", fetch.Asked[0].Host);
        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.True(outcome.Searched);
        Assert.Equal("Nyaa", outcome.Source);
        Assert.Equal("D8FA5AC1CE27DAB1F07F0C93111C5763908182E8", outcome.InfoHash);
        Assert.Single(engine.Taken);
    }

    /// <summary>The four name sources' searches for South Park's episode of season 29.</summary>
    private static string[] Searches(string episode)
    {
        return
        [
            $"https://predb.me/?search=South+Park+{episode.ToUpperInvariant()}&rss=1",
            $"https://api.srrdb.com/v1/search/south-park-{episode}",
            $"https://api.predb.net/feed/?q=South+Park+{episode.ToUpperInvariant()}",
            $"https://www.scnsrc.me/feed/?s=South+Park+{episode.ToUpperInvariant()}",
        ];
    }

    /// <summary>
    /// The four searches for S29E03 answering what each answered when asked about <paramref name="episode"/>:
    /// <c>s29e03</c> after 06:00, or <c>s29e04</c>, the answer of a site that names no release of the episode.
    /// </summary>
    private static FakeFetch AnsweringSearches(FakeFetch fetch, string episode)
    {
        string[] asked = Searches("s29e03");

        return fetch
            .Answers(asked[0], Capture.Fixture($"names-predb-search-south-park-{episode}.xml"))
            .Answers(asked[1], Capture.Fixture($"names-srrdb-search-south-park-{episode}.json"))
            .Answers(asked[2], Capture.Fixture($"names-predbnet-search-south-park-{episode}.xml"))
            .Answers(asked[3], Capture.Fixture($"names-scenesource-search-south-park-{episode}.xml"));
    }

    /// <summary>
    /// The indexers answering the show and episode and the CAKES name as they did that morning.
    /// </summary>
    /// <remarks>
    /// Anything else — a TorrentGalaxy row's own page, which a search by show and episode would read for a
    /// hash — answers empty: no page of it was captured, and an empty answer gives a row no hash.
    /// </remarks>
    private static FakeFetch Indexers(FakeFetch fetch)
    {
        return fetch
            .AnswersAnything(string.Empty)
            .Answers(IndexerSites.Exact(LimeTorrents, "South Park S29E03"), Capture.Fixture("s29e03-limetorrents-south-park-s29e03-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, "South Park S29E03"), Capture.Fixture("s29e03-limetorrents-south-park-s29e03-words.html"))
            .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, "South Park S29E03"), Capture.Fixture("s29e03-apibay-south-park-s29e03-exact.json"))
            .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, "South Park S29E03"), Capture.Fixture("s29e03-apibay-south-park-s29e03-words.json"))
            .Answers(IndexerSites.Words(IndexerSites.TorrentGalaxy, "South Park S29E03"), Capture.Fixture("s29e03-torrentgalaxy-south-park-s29e03.html"))
            .Answers(IndexerSites.Exact(LimeTorrents, Cakes), Capture.Fixture("s29e03-limetorrents-cakes-exact.html"))
            .Answers(IndexerSites.Words(LimeTorrents, Cakes), Capture.Fixture("s29e03-limetorrents-cakes-words.html"))
            .Answers(IndexerSites.Exact(IndexerSites.ThePirateBay, Cakes), Capture.Fixture("s29e03-apibay-cakes-exact.json"))
            .Answers(IndexerSites.Words(IndexerSites.ThePirateBay, Cakes), Capture.Fixture("s29e03-apibay-cakes-words.json"));
    }

    private static SearchCycle Cycle(FakeFetch fetch, ActivityJournal journal, FakeTorrentEngine engine, SourceDefinition[] indexers)
    {
        return new(
            NameSourceSites.Over(fetch, journal),
            IndexerSites.Finding(fetch, sources: indexers, journal: journal),
            journal,
            new Grab(engine, new EndlessDisk(null), journal));
    }
}
