using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The pages the sources really sent, and the rows the readers really read off
/// them.
/// </summary>
/// <remarks>
/// Every test that needs a page uses one of these rather than markup written
/// for the occasion. A sample written to suit a test agrees with the test and
/// with nothing else, which is the whole of H3.
/// </remarks>
public static class Capture
{
    /// <summary>A captured page, exactly as it was saved.</summary>
    public static string Fixture(string name)
    {
        return File.ReadAllText(Path.Combine(Folder, name));
    }

    /// <summary>The titles a reader reads off a captured page.</summary>
    public static IReadOnlyList<string> Rows(string fixture, string reader)
    {
        return
        [
            .. Readers.Shipped().Named(reader)!
                .Read(Fixture(fixture), new("https://capture.invalid/"))
                .Select(row => row.Title),
        ];
    }

    private static string Folder
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "tests", "fixtures");
        }
    }
}
