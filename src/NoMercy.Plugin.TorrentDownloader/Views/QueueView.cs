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
                wanted.Count > PreviewLength
                    ? "The next few; the rest follow as these finish."
                    : null,
                Rows(wanted)));

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
            rows.Add(Ui.Row(
                $"queue-wanted-{episode.Key}",
                Ui.Text($"queue-wanted-show-{episode.Key}", episode.ShowTitle),
                Ui.Text($"queue-wanted-slot-{episode.Key}", Format.Slot(episode.Key), "caption"),
                StateBadge(episode),

                // The cadence works least-recently-searched first, ten at a time, which is
                // the right order for a machine and the wrong one for somebody who wants
                // tonight's episode.
                Ui.Button(
                    $"queue-search-now-{episode.Key}",
                    "Search now",
                    PluginActionIntent.CallPlugin(
                        $"{PluginMethods.SearchNow}/{episode.Key.ShowId}/{episode.Key.Season}/{episode.Key.Episode}"))));
        }

        return Ui.List("queue-list", [.. rows]);
    }

    private static PluginComponent StateBadge(WantedEpisode episode) =>
        Ui.Badge(
            $"queue-wanted-state-{episode.Key}",
            episode.State switch
            {
                _ when Format.NotOutYet(episode) => $"Airs {episode.AirDate:d MMM}",
                WantedState.Searching => "Searching",
                WantedState.Grabbed => "Downloading",
                WantedState.Unavailable => "Not found",
                _ => "Wanted",
            },

            // Semantic, never a colour. What "not found" should look like is the theme's
            // business and changes between light, dark and a television.
            episode.State switch
            {
                _ when Format.NotOutYet(episode) => PluginBadgeVariant.Neutral,
                WantedState.Unavailable => PluginBadgeVariant.Warning,
                WantedState.Grabbed => PluginBadgeVariant.Success,
                _ => PluginBadgeVariant.Neutral,
            });

    private static string Heading(IReadOnlyList<WantedEpisode> wanted) =>
        wanted.Count > PreviewLength
            ? $"Wanted ({PreviewLength} of {wanted.Count})"
            : $"Wanted ({wanted.Count})";
}
