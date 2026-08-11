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
                "What became of each release, after it left the downloads list.",
                Rows(history)));

    private static PluginComponent Rows(IReadOnlyList<HistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return Ui.EmptyState(
                "history-empty",
                "Nothing has happened yet",
                "Grabs, imports and failures are recorded here as they occur.");
        }

        List<PluginComponent> rows = [];

        foreach (HistoryEntry entry in history.Take(Limit))
        {
            string id = $"{entry.At.ToUnixTimeMilliseconds()}-{entry.Key}";

            rows.Add(Ui.Row(
                $"history-row-{id}",
                Ui.Badge($"history-badge-{id}", Format.Outcome(entry.Event), Format.OutcomeVariant(entry.Event)),
                Ui.Text($"history-title-{id}", entry.ReleaseTitle),
                Ui.Text($"history-slot-{id}", Format.Slot(entry.Key), "caption"),
                Ui.Text($"history-when-{id}", Format.Ago(entry.At), "caption"),
                Ui.Text($"history-detail-{id}", entry.Detail ?? "", "caption")));
        }

        return Ui.List("history-list", [.. rows]);
    }
}
