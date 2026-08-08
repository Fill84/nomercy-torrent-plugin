// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What the plugin is doing right now: what is downloading, and what it still wants.
///
/// <para>
/// Pure, like <see cref="SettingsView"/> - rows in, a view out, no I/O. Everything the
/// page shows is read from the store before this is called, which is what lets the
/// whole page be asserted in a test without a server, a library or a swarm.
/// </para>
/// </summary>
public static class DownloadsView
{
    /// <summary>Long enough to be useful, short enough that a first run does not render a thousand rows.</summary>
    public const int QueuePreviewLength = 25;

    /// <summary>
    /// How often the client should come back, in seconds.
    ///
    /// <para>
    /// The transfers cadence is what rewrites these numbers, and it runs once a minute by
    /// default, so half of that sees every change soon after it lands without asking for a
    /// render nothing has moved since. A settings page declares zero; this one cannot -
    /// a progress bar that only advances when the user reloads is a screenshot.
    /// </para>
    /// </summary>
    public const int RefreshSeconds = 30;

    public static PluginView Build(
        IReadOnlyList<Transfer> transfers,
        IReadOnlyList<Grab> grabs,
        IReadOnlyList<WantedEpisode> wanted)
    {
        Dictionary<string, Grab> byHash = grabs
            .GroupBy(grab => grab.InfoHash)
            .ToDictionary(group => group.Key, group => group.First());

        List<PluginComponent> children =
        [
            PluginViews.Text("downloads-heading", "Downloads", "heading"),
            PluginViews.Text("downloads-active-heading", "Active", "subheading"),
            ActiveTable(transfers, byHash),
            PluginViews.Text("downloads-queue-heading", QueueHeading(wanted), "subheading"),
            Queue(wanted),
        ];

        return PluginViews.Declarative(RefreshSeconds, PluginViews.Container("downloads-root", [.. children]));
    }

    private static PluginComponent ActiveTable(IReadOnlyList<Transfer> transfers, IReadOnlyDictionary<string, Grab> byHash)
    {
        List<PluginComponent> rows = [];

        foreach (Transfer transfer in transfers.OrderByDescending(transfer => transfer.Progress))
        {
            byHash.TryGetValue(transfer.InfoHash, out Grab? grab);

            rows.Add(PluginViews.Row(
                $"downloads-row-{transfer.InfoHash}",
                PluginViews.Text($"downloads-title-{transfer.InfoHash}", grab?.ReleaseTitle ?? transfer.InfoHash),
                PluginViews.Text($"downloads-episode-{transfer.InfoHash}", grab is null ? "" : grab.Key.ToString(), "caption"),
                PluginViews.Progress($"downloads-progress-{transfer.InfoHash}", transfer.Progress),

                // The percentage as its own text rather than only a label on the bar: a
                // label that lives inside the bar is one a reader walking the page - or a
                // screen reader - may never reach.
                PluginViews.Text($"downloads-percent-{transfer.InfoHash}", Percentage(transfer), "caption"),
                PluginViews.Text($"downloads-peers-{transfer.InfoHash}", Peers(transfer.Peers), "caption")));
        }

        // A list of rows rather than a table. The design system turns a table's cells
        // into nodes of its own, so its rows do not live where the rest of a payload's
        // children do - which makes them invisible to anything walking the tree, tests
        // included. A row of text and a progress bar draws the same and stays readable.
        return rows.Count == 0
            ? PluginViews.EmptyState(
                "downloads-active-empty",
                "Nothing is downloading right now.",
                "Finished downloads move to the intake and leave this list.")
            : PluginViews.List("downloads-active", [.. rows]);
    }

    private static PluginComponent Queue(IReadOnlyList<WantedEpisode> wanted)
    {
        if (wanted.Count == 0)
        {
            return PluginViews.EmptyState(
                "downloads-queue-empty",
                "Nothing is missing",
                "Every episode the library knows about has a file.");
        }

        List<PluginComponent> rows = [];

        foreach (WantedEpisode episode in wanted.Take(QueuePreviewLength))
        {
            rows.Add(PluginViews.Row(
                $"downloads-wanted-{episode.Key}",
                PluginViews.Text($"downloads-wanted-show-{episode.Key}", episode.ShowTitle),
                PluginViews.Text($"downloads-wanted-slot-{episode.Key}", episode.Key.ToString(), "caption"),
                StateBadge(episode)));
        }

        return PluginViews.List("downloads-queue", [.. rows]);
    }

    private static PluginComponent StateBadge(WantedEpisode episode) =>
        PluginViews.Badge(
            $"downloads-wanted-state-{episode.Key}",
            episode.State switch
            {
                WantedState.Searching => "Searching",
                WantedState.Grabbed => "Downloading",
                WantedState.Unavailable => "Not found",
                _ => "Wanted",
            },

            // Semantic, never a colour. What "not found" should look like is the theme's
            // business and changes between light, dark and a television.
            episode.State switch
            {
                WantedState.Unavailable => PluginBadgeVariant.Warning,
                WantedState.Grabbed => PluginBadgeVariant.Success,
                _ => PluginBadgeVariant.Neutral,
            });

    private static string QueueHeading(IReadOnlyList<WantedEpisode> wanted) =>
        wanted.Count > QueuePreviewLength
            ? $"Wanted ({QueuePreviewLength} of {wanted.Count})"
            : $"Wanted ({wanted.Count})";

    private static string Percentage(Transfer transfer) =>
        transfer.BytesTotal > 0 ? $"{transfer.Progress * 100:0}%" : "starting";

    private static string Peers(int peers) => peers == 1 ? "1 peer" : $"{peers} peers";
}
