using Microsoft.Extensions.Time.Testing;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>The Queue page, drawn from what a refresh derives over the plugin's own settings.</summary>
public sealed class QueueViewTests : IDisposable
{
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-queue-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <c>docs/specs/pages.md</c> § Queue: the page lists the aired episodes without a video file of the
    /// shows switched on with saved settings, and every row carries a button that searches for that
    /// episode now. Silo is on and saved; Family Guy was never touched, so none of its episodes is there.
    /// </remarks>
    [Fact]
    public async Task TheQueueListsTheEpisodesSearchedForWithSearchNow()
    {
        Store database = TestStore.In(_root);
        await database.MigrateAsync(CancellationToken.None);

        ShowSettingsRepository shows = new(database);
        await shows.SaveAsync(new(41) { SwitchedOn = true, Quality = "1080p" }, CancellationToken.None);

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)
            .Episode(41, 1, 1, airDate: new DateTime(2023, 5, 5), hasFile: true)
            .Episode(41, 1, 2, airDate: new DateTime(2023, 5, 12))
            .Episode(41, 3, 6, airDate: new DateTime(2026, 9, 1))
            .Show(99, "Family Guy", TelevisionLibrary, year: 1999)
            .Episode(99, 1, 1, airDate: new DateTime(2020, 1, 1), hasFile: true)
            .Episode(99, 1, 2, airDate: new DateTime(2020, 1, 8));

        IReadOnlyList<TrackedEpisode> tracked = await new MissingRefresh(
                new HostLibrary(shelves),
                new AppliedSettings(shows, new LibraryPreferencesRepository(database)),
                new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero)))
            .DeriveAsync(CancellationToken.None);

        PluginComponent table = Rendered.ById(QueueView.Render(tracked), QueueView.LookingTableId);

        Assert.Equal(["Silo S01E02", "Silo S03E06"], table.Items.Select(row => row.Props["episode"]));

        foreach ((PluginComponent row, EpisodeKey key) in table.Items.Zip([new EpisodeKey(41, 1, 2), new EpisodeKey(41, 3, 6)]))
        {
            PluginTableAction button = Assert.Single(
                Assert.IsAssignableFrom<IReadOnlyList<PluginTableAction>>(row.Props["controls"]));

            Assert.Equal("Search now", button.Label);
            Assert.Equal(QueueView.SearchAction, Assert.IsType<PluginActionIntent>(button.Action).Payload["method"]);

            IReadOnlyDictionary<string, object?> sent =
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.IsType<PluginActionIntent>(button.Action).Payload["payload"]);

            Assert.Equal(key.ShowId, sent["showId"]);
            Assert.Equal(key.Season, sent["season"]);
            Assert.Equal(key.Number, sent["episode"]);
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>An episode to be replaced is on the Queue page, under a tab of its own, with what it
    /// replaces.</strong> It is searched for, so it is in Looking in the order the run asks; it is not
    /// missing, so the overview does not count it — and the To be replaced tab is where the owner reads why
    /// an episode the library has is being searched for at all. Every row carries Search now, as every row on
    /// the page does (<c>docs/specs/pages.md</c> § Queue).
    /// </para>
    /// <para>
    /// Sabotage: leaving it out of the page leaves the owner a search they cannot account for.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEpisodeToBeReplacedIsListedWithTheReleaseItReplaces()
    {
        TrackedEpisode southPark = new(new(2190, 29, 3), "South Park", 1997, LibraryKind.Television, "Episode 3", new DateOnly(2026, 10, 15), EpisodeState.Replace)
        {
            Replaces = new("7445E4910B7A4D7E1F3C35D5A4A9C2E1B0C0FFEE", "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM"),
        };

        TrackedEpisode silo = new(new(41, 3, 6), "Silo", 2023, LibraryKind.Television, null, new DateOnly(2026, 9, 1), EpisodeState.Missing);

        PluginComponent replacing = Rendered.ById(QueueView.Render([southPark, silo], QueueView.ReplacingTableId), QueueView.ReplacingTableId);
        PluginComponent row = Assert.Single(replacing.Items);

        Assert.Equal("South Park S29E03", row.Props["episode"]);
        Assert.Equal(
            "to be replaced: South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM — waiting for a release the sources name",
            row.Props["why"]);
        Assert.Equal("Search now", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<PluginTableAction>>(row.Props["controls"])).Label);

        Assert.Equal(
            ["Silo S03E06", "South Park S29E03"],
            Rendered.ById(QueueView.Render([southPark, silo]), QueueView.LookingTableId).Items.Select(one => one.Props["episode"]));
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}
