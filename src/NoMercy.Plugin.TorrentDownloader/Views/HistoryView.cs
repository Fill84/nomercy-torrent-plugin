// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What happened, newest first.
///
/// <para>
/// Each line says the outcome, the release, and when - and for the ones that went wrong,
/// why, because "Failed" on its own sends a reader to the log file this page exists to save
/// them from.
/// </para>
/// </summary>
public static class HistoryView
{
    /// <summary>
    /// How much history the page shows. The store keeps more; this is a page rather than an
    /// archive, and its own tab means it can afford more than the overview's handful.
    /// </summary>
    public const int Limit = 50;

    /// <summary>The past does not move. It is re-read only because something new may have joined it.</summary>
    public const int RefreshSeconds = 60;

    public static PluginView Build(IReadOnlyList<HistoryEntry> history) =>
        Pages.Page(
            Pages.History,
            RefreshSeconds,
            Ui.Section(
                "history-recent",
                Format.Count("Recently", history.Count),
                null,
                Rows(history)));

    private static readonly PluginTableColumn[] Columns =
    [
        new() { Key = "outcome", Label = "Outcome", Cell = PluginTableCellType.Badge, Width = "8rem" },
        new() { Key = "release", Label = "Release" },
        new() { Key = "episode", Label = "Episode", Width = "7rem" },
        new() { Key = "when", Label = "When", Width = "8rem" },
        new() { Key = "detail", Label = "Why" },
    ];

    private static PluginComponent Rows(IReadOnlyList<HistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return Ui.EmptyState(
                "history-empty",
                "Nothing has happened yet",
                "Grabs, imports and failures land here.");
        }

        List<PluginComponent> rows = [];

        foreach (HistoryEntry entry in history.Take(Limit))
        {
            string id = $"{entry.At.ToUnixTimeMilliseconds()}-{entry.Key}";

            rows.Add(Ui.TableRow(
                $"history-row-{id}",
                new()
                {
                    ["outcome"] = Format.Outcome(entry.Event),
                    ["outcomeVariant"] = Format.OutcomeVariant(entry.Event),
                    ["release"] = entry.ReleaseTitle,
                    ["episode"] = Format.Slot(entry.Key),
                    ["when"] = Format.Ago(entry.At),
                    ["detail"] = entry.Detail ?? "",
                }));
        }

        return Ui.Table("history-list", Columns, rows);
    }
}
