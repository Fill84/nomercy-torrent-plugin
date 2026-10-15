namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>One row the database answered, read by column name.</summary>
/// <remarks>
/// The host answers a row as column name to value, in SQLite's own types: an
/// integer is a <see cref="long"/>, text a <see cref="string"/>, nothing at all
/// <c>null</c>. A column a query did not select is a fault in that query, so it
/// throws rather than reading as nothing.
/// </remarks>
public readonly record struct StoredRow(IReadOnlyDictionary<string, object?> Columns)
{
    public string Text(string column)
    {
        return Value(column) as string
               ?? throw new InvalidOperationException($"The column '{column}' holds no text.");
    }

    public string? TextOrNull(string column)
    {
        return Value(column) as string;
    }

    public long Long(string column)
    {
        return Value(column) is long value
            ? value
            : throw new InvalidOperationException($"The column '{column}' holds no integer.");
    }

    public int Int(string column)
    {
        return checked((int)Long(column));
    }

    public int? IntOrNull(string column)
    {
        return Value(column) is long value ? checked((int)value) : null;
    }

    /// <summary>A yes or no, kept as SQLite keeps one: nought for no, anything else for yes.</summary>
    public bool Flag(string column)
    {
        return Long(column) != 0;
    }

    public bool? FlagOrNull(string column)
    {
        return Value(column) is long value ? value != 0 : null;
    }

    private object? Value(string column)
    {
        return Columns.TryGetValue(column, out object? value)
            ? value
            : throw new InvalidOperationException($"The query selected no column '{column}'.");
    }
}
