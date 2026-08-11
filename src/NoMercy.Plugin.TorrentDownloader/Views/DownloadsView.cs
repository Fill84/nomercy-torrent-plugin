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

    public static PluginView Build(IReadOnlyList<Transfer> allTransfers, IReadOnlyList<Grab> grabs)
    {
        Dictionary<string, Grab> byHash = grabs
            .GroupBy(grab => grab.InfoHash)
            .ToDictionary(group => group.Key, group => group.First());

        // Only what this plugin still holds a grab for. A transfer row outlives its
        // download - the store keeps the last one written for every info hash it ever saw -
        // so the page listed every torrent of the last fortnight, and every one whose grab
        // had since failed or been imported had nothing to take a name from and rendered as
        // its bare info hash. Forty lines of hexadecimal under a heading saying Downloading.
        List<Transfer> transfers = [.. allTransfers.Where(transfer => byHash.ContainsKey(transfer.InfoHash))];

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

    private static readonly PluginTableColumn[] Columns =
    [
        new() { Key = "release", Label = "Release" },
        new() { Key = "size", Label = "Size", Cell = PluginTableCellType.Bytes, Width = "7rem", Align = "right" },
        new() { Key = "progress", Label = "Progress", Cell = PluginTableCellType.Progress, Width = "9rem" },
        new() { Key = "percent", Label = "", Width = "4rem", Align = "right" },
        new() { Key = "state", Label = "Status", Cell = PluginTableCellType.Badge, Width = "8rem" },
        new() { Key = "peers", Label = "Peers", Width = "5rem", Align = "right" },
        new() { Key = "rate", Label = "Down", Cell = PluginTableCellType.Rate, Width = "7rem", Align = "right" },
        new() { Key = "left", Label = "Left", Cell = PluginTableCellType.Duration, Width = "7rem", Align = "right" },
    ];

    /// <summary>
    /// One row per download, the way a torrent client lists them.
    ///
    /// <para>
    /// This was a block each: a heading, a sentence, a full-width bar and two buttons - five
    /// rows of screen for one download. At twenty downloads the page was unreadable, and
    /// twenty is an ordinary evening for a plugin that grabs five at a time. Columns line
    /// up, so twenty of them are read down rather than one at a time.
    /// </para>
    ///
    /// <para>
    /// No buttons in the row, which is what forced the blocks in the first place: a table
    /// cell cannot hold one, and making the row itself the action would put "delete this
    /// download and blacklist the release" one stray click away. The row opens the
    /// download's own page, where those buttons live - the same list-then-detail shape the
    /// sources page already uses.
    /// </para>
    /// </summary>
    private static PluginComponent ActiveList(IReadOnlyList<Transfer> transfers, IReadOnlyDictionary<string, Grab> byHash)
    {
        if (transfers.Count == 0)
        {
            return Ui.EmptyState(
                "downloads-active-empty",
                "Nothing is downloading right now.",
                "Finished downloads move to the intake and leave this list.");
        }

        List<PluginComponent> rows = [];

        foreach (Transfer transfer in transfers.OrderByDescending(transfer => transfer.Progress))
        {
            byHash.TryGetValue(transfer.InfoHash, out Grab? grab);

            (string label, string variant) = State(transfer, grab);

            rows.Add(Ui.TableRow(
                $"downloads-row-{transfer.InfoHash}",
                new()
                {
                    // Always the release name. Every transfer reaching here has a grab, and
                    // the grab is where the name lives.
                    ["release"] = grab?.ReleaseTitle ?? transfer.InfoHash,
                    ["size"] = transfer.BytesTotal,
                    ["progress"] = transfer.Progress,
                    ["percent"] = Format.Percentage(transfer),
                    ["state"] = label,
                    ["stateVariant"] = variant,
                    ["peers"] = transfer.Peers == 0 ? "" : transfer.Peers.ToString(),
                    ["rate"] = transfer.BytesPerSecond,
                    ["left"] = transfer.Remaining is TimeSpan left ? (long)left.TotalSeconds : 0L,
                },
                Pages.Routes.GoTo(
                    Pages.Download,
                    new Dictionary<string, string> { ["infoHash"] = transfer.InfoHash })));
        }

        return Ui.Table("downloads-active", Columns, rows);
    }

    /// <summary>
    /// Where one download stands, in one word.
    ///
    /// <para>
    /// Read off the grab rather than the transfer wherever the two could disagree: the grab
    /// is what the plugin decided, and the transfer is what the engine last measured.
    /// </para>
    /// </summary>
    private static (string Label, string Variant) State(Transfer transfer, Grab? grab) => (transfer, grab) switch
    {
        (_, { State: GrabState.Failed }) => ("Failed", PluginBadgeVariant.Danger),
        (_, { State: GrabState.Imported }) => ("Imported", PluginBadgeVariant.Success),
        (_, { State: GrabState.Downloaded }) => ("Finished", PluginBadgeVariant.Success),
        ({ Paused: true }, _) => ("Paused", PluginBadgeVariant.Neutral),
        (_, { State: GrabState.Resolving }) => ("Finding peers", PluginBadgeVariant.Info),

        // Measured, not decided: a torrent with peers and no bytes for a while is the
        // difference between a download and a download that has quietly died.
        ({ BytesPerSecond: 0 }, _) => ("Stalled", PluginBadgeVariant.Warning),
        _ => ("Downloading", PluginBadgeVariant.Info),
    };

    /// <summary>
    /// One download on its own page, with the two buttons that change it.
    ///
    /// <para>
    /// Reached from the list, never from the tab bar - the same shape a source uses. The
    /// destructive one is behind a deliberate navigation rather than sitting on every row of
    /// a twenty-row table.
    /// </para>
    /// </summary>
    public static PluginView Detail(Transfer transfer, Grab? grab)
    {
        (string label, string variant) = State(transfer, grab);

        return Pages.Page(
            Pages.Download,
            grab?.ReleaseTitle ?? transfer.InfoHash,
            RefreshSeconds,
            Ui.Row(
                "download-state",
                Ui.Badge("download-state-badge", label, variant),
                Ui.Text("download-summary", Description(transfer, grab))),
            Ui.Progress($"download-progress-{transfer.InfoHash}", transfer.Progress),
            Ui.Row(
                "download-actions",
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
                        + "The episode goes back on the queue and the plugin looks for a different release.")),
            Ui.Row(
                "download-back",
                Ui.Button("download-back-button", "Back to downloads", Pages.Routes.GoTo(Pages.Downloads))));
    }

    private static string Description(Transfer transfer, Grab? grab)
    {
        List<string> parts = [];

        if (grab is not null)
            parts.Add(Format.Covers(grab));

        // No percentage and no rate: there is nothing to be a fraction of until a peer says
        // how big the torrent is. "0%" here reads as a download that has stalled, which is
        // a different thing to worry about from one that has not begun.
        if (grab?.State == GrabState.Resolving)
        {
            parts.Add("Finding peers…");

            return string.Join(" · ", parts);
        }

        parts.Add(Format.Percentage(transfer));
        parts.Add(Format.Rate(transfer));
        parts.Add(Format.Peers(transfer.Peers));

        return string.Join(" · ", parts);
    }
}
