using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The whole chain, once: a library shaped like the server's, through the
/// adapter, the derivation and the store, to the page an owner reads.
/// </summary>
/// <remarks>
/// Sprint 1 is done when the Shows page matches a library counted by hand, so
/// the library below is counted by hand in the comments and the page is
/// asserted against those numbers. Every part of this has its own test; what
/// this adds is that they still agree when joined up, which is the thing unit
/// tests cannot say.
/// </remarks>
public class HandCountedLibraryTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task TheShowsPageMatchesALibraryCountedByHand()
    {
        // Today is 14 August 2026. Everything below is counted against that.
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero));

        FakeLibraryQuery server = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Library("lib-anime", "Anime", "anime")
            .Library("lib-films", "Films", "movie")

            // Silo: four episodes. One on disk, two aired without one, one still
            // to come. → 2 missing, 1 waiting.
            .Show(1, "Silo", "lib-tv", year: 2023)
            .Episode(1, 1, 1, airDate: new DateTime(2023, 5, 5), hasFile: true)
            .Episode(1, 1, 2, airDate: new DateTime(2023, 5, 12))
            .Episode(1, 2, 1, airDate: new DateTime(2026, 8, 14))
            .Episode(1, 2, 2, airDate: new DateTime(2026, 8, 21))

            // Frieren: anime. A complete first season of three on disk, and one
            // missing episode of season two. → 1 missing, 0 waiting, and that
            // episode is the fourth of the series.
            .Show(2, "Frieren", "lib-anime", year: 2023)
            .Episode(2, 1, 1, airDate: new DateTime(2023, 9, 29), hasFile: true)
            .Episode(2, 1, 2, airDate: new DateTime(2023, 10, 6), hasFile: true)
            .Episode(2, 1, 3, airDate: new DateTime(2023, 10, 13), hasFile: true)
            .Episode(2, 2, 1, airDate: new DateTime(2026, 4, 1))

            // A special, and the owner has not asked for specials. → not counted.
            // The second episode is on disk, which is what puts the show in the
            // library at all: a show with nothing on disk is one the server has
            // recommended, not one the owner has.
            .Show(3, "Lioness", "lib-tv", year: 2023)
            .Episode(3, 0, 1, airDate: new DateTime(2023, 7, 1))
            .Episode(3, 1, 1, airDate: new DateTime(2023, 7, 23))
            .Episode(3, 1, 2, airDate: new DateTime(2023, 7, 30), hasFile: true)

            // No folder: nowhere to download to, so out of scope entirely.
            .Show(4, "Homeless", "lib-tv", folder: null)
            .Episode(4, 1, 1, airDate: new DateTime(2020, 1, 1), hasFile: true)

            // A film's show. This plugin has no business with it at all.
            .Show(5, "A film's show", "lib-films")
            .Episode(5, 1, 1, airDate: new DateTime(2020, 1, 1), hasFile: true);

        // Counted by hand: Silo 2 missing 1 waiting, Frieren 1 missing,
        // Lioness 1 missing. Three shows, four missing, one waiting.
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);
        EpisodeRepository episodes = new(database);

        IReadOnlyList<TrackedEpisode> derived = await new MissingRefresh(new HostLibrary(server), clock)
            .DeriveAsync(new Profile(), CancellationToken.None);

        await episodes.ReplaceAsync(derived, CancellationToken.None);

        IReadOnlyList<ShowSummary> shows =
            ShowSummaries.Summarise(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(["Frieren", "Lioness", "Silo"], shows.Select(show => show.Title));
        Assert.Equal(4, shows.Sum(show => show.Missing));
        Assert.Equal(1, shows.Sum(show => show.WaitingToAir));

        ShowSummary silo = shows.Single(show => show.Title == "Silo");
        Assert.Equal(2, silo.Missing);
        Assert.Equal(1, silo.WaitingToAir);
        Assert.Equal(LibraryKind.Television, silo.Kind);

        ShowSummary frieren = shows.Single(show => show.Title == "Frieren");
        Assert.Equal(1, frieren.Missing);
        Assert.Equal(0, frieren.WaitingToAir);
        Assert.Equal(LibraryKind.Anime, frieren.Kind);

        // Three on disk in season one, so season two's first is the fourth of
        // the series — and it is still counted from the episodes on disk.
        Assert.Equal(
            4,
            (await episodes.AllAsync(CancellationToken.None))
                .Single(episode => episode.Key == new EpisodeKey(2, 2, 1))
                .Absolute);

        // And the page says the same numbers as the summaries it was built from.
        PluginView page = ShowsView.Render(shows);
        IReadOnlyList<string> words = Rendered.Words(page);

        Assert.Contains("Silo (2023)", words);
        Assert.Contains("Frieren (2023)", words);
        Assert.Contains("anime", words);
        Assert.DoesNotContain("A film's show", words);
        Assert.DoesNotContain("Homeless", words);
    }

    public void Dispose()
    {

        TemporaryFolder.Forget(_folder);
    }
}
