using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// From a seeded store, through the plugin, to the page — the whole way, so
/// what is asserted is what an owner would read rather than what a view was
/// handed by a test.
/// </summary>
public class ShowsAndQueueViewTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    /// <remarks>
    /// The number on the overview's row equals the rows it summarises, and a show sits in the list of
    /// the library the server put it in. A count kept anywhere else is a second number that can disagree
    /// with its own list — 0.3.4 showed "0 downloads" while two were running.
    /// </remarks>
    [Fact]
    public async Task TheMissingCountIsTheRowsForThatShow()
    {
        using TorrentDownloaderPlugin plugin = await Seeded(
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(1, 1, 2, EpisodeState.Missing),
            Episode(1, 1, 3, EpisodeState.Missing),
            Episode(1, 1, 4, EpisodeState.NotAired),
            Episode(2, 1, 1, EpisodeState.Missing, "Frieren", LibraryKind.Anime),
        ]);

        PluginView page = await View(plugin, Pages.OverviewRoute);

        // Silo: three missing, one waiting. Frieren: one missing, none waiting.
        Assert.Equal("3", Rendered.ById(page, "show-1").Props["missing"]);
        Assert.Equal("1", Rendered.ById(page, "show-2").Props["missing"]);
        Assert.Contains(Rendered.ById(page, OverviewView.TableId(TvLibrary)).Items, row => row.Id == "show-1");
        Assert.Contains(Rendered.ById(page, OverviewView.TableId(AnimeLibrary)).Items, row => row.Id == "show-2");
    }

    /// <remarks>
    /// Two lists, never one. An unaired episode among the missing is work the
    /// plugin is not doing. There used to be a third, <em>given up for now</em>
    /// — the owner's decision of 12 September 2026 dropped the state behind it,
    /// so an episode however many times searched stays in Looking rather than
    /// disappearing into a list that no longer exists.
    /// </remarks>
    [Fact]
    public async Task TheQueueDrawsTwoListsNotThree()
    {
        using TorrentDownloaderPlugin plugin = await Seeded(
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(1, 1, 2, EpisodeState.NotAired),
            Episode(1, 1, 3, EpisodeState.Missing) with { Attempts = 70 },
        ]);

        PluginView page = await View(plugin, Pages.QueueRoute);

        Assert.Equal(
            ["Silo S01E01", "Silo S01E03"],
            RowsOf(page, QueueView.LookingTableId));
        Assert.Equal(
            ["Silo S01E02"],
            RowsOf(page, QueueView.WaitingTableId));

        // Nothing on the page still calls this "given up" — the heading is
        // gone along with the state, not merely emptied.
        Assert.DoesNotContain("Given up for now", Rendered.Words(page));
    }

    /// <remarks>
    /// A hopeless episode is still visible as one rather than gone from the
    /// page: it stays in Looking, and its attempts and when it was last tried
    /// travel with the row exactly as any other episode's do.
    /// </remarks>
    [Fact]
    public async Task ALookingRowCarriesItsAttemptsAndWhenItWasLastTried()
    {
        using TorrentDownloaderPlugin plugin = await Seeded([Episode(1, 1, 1, EpisodeState.Missing)]);

        // attempts and last_search_at are this plugin's own bookkeeping, never
        // part of what a refresh derives — recording a search is the one way
        // anything moves them, so that is the only way to seed them here too.
        EpisodeRepository episodes = await plugin.EpisodesAsync(CancellationToken.None);
        DateTimeOffset lastTried = new(2026, 9, 10, 3, 0, 0, TimeSpan.Zero);

        for (int already = 0; already < 69; already++)
        {
            await episodes.RecordSearchAsync(new(1, 1, 1), lastTried.AddDays(-1), CancellationToken.None);
        }

        await episodes.RecordSearchAsync(new(1, 1, 1), lastTried, CancellationToken.None);

        PluginComponent row = Rendered.All(await View(plugin, Pages.QueueRoute))
            .Single(component => component.Id == $"{QueueView.LookingTableId}-1-1-1");

        Assert.Equal(70, row.Props["attempts"]);
        Assert.Equal("2026-09-10 03:00:00Z", row.Props["last"]);
    }

    /// <remarks>
    /// The order shown is the order a run will ask in: always from the top,
    /// show, season, episode — the owner's decision of 11 September 2026. A
    /// page in any other order is a guess about what the plugin is about to do.
    /// </remarks>
    [Fact]
    public async Task TheOrderIsTheOrderTheyWillBeAskedIn()
    {
        using TorrentDownloaderPlugin plugin = await Seeded(
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(1, 1, 2, EpisodeState.Missing),
            Episode(1, 1, 3, EpisodeState.Missing),
        ]);

        EpisodeRepository episodes = await plugin.EpisodesAsync(CancellationToken.None);
        await episodes.RecordSearchAsync(new(1, 1, 1), new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        await episodes.RecordSearchAsync(new(1, 1, 2), new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        // From the top, whatever was searched when: a stopped run and the next
        // one both begin at the first episode.
        Assert.Equal(
            ["Silo S01E01", "Silo S01E02", "Silo S01E03"],
            RowsOf(await View(plugin, Pages.QueueRoute), QueueView.LookingTableId));
    }

    /// <remarks>
    /// Never searched is not searched long ago, and it is certainly not nought.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeNeverSearchedSaysSo()
    {
        using TorrentDownloaderPlugin plugin = await Seeded([Episode(1, 1, 1, EpisodeState.Missing)]);

        Assert.Contains("never", Rendered.Words(await View(plugin, Pages.QueueRoute)));
    }

    /// <remarks>
    /// An anime episode is named by both forms, because both are what a release
    /// will be called and an owner comparing the page against a site needs
    /// whichever that site uses.
    /// </remarks>
    [Fact]
    public async Task AnAnimeEpisodeIsNamedByBothItsNumbers()
    {
        using TorrentDownloaderPlugin plugin = await Seeded(
        [
            Episode(2, 2, 13, EpisodeState.Missing, "Frieren", LibraryKind.Anime) with { Absolute = 37 },
        ]);

        Assert.Equal(
            ["Frieren S02E13 (37)"],
            RowsOf(await View(plugin, Pages.QueueRoute), QueueView.LookingTableId));
    }

    /// <remarks>
    /// Shows and Queue are reached from the dashboard, not from navigation. The
    /// route table says they exist and which shell each wants, and only two of
    /// the eight pages are ever mounted: the plugin's own page and its settings.
    /// Which sections those two are placed in is the manifest's business and
    /// changes — this counted the entries instead, and said so by failing when
    /// the plugin was put beside the libraries as well as on the dashboard.
    /// </remarks>
    [Fact]
    public void EveryPageIsDeclaredAndOnlyTwoAreMounted()
    {
        using TorrentDownloaderPlugin plugin = new();

        // Which pages the table holds is asserted whole in PagesReachableTests;
        // what matters here is that these two are on it and are not mounts.
        Assert.Contains(Pages.ShowSettingsRoute, plugin.Routes.Routes.Select(route => route.Path));
        Assert.Contains(Pages.QueueRoute, plugin.Routes.Routes.Select(route => route.Path));

        // Resolve answers null for a path no page claims, which is the point of
        // declaring the table at all — and a show's page is found by its id.
        PluginRouteMatch shows = Assert.IsType<PluginRouteMatch>(plugin.Routes.Resolve("/shows/41"));
        Assert.Equal("41", shows.Param("id"));
        PluginRouteMatch settings = Assert.IsType<PluginRouteMatch>(plugin.Routes.Resolve(Pages.SettingsRoute));

        Assert.Equal(PluginLayout.Wide, shows.Route.Layout);
        Assert.Equal(PluginLayout.Wide, settings.Route.Layout);
        Assert.Null(plugin.Routes.Resolve("/no-such-page"));

        // Two pages, however many sections they are placed in.
        Assert.Equal(
            [Pages.OverviewRoute, Pages.SettingsRoute],
            plugin.NavEntries.Select(entry => entry.Route).Distinct().Order());
    }

    private static IReadOnlyList<string> RowsOf(PluginView page, string tableId)
    {
        // The episode cell of every row. A row's cells are its props, read
        // under the column's key, which is how the client reads a table.
        return
        [
            .. Rendered.All(page)
                .Where(component => component.Id.StartsWith($"{tableId}-", StringComparison.Ordinal))
                .Select(component => component.Props.GetValueOrDefault("episode")?.ToString())
                .OfType<string>(),
        ];
    }

    private static Task<PluginView> View(TorrentDownloaderPlugin plugin, string route)
    {
        return plugin.GetViewAsync(new() { Route = route }, CancellationToken.None);
    }

    private const string TvLibrary = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private const string AnimeLibrary = "01HQ5W4GAVF30N10RT6XCF6AJQ";

    private async Task<TorrentDownloaderPlugin> Seeded(IReadOnlyList<TrackedEpisode> episodes)
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new FakeLibraryQuery()
                .Library(TvLibrary, "Series", "tv")
                .Library(AnimeLibrary, "Anime", "anime")
                .Show(1, "Silo", TvLibrary, year: 2023)
                .Show(2, "Frieren", AnimeLibrary, year: 2023),
        });

        await (await plugin.EpisodesAsync(CancellationToken.None)).ReplaceAsync(episodes, CancellationToken.None);

        return plugin;
    }

    private static TrackedEpisode Episode(
        int show,
        int season,
        int number,
        EpisodeState state,
        string title = "Silo",
        LibraryKind kind = LibraryKind.Television)
    {
        return new(
            new(show, season, number),
            title,
            2023,
            kind,
            "An episode",
            new DateOnly(2026, 1, 1),
            state);
    }

    public void Dispose()
    {

        TemporaryFolder.Forget(_folder);
    }
}
