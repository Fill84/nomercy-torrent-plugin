using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The show list through the plugin as the server loads it: a row switched, a form saved and read
/// back, a refused form, and a library's preferences followed. <c>docs/specs/show-list.md</c>.
/// </summary>
public sealed class TheShowListTests : IDisposable
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    /// <summary>A broadcast day well in the past, so an episode without a file is a gap.</summary>
    private static readonly DateTime Aired = DateTime.UtcNow.AddMonths(-2);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", "show-list-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AShowSwitchedOnFromItsRowIsOnAndNotSaved()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Equal("Off", await StateOf(plugin, 41));

        await plugin.SwitchShowAsync(41, on: true, CancellationToken.None);

        Assert.Equal("On, not saved", await StateOf(plugin, 41));
    }

    [Fact]
    public async Task ASavedFormIsWhatTheShowsPageHoldsAfterward()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        IReadOnlyList<string> refused = await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?>
            {
                ["switchedOn"] = "true",
                ["quality"] = "2160p",
                ["wishes"] = "WEB",
                ["wishes.add"] = "NTb",
            },
            CancellationToken.None);

        Assert.Empty(refused);
        Assert.Equal("On", await StateOf(plugin, 41));

        PluginView page = await plugin.GetViewAsync(new() { Route = "/shows/41" }, CancellationToken.None);
        IReadOnlyList<PluginFormField> fields = Assert.IsAssignableFrom<IReadOnlyList<PluginFormField>>(
            Rendered.ById(page, ShowSettingsView.FormId).Props["fields"]);

        Assert.Equal("2160p", fields.Single(field => field.Name == "quality").Value);
        Assert.Equal("WEB, NTb", fields.Single(field => field.Name == "wishes").Value);
    }

    [Fact]
    public async Task ARefusedFormSavesNothing()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        IReadOnlyList<string> refused = await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?> { ["switchedOn"] = "true", ["quality"] = "1080" },
            CancellationToken.None);

        Assert.NotEmpty(refused);
        Assert.Equal("Off", await StateOf(plugin, 41));

        // Not even marked saved: a refused form is one the owner has not saved.
        Assert.False((await (await plugin.ShowSettingsAsync(CancellationToken.None)).ForAsync(41, CancellationToken.None)).Saved);
    }

    [Fact]
    public async Task AShowFollowsWhatItsLibraryWasSavedWith()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Equal("not set", await CellOf(plugin, 41, "quality"));

        Assert.Empty(await plugin.SaveLibraryPreferencesAsync(
            Tv,
            new Dictionary<string, string?> { ["quality"] = "720p", ["codec"] = "h265" },
            CancellationToken.None));

        Assert.Equal("720p", await CellOf(plugin, 41, "quality"));
        Assert.Equal("h265", await CellOf(plugin, 41, "codec"));
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's report of 16 September 2026: "not counted" for every row, and the information
    /// is there.</strong> The column was fed from the episodes the plugin tracks, and it tracks only what
    /// is switched on, so every row of a fresh install read "not counted".
    /// </para>
    /// <para>
    /// It counts the aired episodes with no file, from the library itself. Not from the server's
    /// <c>HaveEpisodes</c>: on the owner's library that column reads nought for all 69 shows while 57 of
    /// them hold files.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheMissingColumnCountsAiredGapsEvenForAShowThatIsOff()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Equal("Off", await StateOf(plugin, 41));
        Assert.Equal("2", await CellOf(plugin, 41, "missing"));
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's report of 16 September 2026: shows they do not have, over and over.</strong>
    /// The server writes a row for every show it ever identified — twelve of the owner's sixty-nine are
    /// shows nobody added, imported on a guess (media-server #36) — and the overview listed all of them,
    /// which read as recommendations.
    /// </para>
    /// <para>
    /// A show the library holds no file of is not the owner's, unless they switched it on themselves:
    /// switching one on is how a show they are waiting for their first episode of stays on the page.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AShowTheLibraryHoldsNoFileOfIsNotListedUnlessItIsSwitchedOn()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        PluginView before = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(before), one => one.Id == "show-41");
        Assert.DoesNotContain(Rendered.All(before), one => one.Id == "show-42");

        await plugin.SwitchShowAsync(42, on: true, CancellationToken.None);

        PluginView after = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(after), one => one.Id == "show-42");
    }

    [Fact]
    public async Task AShowThatIsInNoLibrarySaysSo()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        PluginView page = await plugin.GetViewAsync(new() { Route = "/shows/999" }, CancellationToken.None);

        Assert.Contains(Rendered.Words(page), word => word.Contains("999", StringComparison.Ordinal));
        Assert.DoesNotContain(Rendered.All(page), component => component.Component == Ui.FormComponent);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    private TorrentDownloaderPlugin Loaded()
    {
        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            // Silo is the owner's: one episode of it is on disk and two aired
            // gaps are not. Brilliant Minds is one of the rows the server wrote
            // on a guess — every episode aired, not one file.
            Shelves = new FakeLibraryQuery()
                .Library(Tv, "Series", "tv")
                .Show(41, "Silo", Tv, year: 2023)
                .Episode(41, 1, 1, airDate: Aired, hasFile: true)
                .Episode(41, 1, 2, airDate: Aired, hasFile: false)
                .Episode(41, 1, 3, airDate: Aired, hasFile: false)
                .Episode(41, 1, 4, airDate: DateTime.UtcNow.AddMonths(1), hasFile: false)
                .Show(42, "Brilliant Minds", Tv, year: 2024)
                .Episode(42, 1, 1, airDate: Aired, hasFile: false)
                .Episode(42, 1, 2, airDate: Aired, hasFile: false),
        });

        return plugin;
    }

    private static async Task<string> StateOf(TorrentDownloaderPlugin plugin, int showId)
    {
        return await CellOf(plugin, showId, "state");
    }

    private static async Task<string> CellOf(TorrentDownloaderPlugin plugin, int showId, string key)
    {
        PluginView page = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        return Assert.IsType<string>(Rendered.ById(page, $"show-{showId}").Props[key]);
    }
}
