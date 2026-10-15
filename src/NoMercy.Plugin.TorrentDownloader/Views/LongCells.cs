using System.Text.RegularExpressions;

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>How a cell longer than its column is drawn short.</summary>
public enum Cut
{
    /// <summary>Its first sentence, cut after the last whole word that fits: a refusal, a reason, a note.</summary>
    FirstSentence,

    /// <summary>
    /// Cut after the last whole word that fits, a word ending at a space, a dash, an underscore or a dot
    /// that is not inside a number: a release name, whose dots are its spaces and whose full stops are not
    /// the end of anything ("Atmos. X265"), or a tag list.
    /// </summary>
    AtWord,

    /// <summary>
    /// Cut after the last whole part that fits, the parts joined by " · ": a list of counts, where half a
    /// part is a number without what it counts.
    /// </summary>
    AtPart,
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


    /// <summary>What a cell of <paramref name="column"/> shows of <paramref name="text"/>.</summary>
    /// <remarks>
    /// <para>
    /// Text that fits and holds no address is the cell as it is. Anything else is drawn short by the
    /// column's <see cref="Cut"/>, with "…" to say the rest is under the table.
    /// </para>
    /// <para>
    /// <strong>A web address (http or https) is never in the cell</strong>, however short the text; a
    /// host and port such as "(torrentz2.nz:443)" is not one, and stays. A web address is what made
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
            int word = column.Cut switch
            {
                Cut.AtWord => LastNameBreak(said, room),
                Cut.AtPart => said.LastIndexOf(" · ", room, StringComparison.Ordinal),
                _ => said.LastIndexOf(' ', room),
            };

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

        // Field by field, because PluginComponent is a class and has no `with`. Design is not carried: its
        // fields are merged into Props on read, so they are in the cells already, and carried as well they
        // would be merged back over the short form. A property the SDK adds later has to be added here.
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

    /// <summary>
    /// The last place at or before <paramref name="from"/> where a word of a release name ends: a space, a
    /// dash, an underscore, or a dot that is not inside a number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dot is inside a number in two shapes only: after a single letter ("H.265", "H.264"), and with a
    /// digit before it and one digit after ("DD5.1", "2.0"). Cut there, "H" or "DD5" stands on the page and
    /// reads as something else.
    /// </para>
    /// <para>
    /// Every other dot before a digit ends a word: "See.It.2160p", "McGovern.S01E01.1080p" — a digit on both
    /// sides, but four after it — and "Colbert.2024". Treating those as one word too cut "The.Big.Deal.with.
    /// Steph.McGovern.S01E01.1080p…" before its episode.
    /// </para>
    /// <para>-1 when there is no such place, and the name is cut where the cell ends.</para>
    /// </remarks>
    private static int LastNameBreak(string name, int from)
    {
        for (int at = from; at > 0; at--)
        {
            char here = name[at];

            if (here is ' ' or '-' or '_')
            {
                return at;
            }

            if (here == '.' && !InsideANumber(name, at))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Whether the dot at <paramref name="at"/> is inside "H.265" or "DD5.1".</summary>
    private static bool InsideANumber(string name, int at)
    {
        if (at + 1 >= name.Length || !char.IsDigit(name[at + 1]))
        {
            return false;
        }

        bool singleLetterBefore = char.IsLetter(name[at - 1]) && (at < 2 || !char.IsLetterOrDigit(name[at - 2]));
        bool oneDigitAfterADigit = char.IsDigit(name[at - 1]) && (at + 2 >= name.Length || !char.IsDigit(name[at + 2]));

        return singleLetterBefore || oneDigitAfterADigit;
    }
}
