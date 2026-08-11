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
                "For an episode already on the queue. Matched by name.",
                Ui.Form(
                    "downloads-add-form",
                    "Add",
                    PluginActionIntent.CallPlugin(PluginMethods.AddTorrent),
                    new PluginFormField { Name = "source", Label = "Magnet link", Required = true })));
    }

    /// <summary>
    /// One block per download rather than one wrapping row.
    ///
    /// <para>
    /// The row was the reason this page was unreadable. A progress bar takes the full width
    /// of whatever holds it, so a row containing one re-flowed around it and every other
    /// value landed somewhere different on every row - a title here, a rate below it, two
    /// buttons on a third line, and nothing lining up with the row above. A block has a
    /// name, a line saying where it stands, the bar, and the buttons, in that order, every
    /// time.
    /// </para>
    ///
    /// <para>
    /// Not a table either, unlike the pages that only list things: the two buttons here
    /// pause and destroy, and a table cell cannot hold a button. Making the row itself the
    /// action would mean a click that deletes a download.
    /// </para>
    /// </summary>
    private static PluginComponent ActiveList(IReadOnlyList<Transfer> transfers, IReadOnlyDictionary<string, Grab> byHash)
    {
        List<PluginComponent> blocks = [];

        foreach (Transfer transfer in transfers.OrderByDescending(transfer => transfer.Progress))
        {
            byHash.TryGetValue(transfer.InfoHash, out Grab? grab);

            blocks.Add(Ui.Detail(
                $"downloads-row-{transfer.InfoHash}",
                grab?.ReleaseTitle ?? transfer.InfoHash,

                // One line, in the order a reader asks: which episode, how far, how fast,
                // and who from. Percentage as words rather than only the bar's own label,
                // because a label inside a bar is one a reader walking the page - or a
                // screen reader - may never reach. Rate and peers together, because
                // percentage alone cannot tell a download apart from a stall: a torrent at
                // 34% with four peers looks healthy right up until you notice it looked that
                // way an hour ago.
                Description(transfer, grab),

                Ui.Progress($"downloads-progress-{transfer.InfoHash}", transfer.Progress),

                Ui.Row(
                    $"downloads-actions-{transfer.InfoHash}",
                    transfer.Paused
                        ? Ui.Button(
                            $"downloads-resume-{transfer.InfoHash}",
                            "Resume",
                            PluginActionIntent.CallPlugin($"{PluginMethods.ResumeDownload}/{transfer.InfoHash}"))
                        : Ui.Button(
                            $"downloads-pause-{transfer.InfoHash}",
                            "Pause",
                            PluginActionIntent.CallPlugin($"{PluginMethods.PauseDownload}/{transfer.InfoHash}")),

                    // Confirmed, because it deletes the bytes and blacklists the release for
                    // a fortnight. That is not an undo away.
                    Ui.DestructiveButton(
                        $"downloads-cancel-{transfer.InfoHash}",
                        "Cancel",
                        $"{PluginMethods.CancelDownload}/{transfer.InfoHash}",
                        "Cancel this download?",
                        $"The files are deleted and {grab?.ReleaseTitle ?? "this release"} is skipped for 14 days. "
                            + "The episode goes back on the queue and the plugin looks for a different release."))));
        }

        return blocks.Count == 0
            ? Ui.EmptyState(
                "downloads-active-empty",
                "Nothing is downloading right now.",
                "Finished downloads move to the intake and leave this list.")
            : Ui.List("downloads-active", [.. blocks]);
    }

    private static string Description(Transfer transfer, Grab? grab)
    {
        List<string> parts = [];

        if (grab is not null)
            parts.Add(Format.Covers(grab));

        parts.Add(Format.Percentage(transfer));
        parts.Add(Format.Rate(transfer));
        parts.Add(Format.Peers(transfer.Peers));

        return string.Join(" · ", parts);
    }
}
