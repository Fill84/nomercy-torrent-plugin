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
        IReadOnlyList<string> ungrantedHosts,
        int shows)
    {
        Dictionary<string, Grab> byHash = grabs
            .GroupBy(grab => grab.InfoHash)
            .ToDictionary(group => group.Key, group => group.First());

        List<PluginComponent> children =
        [
            // The first line, and not a caption: it is the answer to the question somebody
            // opened the page with, and the faintest text on the page is the wrong place for
            // it. Everything below is the same answer in more detail.
            Ui.Text("overview-summary", Summary(transfers, wanted, history, shows)),
        ];

        List<PluginComponent> attention = [.. Attention(wanted, ungrantedHosts)];

        if (attention.Count > 0)
        {
            // No explanatory line under the heading. Each item below already says what it is
            // and what it is stopping, and a sentence repeating that is one more thing
            // between the reader and the thing they came for.
            children.Add(Ui.Section(
                "overview-attention",
                "Needs you",
                null,
                Ui.List("overview-attention-list", attention)));
        }

        // Only when there is something. "0 downloading" is already in the line at the top,
        // and an empty state is half a screen of icon and heading saying it a second time -
        // which on the page you open to check whether anything is wrong is exactly the space
        // the things that are wrong should be using.
        if (transfers.Count > 0)
        {
            children.Add(Ui.Section(
                "overview-now",
                Format.Count("Downloading", transfers.Count(transfer => !transfer.Paused)),
                Format.TotalRate(transfers),
                Now(transfers, byHash)));
        }

        // There used to be a "Not started" section here, counting the library rows with no
        // episode on the server so an owner would know the plugin was passing over them.
        // It is gone with the list behind it: those shows are not the plugin's, so counting
        // them was reporting on somebody else's business on the page that answers "what is
        // this plugin doing". Following one by name is on the Shows page.

        // The one case an empty state belongs in: the whole page has nothing, rather than
        // one section of it.
        if (children.Count == 1)
        {
            children.Add(Ui.EmptyState(
                "overview-idle",
                "Nothing needs you",
                "Every show it follows is up to date."));
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
            // In a row: a button loose in a column is stretched to the page's full width.
            children.Add(Ui.Row(
                "overview-now-more-actions",
                Ui.Button(
                    "overview-now-more",
                    $"All {transfers.Count} downloads",
                    Pages.Routes.GoTo(Pages.Downloads))));
        }

        return Ui.Container("overview-now-body", children);
    }

    /// <summary>
    /// The shows the plugin is leaving alone, each with the one button that changes that.
    /// <summary>
    /// The whole plugin in one sentence: what is moving, what is waiting, what has landed.
    ///
    /// <para>
    /// The wanted count says what it is counting and how many shows it is spread across,
    /// because the two numbers this plugin puts in front of somebody are episodes here and
    /// shows on the next tab. Read one after the other - "42" and then a list of 25 - they
    /// look like a contradiction, and the reader is left doing the arithmetic to find out
    /// it is not one. That is work the page should have done.
    /// </para>
    /// </summary>
    private static string Summary(
        IReadOnlyList<Transfer> transfers,
        IReadOnlyList<WantedEpisode> wanted,
        IReadOnlyList<HistoryEntry> history,
        int shows)
    {
        int active = transfers.Count(transfer => !transfer.Paused);
        int paused = transfers.Count(transfer => transfer.Paused);

        List<string> parts =
        [
            active == 1 ? "1 downloading" : $"{active} downloading",
            Wanted(wanted.Count, shows),
        ];

        if (paused > 0)
            parts.Insert(1, paused == 1 ? "1 paused" : $"{paused} paused");

        int imported = history.Count(entry => entry.Event == HistoryEvent.Imported);

        if (imported > 0)
            parts.Add(imported == 1 ? "1 imported recently" : $"{imported} imported recently");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// How many episodes are wanted, and across how many shows.
    ///
    /// <para>
    /// The shows are named only when there is something to want. "0 episodes wanted across
    /// 25 shows" is a sentence that makes an idle plugin sound busy, and the whole point of
    /// the line is to be read at a glance.
    /// </para>
    /// </summary>
    private static string Wanted(int episodes, int shows)
    {
        string counted = episodes == 1 ? "1 episode wanted" : $"{episodes} episodes wanted";

        if (episodes == 0 || shows == 0)
            return counted;

        return shows == 1 ? $"{counted} from 1 show" : $"{counted} across {shows} shows";
    }
}
