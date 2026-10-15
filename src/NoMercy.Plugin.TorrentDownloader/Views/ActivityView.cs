using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.PluginSdk.Abstractions;

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
public static class ActivityView
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
    /// <param name="tab">The table the path names, or null for the first.</param>
    /// <param name="query">How the client asked for the table shown to be sorted and filtered.</param>
    public static PluginView Render(
        ActivitySnapshot activity,
        CycleStatus cycle,
        IReadOnlyList<DownloadRow>? downloads = null,
        string? tab = null,
        TableQuery? query = null)
    {
        List<Tab> tables = [];

        if (activity.Run is SearchProgress run)
        {
            // No count on the stages: they are the same six rows on every run, and a number that never
            // changes says nothing about it.
            tables.Add(new(StagesTableId, "Stages", null, TabRoute(StagesTableId), StagesTable(run, downloads ?? [], query)));
        }

        tables.Add(new(NowTableId, "Now", activity.InFlight.Count, TabRoute(NowTableId), NowTable(activity, query)));

        if (activity.Run is not null || activity.Notes.Count > 0)
        {
            // The table and the whole of every note it draws short, under it in the tab (S15-02).
            tables.Add(new(NotesTableId, "Notes", activity.Notes.Count, TabRoute(NotesTableId), NotesTable(activity.Notes, query)));
        }

        return new()
        {
            Layout = PluginLayout.Wide,

            // One table at a time while a run gives the page three (GitHub issue #4); the status line stays
            // above the tabs, because whether a run is going is the answer to every one of them.
            Components = [RunStatusView.Line(cycle), .. Tabs.Draw(tables, tab)],
        };
    }

    private static string TabRoute(string tab)
    {
        return Pages.Routes.PathTo(Pages.ActivityTabName, new Dictionary<string, string> { ["tab"] = tab });
    }

    /// <summary>A row per stage of the run, each with what it has counted so far.</summary>
    private static IReadOnlyList<PluginComponent> StagesTable(SearchProgress run, IReadOnlyList<DownloadRow> downloads, TableQuery? query)
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

        // Words both, so both sort by the words: the stages' own order is the one a run goes through them in.
        return TableRows.Table(
            StagesTableId,
            [
                new() { Key = "stage", Label = "Stage" },
                new() { Key = "counts", Label = "So far" },
            ],
            stages.Select(((string Stage, string Counts) stage, int index) => (stage.Stage, stage.Counts, Index: index)),
            stage => Ui.Row(
                $"{StagesTableId}-{stage.Index}",
                new Dictionary<string, object?>
                {
                    ["stage"] = stage.Stage,
                    ["counts"] = stage.Counts,
                }),
            "Nothing counted yet.",
            query);
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
    private static IReadOnlyList<PluginComponent> NotesTable(IReadOnlyList<EpisodeNote> notes, TableQuery? query)
    {
        return TableRows.Table(
            NotesTableId,
            [
                new() { Key = "at", Label = "Time" },
                new() { Key = "episode", Label = "Episode" },
                new() { Key = "step", Label = "Step" },
                new() { Key = "what", Label = "What" },
            ],
            notes.Select((EpisodeNote note, int index) => (Note: note, Index: index)),
            noted => Ui.Row(
                $"{NotesTableId}-{noted.Index}",
                new Dictionary<string, object?>
                {
                    ["at"] = noted.Note.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ["episode"] = noted.Note.Episode,
                    ["step"] = noted.Note.Stage.ToString(),
                    ["what"] = noted.Note.Line,
                }),
            "Nothing noted yet.",
            query,
            new Dictionary<string, Func<(EpisodeNote Note, int Index), IComparable?>>
            {
                // The time itself: the clock in the cell starts again at midnight.
                ["at"] = noted => noted.Note.At,

                // A step's place in a run, not its name: Names comes before Find.
                ["step"] = noted => noted.Note.Stage,
            },

            // A note quotes what a source answered, address and all, and is as long as that answer.
            freeText: ["what"]);
    }

    private static IReadOnlyList<PluginComponent> NowTable(ActivitySnapshot activity, TableQuery? query)
    {
        return TableRows.Table(
            NowTableId,
            [
                new() { Key = "subject", Label = "Episode" },
                new() { Key = "stage", Label = "Stage" },
                new() { Key = "waiting", Label = "Waiting on" },
            ],
            activity.InFlight.Select((ActivityEvent work, int index) => (Work: work, Index: index)),
            flying => Ui.Row(
                $"{NowTableId}-{flying.Index}",
                new Dictionary<string, object?>
                {
                    ["subject"] = flying.Work.Subject,
                    ["stage"] = flying.Work.Stage.ToString(),
                    // Empty rather than invented: the stage on its own is the
                    // whole answer when a stage reported no detail.
                    ["waiting"] = flying.Work.Detail ?? string.Empty,
                }),
            // Not an EmptyState: that is for a plugin with nothing configured.
            // An idle plugin with nothing in flight is working correctly.
            "Nothing in flight.",
            query,
            new Dictionary<string, Func<(ActivityEvent Work, int Index), IComparable?>>
            {
                // A stage's place in a run, not its name: Names comes before Find.
                ["stage"] = flying => flying.Work.Stage,
            });
    }
}
