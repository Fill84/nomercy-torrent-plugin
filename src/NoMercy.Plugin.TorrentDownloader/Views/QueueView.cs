// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What the plugin is looking for, in the order it will look.
///
/// <para>
/// This page used to be one list called "Wanted", and an owner who wants everything learns
/// nothing from a list of everything. The three questions it could not answer were: which of
/// these is it about to search for, has it already tried this one and how often, and why is
/// that one just sitting there. They are three different states with three different
/// answers, so they are three sections.
/// </para>
///
/// <para>
/// The order inside the first one is the cadence's own - least recently searched first - so
/// the top of that list is literally the next thing the plugin will ask an indexer about.
/// </para>
/// </summary>
public static class QueueView
{
    /// <summary>Long enough to be useful, short enough that a first run does not render a thousand rows.</summary>
    public const int PreviewLength = 25;

    /// <summary>
    /// Slower than the downloads page: a queue changes when the search cadence runs, not
    /// second by second, and this page has no bar on it that would look frozen.
    /// </summary>
    public const int RefreshSeconds = 60;

    public static PluginView Build(IReadOnlyList<WantedEpisode> wanted)
    {
        List<WantedEpisode> unavailable = [.. wanted.Where(episode => episode.State == WantedState.Unavailable)];
        List<WantedEpisode> waiting = [.. wanted.Where(episode => Format.NotOutYet(episode) && episode.State != WantedState.Unavailable)];

        // Everything that has aired and has not been given up on, in the order the cadence
        // takes them: the store hands them back least recently searched first, and that is
        // the order this list must not re-sort.
        List<WantedEpisode> next =
        [
            .. wanted.Where(episode =>
                !Format.NotOutYet(episode) && episode.State != WantedState.Unavailable),
        ];

        List<PluginComponent> children =
        [
            Ui.Text("queue-summary", Summary(next.Count, waiting.Count, unavailable.Count)),
            Ui.Section(
                "queue-next",
                Count("Searching for", next.Count),
                next.Count == 0
                    ? null
                    : "In the order it will ask, soonest first. Ten go out every search cycle. Click one to ask now.",
                Next(next)),
        ];

        if (waiting.Count > 0)
        {
            children.Add(Ui.Section(
                "queue-waiting",
                Count("Waiting to air", waiting.Count),
                "Nobody can seed an episode that has not gone out yet, so these are left alone until they do.",
                Waiting(waiting)));
        }

        if (unavailable.Count > 0)
        {
            children.Add(Ui.Section(
                "queue-unavailable",
                Count("Given up on", unavailable.Count),
                "Asked for often enough that the answer is not going to change on its own. Click one to try it again anyway.",
                Next(unavailable)));
        }

        return Pages.Page(Pages.Queue, RefreshSeconds, [.. children]);
    }

    private static readonly PluginTableColumn[] NextColumns =
    [
        new() { Key = "show", Label = "Show" },
        new() { Key = "episode", Label = "Episode", Width = "7rem" },
        new() { Key = "state", Label = "State", Cell = PluginTableCellType.Badge, Width = "9rem" },
        new() { Key = "tried", Label = "Asked", Width = "7rem", Align = "right" },
        new() { Key = "last", Label = "Last tried", Width = "9rem" },
    ];

    private static PluginComponent Next(IReadOnlyList<WantedEpisode> episodes)
    {
        if (episodes.Count == 0)
        {
            return Ui.EmptyState(
                "queue-empty",
                "Nothing to look for",
                "Every episode that has aired is either here already or on its way.");
        }

        List<PluginComponent> rows = [];

        foreach (WantedEpisode episode in episodes.Take(PreviewLength))
        {
            (string label, string variant) = State(episode);

            rows.Add(Ui.TableRow(
                $"queue-wanted-{episode.Key}",
                new()
                {
                    ["show"] = episode.ShowTitle,
                    ["episode"] = Format.Slot(episode.Key),
                    ["state"] = label,
                    ["stateVariant"] = variant,

                    // Blank rather than nought: a row nobody has asked about yet is a
                    // different thing from one asked about and answered nothing, and a
                    // column of zeroes hides which is which.
                    ["tried"] = episode.SearchAttempts == 0 ? "" : episode.SearchAttempts.ToString(),
                    ["last"] = episode.LastSearchedAt is { } when ? Format.Ago(when) : "not yet",
                },

                // The whole row, because the cadence works least-recently-searched first,
                // ten at a time - the right order for a machine and the wrong one for
                // somebody who wants tonight's episode. A twenty-five row list of buttons
                // was what made this page unreadable; a row that is itself the button is a
                // bigger target and leaves the columns lined up.
                PluginActionIntent.CallPlugin(
                    $"{PluginMethods.SearchNow}/{episode.Key.ShowId}/{episode.Key.Season}/{episode.Key.Episode}")));
        }

        return Ui.Table("queue-list", NextColumns, rows);
    }

    private static readonly PluginTableColumn[] WaitingColumns =
    [
        new() { Key = "show", Label = "Show" },
        new() { Key = "episode", Label = "Episode", Width = "7rem" },
        new() { Key = "airs", Label = "Airs", Width = "10rem" },
    ];

    /// <summary>
    /// By air date, because that is the only thing anybody wants to know about a list of
    /// episodes that have not happened yet: which one is next.
    /// </summary>
    private static PluginComponent Waiting(IReadOnlyList<WantedEpisode> episodes)
    {
        List<PluginComponent> rows = [];

        foreach (WantedEpisode episode in episodes.OrderBy(episode => episode.AirDate).Take(PreviewLength))
        {
            rows.Add(Ui.TableRow(
                $"queue-waiting-{episode.Key}",
                new()
                {
                    ["show"] = episode.ShowTitle,
                    ["episode"] = Format.Slot(episode.Key),
                    ["airs"] = episode.AirDate is { } airs ? airs.ToString("d MMM yyyy") : "",
                }));
        }

        return Ui.Table("queue-waiting-list", WaitingColumns, rows);
    }

    /// <summary>The whole page in one line, so the three counts do not have to be added up by eye.</summary>
    private static string Summary(int next, int waiting, int unavailable)
    {
        List<string> parts =
        [
            next == 1 ? "1 episode to look for" : $"{next} episodes to look for",
        ];

        if (waiting > 0)
            parts.Add(waiting == 1 ? "1 waiting to air" : $"{waiting} waiting to air");

        if (unavailable > 0)
            parts.Add(unavailable == 1 ? "1 given up on" : $"{unavailable} given up on");

        return string.Join(" · ", parts);
    }

    private static string Count(string label, int count) => $"{label} ({count})";

    private static (string Label, string Variant) State(WantedEpisode episode) =>
        episode switch
        {
            { State: WantedState.Searching } => ("Searching", PluginBadgeVariant.Info),
            { State: WantedState.Grabbed } => ("Downloading", PluginBadgeVariant.Success),
            { State: WantedState.Unavailable } => ("Not found", PluginBadgeVariant.Warning),

            // Asked about and told no, which is not the same as never asked. Without this
            // the whole list reads "Wanted" for hours and looks like nothing is happening.
            { SearchAttempts: > 0 } => ("Looking", PluginBadgeVariant.Info),

            // Semantic, never a colour. What "not found" should look like is the theme's
            // business and changes between light, dark and a television.
            _ => ("Queued", PluginBadgeVariant.Neutral),
        };
}
