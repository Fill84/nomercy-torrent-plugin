// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

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
                Rows(shows)));

    private static PluginComponent Rows(IReadOnlyList<ShowSummary> shows)
    {
        if (shows.Count == 0)
        {
            return Ui.EmptyState(
                "shows-empty",
                "No shows yet",
                "A show appears here once the library knows about it.");
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
    /// "Not started" is first because it is the one state that means the plugin is
    /// deliberately doing nothing - and an owner reading "0 missing" against a show they
    /// have never watched would otherwise conclude it was complete.
    /// </para>
    /// </summary>
    private static (string Label, string Variant) State(ShowSummary show) => show switch
    {
        { Started: false } => ("Not started", PluginBadgeVariant.Neutral),
        { Downloading: > 0 } => ("Downloading", PluginBadgeVariant.Success),
        { Missing: > 0 } => ("Missing", PluginBadgeVariant.Warning),

        // Up to date and still going out is not the same as up to date and finished. Both
        // have nothing missing today; only one of them will have something missing next
        // week, and that is the whole difference an owner is looking for in this column.
        { Running: true } => ("Airing", PluginBadgeVariant.Success),
        _ => ("Complete", PluginBadgeVariant.Neutral),
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
            return "Nothing of this is on the server, so the plugin leaves it alone.";

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
/// Whether anything of it is on the server. A show nobody has watched is one the plugin
/// deliberately leaves alone, which is a different thing from a show with nothing missing.
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
    /// Whether it is still going out.
    ///
    /// <para>
    /// The state an owner most wants to pick out of a library: a show that is up to date and
    /// finished needs nothing, and one that is up to date until Tuesday needs watching. Both
    /// read as "0 missing" without this.
    /// </para>
    /// </summary>
    public bool Running { get; init; }

    /// <summary>When the next episode airs, when one is scheduled.</summary>
    public DateOnly? NextAirDate { get; init; }
}
