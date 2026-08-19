using Microsoft.Data.Sqlite;
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
    /// The number on the row equals the rows it summarises. A count kept
    /// anywhere else is a second number that can disagree with its own list —
    /// 0.3.4 showed "0 downloads" while two were running.
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

        PluginView page = await View(plugin, Pages.ShowsRoute);
        IReadOnlyList<string> words = Rendered.Words(page);

        // Silo: three missing, one waiting. Frieren: one missing, none waiting.
        Assert.Contains("Silo (2023)", words);
        Assert.Contains("3", words);
        Assert.Contains("Frieren (2023)", words);
    }

    /// <remarks>
    /// Which library an episode goes back to is the server's own decision, and
    /// the page says which without guessing from the title.
    /// </remarks>
    [Fact]
    public async Task TheMediaTypeIsRenderedPerShow()
    {
        using TorrentDownloaderPlugin plugin = await Seeded(
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(2, 1, 1, EpisodeState.Missing, "Frieren", LibraryKind.Anime),
        ]);

        IReadOnlyList<string> words = Rendered.Words(await View(plugin, Pages.ShowsRoute));

        Assert.Contains("tv", words);
        Assert.Contains("anime", words);
    }

    /// <remarks>
    /// Three lists, never one. An unaired episode among the missing is work the
    /// plugin is not doing, and one in no list at all is an episode nobody can
    /// see has stopped moving.
    /// </remarks>
    [Fact]
    public async Task TheQueueSeparatesLookingFromWaitingToAir()
    {
        using TorrentDownloaderPlugin plugin = await Seeded(
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(1, 1, 2, EpisodeState.NotAired),
            Episode(1, 1, 3, EpisodeState.Unavailable),
        ]);

        PluginView page = await View(plugin, Pages.QueueRoute);

        Assert.Equal(
            ["Silo S01E01"],
            RowsOf(page, QueueView.LookingTableId));
        Assert.Equal(
            ["Silo S01E02"],
            RowsOf(page, QueueView.WaitingTableId));
        Assert.Equal(
            ["Silo S01E03"],
            RowsOf(page, QueueView.GivenUpTableId));
    }

    /// <remarks>
    /// The order shown is the order the search cadence will ask in: never
    /// searched first, then longest waiting. A page in any other order is a
    /// guess about what the plugin is about to do.
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

        // Three has never been searched, then two waited longest, then one.
        Assert.Equal(
            ["Silo S01E03", "Silo S01E02", "Silo S01E01"],
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
    /// route table says they exist and which shell each wants; the two mounts
    /// stay exactly the two the manifest declares.
    /// </remarks>
    [Fact]
    public void EveryPageIsDeclaredAndOnlyTwoAreMounted()
    {
        using TorrentDownloaderPlugin plugin = new();

        // Which pages the table holds is asserted whole in PagesReachableTests;
        // what matters here is that these two are on it and are not mounts.
        Assert.Contains(Pages.ShowsRoute, plugin.Routes.Routes.Select(route => route.Path));
        Assert.Contains(Pages.QueueRoute, plugin.Routes.Routes.Select(route => route.Path));

        // Resolve answers null for a path no page claims, which is the point of
        // declaring the table at all.
        PluginRouteMatch shows = Assert.IsType<PluginRouteMatch>(plugin.Routes.Resolve(Pages.ShowsRoute));
        PluginRouteMatch settings = Assert.IsType<PluginRouteMatch>(plugin.Routes.Resolve(Pages.SettingsRoute));

        Assert.Equal(PluginLayout.ListDetail, shows.Route.Layout);
        Assert.Equal(PluginLayout.Form, settings.Route.Layout);
        Assert.Null(plugin.Routes.Resolve("/no-such-page"));
        Assert.Equal(2, plugin.NavEntries.Count);
    }

    private static IReadOnlyList<string> RowsOf(PluginView page, string tableId)
    {
        // The first cell of every body row, which is the episode's name. The
        // header row is the one whose id ends "-head".
        return
        [
            .. Rendered.All(page)
                .Where(component => component.Id.StartsWith($"{tableId}-", StringComparison.Ordinal)
                                    && component.Id.EndsWith("-episode-value", StringComparison.Ordinal))
                .Select(component => component.Props.GetValueOrDefault("text")?.ToString() ?? string.Empty),
        ];
    }

    private static Task<PluginView> View(TorrentDownloaderPlugin plugin, string route)
    {
        return plugin.GetViewAsync(new() { Route = route }, CancellationToken.None);
    }

    private async Task<TorrentDownloaderPlugin> Seeded(IReadOnlyList<TrackedEpisode> episodes)
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder });

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
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
