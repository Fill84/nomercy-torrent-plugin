using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class TorrentDownloaderPluginTests
{
    /// <remarks>
    /// <para>
    /// <strong>The host is told hourly, and that is not the owner's cadence.</strong>
    /// A plugin's job list is read when it is installed, hot-swapped or enabled
    /// and never asked for again, so a cadence the owner saves could not reach
    /// the host by being declared here — the owner changed one on 3 September
    /// 2026, watched the old one go on firing, and reasonably concluded the
    /// setting did nothing. The plugin keeps its own clock, and this tick only
    /// winds it.
    /// </para>
    /// <para>
    /// Declared rather than declined because the contract has no way to
    /// decline: a plugin whose Jobs is empty is registered under its single
    /// CronExpression instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void OneJobIsDeclaredAndItIsNotTheOwnersCadence()
    {
        using TorrentDownloaderPlugin plugin = new();

        PluginScheduledJob job = Assert.Single(plugin.Jobs);

        Assert.Equal(JobNames.Cycle, job.Name);
        Assert.Equal("0 * * * *", job.CronExpression);
        Assert.Equal(job.CronExpression, plugin.CronExpression);

        // And it is no longer every minute. Transfers was * * * * * because the
        // torrent client did its whole housekeeping inside the method the pages
        // call to draw a table; `S12-13` gave those their own moments.
        Assert.NotEqual("* * * * *", job.CronExpression);
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

        await plugin.RunCycleAsync(CancellationToken.None);

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
    /// What a run is doing renders from the journal rather than from anything held between
    /// requests, and it is on the Activity page: the landing route is the overview of the shows since
    /// the owner's requirements of 15 September 2026 (<c>docs/specs/pages.md</c>).
    /// </remarks>
    [Fact]
    public async Task TheActivityPageRendersWhatTheRunIsDoingFromTheJournal()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());
        plugin.Journal.Started(ActivityStage.Find, "Silo S03E06", "asking 1337x");

        PluginView view = await plugin.GetViewAsync(
            Requests.View(Pages.ActivityRoute),
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
            Requests.View(Pages.SettingsRoute),
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
    /// <strong>One cycle, and the steps are steps rather than schedules.</strong>
    /// This used to assert that a tick ran whichever of feed, search and
    /// maintenance the clock said was due, and that a second tick a moment later
    /// re-ran none of them. There are no longer three cadences to be due: feed
    /// runs, then search, then what they started, and maintenance once there is
    /// nothing left in hand.
    /// </para>
    /// <para>
    /// What is left to prove is that a cycle really runs all of it and writes
    /// down that it finished — the clock's record is what keeps the next one
    /// from coming round early.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneCycleRunsEveryStepAndSaysWhenItFinished()
    {
        FakeLibraryQuery shelves = new();

        shelves
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2021, folder: "/Silo.(2021)")

            // One missing episode for the refresh to pick up, and one on disk.
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
                Encodes = new FakeEncoder(),
            });

            Settings settings = new() { IncompleteFolder = folder, IntakeFolder = folder };

            // So the feed reads nobody — these tests touch no network.
            DisableEveryShippedSource(settings);

            await plugin.Settings.SaveAsync(settings, CancellationToken.None);

            // Switched on and saved, or the refresh has no show to search for.
            Assert.Empty(await plugin.SaveShowSettingsAsync(
                41,
                new Dictionary<string, string?> { ["switchedOn"] = "true", ["quality"] = "1080p" },
                CancellationToken.None));

            // A second reader of the same file, never the plugin's own
            // repository: what is under test is what actually landed on disk.
            CadenceRepository cadences = new(new Store(folder));

            await plugin.RunCycleAsync(CancellationToken.None);

            // The library was read, which only search and maintenance do.
            IReadOnlyList<TrackedEpisode> tracked =
                await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None);

            Assert.Single(tracked);

            // And the cycle wrote down that it finished, which is what the next
            // one is timed from.
            IReadOnlyDictionary<string, DateTimeOffset> finished = await UntilAsync(
                cadences,
                done => done.ContainsKey(JobNames.Cycle),
                TimeSpan.FromSeconds(20));

            Assert.True(finished.ContainsKey(JobNames.Cycle), "the cycle never recorded a finish");

            // Nothing was downloading and nothing was waiting on an encode, so
            // the cycle closed itself rather than staying open.
            Assert.False(plugin.Running, "the cycle stayed open with nothing in hand");
        }
        finally
        {
            TemporaryFolder.Forget(folder);
        }
    }

    /// <summary>Polls <paramref name="cadences"/> until <paramref name="ready"/> says so, or gives up.</summary>
    /// <remarks>
    /// Bounded, per <c>docs/plan/PROGRESS.md</c>'s own rule for a test that
    /// waits on real background work: a regression that stops a cadence from
    /// ever finishing must fail this test, not hang the suite.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> UntilAsync(
        CadenceRepository cadences,
        Func<IReadOnlyDictionary<string, DateTimeOffset>, bool> ready,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            IReadOnlyDictionary<string, DateTimeOffset> finished = await cadences.LastFinishedAsync(CancellationToken.None);

            if (ready(finished) || DateTime.UtcNow >= deadline)
            {
                return finished;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>The four retired names are still answered to.</strong> This
    /// plugin declared transfers, feed, search and maintenance, and now declares
    /// one. The host registers a plugin's jobs when the plugin loads and removes
    /// them by the names the loaded instance declares — so an upgrade can leave
    /// the previous four registrations in the queue with nothing left to take
    /// them out, and each goes on firing on its own old cadence.
    /// </para>
    /// <para>
    /// A tick under one of those is an upgrade window and not a fault. Thrown
    /// at, it would be four stack traces an hour in the owner's log for as long
    /// as it lasted — and thrown is for a name genuinely unknown to this plugin,
    /// which <see cref="AnUnknownJobNameThrows"/> covers.
    /// </para>
    /// <para>
    /// It starts nothing, which is the same as a tick under the name this
    /// plugin does declare: the owner's cadence is kept by the plugin's own
    /// clock, and a tick only winds it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("transfers")]
    [InlineData("feed")]
    [InlineData("search")]
    [InlineData("maintenance")]
    public async Task ATickUnderAnOldJobNameIsStillAccepted(string retired)
    {
        FakeLibraryQuery shelves = new();

        shelves
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2021, folder: "/Silo.(2021)")
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
                Encodes = new FakeEncoder(),
            });

            Settings settings = new() { IncompleteFolder = folder, IntakeFolder = folder };

            DisableEveryShippedSource(settings);

            await plugin.Settings.SaveAsync(settings, CancellationToken.None);

            // Accepted. The assertion is that this returns at all.
            await plugin.ExecuteAsync(retired, CancellationToken.None);

            // Nothing about the tick. The listen port may be held by another
            // test running beside this one, and the client saying so is not this
            // tick's business.
            Assert.DoesNotContain(
                plugin.Journal.Snapshot().History,
                entry => entry.Outcome == ActivityOutcome.Failed && entry.Stage != ActivityStage.Download);
        }
        finally
        {
            TemporaryFolder.Forget(folder);
        }
    }

    /// <summary>Every shipped source, by name, so a test can switch them all off.</summary>
    private static void DisableEveryShippedSource(Settings settings)
    {
        foreach (string source in System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sources.json")))
            .RootElement
            .GetProperty("sources")
            .EnumerateArray()
            .Select(one => one.GetProperty("name").GetString()!))
        {
            settings.DisabledDefaultSources.Add(source);
        }
    }

    /// <summary>
    /// A cycle stays open while something it started is still in hand, and
    /// maintenance waits for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's model of 13 September 2026: a cycle is over when everything
    /// is downloaded and there are no encodes left, and only then does
    /// maintenance run. That order is not a preference — maintenance sweeps
    /// download folders no grab answers for, and a sweep that runs while a
    /// download is in flight is a sweep that can take it.
    /// </para>
    /// <para>
    /// A grab that has been handed to the encoder is exactly that case: nothing
    /// is downloading any more, the episode is not in the library yet, and the
    /// file the server is reading is still on disk.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACycleStaysOpenWhileAnEncodeItAskedForIsStillGoing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);

        try
        {
            using TorrentDownloaderPlugin plugin = new();

            plugin.Initialize(new FakePluginContext
            {
                DataFolderPath = folder,
                Permits = new FakeGrants(),
                Encodes = new FakeEncoder(),
            });

            Settings settings = new() { IncompleteFolder = folder, IntakeFolder = folder };

            DisableEveryShippedSource(settings);

            await plugin.Settings.SaveAsync(settings, CancellationToken.None);

            // A grab the plugin has already handed to the encoder.
            GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

            await grabs.RecordAsync(
                new EpisodeKey(41, 3, 6),
                "Silo",
                "Silo S03E06 1080p WEB H264-CAKES",
                "1337x",
                "0123456789ABCDEF0123456789ABCDEF01234567",
                "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567",
                [new EpisodeKey(41, 3, 6)],
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            await grabs.StateAsync(
                "0123456789ABCDEF0123456789ABCDEF01234567", GrabState.Dispatched, CancellationToken.None);

            await plugin.RunCycleAsync(CancellationToken.None);

            // Feed and search are done, and the cycle is not: the encoder still
            // has the file this cycle gave it.
            Assert.True(plugin.Running, "the cycle closed while an encode was still going");

            CadenceRepository cadences = new(new Store(folder));

            Assert.False(
                (await cadences.LastFinishedAsync(CancellationToken.None)).ContainsKey(JobNames.Cycle),
                "the cycle wrote down a finish it had not reached");
        }
        finally
        {
            TemporaryFolder.Forget(folder);
        }
    }

    /// <summary>
    /// A page being fetched is what says somebody is looking, and nothing else does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner asked on 13 September 2026 for the pages to be told of a change
    /// only while one is open, because every push makes the web app fetch the
    /// whole view again. The hub cannot say who is watching; a fetch can.
    /// </para>
    /// <para>
    /// A plugin that has only been loaded has nobody looking, however much it is
    /// doing — a cycle, a download, an encode — and so nothing samples the client
    /// on a page's behalf.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APageBeingFetchedIsWhatSaysSomebodyIsLooking()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        plugin.Initialize(context);

        Assert.False(plugin.Watched, "a plugin nobody has opened said somebody was looking.");

        // Something happening in the plugin is not somebody looking.
        plugin.Journal.Failed(ActivityStage.Download, "Silo S03E06", "no peer served its metadata");

        Assert.False(plugin.Watched, "the plugin doing its work was taken for somebody looking.");

        _ = await plugin.GetViewAsync(Requests.View("/downloads"), CancellationToken.None);

        Assert.True(plugin.Watched, "a page was fetched and nobody was said to be looking.");
    }
}
