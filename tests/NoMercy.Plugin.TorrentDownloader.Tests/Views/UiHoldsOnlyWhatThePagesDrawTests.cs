using System.Reflection;

using NoMercy.Plugin.TorrentDownloader.Views;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// <see cref="Ui"/> holds what the pages draw, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// It is the one place a component is built — there is not one
/// <c>new PluginComponent</c> outside it — which is what keeps the design
/// system applied in one place rather than page by page. A helper nothing
/// draws is the other half of that going wrong: it reads as a decision the
/// pages have made when no page has ever reached it, and the next person to
/// need something like it has to work out whether it was abandoned or simply
/// forgotten.
/// </para>
/// <para>
/// <c>Ui.List</c>, <c>Ui.Container</c> and <c>Ui.EmptyState</c> were all three
/// of those. Fixing the fault is deleting them, and this is what stops the next
/// one being written.
/// </para>
/// </remarks>
public class UiHoldsOnlyWhatThePagesDrawTests
{
    [Fact]
    public void EveryHelperOnUiIsDrawnByAPage()
    {
        string views = Path.Combine(
            RepositoryRoot(),
            "src",
            "NoMercy.Plugin.TorrentDownloader",
            "Views");

        // Every view but Ui itself: a helper calling another helper is not a
        // page drawing it.
        string drawn = string.Concat(
            Directory
                .EnumerateFiles(views, "*.cs")
                .Where(file => Path.GetFileName(file) != "Ui.cs")
                .Select(File.ReadAllText));

        string[] helpers =
        [
            .. typeof(Ui)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Distinct(),
        ];

        Assert.NotEmpty(helpers);

        string[] unreached =
        [
            .. helpers.Where(helper => !drawn.Contains($"Ui.{helper}(", StringComparison.Ordinal)),
        ];

        Assert.True(
            unreached.Length == 0,
            $"No page draws {string.Join(", ", unreached)}. Ui is the vocabulary the pages use, so a "
            + "helper with no page behind it is either a page that was never written or a helper "
            + "that should go.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"No NoMercy.Plugin.TorrentDownloader.sln above {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
