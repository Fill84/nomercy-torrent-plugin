using System.Reflection;
using System.Runtime.Loader;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// The one test that can tell C1's fix from C1 itself.
/// </summary>
/// <remarks>
/// Everywhere else, the assembly's folder and <c>AppContext.BaseDirectory</c>
/// are the same directory, so every other test passes either way. They differ
/// exactly where it matters: when the assembly is loaded from somewhere other
/// than the process that loaded it — which is what a plugin is. So this copies
/// the assembly somewhere else and loads it there, the way the media server
/// does.
/// </remarks>
public class AssemblyFolderTests
{
    [Fact]
    public void TheCatalogueFolderFollowsTheAssemblyNotTheProcess()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(elsewhere);

        try
        {
            string original = typeof(CatalogueLoader).Assembly.Location;
            string copy = Path.Combine(elsewhere, Path.GetFileName(original));
            File.Copy(original, copy);

            AssemblyLoadContext context = new($"elsewhere-{Guid.NewGuid():n}", isCollectible: true);

            try
            {
                Assembly loaded = context.LoadFromAssemblyPath(copy);
                Type loader = loaded.GetType(typeof(CatalogueLoader).FullName!)!;
                string folder = (string)loader
                    .GetProperty(nameof(CatalogueLoader.AssemblyFolder), BindingFlags.Public | BindingFlags.Static)!
                    .GetValue(null)!;

                Assert.Equal(elsewhere, folder.TrimEnd(Path.DirectorySeparatorChar));

                // And that is a different folder from this process's, which is
                // the whole point: reading AppContext.BaseDirectory here would
                // have answered the test runner's folder — for the plugin, the
                // media server's.
                Assert.NotEqual(
                    AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    folder.TrimEnd(Path.DirectorySeparatorChar));
            }
            finally
            {
                context.Unload();
            }
        }
        finally
        {
            // Unloading is not immediate — the runtime releases the file when
            // it collects the context, which is its business and not this
            // test's. The folder is in the temporary directory either way.
            try
            {
                Directory.Delete(elsewhere, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the operating system to clear up.
            }
        }
    }
}
