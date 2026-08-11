// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Every episode the plugin still wants, and where each one stands.
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

    public static PluginView Build(IReadOnlyList<WantedEpisode> wanted) =>
        Pages.Page(
            Pages.Queue,
            RefreshSeconds,
            Ui.Section(
                "queue-wanted",
                Heading(wanted),
                Note(wanted),
                Rows(wanted)));

    private static readonly PluginTableColumn[] Columns =
    [
        new() { Key = "show", Label = "Show" },
        new() { Key = "episode", Label = "Episode", Width = "7rem" },
        new() { Key = "state", Label = "State", Cell = PluginTableCellType.Badge, Width = "10rem" },
    ];

    private static PluginComponent Rows(IReadOnlyList<WantedEpisode> wanted)
    {
        if (wanted.Count == 0)
        {
            return Ui.EmptyState(
                "queue-empty",
                "Nothing is missing",
                "Every episode the library knows about has a file.");
        }

        List<PluginComponent> rows = [];

        foreach (WantedEpisode episode in wanted.Take(PreviewLength))
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
                },

                // The whole row, because the cadence works least-recently-searched first,
                // ten at a time - the right order for a machine and the wrong one for
                // somebody who wants tonight's episode. A twenty-five row list of buttons
                // was what made this page unreadable; a row that is itself the button is a
                // bigger target and leaves the columns lined up.
                PluginActionIntent.CallPlugin(
                    $"{PluginMethods.SearchNow}/{episode.Key.ShowId}/{episode.Key.Season}/{episode.Key.Episode}")));
        }

        return Ui.Table("queue-list", Columns, rows);
    }

    /// <summary>
    /// What a row is for, said in words, because a clickable row does not announce itself
    /// the way a button labelled "Search now" did.
    /// </summary>
    private static string? Note(IReadOnlyList<WantedEpisode> wanted) =>
        wanted.Count == 0 ? null : "Click an episode to search for it now.";

    private static (string Label, string Variant) State(WantedEpisode episode) =>
        episode switch
        {
            _ when Format.NotOutYet(episode) => ($"Airs {episode.AirDate:d MMM}", PluginBadgeVariant.Neutral),
            { State: WantedState.Searching } => ("Searching", PluginBadgeVariant.Info),
            { State: WantedState.Grabbed } => ("Downloading", PluginBadgeVariant.Success),
            { State: WantedState.Unavailable } => ("Not found", PluginBadgeVariant.Warning),

            // Semantic, never a colour. What "not found" should look like is the theme's
            // business and changes between light, dark and a television.
            _ => ("Wanted", PluginBadgeVariant.Neutral),
        };

    private static string Heading(IReadOnlyList<WantedEpisode> wanted) =>
        wanted.Count > PreviewLength
            ? $"Wanted ({PreviewLength} of {wanted.Count})"
            : $"Wanted ({wanted.Count})";
}
