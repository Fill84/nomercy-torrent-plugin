using System.Globalization;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What a client asked of the table on the page: how to sort it, what to keep, and which page.
/// </summary>
/// <remarks>
/// <para>
/// GitHub issue #5, 13 October 2026, and its platform side media-server #593: the keys are #593's own
/// words, read off the query the server hands every view request (<c>PluginViewRequest.Query</c>). They
/// name no table, because a page draws one table at a time since the tabs of issue #4, so they are about
/// the table shown.
/// </para>
/// <para>
/// <strong>Nothing a client sends makes a page fail.</strong> A key that is not one of a table's columns,
/// an order that is neither <c>asc</c> nor <c>desc</c>, or a page that is not a number reads as not asked,
/// and the table is drawn as it is without them.
/// </para>
/// </remarks>
/// <param name="Sort">The key of the column to sort by, or null for the table's own order.</param>
/// <param name="Descending">Whether the sort runs from the largest down.</param>
/// <param name="Filter">The text a row's shown cells have to hold, or null to keep every row.</param>
/// <param name="Page">Which page, counted from one, or null for the one the path names.</param>
/// <param name="PageSize">How many rows a page holds, or null for the table's own.</param>
public sealed record TableQuery(string? Sort, bool Descending, string? Filter, int? Page, int? PageSize)
{
    public const string SortKey = "sort";

    public const string OrderKey = "order";

    public const string FilterKey = "filter";

    public const string PageKey = "page";

    public const string PageSizeKey = "pageSize";

    /// <summary>A request that asked nothing of the table, which draws it exactly as it always was.</summary>
    public static TableQuery None { get; } = new(null, false, null, null, null);

    /// <summary>Whether the client asked anything of the table at all.</summary>
    public bool Asked => Sort is not null || Filter is not null || Page is not null || PageSize is not null;

    /// <summary>Read off the request's query.</summary>
    public static TableQuery From(IReadOnlyDictionary<string, string>? query)
    {
        if (query is null)
        {
            return None;
        }

        string? sort = Read(query, SortKey);
        string? order = Read(query, OrderKey);
        bool descending = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);

        // An order that is neither sets the sort aside rather than guessing a direction: the issue's own
        // words, an unknown sort falls back to the default order.
        if (order is not null && !descending && !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase))
        {
            sort = null;
        }

        return new(sort, descending, Read(query, FilterKey), Number(query, PageKey), Number(query, PageSizeKey));
    }

    private static string? Read(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static int? Number(IReadOnlyDictionary<string, string> query, string key)
    {
        return int.TryParse(Read(query, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;
    }
}
