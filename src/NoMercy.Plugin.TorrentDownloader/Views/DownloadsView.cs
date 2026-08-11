// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Only what is moving: progress, rate, peers, and the three buttons that change it.
///
/// <para>
/// Pure - rows in, a view out, no I/O. Everything the page shows is read from the store
/// before this is called, which is what lets the whole page be asserted in a test without a
/// server, a library or a swarm.
/// </para>
/// </summary>
public static class DownloadsView
{
    /// <summary>
    /// How often the client should come back, in seconds.
    ///
    /// <para>
    /// The transfers cadence is what rewrites these numbers, and it runs once a minute by
    /// default, so half of that sees every change soon after it lands without asking for a
    /// render nothing has moved since. A settings page declares zero; this one cannot - a
    /// progress bar that only advances when the user reloads is a screenshot.
    /// </para>
    /// </summary>
    public const int RefreshSeconds = 30;

    public static PluginView Build(IReadOnlyList<Transfer> transfers, IReadOnlyList<Grab> grabs)
    {
        Dictionary<string, Grab> byHash = grabs
            .GroupBy(grab => grab.InfoHash)
            .ToDictionary(group => group.Key, group => group.First());

        return Pages.Page(
            Pages.Downloads,
            RefreshSeconds,
            Ui.Section(
                "downloads-active",
                Format.Count("Downloading", transfers.Count(transfer => !transfer.Paused)),
                Format.TotalRate(transfers),
                ActiveList(transfers, byHash)),

            // The escape hatch. Every other page is the plugin deciding; this is the owner
            // overruling it with a link they found themselves.
            Ui.Section(
                "downloads-add",
                "Add a link",
                "A magnet link for an episode already on the queue. It is matched by name and taken as-is.",
                Ui.Form(
                    "downloads-add-form",
                    "Add",
                    PluginActionIntent.CallPlugin(PluginMethods.AddTorrent),
                    new PluginFormField { Name = "source", Label = "Magnet link", Required = true })));
    }

    private static PluginComponent ActiveList(IReadOnlyList<Transfer> transfers, IReadOnlyDictionary<string, Grab> byHash)
    {
        List<PluginComponent> rows = [];

        foreach (Transfer transfer in transfers.OrderByDescending(transfer => transfer.Progress))
        {
            byHash.TryGetValue(transfer.InfoHash, out Grab? grab);

            rows.Add(Ui.Row(
                $"downloads-row-{transfer.InfoHash}",
                Ui.Text($"downloads-title-{transfer.InfoHash}", grab?.ReleaseTitle ?? transfer.InfoHash),
                Ui.Text($"downloads-episode-{transfer.InfoHash}", grab is null ? "" : Format.Covers(grab), "caption"),
                Ui.Progress($"downloads-progress-{transfer.InfoHash}", transfer.Progress),

                // The percentage as its own text rather than only a label on the bar: a
                // label that lives inside the bar is one a reader walking the page - or a
                // screen reader - may never reach.
                Ui.Text($"downloads-percent-{transfer.InfoHash}", Format.Percentage(transfer), "caption"),

                // Rate and estimate beside the peers, because percentage alone cannot tell a
                // download apart from a stall. A torrent sitting at 34% with four peers looks
                // healthy right up until you notice it looked that way an hour ago.
                Ui.Text($"downloads-rate-{transfer.InfoHash}", Format.Rate(transfer), "caption"),
                Ui.Text($"downloads-peers-{transfer.InfoHash}", Format.Peers(transfer.Peers), "caption"),

                transfer.Paused
                    ? Ui.Button(
                        $"downloads-resume-{transfer.InfoHash}",
                        "Resume",
                        PluginActionIntent.CallPlugin($"{PluginMethods.ResumeDownload}/{transfer.InfoHash}"))
                    : Ui.Button(
                        $"downloads-pause-{transfer.InfoHash}",
                        "Pause",
                        PluginActionIntent.CallPlugin($"{PluginMethods.PauseDownload}/{transfer.InfoHash}")),

                // Confirmed, because it deletes the bytes and blacklists the release for a
                // fortnight. That is not an undo away.
                Ui.DestructiveButton(
                    $"downloads-cancel-{transfer.InfoHash}",
                    "Cancel",
                    $"{PluginMethods.CancelDownload}/{transfer.InfoHash}",
                    "Cancel this download?",
                    $"The files are deleted and {grab?.ReleaseTitle ?? "this release"} is skipped for 14 days. "
                        + "The episode goes back on the queue and the plugin looks for a different release.")));
        }

        // A list of rows rather than a table. The design system turns a table's cells into
        // nodes of its own, so its rows do not live where the rest of a payload's children
        // do - which makes them invisible to anything walking the tree, tests included. A row
        // of text and a progress bar draws the same and stays readable.
        return rows.Count == 0
            ? Ui.EmptyState(
                "downloads-active-empty",
                "Nothing is downloading right now.",
                "Finished downloads move to the intake and leave this list.")
            : Ui.List("downloads-active", [.. rows]);
    }
}
