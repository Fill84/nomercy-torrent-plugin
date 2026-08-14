using System.Xml.Linq;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

public class ShellReferencesTests
{
    /// <summary>
    /// The shell is the only project allowed to know a media server exists, and
    /// it has to know: without both contract packages it cannot be loaded, and
    /// without Core and Bittorrent it has nothing to load.
    /// </summary>
    /// <remarks>
    /// Presence, not an exact set: later slices add packages here, and a test
    /// that has to be edited every time one does is a test people learn to
    /// edit without reading.
    /// </remarks>
    [Fact]
    public void TheShellReferencesCoreBittorrentAndBothContractPackages()
    {
        string project = Path.Combine(
            RepositoryRoot(),
            "src",
            "NoMercy.Plugin.TorrentDownloader",
            "NoMercy.Plugin.TorrentDownloader.csproj");

        XDocument document = XDocument.Load(project);

        string[] references = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")?.Value ?? string.Empty))
            .Concat(document
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty))
            .ToArray();

        Assert.Contains("NoMercy.Plugin.TorrentDownloader.Core", references);
        Assert.Contains("NoMercy.Plugin.TorrentDownloader.Bittorrent", references);
        Assert.Contains("NoMercy.Plugins.Abstractions", references);
        Assert.Contains("NoMercy.Plugins.Mvc", references);
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
