// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The page the menu entry lands on: is it working, what is happening, what needs me.
///
/// <para>
/// It holds nothing of its own. Everything here is a shorter answer to a question one of the
/// other pages answers in full, which is the point - somebody opening this plugin should not
/// have to visit six tabs to find out whether anything is wrong.
/// </para>
/// </summary>
public static class OverviewView
{
    /// <summary>Enough to see what is going on, few enough that the page stays one screen.</summary>
    public const int DigestLength = 5;

    /// <summary>The same cadence as the downloads page, because it carries the same moving bars.</summary>
    public const int RefreshSeconds = 30;

    public static PluginView Build(
        IReadOnlyList<Transfer> transfers,
        IReadOnlyList<Grab> grabs,
        IReadOnlyList<WantedEpisode> wanted,
        IReadOnlyList<HistoryEntry> history,
        IReadOnlyList<FollowableShow> unstartedShows,
        IReadOnlyList<string> ungrantedHosts)
    {
        Dictionary<string, Grab> byHash = grabs
            .GroupBy(grab => grab.InfoHash)
            .ToDictionary(group => group.Key, group => group.First());

        List<PluginComponent> children =
        [
            // One line before any list, because the first question is "is it doing
            // anything", and answering that by counting rows is work the page should have
            // done for the reader.
            Ui.Text("overview-summary", Summary(transfers, wanted, history), "caption"),
        ];

        List<PluginComponent> attention = [.. Attention(wanted, ungrantedHosts)];

        if (attention.Count > 0)
        {
            children.Add(Ui.Section(
                "overview-attention",
                "Needs you",
                "Nothing below will move until these are dealt with.",
                Ui.List("overview-attention-list", attention)));
        }

        children.Add(Ui.Section(
            "overview-now",
            Format.Count("Downloading", transfers.Count(transfer => !transfer.Paused)),
            Format.TotalRate(transfers),
            Now(transfers, byHash)));

        if (unstartedShows.Count > 0)
        {
            children.Add(Ui.Section(
                "overview-unstarted",
                Format.Count("Not started", unstartedShows.Count),
                "These shows have no episode on the server, so nothing is downloaded for them. Follow one and it joins the queue.",
                Unstarted(unstartedShows)));
        }

        return Pages.Page(Pages.Overview, RefreshSeconds, [.. children]);
    }

    /// <summary>
    /// The things a person has to act on, as opposed to the things the plugin is getting on
    /// with. A host waiting on a grant is the difference between a plugin that is searching
    /// and one that only looks like it is.
    /// </summary>
    private static IEnumerable<PluginComponent> Attention(
        IReadOnlyList<WantedEpisode> wanted,
        IReadOnlyList<string> ungrantedHosts)
    {
        if (ungrantedHosts.Count > 0)
        {
            yield return Ui.Row(
                "overview-grants",
                Ui.Badge("overview-grants-badge", "Access needed", PluginBadgeVariant.Warning),
                Ui.Text(
                    "overview-grants-text",
                    $"Waiting on host access for: {string.Join(", ", ungrantedHosts)}. Until it is granted, no search reaches them."));
        }

        int unavailable = wanted.Count(episode => episode.State == WantedState.Unavailable);

        if (unavailable > 0)
        {
            yield return Ui.Row(
                "overview-unavailable",
                Ui.Badge("overview-unavailable-badge", "Not found", PluginBadgeVariant.Warning),
                Ui.Text(
                    "overview-unavailable-text",
                    unavailable == 1
                        ? "1 episode has been searched for and not found. Skipped may say why."
                        : $"{unavailable} episodes have been searched for and not found. Skipped may say why."));
        }
    }

    private static readonly PluginTableColumn[] NowColumns =
    [
        new() { Key = "release", Label = "Release" },
        new() { Key = "progress", Label = "Progress", Cell = PluginTableCellType.Progress, Width = "10rem" },
        new() { Key = "percent", Label = "", Width = "4rem", Align = "right" },
        new() { Key = "rate", Label = "Rate", Width = "12rem" },
    ];

    /// <summary>
    /// What is moving, without the buttons. Pausing and cancelling live on the downloads
    /// page; a glance page that can also destroy things is one people stop glancing at.
    ///
    /// <para>
    /// A table precisely because there is nothing to press: columns line up, so five
    /// downloads can be read down rather than one at a time.
    /// </para>
    /// </summary>
    private static PluginComponent Now(IReadOnlyList<Transfer> transfers, IReadOnlyDictionary<string, Grab> byHash)
    {
        if (transfers.Count == 0)
        {
            return Ui.EmptyState(
                "overview-now-empty",
                "Nothing is downloading right now.",
                "Finished downloads move to the intake and leave this list.");
        }

        List<PluginComponent> rows = [];

        foreach (Transfer transfer in transfers.OrderByDescending(transfer => transfer.Progress).Take(DigestLength))
        {
            byHash.TryGetValue(transfer.InfoHash, out Grab? grab);

            rows.Add(Ui.TableRow(
                $"overview-now-{transfer.InfoHash}",
                new()
                {
                    ["release"] = grab?.ReleaseTitle ?? transfer.InfoHash,
                    ["progress"] = transfer.Progress,
                    ["percent"] = Format.Percentage(transfer),
                    ["rate"] = Format.Rate(transfer),
                },
                Pages.Routes.GoTo(Pages.Downloads)));
        }

        List<PluginComponent> children = [Ui.Table("overview-now-list", NowColumns, rows)];

        if (transfers.Count > DigestLength)
        {
            children.Add(Ui.Button(
                "overview-now-more",
                $"All {transfers.Count} downloads",
                Pages.Routes.GoTo(Pages.Downloads)));
        }

        return Ui.Container("overview-now-body", children);
    }

    /// <summary>
    /// The shows the plugin is leaving alone, each with the one button that changes that.
    ///
    /// <para>
    /// The counterpart of the rule that keeps a first run from being a thousand downloads:
    /// the rule is right, and without somewhere to say "except this one" it means the plugin
    /// can never start a show at all. This is that somewhere until the Shows page exists.
    /// </para>
    /// </summary>
    private static PluginComponent Unstarted(IReadOnlyList<FollowableShow> shows)
    {
        List<PluginComponent> tiles = [];

        foreach (FollowableShow show in shows.OrderBy(show => show.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            // Tiles rather than a list of buttons, because a card is the one component with
            // a surface of its own - twenty shows read as twenty things instead of forty
            // words in a column. The whole tile is the button, so its subtitle has to say
            // what pressing it does; a card that only named the show would be a click into
            // the dark.
            tiles.Add(show.Followed
                ? Ui.Card(
                    $"overview-unfollow-{show.ShowId}",
                    show.Title,
                    "Following - click to stop",
                    PluginActionIntent.CallPlugin($"{PluginMethods.UnfollowShow}/{show.ShowId}"))
                : Ui.Card(
                    $"overview-follow-{show.ShowId}",
                    show.Title,
                    "Click to follow",
                    PluginActionIntent.CallPlugin($"{PluginMethods.FollowShow}/{show.ShowId}")));
        }

        return Ui.Grid("overview-unstarted-list", tiles);
    }

    /// <summary>The whole plugin in one sentence: what is moving, what is waiting, what has landed.</summary>
    private static string Summary(
        IReadOnlyList<Transfer> transfers,
        IReadOnlyList<WantedEpisode> wanted,
        IReadOnlyList<HistoryEntry> history)
    {
        int active = transfers.Count(transfer => !transfer.Paused);
        int paused = transfers.Count(transfer => transfer.Paused);

        List<string> parts =
        [
            active == 1 ? "1 downloading" : $"{active} downloading",
            wanted.Count == 1 ? "1 episode wanted" : $"{wanted.Count} episodes wanted",
        ];

        if (paused > 0)
            parts.Insert(1, paused == 1 ? "1 paused" : $"{paused} paused");

        int imported = history.Count(entry => entry.Event == HistoryEvent.Imported);

        if (imported > 0)
            parts.Add(imported == 1 ? "1 imported recently" : $"{imported} imported recently");

        return string.Join(" · ", parts);
    }
}
