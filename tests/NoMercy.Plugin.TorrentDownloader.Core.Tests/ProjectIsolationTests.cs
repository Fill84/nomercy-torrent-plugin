using System.Xml.Linq;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests;

public class ProjectIsolationTests
{
    /// <summary>
    /// Core is the whole pipeline, and it has to be judgeable without a media
    /// server and without a swarm. One reference is all it takes for its tests
    /// to start needing a host, and by then the damage is spread over a sprint.
    /// </summary>
    /// <remarks>
    /// The project file, not the compiled assembly: the compiler leaves out a
    /// reference nothing uses yet, so a build would still look clean on the day
    /// somebody adds one — and go quietly wrong on the day they first use it.
    /// </remarks>
    [Fact]
    public void CoreReferencesNothingAtAll()
    {
        string project = Path.Combine(
            RepositoryRoot(),
            "src",
            "NoMercy.Plugin.TorrentDownloader.Core",
            "NoMercy.Plugin.TorrentDownloader.Core.csproj");

        string[] references = XDocument.Load(project)
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference")
            .Select(element => element.Attribute("Include")?.Value ?? element.Name.LocalName)
            .ToArray();

        Assert.Empty(references);
    }

    private static string RepositoryRoot()
    {
        // Walk up from the test binary rather than guess how deep bin/ is, so
        // this keeps working whatever configuration or framework folder it ran
        // out of.
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
