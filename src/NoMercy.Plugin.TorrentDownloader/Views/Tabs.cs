using System.Globalization;

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>One table of a page, drawn as one of its tabs.</summary>
/// <param name="Key">What the path calls this tab: <c>waiting</c> in <c>/queue/waiting</c>.</param>
/// <param name="Name">The table's name, which is the tab's label.</param>
/// <param name="Count">How many rows the table holds, or null where a count would say nothing.</param>
/// <param name="Route">The address that draws this tab.</param>
/// <param name="Content">What is drawn while this tab is chosen: the table, and whatever belongs to it.</param>
public sealed record Tab(string Key, string Name, int? Count, string Route, IReadOnlyList<PluginComponent> Content);

/// <summary>
/// A page with two or more tables, drawn one table at a time under a row of tabs.
/// </summary>
/// <remarks>
/// <para>
/// GitHub issue #4, 13 October 2026: the Queue, the Activity page and the overview stacked their tables
/// below each other, so the second of them began wherever the first happened to end, and the anime
/// library was reached by scrolling past every show of the television library above it.
/// </para>
/// <para>
/// <strong>The chosen tab is in the path.</strong> The web app hands the plugin the path of a page and
/// nothing after a question mark, so a tab carried in a query would arrive as nothing and every reload
/// would fall back to the first tab. Each tab is an address of its own, which is what makes reload and
/// Back keep it.
/// </para>
/// <para>
/// <strong>Buttons in a row, the way the navigation is drawn.</strong> The contract has no tab
/// component, and a row of buttons is what both clients already move along: focusable on a television,
/// left and right step from one to the next. The chosen tab is marked the way the navigation marks the
/// page being read, rather than left out, so the row never shifts under the reader.
/// </para>
/// </remarks>
public static class Tabs
{
    public const string RowId = "tabs";

    /// <summary>The id of one tab's button.</summary>
    public static string ButtonId(string key) => $"tab-{key}";

    /// <param name="tabs">Every table of the page, in the order the page reads them.</param>
    /// <param name="chosen">The tab the path names, or null when it names none.</param>
    public static IEnumerable<PluginComponent> Draw(IReadOnlyList<Tab> tabs, string? chosen)
    {
        // One table is not a choice. A row holding a single tab is a button that goes where the reader
        // already is.
        if (tabs.Count < 2)
        {
            return tabs.SelectMany(tab => tab.Content);
        }

        // A path naming no tab, or one that is not there any more - the stages of a run that has ended -
        // draws the first, rather than a page with no table on it at all.
        Tab drawn = tabs.FirstOrDefault(tab => string.Equals(tab.Key, chosen, StringComparison.OrdinalIgnoreCase))
                    ?? tabs[0];

        return
        [
            Ui.Row(
                RowId,
                [
                    .. tabs.Select(tab => Ui.Button(
                        ButtonId(tab.Key),
                        Label(tab),
                        PluginActionIntent.Navigate(tab.Route),
                        variant: ReferenceEquals(tab, drawn) ? Pages.Marked : Pages.Unmarked)),
                ]),
            .. drawn.Content,
        ];
    }

    /// <summary>The table's name, and how many it holds where that is known.</summary>
    private static string Label(Tab tab)
    {
        return tab.Count is int count
            ? string.Create(CultureInfo.InvariantCulture, $"{tab.Name} ({count})")
            : tab.Name;
    }
}
