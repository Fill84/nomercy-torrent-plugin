using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The landing page at <c>/</c>, built from a snapshot and nothing else.
/// </summary>
/// <remarks>
/// A pure function of what it is handed: no store, no clock, no journal. Two
/// clients rendering the same push have to see the same page, and a view that
/// read the time itself would draw "14 min ago" and "15 min ago" from the same
/// state. Even "now" comes from the snapshot.
/// </remarks>
public static class DashboardView
{
    public const string NowTableId = "now";

    /// <summary>
    /// The sections that exist yet. Stages, Sources and Downloads join them as
    /// the pipeline behind each one is built — an empty section drawn now would
    /// be a promise the page cannot keep.
    /// </summary>
    public static PluginView Render(ActivitySnapshot activity, CycleStatus cycle)
    {
        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                StatusBar(cycle, activity.TakenAt),
                NowTable(activity),
            ],
        };
    }

    private static PluginComponent StatusBar(CycleStatus cycle, DateTimeOffset now)
    {
        return Ui.Row(
            "status",
            Ui.Badge(
                "status-state",
                cycle.Running ? "Running" : "Idle",
                cycle.Running ? PluginBadgeVariant.Info : PluginBadgeVariant.Neutral),
            Ui.Text("status-last", LastRan(cycle.LastRanAt)),
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

    private static PluginComponent NowTable(ActivitySnapshot activity)
    {
        List<PluginComponent> rows =
        [
            .. activity.InFlight.Select((ActivityEvent work, int index) => Ui.Row(
                $"{NowTableId}-{index}",
                new Dictionary<string, object?>
                {
                    ["subject"] = work.Subject,
                    ["stage"] = work.Stage.ToString(),
                    // Empty rather than invented: the stage on its own is the
                    // whole answer when a stage reported no detail.
                    ["waiting"] = work.Detail ?? string.Empty,
                })),
        ];

        return Ui.Table(
            NowTableId,
            [
                new() { Key = "subject", Label = "Episode" },
                new() { Key = "stage", Label = "Stage" },
                new() { Key = "waiting", Label = "Waiting on" },
            ],
            rows,
            // Not an EmptyState: that is for a plugin with nothing configured.
            // An idle plugin with nothing in flight is working correctly.
            "Nothing in flight.");
    }

    private static string LastRan(DateTimeOffset? lastRanAt)
    {
        return lastRanAt is null ? "never run" : $"last ran {Clock(lastRanAt.Value)}";
    }

    private static string NextDue(DateTimeOffset? nextDueAt)
    {
        // Not "not scheduled": the cadences are registered with the server from
        // the moment the plugin loads, so saying they are not would be false.
        // What is missing is the time, and that is what it says.
        return nextDueAt is null ? "next run time not known" : $"next due {Clock(nextDueAt.Value)}";
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
