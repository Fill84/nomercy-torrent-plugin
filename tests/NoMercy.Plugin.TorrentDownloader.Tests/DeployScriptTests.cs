using System.Runtime.InteropServices;
using System.Text.Json;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// What a deploy really copies.
/// </summary>
/// <remarks>
/// <para>
/// This used to read the deploy script and check that a hand-written list of
/// filenames contained the ones it knew about. That list was itself the fault,
/// three times over, and the tests passed through every one of them: they
/// proved the list said what it said.
/// </para>
/// <para>
/// The last time cost a deploy. The plugin is a class library, and a class
/// library's build does not copy the packages it depends on into its output —
/// so the folder held three assemblies while the manifest beside it named
/// twelve. The host resolves a plugin's dependencies from beside the plugin,
/// found none of them, and <c>PluginLoader</c>'s
/// <c>ReflectionTypeLoadException</c> path reports the failure and returns
/// <em>without registering the plugin at all</em>. It was simply absent from
/// the server's list, with nothing anywhere to say why.
/// </para>
/// <para>
/// So what is checked now is the thing that has to be true: everything the
/// manifest says this plugin needs is sitting beside it when the build is
/// finished. The deploy script ships whatever the build produced and no longer
/// keeps a list to go stale.
/// </para>
/// </remarks>
public class DeployScriptTests
{
    /// <remarks>
    /// Deleting <c>EnableDynamicLoading</c> from the plugin's project file
    /// fails this, which is the only place it can fail before a server does.
    /// </remarks>
    [Fact]
    public void EveryAssemblyTheDependencyFileNamesIsBesideThePlugin()
    {
        string output = Output();

        foreach (string assembly in RuntimeAssemblies())
        {
            Assert.True(
                File.Exists(Path.Combine(output, assembly)),
                $"{assembly} is named in the plugin's .deps.json and is not beside the plugin. "
                + "The host resolves dependencies from the plugin's own folder, so this one "
                + "cannot be found and the plugin does not load at all.");
        }
    }

    /// <remarks>
    /// The store is SQLite and SQLite is native code, which arrives under
    /// <c>runtimes/</c> rather than beside the assembly. Managed assemblies
    /// present and this missing opens a database by throwing.
    /// </remarks>
    [Fact]
    public void TheNativeCodeTheStoreNeedsIsBesideThePluginToo()
    {
        string rid = RuntimeInformation.RuntimeIdentifier;
        string native = Path.Combine(Output(), "runtimes", rid, "native");

        Assert.True(
            Directory.Exists(native),
            $"No native code for {rid} under runtimes/. SQLite is native and the store cannot open without it.");

        Assert.NotEmpty(Directory.EnumerateFiles(native, "*e_sqlite3*"));
    }

    /// <remarks>
    /// <para>
    /// The catalogue is read from the assembly's own folder — that is
    /// <strong>C1</strong>. A build that leaves it behind gives a plugin
    /// reading yesterday's sources, or none at all on a fresh install, while
    /// looking perfectly healthy and asking nobody anything.
    /// </para>
    /// <para>
    /// The manifest is there for its own reason: it carries the version
    /// independently of the assembly, and one without the other leaves a server
    /// reporting a version it is not running.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("sources.json")]
    [InlineData("plugin.json")]
    public void EveryFileTheAssemblyReadsFromItsOwnFolderIsBesideIt(string file)
    {
        Assert.True(
            File.Exists(Path.Combine(Output(), file)),
            $"{file} is read from the assembly's own folder and the build did not put one there.");
    }

    /// <remarks>
    /// Every project this solution builds ends up beside the entry assembly.
    /// One added tomorrow and forgotten fails here rather than on a server.
    /// </remarks>
    [Fact]
    public void EveryAssemblyThisSolutionBuildsIsBesideThePlugin()
    {
        string output = Output();

        foreach (string project in Directory
                     .EnumerateDirectories(Path.Combine(Root(), "src"))
                     .Select(Path.GetFileName)
                     .OfType<string>())
        {
            Assert.True(
                File.Exists(Path.Combine(output, $"{project}.dll")),
                $"{project} is part of this solution and its assembly is not beside the plugin.");
        }
    }

    /// <summary>Everything the plugin's dependency file expects to load at runtime.</summary>
    private static IEnumerable<string> RuntimeAssemblies()
    {
        string deps = Path.Combine(Output(), "NoMercy.Plugin.TorrentDownloader.deps.json");

        Assert.True(File.Exists(deps), $"no dependency file at {deps}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(deps));

        foreach (JsonProperty target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
                {
                    continue;
                }

                foreach (JsonProperty assembly in runtime.EnumerateObject())
                {
                    // Written as the path inside the package - lib/net10.0/X.dll -
                    // and it lands beside the plugin under its own name.
                    yield return Path.GetFileName(assembly.Name);
                }
            }

            // One target framework, and reading the second would only repeat it.
            yield break;
        }
    }

    /// <summary>Where the plugin project's own build went.</summary>
    /// <remarks>
    /// The plugin's output, never this test's. A test project is an executable
    /// and copies every package it references into its own folder whatever the
    /// plugin project does, so asking here would pass happily with the one
    /// setting missing that makes the plugin loadable.
    /// </remarks>
    private static string Output()
    {
        DirectoryInfo self = new(AppContext.BaseDirectory);
        string framework = self.Name;
        string configuration = self.Parent?.Name
            ?? throw new InvalidOperationException("cannot tell Debug from Release above the test assembly");

        return Path.Combine(
            Root(),
            "src",
            "NoMercy.Plugin.TorrentDownloader",
            "bin",
            configuration,
            framework);
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
