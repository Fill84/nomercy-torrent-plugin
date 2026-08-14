using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class TorrentDownloaderPluginTests
{
    /// <remarks>
    /// A plugin is constructed and initialised while the server is still coming
    /// up. Anything slow here — opening the database, reading the catalogue,
    /// reaching a tracker — delays the server, and anything that throws takes
    /// the plugin out before it has a page on which to say why.
    /// </remarks>
    [Fact]
    public void InitializeTouchesNoDisk()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        plugin.Initialize(context);

        Assert.False(Directory.Exists(context.DataFolderPath));
        Assert.Empty(context.Log.Lines);
    }

    /// <remarks>
    /// The four cadences are registered when the server starts and never again,
    /// so a job this list forgets does not begin ticking when a later slice
    /// implements it — it waits for the next restart.
    /// </remarks>
    [Fact]
    public void TheFourCadencesAreDeclaredWithTheirDefaults()
    {
        using TorrentDownloaderPlugin plugin = new();

        Assert.Equal(
            [
                ("transfers", "* * * * *"),
                ("feed", "*/15 * * * *"),
                ("search", "0 */6 * * *"),
                ("maintenance", "0 4 * * *"),
            ],
            plugin.Jobs.Select(job => (job.Name, job.CronExpression)));
    }

    /// <remarks>
    /// A tick that overruns its interval should skip the next one rather than
    /// pile up. Transfers ticks every minute and a cycle can take longer.
    /// </remarks>
    [Fact]
    public void NoCadenceOverlapsItself()
    {
        using TorrentDownloaderPlugin plugin = new();

        Assert.All(plugin.Jobs, job => Assert.False(job.AllowConcurrent));
    }

    /// <remarks>
    /// The server passes back whatever name it registered. A name this plugin
    /// does not know means the two lists have drifted, and silently doing
    /// nothing would hide that for as long as the job kept ticking.
    /// </remarks>
    [Fact]
    public async Task AnUnknownJobNameThrows()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => plugin.ExecuteAsync("harvest", CancellationToken.None));
    }

    /// <remarks>
    /// One line, however many ticks. Transfers alone ticks every minute, and a
    /// line a minute is a line nobody reads. What it answers is which version
    /// is loaded — the question a deploy that copied nothing leaves open.
    /// </remarks>
    [Fact]
    public async Task FourTicksSayOnceThatThisVersionIsAwake()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        foreach (PluginScheduledJob job in plugin.Jobs)
        {
            await plugin.ExecuteAsync(job.Name, CancellationToken.None);
        }

        string[] awake = context.Log.Lines
            .Where(line => line.Contains("awake", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string single = Assert.Single(awake);
        Assert.Contains(PluginIdentity.Version.ToString(), single, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The landing route is the dashboard, and it renders from the journal
    /// rather than from anything held between requests. A page nobody can reach
    /// is not a page: the dashboard has to be what the mount at "/" serves.
    /// </remarks>
    [Fact]
    public async Task TheLandingRouteIsTheDashboard()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());
        plugin.Journal.Started(ActivityStage.Find, "Silo S03E06", "asking 1337x");

        PluginView view = await plugin.GetViewAsync(
            new() { Route = Pages.DashboardRoute },
            CancellationToken.None);

        Assert.Contains(Rendered.Words(view), word => word == "Silo S03E06");
    }

    /// <remarks>
    /// The whole way through, with a real secret in a real store: the plugin
    /// loads the settings, renders the page, and the passkey appears in no prop
    /// of no component anywhere in it. The view cannot leak one because it is
    /// never handed one, and this is the test that would notice if that ever
    /// stopped being true.
    /// </remarks>
    [Fact]
    public async Task AStoredPasskeyNeverReachesTheSettingsPage()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        Settings settings = new();
        settings.PrivateTrackers.Add(new()
        {
            Id = "trk-1",
            Host = "tracker.example",
            AnnounceTemplate = "https://tracker.example/announce?passkey={passkey}",
        });
        settings.IncompleteFolder = Path.GetTempPath();
        settings.IntakeFolder = Path.GetTempPath();

        await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        await plugin.Settings.SetSecretAsync(
            SettingsStore.TrackerPasskey("trk-1"),
            "a1b2c3d4e5f6",
            CancellationToken.None);

        PluginView page = await plugin.GetViewAsync(
            new() { Route = Pages.SettingsRoute },
            CancellationToken.None);

        Assert.All(
            Rendered.EveryValue(page),
            value => Assert.DoesNotContain("a1b2c3d4e5f6", value, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Passkey: set", string.Join(" ", Rendered.Words(page)), StringComparison.Ordinal);
    }

    /// <remarks>
    /// The token is what every long-running thing the plugin owns — the engine,
    /// the solver's browser, the journal — is meant to stop on. A dispose that
    /// left it uncancelled would leave those running inside a server that
    /// believes the plugin is gone.
    /// </remarks>
    [Fact]
    public void DisposeCancelsTheLifetimeAndIsSafeTwice()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        CancellationToken lifetime = plugin.Lifetime;
        Assert.False(lifetime.IsCancellationRequested);

        plugin.Dispose();
        plugin.Dispose();

        Assert.True(lifetime.IsCancellationRequested);
    }
}
