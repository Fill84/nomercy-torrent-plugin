using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// What a deploy really copies.
/// </summary>
/// <remarks>
/// <para>
/// The file list has gone stale once already and it cost a release: the
/// protocol assembly arrived with 0.4.0, the list did not grow with it, and the
/// deploy shipped an entry assembly referencing a dll that was not there. The
/// plugin vanished from the server's list altogether — no error, no entry,
/// nothing anywhere to say why.
/// </para>
/// <para>
/// So the list is checked against what the build really produces rather than
/// against a copy of itself. A project added tomorrow and forgotten fails here,
/// which is the only place it can fail before the owner finds out from a server
/// that has stopped listing the plugin.
/// </para>
/// </remarks>
public class DeployScriptTests
{
    [Fact]
    public void EveryAssemblyThisSolutionBuildsIsInTheDeployList()
    {
        string script = Script();

        foreach (string project in Directory
                     .EnumerateDirectories(Path.Combine(Root(), "src"))
                     .Select(Path.GetFileName)
                     .OfType<string>())
        {
            // Written as "$project.Core.dll" and the like, so the entry
            // assembly's own name is the part that varies.
            string entry = "NoMercy.Plugin.TorrentDownloader";
            string file = project == entry ? "$project.dll" : $"$project{project[entry.Length..]}.dll";

            Assert.Contains(file, script, StringComparison.Ordinal);
        }
    }

    /// <remarks>
    /// <para>
    /// The catalogue is read from the assembly's own folder — that is
    /// <strong>C1</strong>, and the whole reason it is copied beside the dll.
    /// A deploy that shipped every assembly and not the catalogue leaves the
    /// plugin reading yesterday's sources, or none at all on a fresh install,
    /// and it asks nobody anything while looking perfectly healthy.
    /// </para>
    /// <para>
    /// The manifest travels for its own reason: it carries the version
    /// independently of the assembly, and updating one without the other leaves
    /// every server reporting a version it is not running.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("sources.json")]
    [InlineData("plugin.json")]
    [InlineData("$project.deps.json")]
    public void EveryFileTheAssemblyNeedsBesideItIsInTheDeployList(string file)
    {
        Assert.Contains(file, Script(), StringComparison.Ordinal);
    }

    /// <remarks>
    /// Everything the deploy list names is something the build really produces.
    /// A file listed and never built is skipped in silence, so the list would go
    /// on claiming to ship something that has not existed for a release.
    /// </remarks>
    [Fact]
    public void TheDeployListNamesNothingTheBuildDoesNotProduce()
    {
        string plugin = Path.Combine(Root(), "src", "NoMercy.Plugin.TorrentDownloader");

        foreach (string file in (string[])["sources.json", "plugin.json"])
        {
            Assert.True(
                File.Exists(Path.Combine(plugin, file)),
                $"The deploy list ships {file} and the project does not have one.");
        }
    }

    private static string Script()
    {
        return File.ReadAllText(Path.Combine(Root(), "scripts", "deploy-to-server.ps1"));
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("no solution folder above the test assembly");
    }
}
