using System.Globalization;
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

        // Clock times, not distances from now. "14 min ago" is true for a
        // minute and then quietly wrong, and nothing pushes to correct it: the
        // passing of a minute changes nothing the plugin holds, so a page left
        // open sat on it for an hour. The owner's decision of 11 September 2026.
        Assert.Contains(
            Now.AddMinutes(-14).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            bar,
            StringComparison.Ordinal);
        Assert.Contains(
            Now.AddHours(6).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            bar,
            StringComparison.Ordinal);

        Assert.DoesNotContain("ago", bar, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Rendered.All(view),
            component => component.Component == PluginComponentType.Spinner);
        Assert.DoesNotContain(
            Rendered.All(view),
            component => component.Component == PluginComponentType.EmptyState);
    }

    /// <remarks>
    /// While a run is going the bar says since when, as a clock time — the
    /// owner's decision of 11 September 2026. "Never run" beside a Running
    /// badge, which is what it said that day, contradicted itself.
    /// </remarks>
    [Fact]
    public void ARunningDashboardSaysSinceWhen()
    {
        PluginView view = DashboardView.Render(
            new([], [], Now),
            new(true, null, Now.AddHours(6)) { StartedAt = Now.AddMinutes(-3) });

        string bar = string.Join(" ", Rendered.Words(view));

        Assert.Contains($"running since {Clock(Now.AddMinutes(-3))}", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("never run", bar, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A run that was stopped says so, and when. It is not the same as one that
    /// finished, and the owner, who pressed Stop, is the one who needs to see
    /// that the stop took.
    /// </remarks>
    [Fact]
    public void AStoppedRunSaysItWasStoppedAndWhen()
    {
        PluginView view = DashboardView.Render(
            new([], [], Now),
            new(false, Now.AddMinutes(-1), Now.AddHours(6)) { LastEnd = RunEnd.Stopped });

        string bar = string.Join(" ", Rendered.Words(view));

        Assert.Contains($"stopped at {Clock(Now.AddMinutes(-1))}", bar, StringComparison.Ordinal);
        Assert.Contains($"next run {Clock(Now.AddHours(6))}", bar, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And one that finished says that.
    /// </remarks>
    [Fact]
    public void AFinishedRunSaysWhenItFinished()
    {
        PluginView view = DashboardView.Render(
            new([], [], Now),
            new(false, Now.AddMinutes(-14), Now.AddHours(6)) { LastEnd = RunEnd.Finished });

        string bar = string.Join(" ", Rendered.Words(view));

        Assert.Contains($"last run finished at {Clock(Now.AddMinutes(-14))}", bar, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A row per stage of the run, each with its counts — the owner's approval
    /// of 11 September 2026. Numbers the run really counted, from nought when it
    /// began.
    /// </remarks>
    [Fact]
    public void ARunningDashboardHasARowPerStageWithItsCounts()
    {
        ActivitySnapshot snapshot = new([], [], Now)
        {
            Run = new(
                Now.AddMinutes(-3),
                new Dictionary<RunCounter, int>
                {
                    [RunCounter.SitesLookedAt] = 5,
                    [RunCounter.SitesChallenged] = 5,
                    [RunCounter.SitesCleared] = 5,
                    [RunCounter.Episodes] = 42,
                    [RunCounter.EpisodesAsked] = 3,
                    [RunCounter.NamesFound] = 17,
                    [RunCounter.NamesRefused] = 9,
                    [RunCounter.Questions] = 31,
                    [RunCounter.QuestionsAnswered] = 12,
                    [RunCounter.Decided] = 2,
                    [RunCounter.Taken] = 1,
                }),
        };

        PluginView view = DashboardView.Render(snapshot, new(true, null, Now.AddHours(6)) { StartedAt = Now.AddMinutes(-3) });

        string[] words = [.. Rendered.Words(new() { Components = [Rendered.ById(view, DashboardView.StagesTableId)] })];

        Assert.Contains("5 sites looked at · 5 challenged · 5 cleared · 0 failed", words);
        Assert.Contains("3 of 42 episodes asked · 17 names · 9 refused", words);
        Assert.Contains("31 questions · 12 answered with rows", words);
        Assert.Contains("2 of 42 episodes decided", words);
        Assert.Contains("1 handed to the client", words);
    }

    /// <remarks>
    /// No run, no stage rows. The owner asked for the page to be cleared when a
    /// run stops, and rows left standing read as a run still going.
    /// </remarks>
    [Fact]
    public void AnIdleDashboardHasNoStageRows()
    {
        PluginView view = DashboardView.Render(new([], [], Now), new(false, Now.AddMinutes(-1), null));

        Assert.DoesNotContain(Rendered.All(view), component => component.Id == DashboardView.StagesTableId);
    }

    /// <remarks>
    /// What the run did for each episode it is still working on: which source
    /// was asked and what it said, which name was refused and why, every
    /// question to every indexer and its answer.
    /// </remarks>
    [Fact]
    public void WhatWasNotedAboutAnEpisodeIsOnThePage()
    {
        ActivitySnapshot snapshot = new([], [], Now)
        {
            Run = new(Now.AddMinutes(-3), new Dictionary<RunCounter, int>()),
            Notes =
            [
                new(ActivityStage.Names, "Dark Matter S02E03", Now.AddSeconds(-20),
                    "PreDB · Dark Matter S02E03 1080p · 1 name: Dark.Matter.2024.S02E03.1080p.WEB.H264-CAKES"),
                new(ActivityStage.Find, "Dark Matter S02E03", Now.AddSeconds(-5),
                    "TorrentBay · Dark.Matter.2024.S02E03.1080p.WEB.H264-CAKES · 1 row"),
            ],
        };

        PluginView view = DashboardView.Render(snapshot, new(true, null, null) { StartedAt = Now.AddMinutes(-3) });

        string[] words = [.. Rendered.Words(new() { Components = [Rendered.ById(view, DashboardView.NotesTableId)] })];

        Assert.Contains("Dark Matter S02E03", words);
        Assert.Contains("PreDB · Dark Matter S02E03 1080p · 1 name: Dark.Matter.2024.S02E03.1080p.WEB.H264-CAKES", words);
        Assert.Contains("TorrentBay · Dark.Matter.2024.S02E03.1080p.WEB.H264-CAKES · 1 row", words);
    }

    private static string Clock(DateTimeOffset moment)
    {
        return moment.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
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

        Assert.Equal(Ui.TableComponent, Rendered.ById(view, DashboardView.NowTableId).Component);
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
