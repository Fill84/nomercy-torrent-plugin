using System.Xml.Linq;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

public class ProjectIsolationTests
{
    /// <summary>
    /// The protocol is written here, against sockets and nothing else. No
    /// third-party torrent library, and nothing from the media server either:
    /// its tests run against captured wire bytes, which only stays possible
    /// while the project has no idea a host exists.
    /// </summary>
    /// <remarks>
    /// The project file, not the compiled assembly — see the same test in
    /// Core.Tests for why.
    /// </remarks>
    [Fact]
    public void BittorrentReferencesNothingAtAll()
    {
        string project = Path.Combine(
            RepositoryRoot(),
            "src",
            "NoMercy.Plugin.TorrentDownloader.Bittorrent",
            "NoMercy.Plugin.TorrentDownloader.Bittorrent.csproj");

        string[] references = XDocument.Load(project)
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference")
            .Select(element => element.Attribute("Include")?.Value ?? element.Name.LocalName)
            .ToArray();

        Assert.Empty(references);
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
