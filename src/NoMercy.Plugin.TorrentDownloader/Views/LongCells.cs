using System.Text.RegularExpressions;

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>How a cell longer than its column is drawn short.</summary>
public enum Cut
{
    /// <summary>Its first sentence, cut after the last whole word that fits: a refusal, a reason, a note.</summary>
    FirstSentence,

    /// <summary>
    /// Cut after the last whole word that fits, a word ending at a space, a dot, a dash or an underscore: a
    /// release name, whose dots are its spaces and whose full stops are not the end of anything ("Mr.
    /// Robot").
    /// </summary>
    AtWord,
}

/// <summary>A column whose text can be longer than the table has room for.</summary>
/// <param name="Key">The column's key.</param>
/// <param name="Characters">The most the cell shows, "…" included.</param>
/// <param name="Cut">How the cell is drawn short.</param>
public sealed record LongColumn(string Key, int Characters = LongCells.CellCharacters, Cut Cut = Cut.FirstSentence);

/// <summary>
/// Long text in a table: a short form in the cell, and the whole text under the table.
/// </summary>
/// <remarks>
/// <para>
/// The client draws every cell on one line (<c>whitespace-nowrap</c> in <c>PluginTable.vue</c>) and gives
/// each column at least the width of its longest cell (<c>minmax(max-content, …)</c>), so one long cell
/// makes the whole table wider than the page, and it scrolls sideways. That is what the owner saw on
/// 15 October 2026, with Torrentz2's refusal in the Sources table's Refusal column. A line of text
/// (<c>PluginText</c>, a paragraph) wraps, so the whole text is read there.
/// </para>
/// <para>
/// <strong>The measure every cap here is taken from.</strong> The table was about 1600 px wide in the
/// owner's screenshot. A cell is the app's body text, Inter at 16 px, which runs to about 9 px a character
/// in ordinary words, and is padded 12 px on each side, so a cell of n characters is about 9n + 24 px.
/// </para>
/// </remarks>
public static class LongCells
{
    /// <summary>The most characters a free-text cell shows, "…" included, where a table sets no other.</summary>
    /// <remarks>
    /// History is the tightest of the tables with prose in a column: When ("2026-10-15 12:34:56Z", 204 px),
    /// What ("dispatched", 114 px) and Which, a release name cut at 60 characters (564 px), leave 718 px,
    /// which is 77 characters with the cell's own padding. Sources and Activity's Notes leave more.
    /// </remarks>
    public const int CellCharacters = 77;

    /// <summary>Where an address begins, with the bracket a failure puts round it.</summary>
    private static readonly Regex Address = new(@"\(?https?://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A full stop, question or exclamation mark that ends a sentence: "torrentz2.nz" has none.</summary>
    private static readonly Regex SentenceEnd = new(@"[.!?](?=\s|$)", RegexOptions.CultureInvariant);

    /// <summary>What a cut leaves at the end that reads as nothing before the "…".</summary>
    private static readonly char[] Dangling = [' ', ',', ';', ':', '(', '·', '—', '-', '.', '_'];

    /// <summary>Where a word of a release name ends.</summary>
    private static readonly char[] NameBreaks = [' ', '.', '-', '_'];

    /// <summary>What a cell of <paramref name="column"/> shows of <paramref name="text"/>.</summary>
    /// <remarks>
    /// <para>
    /// Text that fits and holds no address is the cell as it is. Anything else is drawn short by the
    /// column's <see cref="Cut"/>, with "…" to say the rest is under the table.
    /// </para>
    /// <para>
    /// <strong>An address is never in the cell</strong>, however short the text: it is what made
    /// Torrentz2's refusal run off the page, it cannot be read at a glance, and an address with a space in
    /// its query ("?q=Dark Matter S02E08") does not even end where it looks to. So the cell stops before the
    /// first one, and the address is in the whole text under the table.
    /// </para>
    /// </remarks>
    public static string InCell(string text, LongColumn column)
    {
        Match address = Address.Match(text);

        if (text.Length <= column.Characters && !address.Success)
        {
            return text;
        }

        // One character is the "…".
        int room = column.Characters - 1;

        string said = address.Success ? text[..address.Index] : text;

        if (column.Cut == Cut.FirstSentence && SentenceEnd.Match(said) is { Success: true } end)
        {
            said = said[..end.Index];
        }

        said = said.TrimEnd(Dangling);

        if (said.Length > room)
        {
            int word = column.Cut == Cut.AtWord ? said.LastIndexOfAny(NameBreaks, room) : said.LastIndexOf(' ', room);

            // A first word longer than the cell has no word to cut at, and is cut where the cell ends.
            said = (word > 0 ? said[..word] : said[..room]).TrimEnd(Dangling);
        }

        return said + "…";
    }

    /// <summary>
    /// <paramref name="row"/> with its long cells drawn short, and the whole of each such text added to
    /// <paramref name="lines"/>.
    /// </summary>
    internal static PluginComponent Shortened(PluginComponent row, IReadOnlyList<LongColumn> columns, List<PluginComponent> lines)
    {
        Dictionary<string, object?>? cells = null;

        foreach (LongColumn column in columns)
        {
            if (row.Props.GetValueOrDefault(column.Key) is not string whole)
            {
                continue;
            }

            string cell = InCell(whole, column);

            if (cell == whole)
            {
                continue;
            }

            cells ??= new(row.Props);
            cells[column.Key] = cell;

            // Its id is the row's and the column's, which no other line shares.
            lines.Add(Ui.Text($"{row.Id}-{column.Key}", whole));
        }

        return cells is null
            ? row
            : new()
            {
                Id = row.Id,
                Component = row.Component,
                Props = cells,
                Action = row.Action,
                Items = row.Items,
            };
    }
}
