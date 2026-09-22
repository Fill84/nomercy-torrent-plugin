using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// A cycle that has been started says so, from the moment it is started.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The owner's report of 11 September 2026: the Run button was still
/// enabled on a run they had just started, and stayed that way through a
/// refresh.</strong>
/// </para>
/// <para>
/// The guard was claimed inside the background task rather than by the caller
/// that started it. So the endpoint answered "started", the task had not
/// reached the guard yet, and everything that asked in between was told the
/// plugin was idle — including the push sent on the very next line, which is
/// what redraws every open page.
/// </para>
/// </remarks>
public class ARunSaysItIsRunningTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-running-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Read on the thread that started it, with nothing awaited in between:
    /// that is the window the owner was looking through, and the background
    /// task has not run a line of itself yet.
    /// </remarks>
    [Fact]
    public void ACycleIsRunningTheInstantItHasBeenStarted()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new(),
        });

        Assert.True(plugin.StartRun());

        // No await, no delay, no yielding to the task that was just started.
        Assert.True(plugin.Running);
    }

    /// <remarks>
    /// And a second press while the first is still held does not start another.
    /// Two cycles at once ask every site twice and can grab one episode twice,
    /// because what one has decided is state the other cannot see.
    /// </remarks>
    [Fact]
    public void ASecondPressWhileTheFirstIsHeldStartsNothing()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new(),
        });

        Assert.True(plugin.StartRun());
        Assert.False(plugin.StartRun());
    }

    /// <remarks>
    /// <strong>Stop is a stop, not a pause.</strong> The owner's report of
    /// 11 September 2026: after Stop every row the run had started stayed on the
    /// dashboard, so the page looked like a run waiting to carry on. When a run
    /// ends, however it ends, nothing it had in flight is still there.
    /// </remarks>
    [Fact]
    public async Task AStoppedRunLeavesNothingOfItselfInFlight()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new(),
        });

        // What a run has in flight the moment Stop is pressed: a question to
        // an indexer, and the episode it is for. Put there before the run is
        // started, because a run over an empty test library can be over before
        // the next line of this test runs — and what is being held is that the
        // end of a run, however it comes, takes them away.
        plugin.Journal.Started(ActivityStage.Find, "Silo S03E06 1080p · 1337x");
        plugin.Journal.Started(ActivityStage.Decide, "Silo S03E06");

        Assert.True(plugin.StartRun());

        plugin.StopRun();

        // Bounded, and generously. Running is the whole cycle since S12-15, so
        // a stopped run is not over until maintenance has run and the finish
        // is written down — on the CI runner of 14 September 2026, loaded by
        // every test project at once, opening a database alone took twelve
        // seconds and a ten-second bound failed with nothing wrong. A run that
        // never stops still fails here, once Hang.Limit has passed.
        await Until(() => !plugin.Running);

        Assert.False(plugin.Running);
        Assert.Empty(plugin.Journal.Snapshot().InFlight);
    }

    /// <remarks>
    /// <strong>When it last ran survives a restart.</strong> The server was
    /// restarted on 11 September 2026 and the dashboard said "never run" for a
    /// plugin that had run a dozen times that night, because it was a field in
    /// memory. A fresh plugin over the same folder says how the last run ended.
    /// </remarks>
    [Fact]
    public async Task HowTheLastRunEndedIsStillKnownAfterARestart()
    {
        using (TorrentDownloaderPlugin before = new())
        {
            before.Initialize(new FakePluginContext
            {
                DataFolderPath = _folder,
                Shelves = new(),
            });

            Assert.True(before.StartRun());
            before.StopRun();

            await Until(() => !before.Running);

            Assert.False(before.Running);
        }

        using TorrentDownloaderPlugin after = new();

        after.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new(),
        });

        string bar = string.Join(" ", Rendered.Words(await after.GetViewAsync(Requests.View("/"), CancellationToken.None)));

        // Finished or stopped: a run over an empty test library can be done
        // before the stop reaches it. Either way it is not "never".
        Assert.DoesNotContain("never run", bar, StringComparison.Ordinal);
        Assert.True(
            bar.Contains("last run finished at", StringComparison.Ordinal)
            || bar.Contains("stopped at", StringComparison.Ordinal),
            bar);
    }

    /// <remarks>
    /// <para>
    /// <strong>Stop pressed straight after Run was ignored.</strong> The run said
    /// it was running from the instant Run was pressed, but Stop only knew about
    /// a run once its background task had reached the search. In between, Stop
    /// answered that there was nothing to stop and the whole search went ahead.
    /// </para>
    /// <para>
    /// Over an empty library a run that is not stopped finishes, so a status bar
    /// that says "stopped at" is a run that searched nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StopPressedTheMomentRunWasPressedStopsThatRun()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new(),
        });

        Assert.True(plugin.StartRun());

        // No await in between: the background task has not run a line yet.
        Assert.True(plugin.StopRun(), "Stop was refused on a run that said it was running.");

        await Until(() => !plugin.Running);

        Assert.False(plugin.Running);

        string bar = string.Join(" ", Rendered.Words(await plugin.GetViewAsync(Requests.View("/"), CancellationToken.None)));

        Assert.Contains("stopped at", bar, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <strong>Stop pressed while a run waits on what it started was ignored
    /// too.</strong> Running is the whole cycle, downloads and encodes included,
    /// but Stop only reached the search half. A run waiting on an encode said it
    /// was running, and Stop answered that there was nothing to stop.
    /// </para>
    /// <para>
    /// Stop ends the run; what was already handed over carries on
    /// (docs/specs/run.md). So the run closes and the grab stays exactly where
    /// it was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StopPressedWhileARunWaitsOnAnEncodeEndsTheRunAndLeavesTheGrab()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Permits = new FakeGrants(),
            Encodes = new FakeEncoder(),
        });

        Settings settings = new() { IncompleteFolder = _folder, IntakeFolder = _folder };

        // So the run asks nobody anything: what is under test is the stop.
        foreach (string source in Shipped())
        {
            settings.DisabledDefaultSources.Add(source);
        }

        await plugin.Settings.SaveAsync(settings, CancellationToken.None);

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        // A grab already handed to the encoder, which holds the run open.
        await grabs.RecordAsync(
            Episode,
            "Silo",
            "Silo S03E06 1080p WEB H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await grabs.StateAsync(Hash, GrabState.Dispatched, CancellationToken.None);

        await plugin.RunCycleAsync(CancellationToken.None);

        // The search is over and the run is not: it is settling.
        Assert.True(plugin.Running, "the run closed while an encode it asked for was still going");

        Assert.True(plugin.StopRun(), "Stop was refused on a run that said it was running.");

        await Until(() => !plugin.Running);

        Assert.False(plugin.Running, "Stop did not end a run that was waiting on an encode");

        // Stopping the run is not stopping what it handed over.
        StoredDownload still = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Dispatched, still.State);
    }

    /// <remarks>
    /// <para>
    /// <strong>A start that arrives while a run waits on its downloads is added to that run, once.</strong>
    /// <c>docs/specs/run.md</c>: a start while a run is going is added to that run. While the run was searching,
    /// that meant searching once more. While it waited on an encode, the start was marked and nothing searched —
    /// and the mark stayed, so the next run searched twice over.
    /// </para>
    /// <para>
    /// Here a run waits on an encode, a start arrives, and the grab is then over. The start searches again within
    /// that run and the run closes; the next run searches exactly once. Every search writes one row to
    /// <c>runs</c>, which is what is counted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStartWhileARunWaitsIsAddedToThatRunOnceAndTheNextRunSearchesOnce()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Permits = new FakeGrants(),
            Encodes = new FakeEncoder(),
        });

        Settings settings = new() { IncompleteFolder = _folder, IntakeFolder = _folder };

        foreach (string source in Shipped())
        {
            settings.DisabledDefaultSources.Add(source);
        }

        // A cycle finished a moment ago, written before the folders are: a
        // plugin that has never run one is due at once, and its own cadence
        // would start a run beside the ones this test starts by hand and
        // counts. The database is made and migrated by the first thing that
        // reads it, which has to be the plugin.
        _ = await plugin.EpisodesAsync(CancellationToken.None);
        await new CadenceRepository(new Store(_folder)).RecordFinishedAsync(JobNames.Cycle, DateTimeOffset.UtcNow, CancellationToken.None);

        await plugin.Settings.SaveAsync(settings, CancellationToken.None);

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        await grabs.RecordAsync(
            Episode,
            "Silo",
            "Silo S03E06 1080p WEB H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await grabs.StateAsync(Hash, GrabState.Dispatched, CancellationToken.None);

        await plugin.RunCycleAsync(CancellationToken.None);

        Assert.True(plugin.Running, "the run closed while an encode it asked for was still going");
        Assert.Equal(1, await SearchesAsync());

        // The encode lands, and a start arrives while the run is still open.
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        Assert.False(plugin.StartRun(), "a start while a run was open started a second run beside it");

        await Until(() => !plugin.Running);

        Assert.Equal(2, await SearchesAsync());

        // The next run searches once.
        await plugin.RunCycleAsync(CancellationToken.None);
        await Until(() => !plugin.Running);

        Assert.Equal(3, await SearchesAsync());
    }

    /// <summary>How many searches have been written down.</summary>
    private async Task<long> SearchesAsync()
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection = await new Store(_folder).OpenAsync(CancellationToken.None);
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM runs;";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static EpisodeKey Episode => new(41, 3, 6);

    /// <summary>Every shipped source, by name, read from the catalogue that ships.</summary>
    private static IEnumerable<string> Shipped()
    {
        return System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sources.json")))
            .RootElement
            .GetProperty("sources")
            .EnumerateArray()
            .Select(one => one.GetProperty("name").GetString()!)
            .ToArray();
    }

    /// <summary>Waits for a condition, for at most <see cref="Hang.Limit"/>.</summary>
    private static async Task Until(Func<bool> done)
    {
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

        while (!done() && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    public void Dispose()
    {
        // Through the helper every store test uses: a run writes how it ended
        // into the database, and SQLite's pool can still be holding the file
        // when the test is over.
        TemporaryFolder.Forget(_folder);
    }
}
