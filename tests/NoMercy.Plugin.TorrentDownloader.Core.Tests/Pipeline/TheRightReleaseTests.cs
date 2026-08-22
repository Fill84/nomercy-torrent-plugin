using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// One episode, four sites, and the release that ought to win.
/// </summary>
/// <remarks>
/// <para>
/// The owner watched a cycle take
/// <c>Sugar 2024 S02E08 1080p ATVP WEB-DL DDP5 1 Atmos H 264-FLUX exe</c> on
/// 22 August 2026 and said, correctly, that
/// <c>Sugar 2024 S02E08 1080p WEB H264-CAKES</c> is the release. This is that
/// episode, against what those four sites really answered that day, through
/// the real catalogue, the real readers, the real profile and the real
/// decision.
/// </para>
/// <para>
/// Nothing here is stood in for except the wire and the torrent client, and
/// neither of those decides anything. It exists so that the next change to any
/// of it has to keep answering with the right release.
/// </para>
/// </remarks>
public class TheRightReleaseTests
{
    /// <remarks>
    /// <para>
    /// Two episodes of one programme, and the answer is different for each
    /// because what is posted is different. S02E08 has a CAKES release and it
    /// is the best seeded thing on every site that carries it. S02E01 has no
    /// CAKES release at all, and the best seeded 1080p copy is playWEB's — so
    /// that is the right answer there, and taking CAKES would be as wrong as
    /// taking FLUX was.
    /// </para>
    /// <para>
    /// The counts are the swarm's rather than any one site's: apibay says 458
    /// for the S02E08 release and LimeTorrents 439 for the same file.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(8, "Sugar 2024 S02E08 1080p WEB H264-CAKES", 458)]
    [InlineData(1, "Sugar 2024 S02E01 Home Away from Home 1080p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB", 247)]
    public async Task TheBestCopyAnybodyIsServingIsTheOneThatIsTaken(int number, string release, int seeders)
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(engine, Answering(number)).RunAsync(
            [Gap(number)],
            new(new() { MaximumResolution = "1080p" }, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(release, outcome.Release);
        Assert.Equal(seeders, outcome.Seeders);

        // Something the client can be handed, carrying the hash of the torrent
        // that was chosen rather than of whatever the page happened to mention.
        string magnet = Assert.Single(engine.Taken).Source;

        Assert.StartsWith("magnet:?xt=urn:btih:", magnet, StringComparison.Ordinal);
        Assert.Equal(40, Magnets.HashOf(magnet)!.Length);

        Console.WriteLine($"S02E{number:00}: {outcome.Source} -> {Magnets.HashOf(magnet)}");
    }

    /// <remarks>
    /// <para>
    /// The show whose name five other programmes begin with. This page carries
    /// <c>Lucky Luke 2026 S01E02 1080p WEB h264-EDITH</c> — a different
    /// programme, the same slot, the same resolution — and
    /// <c>Dexter Resurrection S01E02 MULTi 1080p WEB x264-LUCKY</c>, where
    /// LUCKY is the release group. Neither is this episode and neither may be
    /// taken for it.
    /// </para>
    /// <para>
    /// Three sites rather than four: apibay answered 429 to the capture and
    /// what those three served is enough to decide. The right release is
    /// <c>Lucky 2026 S01E02 1080p WEB h264-ETHEL</c> at 878 seeders, confirmed
    /// by the owner.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADifferentProgrammeBeginningWithTheSameWordIsNotTaken()
    {
        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(engine, AnsweringLucky()).RunAsync(
            [
                new TrackedEpisode(
                    new(4471, 1, 2),
                    "Lucky",
                    2026,
                    LibraryKind.Television,
                    null,
                    new DateOnly(2026, 7, 7),
                    EpisodeState.Missing),
            ],
            new(new() { MaximumResolution = "1080p" }, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("Lucky 2026 S01E02 1080p WEB h264-ETHEL", outcome.Release);
        Assert.Equal(878, outcome.Seeders);

        string magnet = Assert.Single(engine.Taken).Source;

        Assert.StartsWith("magnet:?xt=urn:btih:", magnet, StringComparison.Ordinal);

        Console.WriteLine($"Lucky S01E02: {outcome.Source} -> {Magnets.HashOf(magnet)}");

        // And the two decoys were never even weighed against it.
        Assert.DoesNotContain(
            report.Skipped,
            skipped => skipped.Title.Contains("Lucky Luke", StringComparison.OrdinalIgnoreCase)
                       || skipped.Title.Contains("Dexter", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// <strong>Why that one and not the other.</strong> A copy that is
    /// acceptable and not taken is recorded nowhere, so on 22 August 2026 the
    /// owner asked why an x265 release had won over the CAKES one and nothing
    /// in the plugin could answer: the winner was on the page, the runner-up
    /// was on no page at all. The decision now carries what it beat and by how
    /// much.
    /// </remarks>
    [Fact]
    public async Task TheDecisionSaysWhatItWasTakenAheadOf()
    {
        CycleReport report = await Cycle(new(), Answering(8)).RunAsync(
            [Gap(8)],
            new(new() { MaximumResolution = "1080p" }, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.NotNull(outcome.Considered);

        // The runner-up on this page is the x265 release, and the number it
        // lost by is there to be read.
        Assert.Contains("MeGusta", outcome.Considered!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seeders", outcome.Considered!, StringComparison.Ordinal);

        // And never the copy that was taken.
        Assert.DoesNotContain(outcome.Release!, outcome.Considered!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <strong>The release name comes from the name databases, and the copy
    /// that is that release wins outright.</strong> SceneSource publishes the
    /// scene name minutes after a release lands, which is what the pool is for
    /// and what the indexers are meant to be asked about. Ranking on seeders
    /// alone throws that away: for Silo S03E04 an x265 re-encode is seeded by
    /// 2,898 and the scene release by 1,774, so the re-encode wins a contest
    /// it should never have been in.
    /// </para>
    /// <para>
    /// And the name that is recorded is the pool's, not the indexer's. The
    /// same release comes off TorrentDownloads as
    /// <c>- Silo S03E04 1080p WEB H264-CAKES</c> and off another site in lower
    /// case with <c>[EZTVx to]</c> stuck on the end. That name is written
    /// against the grab and is what staging matches a finished file by, so a
    /// site's rendering of it is a name nothing answers to.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSceneReleaseTheNameDatabasesKnowIsTheOneThatIsTaken()
    {
        FakePool pool = new();

        // Exactly what SceneSource had for this episode on 22 August 2026.
        await pool.AddAsync(
            [new("silo|s03e04", "Silo S03E04 1080p WEB H264-CAKES", "SceneSource", DateTimeOffset.UtcNow)],
            CancellationToken.None);

        FakeTorrentEngine engine = new();

        CycleReport report = await Cycle(engine, AnsweringSilo(), pool).RunAsync(
            [SiloGap(4)],
            new(new() { MaximumResolution = "1080p" }, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes);

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal("Silo S03E04 1080p WEB H264-CAKES", outcome.Release);
    }

    /// <summary>The Silo gap, as the owner's library holds it.</summary>
    private static TrackedEpisode SiloGap(int number)
    {
        return new(
            new(1399, 3, number),
            "Silo",
            2023,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 7, 23),
            EpisodeState.Missing);
    }

    /// <summary>What three sites really answered for Silo S03E04.</summary>
    private static FakeFetch AnsweringSilo()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        fetch.Answers("https://apibay.org/q.php?q=Silo+S03E04&cat=", Capture.Fixture("silo4-apibay.json"));
        fetch.Answers(
            "https://www.limetorrents.lol/search/all/Silo+S03E04/",
            Capture.Fixture("silo4-limetorrents.html"));
        fetch.Answers(
            "https://torrentgalaxy.one/get-posts/keywords:Silo%20S03E04/",
            Capture.Fixture("silo4-torrentgalaxy.html"));
        fetch.Answers(
            "https://www.torrentdownloads.pro/search/?search=Silo+S03E04",
            Capture.Fixture("silo4-torrentdownloads.html"));

        return fetch;
    }

    /// <remarks>
    /// Every episode is asked about by its own number. What another episode's
    /// search turned up is a candidate and never an answer — the fault that let
    /// a leftover settle Sugar S02E08 while the release everybody was seeding
    /// went unfetched.
    /// </remarks>
    [Fact]
    public async Task TheEpisodeIsAskedAboutByItsOwnNumber()
    {
        FakeFetch fetch = Answering(8);

        await Cycle(new(), fetch).RunAsync(
            [Gap(8)],
            new(new() { MaximumResolution = "1080p" }, Blacklist.None, DryRun: false, @"C:\downloads"),
            CancellationToken.None);

        Assert.Contains(
            fetch.Asked,
            address => address.ToString().Contains("Sugar+S02E08", StringComparison.OrdinalIgnoreCase)
                       || address.ToString().Contains("Sugar%20S02E08", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One gap, as the owner's library holds it.</summary>
    private static TrackedEpisode Gap(int number)
    {
        return new(
            new(9931, 2, number),
            "Sugar",
            2024,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 8, 6),
            EpisodeState.Missing);
    }

    /// <summary>The four sites, at the addresses the plugin itself builds.</summary>
    private static readonly SourceDefinition[] Sources =
    [
        new("The Pirate Bay", "apibay", "https://apibay.org/q.php?q={query}&cat=") { Priority = 45 },
        new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/") { Priority = 35 },
        new("TorrentGalaxy", "site", "https://torrentgalaxy.one/get-posts/keywords:{query}/")
        {
            Reader = "torrentgalaxy",
            Query = QueryStyles.Spaced,
            Priority = 30,
        },
        new("TorrentDownloads", "site", "https://www.torrentdownloads.pro/search/?search={query}")
        {
            Reader = "torrentdownloads",
            Priority = 25,
        },
    ];

    /// <summary>
    /// What those four really answered, and a name database with nothing in it.
    /// </summary>
    /// <remarks>
    /// Nothing has a name for this episode, which is the harder case and the
    /// real one: the pool was empty for most of the owner's library.
    /// </remarks>
    private static FakeFetch Answering(int number)
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        string term = $"Sugar+S02E{number:00}";
        string spaced = $"Sugar%20S02E{number:00}";
        string set = number == 8 ? "sugar" : "sugar1";

        fetch.Answers($"https://apibay.org/q.php?q={term}&cat=", Capture.Fixture($"{set}-apibay.json"));
        fetch.Answers(
            $"https://www.limetorrents.lol/search/all/{term}/",
            Capture.Fixture($"{set}-limetorrents.html"));
        fetch.Answers(
            $"https://torrentgalaxy.one/get-posts/keywords:{spaced}/",
            Capture.Fixture($"{set}-torrentgalaxy.html"));
        fetch.Answers(
            $"https://www.torrentdownloads.pro/search/?search={term}",
            Capture.Fixture($"{set}-torrentdownloads.html"));

        return fetch;
    }

    /// <summary>What three sites really answered for Lucky S01E02.</summary>
    private static FakeFetch AnsweringLucky()
    {
        FakeFetch fetch = new();

        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        fetch.Answers(
            "https://www.limetorrents.lol/search/all/Lucky+S01E02/",
            Capture.Fixture("lucky2-limetorrents.html"));
        fetch.Answers(
            "https://torrentgalaxy.one/get-posts/keywords:Lucky%20S01E02/",
            Capture.Fixture("lucky2-torrentgalaxy.html"));
        fetch.Answers(
            "https://www.torrentdownloads.pro/search/?search=Lucky+S01E02",
            Capture.Fixture("lucky2-torrentdownloads.html"));

        return fetch;
    }

    private static SearchCycle Cycle(FakeTorrentEngine engine, FakeFetch answering, FakePool? pool = null)
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(Sources, [], []);
        ActivityJournal journal = new();
        Readers readers = Readers.Shipped();

        return new(
            new(catalogue, answering, readers, pool ?? new FakePool(), journal, TimeProvider.System),
            new(catalogue, answering, readers, journal),
            journal,
            new Grab(engine, new EndlessDisk(), journal));
    }

    private sealed class EndlessDisk : IStorageSpace
    {
        public long? FreeBytes(string folder)
        {
            return long.MaxValue;
        }
    }
}
