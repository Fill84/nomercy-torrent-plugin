using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
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
    /// <strong>S12-05.</strong> The host reads a plugin's job list only when it
    /// is installed, hot-swapped or enabled, so a saved cadence cannot take
    /// effect by changing what is registered here — only one job is declared
    /// at all now, and the plugin's own clock decides the rest on every tick.
    /// See <see cref="OneJobTicksAndTheWorkIsChosenByTheClock"/>.
    /// </remarks>
    [Fact]
    public void OneJobIsDeclaredEveryMinute()
    {
        using TorrentDownloaderPlugin plugin = new();

        PluginScheduledJob job = Assert.Single(plugin.Jobs);

        Assert.Equal(JobNames.Transfers, job.Name);
        Assert.Equal("* * * * *", job.CronExpression);
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
    /// A plugin nobody has configured does nothing at all, and says so once. It
    /// has nowhere to put a download, so searching for one would spend every
    /// site's patience on a file that could only be thrown away — and the owner
    /// would see activity and no results.
    ///
    /// It is also what keeps this project's rule true: these tests touch no
    /// network. A feed tick on a fresh install builds no chain, starts no
    /// browser and asks nobody anything.
    /// </remarks>
    [Fact]
    public async Task AnUnconfiguredPluginSearchesForNothingAndSaysSo()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        await plugin.ExecuteAsync(JobNames.Feed, CancellationToken.None);
        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);

        Assert.Empty(plugin.LastCycle);
        Assert.Empty(plugin.Journal.Snapshot().History);

        string[] said = [.. context.Log.Lines.Where(line => line.Contains("No folders", StringComparison.Ordinal))];

        Assert.Single(said);
    }

    /// <remarks>
    /// One line, however many ticks. Transfers alone ticks every minute, and a
    /// line a minute is a line nobody reads. What it answers is which version
    /// is loaded — the question a deploy that copied nothing leaves open.
    /// </remarks>
    [Fact]
    public async Task EveryTickSaysOnceThatThisVersionIsAwake()
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

    /// <remarks>
    /// <para>
    /// <strong>S12-05, replacing <c>TheCadencesTheOwnerSavedAreTheCadencesTheServerIsGiven</c>.</strong>
    /// That test asserted the four-job shape this plugin used to register —
    /// which is exactly the fault the design now avoids, because the host
    /// only ever re-reads a job list on an install, a hot-swap or an enable,
    /// and a saved cadence has to take effect without waiting for one of
    /// those. So there is one job, and this plugin decides for itself what a
    /// tick under it does.
    /// </para>
    /// <para>
    /// Nothing has ever run before, so every one of the three retired
    /// cadences is due at once — which is what <c>MissingRefresh</c> running
    /// during the first tick proves. A second tick, straight after the first
    /// with a new episode added to the library in between, must not pick it
    /// up: none of the three has finished long enough ago for its own
    /// interval to have come round again, and if the clock were not gating
    /// them the new episode would appear immediately.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneJobTicksAndTheWorkIsChosenByTheClock()
    {
        FakeLibraryQuery shelves = new();

        shelves
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2021, folder: "/Silo.(2021)")

            // One file on disk, which is what makes this a show the owner
            // actually has — Ownership.Theirs — and one missing episode for
            // the refresh to pick up.
            .Episode(41, 3, 5, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 3, 7, "Descent", new DateTime(2020, 1, 15), hasFile: true);

        string folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);

        try
        {
            using TorrentDownloaderPlugin plugin = new();

            plugin.Initialize(new FakePluginContext
            {
                DataFolderPath = folder,
                Shelves = shelves,
                Permits = new FakeGrants(),
                Container = new FakeProvider(),
            });

            PluginScheduledJob job = Assert.Single(plugin.Jobs);
            Assert.Equal(JobNames.Transfers, job.Name);
            Assert.Equal("* * * * *", job.CronExpression);

            await plugin.Settings.SaveAsync(
                new Settings { IncompleteFolder = folder, IntakeFolder = folder },
                CancellationToken.None);

            // The one job's own name, ticking for the first time ever: every
            // cadence is due, including search and maintenance, both of which
            // derive the missing list from the library.
            await plugin.ExecuteAsync(job.Name, CancellationToken.None);

            IReadOnlyList<TrackedEpisode> afterFirstTick =
                await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None);

            Assert.Single(afterFirstTick);

            // A second episode airs, straight after the first tick.
            shelves.Episode(41, 3, 6, "Under Pressure", new DateTime(2020, 1, 8), hasFile: false);

            await plugin.ExecuteAsync(job.Name, CancellationToken.None);

            IReadOnlyList<TrackedEpisode> afterSecondTick =
                await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None);

            // Still one: search and maintenance both finished a moment ago,
            // and neither the six-hourly nor the daily slot has come round
            // again since. The clock, not this test, is what kept the second
            // tick from redoing their work.
            Assert.Single(afterSecondTick);
        }
        finally
        {
            TemporaryFolder.Forget(folder);
        }
    }

    /// <remarks>
    /// A host that has not yet re-read <see cref="TorrentDownloaderPlugin.Jobs"/>
    /// after this upgrade is still holding its previous four-job registration,
    /// each still firing on its own old cadence. A tick under any of those
    /// three retired names has to be accepted rather than thrown — thrown is
    /// for a name genuinely unknown to this plugin, which
    /// <see cref="AnUnknownJobNameThrows"/> already covers — and it still runs
    /// exactly the one pass that name has always meant, not the clock's
    /// combined tick.
    /// </remarks>
    [Fact]
    public async Task ATickUnderAnOldJobNameIsStillAccepted()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        plugin.Initialize(context);

        await plugin.ExecuteAsync(JobNames.Feed, CancellationToken.None);
        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);
        await plugin.ExecuteAsync(JobNames.Maintenance, CancellationToken.None);

        // Each of the three reached ConfiguredAsync and found nothing set up
        // — the same guard an unconfigured plugin always hits — rather than
        // the clock's own due-cadence loop, which an unknown name would never
        // reach at all.
        string[] said = [.. context.Log.Lines.Where(line => line.Contains("No folders", StringComparison.Ordinal))];

        Assert.Single(said);
    }
}
