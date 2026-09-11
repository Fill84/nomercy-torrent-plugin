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

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
