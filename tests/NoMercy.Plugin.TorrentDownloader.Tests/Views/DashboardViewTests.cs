using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

public class DashboardViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    /// <remarks>
    /// Idle is a state with something to say, not an absence. A spinner claims
    /// work is happening when none is, and an EmptyState is for a plugin with
    /// nothing configured — an owner seeing either would be told to wait for
    /// something that was never coming.
    /// </remarks>
    [Fact]
    public void AnIdleDashboardSaysWhenItLastRanAndWhenItIsNextDue()
    {
        PluginView view = DashboardView.Render(
            new([], [], Now),
            new(false, Now.AddMinutes(-14), Now.AddHours(6)));

        string bar = string.Join(" ", Rendered.Words(view));

        Assert.Contains("14 min ago", bar, StringComparison.Ordinal);
        Assert.Contains("6 h", bar, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Rendered.All(view),
            component => component.Component == PluginComponentType.Spinner);
        Assert.DoesNotContain(
            Rendered.All(view),
            component => component.Component == PluginComponentType.EmptyState);
    }

    /// <remarks>
    /// Every number is real. A plugin that has never run says so; drawing that
    /// as "0 minutes ago" is the shape of 0.3.4's "0 downloads" while two were
    /// running.
    /// </remarks>
    [Fact]
    public void ADashboardThatHasNeverRunSaysSoRatherThanShowingNought()
    {
        PluginView view = DashboardView.Render(new([], [], Now), new(false, null, null));

        string bar = string.Join(" ", Rendered.Words(view));

        Assert.Contains("never run", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("0 min ago", bar, StringComparison.Ordinal);
        // Not "not scheduled": the cadences are registered from the moment the
        // plugin loads. What is missing is the time, not the schedule.
        Assert.Contains("next run time not known", bar, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Now is the answer to "is anything stuck?", so a row has to say which
    /// episode, which stage, and what it is waiting on. Anything the pipeline
    /// does that cannot appear here is a stage that was never instrumented.
    /// </remarks>
    [Fact]
    public void TwoEpisodesInFlightAreTwoRowsSayingStageAndWhatEachWaitsOn()
    {
        ActivitySnapshot snapshot = new(
            [
                new(ActivityStage.Find, ActivityOutcome.Started, "Silo S03E06", Now.AddSeconds(-30),
                    "asking 1337x, TPB, TorrentGalaxy"),
                new(ActivityStage.Decide, ActivityOutcome.Started, "Lioness S03E01", Now.AddSeconds(-10),
                    "waiting on TorrentBay (rate limit, 8s)"),
            ],
            [],
            Now);

        PluginView view = DashboardView.Render(snapshot, new(true, Now.AddMinutes(-1), Now.AddHours(6)));

        PluginComponent now = Rendered.ById(view, DashboardView.NowTableId);
        string[] words = [.. Rendered.Words(new() { Components = [now] })];

        Assert.Contains("Silo S03E06", words);
        Assert.Contains("Find", words);
        Assert.Contains("asking 1337x, TPB, TorrentGalaxy", words);

        Assert.Contains("Lioness S03E01", words);
        Assert.Contains("Decide", words);
        Assert.Contains("waiting on TorrentBay (rate limit, 8s)", words);
    }

    /// <remarks>
    /// Nothing in flight is not the same as nothing configured: the table says
    /// so itself rather than the page falling back to an empty state.
    /// </remarks>
    [Fact]
    public void NothingInFlightIsStillTheNowTable()
    {
        PluginView view = DashboardView.Render(
            new([], [], Now),
            new(false, Now.AddMinutes(-14), Now.AddHours(6)));

        Assert.Equal(PluginComponentType.Table, Rendered.ById(view, DashboardView.NowTableId).Component);
    }

    /// <remarks>
    /// A pure function of what it is handed: the same snapshot has to render
    /// the same page, or two clients reading the same push disagree.
    /// </remarks>
    [Fact]
    public void TheSameSnapshotRendersTheSamePage()
    {
        ActivitySnapshot snapshot = new(
            [new(ActivityStage.Grab, ActivityOutcome.Started, "Sugar S02E02", Now, null)],
            [],
            Now);
        CycleStatus cycle = new(true, Now.AddMinutes(-2), Now.AddHours(6));

        Assert.Equal(
            Rendered.Words(DashboardView.Render(snapshot, cycle)),
            Rendered.Words(DashboardView.Render(snapshot, cycle)));
    }
}
