using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// <c>docs/specs</c> is the plugin's requirements sheet, held to the same placement, structure and
/// wording rules as the NoMercy specs ledger.
/// </summary>
/// <remarks>
/// The ledger enforces its rules with <c>scripts/check-specs.mjs</c> before every commit. This
/// repository's gate is <c>dotnet test</c>, so the same checks live here: a rule nothing checks is a
/// rule that drifts, and the owner found drift in <c>docs/05-sources.md</c> twice before a test held it.
/// </remarks>
public class TheSpecSheetKeepsTheLedgerRulesTests
{
    /// <remarks>
    /// The ledger's list, kept word for word so a page that passes here passes there when its
    /// requirement moves across.
    /// </remarks>
    private static readonly string[] BannedPhrases =
    [
        "superseded by",
        "replaced by",
        "used to be",
        "was previously",
        "formerly",
        "older versions",
        "as of version",
        "will hold",
        "will be ",
        "coming soon",
        "to be added",
        "will contain",
        "todo:",
        "verified on real hardware",
        "confirmed on real hardware",
        "confirmed reproducible on",
        "confirmed on two real",
        "is in progress",
        "the mechanism:",
        "confirmed by name in",
    ];

    [Fact]
    public void TheSheetHasAnIndexAndAtLeastOneFeaturePage()
    {
        string folder = SpecsFolder();

        Assert.True(File.Exists(Path.Combine(folder, "README.md")), "docs/specs/README.md is missing");
        Assert.NotEmpty(FeaturePages(folder));
    }

    [Fact]
    public void EveryPageLivesOneLevelDeepAndIsMarkdown()
    {
        string folder = SpecsFolder();

        Assert.Empty(Directory.GetDirectories(folder));
        Assert.All(Directory.GetFiles(folder), file => Assert.EndsWith(".md", file, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFeaturePageOpensWithItsTitleAndGoesStraightIntoASection()
    {
        foreach (string page in FeaturePages(SpecsFolder()))
        {
            string[] lines = [.. File.ReadAllLines(page).Select(line => line.Trim()).Where(line => line.Length > 0)];

            Assert.True(lines.Length > 1, $"{Path.GetFileName(page)} is empty");
            Assert.StartsWith("# ", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("## ", lines[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoFeaturePageCarriesAProcessSection()
    {
        foreach (string page in FeaturePages(SpecsFolder()))
        {
            string[] headings = [.. File.ReadAllLines(page).Where(line => line.StartsWith("## ", StringComparison.Ordinal))];

            Assert.DoesNotContain(headings, heading => heading.StartsWith("## How to re-verify", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(headings, heading => heading.StartsWith("## Not yet covered", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NoPageSaysWhatWasOrWhatWillBe()
    {
        foreach (string page in Directory.GetFiles(SpecsFolder(), "*.md"))
        {
            string text = File.ReadAllText(page).ToLowerInvariant();

            foreach (string phrase in BannedPhrases)
            {
                Assert.False(text.Contains(phrase, StringComparison.Ordinal), $"{Path.GetFileName(page)} says \"{phrase}\"");
            }
        }
    }

    private static IEnumerable<string> FeaturePages(string folder)
    {
        return Directory.GetFiles(folder, "*.md").Where(file => Path.GetFileName(file) != "README.md");
    }

    private static string SpecsFolder()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        string folder = Path.Combine(directory!.FullName, "docs", "specs");
        Assert.True(Directory.Exists(folder), "docs/specs does not exist");
        return folder;
    }
}
