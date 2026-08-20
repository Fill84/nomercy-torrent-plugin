using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// An anime library, from what the server holds to what would be taken.
/// </summary>
/// <remarks>
/// <para>
/// The two halves of anime were each proved on their own and never joined.
/// <c>S1-03</c> builds an episode's absolute number from the library's own
/// episode list; <c>S3-03</c> asks the pool under that number. Nothing put the
/// two together, so nothing said whether the number the library produces is the
/// number a release is really posted under.
/// </para>
/// <para>
/// It is the whole point of anime support: a fansub row carries no season tag
/// at all, so if the absolute is wrong by one the episode is never found, and
/// every page still reads as though the plugin were working.
/// </para>
/// </remarks>
public class AnimeEndToEndTests
{
    /// <remarks>
    /// The number is the episode's own plus the lengths of the seasons before
    /// it — not its position in a list, which agrees only while the list is
    /// complete. Season one has twelve, so season two's eighth is twenty, and
    /// twenty is what the captured Nyaa row is really posted as. Nothing in the
    /// test says twenty to the pipeline: the library says how long season one
    /// is, and twenty is what comes out.
    /// </remarks>
    [Fact]
    public async Task AnAnimeLibraryProducesADecisionForAnEpisodeNobodyWroteASeasonTagFor()
    {
        FakeLibrary server = Seeded();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

        IReadOnlyList<TrackedEpisode> derived = await new MissingRefresh(server, clock)
            .DeriveAsync(new(), CancellationToken.None);

        TrackedEpisode missing = Assert.Single(derived, one => one.State == EpisodeState.Missing);

        Assert.Equal(new EpisodeKey(77, 2, 8), missing.Key);
        Assert.Equal(20, missing.Absolute);
        Assert.Equal(LibraryKind.Anime, missing.Kind);

        // The pool holds it under the absolute number and under nothing else,
        // which is how a fansub really posts it.
        FakePool pool = new();

        await pool.AddAsync(
            [new(PoolKey.ForAbsolute("Rilakkuma", 20), Posted, "Nyaa", clock.GetUtcNow())],
            CancellationToken.None);

        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-subsplease.xml"));

        CycleReport report = await Cycle(fetch, pool).RunAsync(
            derived,
            new(new() { MaximumResolution = "1080p", EnglishOnly = false }, Blacklist.None, DryRun: true, Folder),
            CancellationToken.None);

        EpisodeOutcome outcome = Assert.Single(report.Outcomes, one => one.Episode == missing.Key);

        Assert.Equal(Posted, outcome.Release);
        Assert.Contains("dry run", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The release as the captured Nyaa page really carries it.</summary>
    private const string Posted = "[SubsPlease] Rilakkuma - 20 (1080p) [A8302A8E].mkv";

    private const string Folder = @"C:\downloads";

    /// <summary>
    /// One anime show with a full first season and a gap in the second.
    /// </summary>
    /// <remarks>
    /// Aired yesterday, so nothing is waiting to air. Every episode but the one
    /// has a file, so the missing list is exactly one long and the decision
    /// cannot be about something else.
    /// </remarks>
    private static FakeLibrary Seeded()
    {
        FakeLibrary server = new();
        DateOnly aired = new(2026, 8, 19);

        server.Show(77, "Rilakkuma", 2019, LibraryKind.Anime, "lib-anime");

        for (int episode = 1; episode <= 12; episode++)
        {
            server.Episode(77, 1, episode, aired, hasFile: true);
        }

        for (int episode = 1; episode <= 10; episode++)
        {
            server.Episode(77, 2, episode, aired, hasFile: episode != 8);
        }

        return server;
    }

    private static SearchCycle Cycle(FakeFetch fetch, FakePool pool)
    {
        SourceCatalogue catalogue = SourceCatalogue.Build(Sources, [], []);
        ActivityJournal journal = new();
        Readers readers = Readers.Shipped();

        return new(
            new NameResolve(catalogue, fetch, readers, pool, journal, TimeProvider.System),
            new Find(catalogue, fetch, readers, journal),
            journal);
    }

    /// <summary>Nyaa alone, scoped to anime as the shipped catalogue scopes it.</summary>
    private static readonly SourceDefinition[] Sources =
    [
        new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}")
        {
            Priority = 50,
            Libraries = [LibraryKinds.Anime],
        },
    ];
}
