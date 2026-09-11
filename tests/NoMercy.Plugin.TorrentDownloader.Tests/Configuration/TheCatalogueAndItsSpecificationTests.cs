using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// The specification and the file it specifies, held against each other.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/05-sources.md</c> opened by claiming seventeen entries ship, and
/// three lines later that all fifteen were measured working. Fifteen ship. Two
/// numbers, one document, neither reconciled and one of them wrong — and the
/// owner found it, not a test.
/// </para>
/// <para>
/// <strong>It also called all fifteen "sources", and that is the half that has
/// really cost something.</strong> Five of them are name sources, which answer
/// what a release is called; ten are indexers, which answer who is serving it.
/// <c>SourceRole</c> keeps them strictly apart because they answer different
/// questions, and known failure <strong>A2</strong> is what happened when they
/// were confused: a feed went into the search set and was asked a question per
/// episode — forty identical requests a cycle, every one of them answering with
/// the same newest twenty posts.
/// </para>
/// </remarks>
public class TheCatalogueAndItsSpecificationTests
{
    /// <remarks>
    /// The prose says the totals once each, in a shape this can read. Adding a
    /// source without correcting the sentence turns this red, which is the
    /// whole point: a document nothing checks drifts, and this one did.
    /// </remarks>
    [Fact]
    public void BothDocumentsNameTheCountsTheFileReallyHolds()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();

        int names = shipped.Count(one => one.Role.HasFlag(SourceRole.Names) || one.Role.HasFlag(SourceRole.Feed));
        int indexers = shipped.Count(one => one.Role.HasFlag(SourceRole.Indexer));

        Assert.Equal(shipped.Count, names + indexers);

        Assert.Contains(
            $"{InWords(shipped.Count)} entries ship in `src/.../sources.json`: {InWords(names)} name sources "
            + $"and {InWords(indexers)} indexers.",
            Document("05-sources.md"),
            StringComparison.Ordinal);

        Assert.Contains(
            $"The {InWords(names)} public name sources and {InWords(indexers)} public indexers ship with the plugin",
            Document("00-goal.md"),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The catalogue table is the list of record, so it holds every entry the
    /// file holds and no others. A source added to one and not the other is a
    /// source the specification does not describe.
    /// </remarks>
    [Fact]
    public void TheCatalogueTableListsEveryEntryInTheFileAndNothingElse()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();

        string[] tabled =
        [
            .. Regex
                .Matches(Document("05-sources.md"), @"^\| (?<name>[^|]+?) \| `(?<kind>[^`]+)`", RegexOptions.Multiline)
                .Select(row => row.Groups["name"].Value.Trim()),
        ];

        Assert.Equal(
            shipped.Select(one => one.Name).Order(StringComparer.Ordinal),
            tabled.Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// And the entries that ship switched off say so in both places. YTS is
    /// films, which are out of scope. EZTV latest is EZTV's API, which ignores
    /// the search term and answers every question with the same hundred newest
    /// torrents — switched off on the owner's decision of 11 September 2026.
    /// Their addresses are recorded so nobody rediscovers them, and they are
    /// never asked.
    /// </remarks>
    [Fact]
    public void TheEntriesThatShipSwitchedOffAreTheOnesTheDocumentSaysAre()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();

        string[] off = [.. shipped.Where(one => !one.Enabled).Select(one => one.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["EZTV latest", "YTS"], off);

        string document = Document("05-sources.md");

        Assert.Contains($"{InWords(shipped.Count - off.Length)} are asked.", document, StringComparison.Ordinal);
        Assert.Contains("YTS ships switched off", document, StringComparison.Ordinal);
        Assert.Contains("EZTV latest because", document, StringComparison.Ordinal);
    }

    /// <summary>The numbers as prose writes them, which is how these documents do.</summary>
    private static string InWords(int count)
    {
        return count switch
        {
            5 => "five",
            11 => "eleven",
            14 => "Fourteen",
            15 => "Fifteen",
            16 => "Sixteen",

            // Deliberately unhelpful: a catalogue that grew past what this knows
            // needs a person to write the sentence, not a number spelled by a
            // switch nobody read.
            _ => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string Document(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "docs", name));
    }
}
