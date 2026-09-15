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
            Shelves = new FakeLibraryQuery()
                .Library(Tv, "Series", "tv")
                .Show(41, "Silo", Tv, year: 2023),
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
