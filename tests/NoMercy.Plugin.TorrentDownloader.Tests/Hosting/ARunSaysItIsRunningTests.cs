using NoMercy.Plugin.TorrentDownloader.Core.Activity;
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

        for (int waited = 0; plugin.Running && waited < 100; waited++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

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

            for (int waited = 0; before.Running && waited < 100; waited++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            Assert.False(before.Running);
        }

        using TorrentDownloaderPlugin after = new();

        after.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new(),
        });

        string bar = string.Join(" ", Rendered.Words(await after.GetViewAsync(new() { Route = "/" }, CancellationToken.None)));

        // Finished or stopped: a run over an empty test library can be done
        // before the stop reaches it. Either way it is not "never".
        Assert.DoesNotContain("never run", bar, StringComparison.Ordinal);
        Assert.True(
            bar.Contains("last run finished at", StringComparison.Ordinal)
            || bar.Contains("stopped at", StringComparison.Ordinal),
            bar);
    }

    public void Dispose()
    {
        // Through the helper every store test uses: a run writes how it ended
        // into the database, and SQLite's pool can still be holding the file
        // when the test is over.
        TemporaryFolder.Forget(_folder);
    }
}
