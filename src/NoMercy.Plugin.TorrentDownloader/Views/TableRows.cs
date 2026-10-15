using System.Globalization;

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>The page of a table that pages: how many rows it holds by itself, and the page the path names.</summary>
public sealed record TablePaging(int PageSize, int Page);

/// <summary>A table's rows as the client asked for them.</summary>
/// <param name="Rows">What the table draws.</param>
/// <param name="Matching">How many rows match the filter, on every page together.</param>
/// <param name="Page">The page drawn, counted from one.</param>
/// <param name="Pages">How many pages the matching rows make; one for a table that does not page.</param>
/// <param name="EmptyMessage">What the table says when it draws no row.</param>
/// <param name="Lines">
/// The whole text of every long cell drawn short, a line each, in the order of the rows drawn: what stands
/// under the table.
/// </param>
public sealed record ShownRows(
    IReadOnlyList<PluginComponent> Rows,
    int Matching,
    int Page,
    int Pages,
    string EmptyMessage,
    IReadOnlyList<PluginComponent> Lines);

/// <summary>
/// Filtering, sorting and paging a table's rows, the same way for every table.
/// </summary>
/// <remarks>
/// <para>
/// GitHub issue #5: every table answers <c>sort</c>, <c>order</c>, <c>filter</c>, <c>page</c> and
/// <c>pageSize</c>. One rule for all of them, here, so that no two tables can disagree on what a filter
/// keeps or where an unknown value sorts.
/// </para>
/// <para>
/// <strong>Filtered, then sorted, then paged.</strong> Paging last is what makes page 2 carry on where
/// page 1 stopped; a sort applied to one page at a time would sort each page on its own and the order
/// would restart on every one. The sort is stable, so rows that sort the same stay in the table's own
/// order, and that order does not change from one request to the next.
/// </para>
/// <para>
/// <strong>A long cell is drawn short last of all</strong>, after the page is cut, and its whole text
/// stands under the table (<see cref="LongCells"/>). The filter and the sort read the whole text, so a row
/// is found by a word that is only in the part under the table.
/// </para>
/// </remarks>
public static class TableRows
{
    /// <summary>Words by their letters whatever their case; anything else by its own order.</summary>
    private static readonly Comparer<IComparable?> Comparer = Comparer<IComparable?>.Create((one, other) =>
        (one, other) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (string left, string right) => StringComparer.OrdinalIgnoreCase.Compare(left, right),
            (IComparable left, IComparable right) => left.CompareTo(right),
        });

    /// <summary>
    /// A table that holds all its rows, sorted and filtered as the client asked, and under it the whole text
    /// of every long cell it drew short.
    /// </summary>
    /// <remarks>
    /// It ignores <c>page</c> and <c>pageSize</c>: it has no paging of its own, and a page of it would
    /// hide the other rows with nothing on the page to reach them by. <c>longColumns</c> names the columns
    /// whose text can be longer than the table has room for, as <see cref="Answer"/> takes them.
    /// </remarks>
    public static IReadOnlyList<PluginComponent> Table<T>(
        string id,
        IReadOnlyList<PluginTableColumn> columns,
        IEnumerable<T> items,
        Func<T, PluginComponent> draw,
        string emptyMessage,
        TableQuery? query,
        IReadOnlyDictionary<string, Func<T, IComparable?>>? values = null,
        IReadOnlyList<LongColumn>? longColumns = null)
    {
        ShownRows shown = Answer(
            items,
            draw,
            columns,
            values ?? new Dictionary<string, Func<T, IComparable?>>(),
            query ?? TableQuery.None,
            emptyMessage,
            longColumns: longColumns);

        return [Ui.Table(id, columns, shown.Rows, shown.EmptyMessage), .. shown.Lines];
    }

    /// <param name="items">What the table is drawn from, in the table's own order.</param>
    /// <param name="draw">One row of the table.</param>
    /// <param name="columns">The table's columns: a sort key is one of their keys.</param>
    /// <param name="values">
    /// The columns that sort by a value rather than by the words in the cell, keyed by column. A size
    /// sorts by its bytes and a time by the time; drawn as text, "10% of 1 GB" sorts before "9% of 1 GB"
    /// and "1.5 s" before "900 ms". A null value is one the row does not know.
    /// </param>
    /// <param name="query">What the client asked.</param>
    /// <param name="emptyMessage">What the table says when it holds no row at all.</param>
    /// <param name="paging">The table's own paging, or null for a table that holds all its rows.</param>
    /// <param name="longColumns">
    /// The columns whose text can be longer than the table has room for, and how each is drawn short
    /// (<see cref="LongCells"/>).
    /// </param>
    public static ShownRows Answer<T>(
        IEnumerable<T> items,
        Func<T, PluginComponent> draw,
        IReadOnlyList<PluginTableColumn> columns,
        IReadOnlyDictionary<string, Func<T, IComparable?>> values,
        TableQuery query,
        string emptyMessage,
        TablePaging? paging = null,
        IReadOnlyList<LongColumn>? longColumns = null)
    {
        (T Item, PluginComponent Row)[] drawn = [.. items.Select(item => (item, draw(item)))];

        (T Item, PluginComponent Row)[] matching = query.Filter is string filter
            ? [.. drawn.Where(one => columns.Any(column => Shown(one.Row, column.Key)?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))]
            : drawn;

        IEnumerable<(T Item, PluginComponent Row)> ordered = Sorted(matching, columns, values, query);

        int size = paging is null ? Math.Max(1, matching.Length) : Size(query, paging);
        int pages = Math.Max(1, (matching.Length + size - 1) / size);

        // A page past the end is the last page and one before the first is the first: an address
        // remembered from a longer list still draws rows rather than an empty table.
        int page = paging is null ? 1 : Math.Clamp(query.Page ?? paging.Page, 1, pages);

        // In the columns' order, so a row with two long cells puts their lines in the order it shows them.
        LongColumn[] longs =
        [
            .. columns.SelectMany(column => (longColumns ?? []).Where(longColumn => longColumn.Key == column.Key)),
        ];
        List<PluginComponent> lines = [];

        // Short only now, on the rows drawn: the filter and the sort above read the whole text.
        PluginComponent[] shownRows =
            [.. ordered.Skip((page - 1) * size).Take(size).Select(one => LongCells.Shortened(one.Row, longs, lines))];

        return new(
            shownRows,
            matching.Length,
            page,
            pages,

            // Which of the two nothings this is. "Nothing is being looked for" under a filter that kept
            // none of twelve rows says the table is empty, which is not true.
            drawn.Length > 0 && matching.Length == 0 ? $"Nothing here matches '{query.Filter}'." : emptyMessage,
            lines);
    }

    /// <summary>
    /// How many rows a page holds: what was asked, between one and the table's own size.
    /// </summary>
    /// <remarks>
    /// Never more than the table's own, as decided for S14-07: a client must not be able
    /// to have the plugin draw a library of thousands in one page. A size out of range is the table's own
    /// rather than the nearest one allowed, since a client asking for something it cannot have has not
    /// asked for the largest it can.
    /// </remarks>
    private static int Size(TableQuery query, TablePaging paging)
    {
        return query.PageSize is int asked && asked >= 1 && asked <= paging.PageSize ? asked : paging.PageSize;
    }

    private static IEnumerable<(T Item, PluginComponent Row)> Sorted<T>(
        (T Item, PluginComponent Row)[] rows,
        IReadOnlyList<PluginTableColumn> columns,
        IReadOnlyDictionary<string, Func<T, IComparable?>> values,
        TableQuery query)
    {
        // A key the table has no column for is the table's own order, never a failure: issue #5's own
        // words. The actions column has a key and nothing to sort by, so every row ties and the stable
        // sort leaves the table's own order too.
        if (query.Sort is not string sort || !columns.Any(column => column.Key == sort))
        {
            return rows;
        }

        Func<(T Item, PluginComponent Row), IComparable?> key = values.TryGetValue(sort, out Func<T, IComparable?>? value)
            ? one => value(one.Item)
            : one => Shown(one.Row, sort);

        // What the row does not know is not the smallest or the largest of anything, so it goes after
        // every row that does, whichever way the table runs: the known values are what was asked for.
        IOrderedEnumerable<(T Item, PluginComponent Row)> unknownLast = rows.OrderBy(one => key(one) is null);

        return query.Descending
            ? unknownLast.ThenByDescending(key, Comparer)
            : unknownLast.ThenBy(key, Comparer);
    }

    /// <summary>
    /// The words a cell shows, which is what a filter is matched against and what a column with no value
    /// of its own sorts by.
    /// </summary>
    /// <remarks>
    /// Every cell of this plugin's tables is text, sizes, rates and times included: each page writes its
    /// own figures, because the client's Bytes, Rate and Duration cells cannot say "not known" (an empty
    /// cell where the row's other unknowns read "—"). So the words in the cell are exactly what the reader
    /// sees, and a filter for "GB" or "never" keeps the rows that say it.
    /// </remarks>
    private static string? Shown(PluginComponent row, string key)
    {
        return row.Props.GetValueOrDefault(key) switch
        {
            string text => text,
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),

            // An actions cell: buttons, whose labels are not something the row is about. "Search now" is
            // on every row of the Queue, and a filter for "search" would keep them all.
            _ => null,
        };
    }
}
