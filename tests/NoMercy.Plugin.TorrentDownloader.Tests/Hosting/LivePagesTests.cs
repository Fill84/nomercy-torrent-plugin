using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Whether an open page is ever actually told anything.
/// </summary>
/// <remarks>
/// <strong>The standing check, and the one it caught.</strong> If this stage
/// silently did nothing, what would say so? Nothing did.
/// <c>LiveSnapshot</c> was written, tested and handed a hub, and the one method
/// that starts a push was called by no code anywhere — so a dashboard opened
/// during a cycle showed the stage the plugin was on when the page loaded and
/// never moved again. Its own tests called that method by hand, which is
/// exactly why they passed the whole time. The owner reported it before any
/// test did.
/// </remarks>
public class LivePagesTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-live-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// A stage reporting is what a page is waiting to hear. The push is
    /// coalesced, so the assertion is that one is due — not that it has already
    /// gone out.
    /// </remarks>
    [Fact]
    public async Task AStageReportingSendsTheOpenPagesTheNewState()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = new(),
        };

        plugin.Initialize(context);

        Assert.Empty(context.Pushes.Pushes);

        plugin.Journal.Started(ActivityStage.Find, "Silo S03E08 · The Pirate Bay");

        // Coalesced at 250 ms, so the push follows rather than lands. Bounded,
        // because a regression here leaves it unsatisfiable and an unbounded
        // wait hangs the suite rather than failing it.
        await Eventually(() => context.Pushes.Pushes.Count > 0);

        Assert.Equal(
            NoMercy.Plugin.TorrentDownloader.Hosting.LiveSnapshot.Channel,
            context.Pushes.Pushes[0].Type);
    }

    /// <remarks>
    /// Every kind of report, because a page that only hears about work starting
    /// shows a cycle full of stages that never end.
    /// </remarks>
    [Fact]
    public async Task FinishingAndFailingAreToldToo()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = new(),
        };

        plugin.Initialize(context);

        plugin.Journal.Finished(ActivityStage.Find, "Silo S03E08 · The Pirate Bay", "12 copies");

        await Eventually(() => context.Pushes.Pushes.Count > 0);

        int afterFinishing = context.Pushes.Pushes.Count;

        plugin.Journal.Failed(ActivityStage.Find, "Silo S03E08 · 1337x", "it did not answer");

        await Eventually(() => context.Pushes.Pushes.Count > afterFinishing);
    }

    /// <summary>Waits for something to become true, and gives up rather than hanging.</summary>
    private static async Task Eventually(Func<bool> settled)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!settled() && DateTimeOffset.UtcNow < giveUp)
        {
            await Task.Delay(25, CancellationToken.None);
        }

        Assert.True(settled(), "The open pages were never told anything.");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        TemporaryFolder.Forget(_folder);
    }
}
