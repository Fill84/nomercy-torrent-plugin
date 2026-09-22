using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// Every page this plugin renders is a page somebody can open.
/// </summary>
/// <remarks>
/// Downloads, History, Skipped and Sources were all written, all tested against
/// a seeded store, and all served by nothing: the route table listed four
/// routes and none of them was one of these. A page that renders and is on no
/// route is a page nobody can reach, and it looks exactly like a page that was
/// never written.
/// </remarks>
public class PagesReachableTests
{
    [Fact]
    public void TheRouteTableServesEveryPageThePluginRenders()
    {
        using TorrentDownloaderPlugin plugin = new();

        Assert.Equal(
            [
                Pages.OverviewRoute,
                Pages.ActivityRoute,
                Pages.QueueRoute,
                Pages.DownloadsRoute,
                Pages.HistoryRoute,
                Pages.SourcesRoute,
                Pages.SettingsRoute,
                Pages.ShowSettingsRoute,
                Pages.LibraryPreferencesRoute,
                Pages.LibraryShowsRoute,
            ],
            plugin.Routes.Routes.Select(route => route.Path));
    }

    /// <remarks>
    /// From the grabs, and every number the client has not reported is a dash.
    /// A page served from an empty in-memory list would render the same words
    /// as one served from nothing at all, so the row has to come off the disk.
    /// </remarks>
    [Fact]
    public async Task TheDownloadsRouteRendersWhatWasGrabbed()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        await Configure(plugin);
        await (await Grabs(plugin, context)).RecordAsync(
            new(41, 3, 6),
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "1337x",
            "0123456789ABCDEF0123456789ABCDEF01234567",
            "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567",
            [new(41, 3, 6)],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        string page = await Words(plugin, Pages.DownloadsRoute);

        Assert.Contains("Silo.S03E06.1080p.WEB.H264-CAKES", page, StringComparison.Ordinal);
        Assert.Contains("grabbed, not started", page, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Every line carries when it happened, which episode it was about and why
    /// — "dispatched" on its own is the entry an owner opens this page to
    /// understand and learns nothing from.
    /// </remarks>
    [Fact]
    public async Task TheHistoryRouteSaysWhenWhichAndWhy()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        await Configure(plugin);
        await (await Grabs(plugin, context)).DispatchedAsync(
            new(41, 3, 6),
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "library-tv",
            new DateTimeOffset(2026, 8, 19, 9, 30, 0, TimeSpan.Zero),
            CancellationToken.None);

        string page = await Words(plugin, Pages.HistoryRoute);

        Assert.Contains("dispatched", page, StringComparison.Ordinal);
        Assert.Contains("2026-08-19 09:30:00Z", page, StringComparison.Ordinal);
        Assert.Contains("Silo S03E06", page, StringComparison.Ordinal);
        Assert.Contains("library-tv", page, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <strong>Nothing about a refusal is written down any more</strong>, on the owner's word of
    /// 16 September 2026: their history held 5,851 of them, every one a name a show's settings refused
    /// before an indexer was asked. A run says what it refuses while it runs, on the Activity page, and
    /// the Skipped page is gone with the rows behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefusedNameIsNotWrittenDownAnywhere()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        await Configure(plugin);

        GrabRepository grabs = await Grabs(plugin, context);

        await grabs.DispatchedAsync(
            new(41, 3, 6),
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "library-tv",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // Nothing in the store writes one, and the page that read them is gone:
        // a route nobody serves falls through to the overview.
        Assert.DoesNotContain(
            await grabs.HistoryAsync(CancellationToken.None),
            row => row.Event == "skipped");

        Assert.DoesNotContain(plugin.Routes.Routes, route => route.Path.Contains("skipped", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <strong>G2.</strong> A site that refused and a site nobody has asked are
    /// two different things, and 0.3.4 reported its own rate-limiting as a
    /// broken parser. The page says which of the two each source is, in the
    /// site's own words, and every source in the catalogue has a row whether or
    /// not it has ever answered.
    /// </remarks>
    [Fact]
    public async Task TheSourcesRouteSaysWhatEachSiteLastAnswered()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        await Configure(plugin);
        await plugin.EpisodesAsync(CancellationToken.None);

        SourceLedgerRepository ledger = new(new(context.DataFolderPath));

        await ledger.RecordAsync(
            new("TorrentBay", DateTimeOffset.UtcNow, 0, "429 Too Many Requests", TimeSpan.FromSeconds(8)),
            CancellationToken.None);
        await ledger.RecordAsync(
            new("1337x", DateTimeOffset.UtcNow, 23, null, TimeSpan.FromMilliseconds(640)),
            CancellationToken.None);

        string page = await Words(plugin, Pages.SourcesRoute);

        Assert.Contains("429 Too Many Requests", page, StringComparison.Ordinal);
        Assert.Contains("640 ms", page, StringComparison.Ordinal);

        // A site nobody has asked says so rather than reading as one that
        // answered with nothing.
        Assert.Contains(SourcesView.Never, page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The grab store over the plugin's own database.
    /// </summary>
    /// <remarks>
    /// Through the plugin's own migration rather than a second one of its own:
    /// a test that migrated the file itself would pass on a schema the plugin
    /// never creates.
    /// </remarks>
    private static async Task<GrabRepository> Grabs(TorrentDownloaderPlugin plugin, FakePluginContext context)
    {
        await plugin.EpisodesAsync(CancellationToken.None);

        return new(new(context.DataFolderPath));
    }

    /// <summary>Folders, or the plugin refuses to do anything at all.</summary>
    private static async Task Configure(TorrentDownloaderPlugin plugin)
    {
        await plugin.Settings.SaveAsync(
            new()
            {
                IncompleteFolder = Path.GetTempPath(),
                IntakeFolder = Path.GetTempPath(),
            },
            CancellationToken.None);
    }

    private static async Task<string> Words(TorrentDownloaderPlugin plugin, string route)
    {
        PluginView view = await plugin.GetViewAsync(Requests.View(route), CancellationToken.None);

        return string.Join(" ", Rendered.EveryValue(view));
    }
}
