using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Scan;

/// <summary>
/// What the marketplace scan refuses in the plugin's own build output — the
/// folder the release packs — is exactly what <c>expected-findings.txt</c> says.
/// </summary>
/// <remarks>
/// Media-server #476: the marketplace refuses this plugin. The list is what is
/// still to be taken out, and it may only change by being edited: a new finding
/// fails this, so nothing new that leaves the SDK slips in, and a finding that
/// went away fails it too, so the list never claims more than is there. Empty is
/// the plugin the marketplace accepts.
/// </remarks>
public sealed class TheMarketplaceScanTests
{
    [Fact]
    public void ThePluginFolderGivesExactlyTheFindingsWrittenDown()
    {
        string root = RepositoryRoot();
        string output = PluginOutput(root);

        IReadOnlyList<string> found = MarketplaceScan.Folder(output, "NoMercy.Plugin.TorrentDownloader.dll");
        IReadOnlyList<string> written = [.. File
            .ReadAllLines(Path.Combine(root, "tests", "NoMercy.Plugin.TorrentDownloader.Tests", "Scan", "expected-findings.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

        List<string> added = [.. found.Except(written, StringComparer.Ordinal)];
        List<string> gone = [.. written.Except(found, StringComparer.Ordinal)];

        Assert.True(
            added.Count == 0 && gone.Count == 0,
            $"Scanned {output}.\nNew, not in the list ({added.Count}):\n  {string.Join("\n  ", added)}\n"
            + $"In the list, no longer found ({gone.Count}):\n  {string.Join("\n  ", gone)}");
    }

    /// <summary>
    /// The plugin project's own output for the configuration these tests were
    /// built in: <c>dotnet test</c> builds the plugin first, through the project
    /// reference, so it is the build under test.
    /// </summary>
    private static string PluginOutput(string root)
    {
        DirectoryInfo framework = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string configuration = framework.Parent!.Name;

        return Path.Combine(root, "src", "NoMercy.Plugin.TorrentDownloader", "bin", configuration, framework.Name);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}
