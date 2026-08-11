// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Everything the plugin is doing, grouped by the thing an owner actually thinks in.
///
/// <para>
/// This is the page the whole overhaul was for. A queue of two hundred episode rows answers
/// "what is missing" and nothing else: whether a show is being worked on, whether it is
/// stuck, and whether anything of it has arrived are all questions you have to reconstruct
/// by scanning for a name. Per show, they are three columns.
/// </para>
/// </summary>
public static class ShowsView
{
    /// <summary>Slower than a progress bar, faster than a settings page: counts change when a cadence runs.</summary>
    public const int RefreshSeconds = 60;

    /// <summary>How much of a show's own history its page shows.</summary>
    public const int HistoryLimit = 10;

    private static readonly PluginTableColumn[] Columns =
    [
        new() { Key = "show", Label = "Show" },
        new() { Key = "state", Label = "State", Cell = PluginTableCellType.Badge, Width = "9rem" },
        new() { Key = "missing", Label = "Missing", Width = "7rem", Align = "right" },
        new() { Key = "downloading", Label = "Downloading", Width = "8rem", Align = "right" },
        new() { Key = "arrived", Label = "Last arrived", Width = "10rem" },
    ];

    public static PluginView Build(IReadOnlyList<ShowSummary> shows) =>
        Pages.Page(
            Pages.Shows,
            RefreshSeconds,
            Ui.Section(
                "shows-all",
                Format.Count("Shows", shows.Count),
                shows.Count == 0 ? null : "Click one to see what is missing from it.",
                Rows(shows)),

            // The way past the two rules that decide the list above. Without it the plugin
            // can only ever finish a show somebody started by hand and can never begin one,
            // or go back to one that has ended - and a list of every show the library has
            // heard of is exactly what this page is not.
            Ui.Section(
                "shows-follow",
                "Follow another show",
                "Type the name of a show in your library. Add the year in brackets if two have the same name.",
                Ui.Form(
                    "shows-follow-form",
                    "Follow",
                    PluginActionIntent.CallPlugin(PluginMethods.FollowByName),
                    new PluginFormField { Name = "name", Label = "Show", Required = true })));

    private static PluginComponent Rows(IReadOnlyList<ShowSummary> shows)
    {
        if (shows.Count == 0)
        {
            return Ui.EmptyState(
                "shows-empty",
                "No shows yet",
                "A show appears here once at least one of its episodes is on the server and it is still going out. Anything else can be followed by name below.");
        }

        List<PluginComponent> rows = [];

        // Whatever needs attention first, then whatever is busy, then the rest by name. A
        // list ordered by title alone buries the one show that is stuck behind twenty that
        // are fine.
        IEnumerable<ShowSummary> ordered = shows
            .OrderByDescending(show => show.Downloading)
            .ThenByDescending(show => show.Missing)
            .ThenBy(show => show.Title, StringComparer.CurrentCultureIgnoreCase);

        foreach (ShowSummary show in ordered)
        {
            (string label, string variant) = State(show);

            rows.Add(Ui.TableRow(
                $"shows-row-{show.ShowId}",
                new()
                {
                    ["show"] = show.Title,
                    ["state"] = label,
                    ["stateVariant"] = variant,
                    ["missing"] = show.Missing == 0 ? "" : show.Missing.ToString(),
                    ["downloading"] = show.Downloading == 0 ? "" : show.Downloading.ToString(),
                    ["arrived"] = show.LastArrived is { } when ? Format.Ago(when) : "",
                },
                Pages.Routes.GoTo(Pages.Show, new Dictionary<string, string> { ["showId"] = show.ShowId.ToString() })));
        }

        return Ui.Table("shows-list", Columns, rows);
    }

    /// <summary>
    /// One show: what is missing from it, what is running, and the one button that decides
    /// whether the plugin looks at it at all.
    /// </summary>
    public static PluginView Detail(
        ShowSummary show,
        IReadOnlyList<WantedEpisode> wanted,
        IReadOnlyList<HistoryEntry> history)
    {
        List<PluginComponent> children =
        [
            Ui.Row(
                "show-state",
                Ui.Badge("show-state-badge", State(show).Label, State(show).Variant),
                Ui.Text("show-summary", Summary(show))),

            Ui.Row("show-actions", FollowButton(show)),
        ];

        if (wanted.Count > 0)
        {
            children.Add(Ui.Section(
                "show-missing",
                Format.Count("Missing", wanted.Count),
                "Click an episode to search for it now.",
                Missing(wanted)));
        }

        if (history.Count > 0)
        {
            children.Add(Ui.Section(
                "show-history",
                "Recently",
                null,
                Recently(history)));
        }

        return Pages.Page(Pages.Show, show.Title, RefreshSeconds, [.. children]);
    }

    private static readonly PluginTableColumn[] MissingColumns =
    [
        new() { Key = "episode", Label = "Episode", Width = "7rem" },
        new() { Key = "title", Label = "Title" },
        new() { Key = "state", Label = "State", Cell = PluginTableCellType.Badge, Width = "10rem" },
    ];

    private static PluginComponent Missing(IReadOnlyList<WantedEpisode> wanted)
    {
        List<PluginComponent> rows = [];

        foreach (WantedEpisode episode in wanted.OrderBy(episode => episode.Key.Season).ThenBy(episode => episode.Key.Episode))
        {
            (string label, string variant) = EpisodeState(episode);

            rows.Add(Ui.TableRow(
                $"show-wanted-{episode.Key}",
                new()
                {
                    ["episode"] = Format.Slot(episode.Key),
                    ["title"] = episode.EpisodeTitle ?? "",
                    ["state"] = label,
                    ["stateVariant"] = variant,
                },
                PluginActionIntent.CallPlugin(
                    $"{PluginMethods.SearchNow}/{episode.Key.ShowId}/{episode.Key.Season}/{episode.Key.Episode}")));
        }

        return Ui.Table("show-missing-list", MissingColumns, rows);
    }

    private static readonly PluginTableColumn[] HistoryColumns =
    [
        new() { Key = "outcome", Label = "Outcome", Cell = PluginTableCellType.Badge, Width = "8rem" },
        new() { Key = "release", Label = "Release" },
        new() { Key = "episode", Label = "Episode", Width = "7rem" },
        new() { Key = "when", Label = "When", Width = "8rem" },
    ];

    private static PluginComponent Recently(IReadOnlyList<HistoryEntry> history)
    {
        List<PluginComponent> rows = [];

        foreach (HistoryEntry entry in history.Take(HistoryLimit))
        {
            rows.Add(Ui.TableRow(
                $"show-history-{entry.At.ToUnixTimeMilliseconds()}-{entry.Key}",
                new()
                {
                    ["outcome"] = Format.Outcome(entry.Event),
                    ["outcomeVariant"] = Format.OutcomeVariant(entry.Event),
                    ["release"] = entry.ReleaseTitle,
                    ["episode"] = Format.Slot(entry.Key),
                    ["when"] = Format.Ago(entry.At),
                }));
        }

        return Ui.Table("show-history-list", HistoryColumns, rows);
    }

    private static PluginComponent FollowButton(ShowSummary show) =>
        show.Followed
            ? Ui.Button(
                $"show-unfollow-{show.ShowId}",
                "Stop following",
                PluginActionIntent.CallPlugin($"{PluginMethods.UnfollowShow}/{show.ShowId}"))
            : Ui.Button(
                $"show-follow-{show.ShowId}",
                "Follow",
                PluginActionIntent.CallPlugin($"{PluginMethods.FollowShow}/{show.ShowId}"));

    /// <summary>
    /// Where a show stands, in one word.
    ///
    /// <para>
    /// "Waiting" is first because it is the one state that means the plugin has nothing of
    /// the show yet - an owner reading "0 missing" against a show they asked for and have
    /// never seen an episode of would otherwise conclude it had arrived.
    /// </para>
    ///
    /// <para>
    /// Ended and Cancelled appear only for a show the owner followed by hand: the refresh
    /// passes over every other finished series. They are named rather than both reading
    /// "Complete", because which of the two it is is the thing worth knowing about a show
    /// that stopped.
    /// </para>
    /// </summary>
    private static (string Label, string Variant) State(ShowSummary show) => show switch
    {
        { Started: false } => ("Waiting", PluginBadgeVariant.Neutral),
        { Downloading: > 0 } => ("Downloading", PluginBadgeVariant.Success),
        { Missing: > 0 } => ("Missing", PluginBadgeVariant.Warning),
        { Status: ShowStatus.Ended } => ("Ended", PluginBadgeVariant.Neutral),
        { Status: ShowStatus.Canceled } => ("Cancelled", PluginBadgeVariant.Neutral),

        // Up to date and still going out. Nothing is missing today and something will be
        // next week, which is the difference this column exists for.
        _ => ("Airing", PluginBadgeVariant.Success),
    };

    private static (string Label, string Variant) EpisodeState(WantedEpisode episode) => episode switch
    {
        _ when Format.NotOutYet(episode) => ($"Airs {episode.AirDate:d MMM}", PluginBadgeVariant.Neutral),
        { State: WantedState.Searching } => ("Searching", PluginBadgeVariant.Info),
        { State: WantedState.Grabbed } => ("Downloading", PluginBadgeVariant.Success),
        { State: WantedState.Unavailable } => ("Not found", PluginBadgeVariant.Warning),
        _ => ("Wanted", PluginBadgeVariant.Neutral),
    };

    private static string Summary(ShowSummary show)
    {
        if (!show.Started)
            return "Nothing of this is on the server yet. You asked for it, so the plugin is looking.";

        List<string> parts = [];

        parts.Add(show.Missing == 1 ? "1 episode missing" : $"{show.Missing} episodes missing");

        if (show.Downloading > 0)
            parts.Add(show.Downloading == 1 ? "1 downloading" : $"{show.Downloading} downloading");

        if (show.LastArrived is { } arrived)
            parts.Add($"last arrived {Format.Ago(arrived)}");

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// One show as the pages need it: the counts already worked out, so a view stays a view.
/// </summary>
/// <param name="Started">
/// Whether anything of it is on the server. False only for a show the owner asked for by
/// name and none of which has arrived - every other show here has an episode, because that
/// is what got it onto this list.
/// </param>
public sealed record ShowSummary(
    int ShowId,
    string Title,
    int Missing,
    int Downloading,
    DateTimeOffset? LastArrived,
    bool Started,
    bool Followed)
{
    /// <summary>
    /// Where the library says the show stands.
    ///
    /// <para>
    /// The state an owner most wants to pick out: a show that is up to date and finished
    /// needs nothing, and one that is up to date until Tuesday needs watching. Both read as
    /// "0 missing" without this.
    /// </para>
    /// </summary>
    public ShowStatus Status { get; init; } = ShowStatus.Unknown;

    /// <summary>When the next episode airs, when one is scheduled.</summary>
    public DateOnly? NextAirDate { get; init; }
}
