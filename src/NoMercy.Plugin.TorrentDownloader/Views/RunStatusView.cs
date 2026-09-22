using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The run's status line: whether a run is going, when the last one ended, when the next one starts,
/// and Run or Stop.
/// </summary>
/// <remarks>
/// One line drawn on two pages — the overview opens with it (<c>docs/specs/show-list.md</c>) and the
/// Activity page carries it above what the run is doing — so it lives here once rather than in both.
/// </remarks>
public static class RunStatusView
{
    /// <summary>The status line, as a row.</summary>
    public static PluginComponent Line(CycleStatus cycle)
    {
        return Ui.Row(
            "status",
            Ui.Badge(
                "status-state",
                cycle.Running ? "Running" : "Idle",
                cycle.Running ? PluginBadgeVariant.Info : PluginBadgeVariant.Neutral),
            Ui.Text(
                "status-last",
                cycle is { Running: true, StartedAt: DateTimeOffset since }
                    ? $"running since {Clock(since)}"
                    : LastRan(cycle.LastRanAt, cycle.LastEnd)),
            Ui.Text("status-next", NextDue(cycle.NextDueAt)),

            // docs/08-ui.md § Actions puts RunNow on the Dashboard as well as
            // on Settings. The dashboard is where an owner watches, so it is
            // where they reach for it when nothing is happening.
            Ui.Button(
                "status-run",
                cycle.Running ? "Stop" : "Run now",
                PluginActionIntent.CallPlugin(
                    cycle.Running ? SettingsView.StopAction : SettingsView.RunAction,
                    null,
                    PluginActionTransport.Rest),
                variant: cycle.Running ? null : "primary"));
    }

    private static string LastRan(DateTimeOffset? lastRanAt, RunEnd? how)
    {
        if (lastRanAt is not DateTimeOffset at)
        {
            return "never run";
        }

        // How it ended, because a stopped run is not a finished one — and the
        // owner, who pressed Stop, needs to see that the stop took.
        return how switch
        {
            RunEnd.Finished => $"last run finished at {Clock(at)}",
            RunEnd.Stopped => $"stopped at {Clock(at)}",
            RunEnd.Failed => $"last run failed at {Clock(at)}",
            _ => $"last run {Clock(at)}",
        };
    }

    private static string NextDue(DateTimeOffset? nextDueAt)
    {
        // Not "not scheduled": the cadences are registered with the server from
        // the moment the plugin loads, so saying they are not would be false.
        // What is missing is the time, and that is what it says.
        return nextDueAt is null ? "next run time not known" : $"next run {Clock(nextDueAt.Value)}";
    }

    /// <summary>
    /// A moment, said as a clock time rather than as a distance from now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The owner's decision of 11 September 2026.</strong> "Last ran 4
    /// minutes ago" is true for one minute and then quietly wrong, and nothing
    /// can push to correct it: the plugin pushes when something it holds
    /// changes, and the passing of a minute changes nothing it holds. A page
    /// left open sat on "4 minutes ago" for an hour.
    /// </para>
    /// <para>
    /// A clock time never goes stale. It is also what the owner can check
    /// against the server log, which is the other place they look.
    /// </para>
    /// </remarks>
    private static string Clock(DateTimeOffset moment)
    {
        return moment.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
