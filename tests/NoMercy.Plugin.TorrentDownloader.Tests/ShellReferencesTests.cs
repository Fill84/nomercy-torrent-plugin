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
            .Select(element => Path.GetFileNameWithoutExtension(Separators(element.Attribute("Include")?.Value)))
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

    /// <summary>
    /// An MSBuild path as this machine spells one.
    /// </summary>
    /// <remarks>
    /// A project file writes <c>..\Foo\Foo.csproj</c> whatever it is built on,
    /// and <c>Path.GetFileNameWithoutExtension</c> only knows the separator of
    /// the machine it runs on. On Linux it therefore saw no separator at all
    /// and answered with the whole path, so this test failed on the first CI
    /// build there had ever been while passing on every developer's machine.
    /// </remarks>
    private static string Separators(string? include)
    {
        return (include ?? string.Empty).Replace('\\', '/');
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
