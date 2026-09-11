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

    public const string StagesTableId = "stages";

    public const string NotesTableId = "notes";

    /// <summary>
    /// The status bar, the stages of the run, what is in flight, and what the
    /// run did for each episode it is still working on.
    /// </summary>
    /// <remarks>
    /// The stages and the notes only while a run is going. The owner asked on
    /// 11 September 2026 for the page to be cleared when a run stops, and rows
    /// left standing read as a run waiting to carry on.
    /// </remarks>
    /// <param name="activity">The journal, frozen.</param>
    /// <param name="cycle">Where the search cycle stands.</param>
    /// <param name="downloads">
    /// What the client holds, for the Download stage, or null when no run is
    /// going and no stage is drawn.
    /// </param>
    public static PluginView Render(
        ActivitySnapshot activity,
        CycleStatus cycle,
        IReadOnlyList<DownloadRow>? downloads = null)
    {
        List<PluginComponent> components = [StatusBar(cycle, activity.TakenAt)];

        if (activity.Run is SearchProgress run)
        {
            components.Add(StagesTable(run, downloads ?? []));
        }

        components.Add(NowTable(activity));

        if (activity.Run is not null || activity.Notes.Count > 0)
        {
            components.Add(NotesTable(activity.Notes));
        }

        return new()
        {
            Layout = PluginLayout.Wide,
            Components = [.. components],
        };
    }

    /// <summary>A row per stage of the run, each with what it has counted so far.</summary>
    private static PluginComponent StagesTable(SearchProgress run, IReadOnlyList<DownloadRow> downloads)
    {
        (string Stage, string Counts)[] stages =
        [
            ("Warm-up", $"{run.Count(RunCounter.SitesLookedAt)} sites looked at · {run.Count(RunCounter.SitesChallenged)} challenged · {run.Count(RunCounter.SitesCleared)} cleared · {run.Count(RunCounter.SitesFailed)} failed"),
            ("Names", $"{run.Count(RunCounter.EpisodesAsked)} of {run.Count(RunCounter.Episodes)} episodes asked · {run.Count(RunCounter.NamesFound)} names · {run.Count(RunCounter.NamesRefused)} refused"),
            ("Find", $"{run.Count(RunCounter.Questions)} questions · {run.Count(RunCounter.QuestionsAnswered)} answered with rows"),
            ("Decide", $"{run.Count(RunCounter.Decided)} of {run.Count(RunCounter.Episodes)} episodes decided"),
            ("Grab", $"{run.Count(RunCounter.Taken)} handed to the client"),
            ("Download", Downloading(downloads)),
        ];

        return Ui.Table(
            StagesTableId,
            [
                new() { Key = "stage", Label = "Stage" },
                new() { Key = "counts", Label = "So far" },
            ],
            [
                .. stages.Select(((string Stage, string Counts) stage, int index) => Ui.Row(
                    $"{StagesTableId}-{index}",
                    new Dictionary<string, object?>
                    {
                        ["stage"] = stage.Stage,
                        ["counts"] = stage.Counts,
                    })),
            ],
            "Nothing counted yet.");
    }

    /// <summary>
    /// What the client holds, counted by the state the Downloads page gives
    /// each one — the same words, so the two pages cannot disagree.
    /// </summary>
    private static string Downloading(IReadOnlyList<DownloadRow> downloads)
    {
        return downloads.Count == 0
            ? "nothing in the client"
            : string.Join(
                " · ",
                downloads
                    .GroupBy(DownloadsView.State)
                    .OrderByDescending(state => state.Count())
                    .Select(state => $"{state.Count()} {state.Key}"));
    }

    /// <summary>
    /// What the run did for each episode it has not decided yet, oldest first.
    /// </summary>
    /// <remarks>
    /// Literally what the plugin did — the owner's words of 11 September 2026:
    /// which source was asked and what it answered, which name was refused and
    /// why, and every question to every indexer with how many rows came back.
    /// </remarks>
    private static PluginComponent NotesTable(IReadOnlyList<EpisodeNote> notes)
    {
        return Ui.Table(
            NotesTableId,
            [
                new() { Key = "at", Label = "Time" },
                new() { Key = "episode", Label = "Episode" },
                new() { Key = "step", Label = "Step" },
                new() { Key = "what", Label = "What" },
            ],
            [
                .. notes.Select((EpisodeNote note, int index) => Ui.Row(
                    $"{NotesTableId}-{index}",
                    new Dictionary<string, object?>
                    {
                        ["at"] = note.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        ["episode"] = note.Episode,
                        ["step"] = note.Stage.ToString(),
                        ["what"] = note.Line,
                    })),
            ],
            "Nothing noted yet.");
    }

    private static PluginComponent StatusBar(CycleStatus cycle, DateTimeOffset now)
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
