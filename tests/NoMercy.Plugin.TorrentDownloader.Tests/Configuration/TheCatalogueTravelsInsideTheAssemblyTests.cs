using System.Reflection;
using System.Runtime.Loader;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// The shipped sources are inside the assembly, so a copy of it alone, in a
/// folder holding nothing else, still knows every one of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>C1</strong> was the catalogue looked for in the wrong folder and
/// never found: the plugin ran for a day on three sources while looking
/// healthy. The answer used to be reading <c>sources.json</c> from the folder
/// the assembly said it was in, which needed <c>Assembly.Location</c> — and the
/// marketplace scan refuses <c>System.Reflection.Assembly</c> (media-server
/// #476). Now the build writes the catalogue into the assembly, so there is no
/// folder left to get wrong.
/// </para>
/// <para>
/// Loaded the way the media server loads a plugin: from a path of its own, in a
/// load context of its own. Everywhere else the assembly's folder and the test
/// runner's are the same directory, which is why this test copies it away.
/// </para>
/// </remarks>
public sealed class TheCatalogueTravelsInsideTheAssemblyTests
{
    [Fact]
    public void ACopyOfTheAssemblyAloneKnowsEveryShippedSource()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(elsewhere);

        string original = Path.Combine(AppContext.BaseDirectory, "NoMercy.Plugin.TorrentDownloader.dll");
        string copy = Path.Combine(elsewhere, Path.GetFileName(original));
        File.Copy(original, copy);

        AssemblyLoadContext context = new($"elsewhere-{Guid.NewGuid():n}", isCollectible: true);

        try
        {
            // Everything it depends on comes from the default context, as it does
            // in the server; only the plugin assembly itself is the copy.
            Assembly loaded = context.LoadFromAssemblyPath(copy);
            Type loader = loaded.GetType(typeof(CatalogueLoader).FullName!)!;
            object instance = Activator.CreateInstance(loader, new CapturingLogger())!;
            // Called as the plugin calls it: with no arguments at all.
            MethodInfo load = loader.GetMethods().Single(method => method.Name == nameof(CatalogueLoader.Load));
            IReadOnlyList<SourceDefinition> sources = (IReadOnlyList<SourceDefinition>)load
                .Invoke(instance, [.. load.GetParameters().Select(_ => Type.Missing)])!;

            Assert.True(
                sources.Count >= CatalogueLoader.Fewest,
                $"A copy of the assembly alone knows {sources.Count} sources.");
        }
        finally
        {
            context.Unload();

            // Unloading is not immediate — the runtime releases the file when it
            // collects the context. The folder is in the temporary directory, so
            // one left behind is the operating system's to clear up.
            try
            {
                Directory.Delete(elsewhere, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the operating system.
            }
        }
    }
}
