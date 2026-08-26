using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// Closing a tab does not stop the browser.
/// </summary>
/// <remarks>
/// <para>
/// A gated source is read through a real browser because its page sits behind a
/// challenge, and solving that challenge earns a clearance that lives in the
/// browser's profile. Stop the browser and the clearance goes with it, so the
/// next host starts from cold and has to be let through all over again.
/// </para>
/// <para>
/// 0.3.11 stopped it as soon as its last tab closed, to keep a killed server
/// from leaving one behind. Asked one after another on 26 August 2026,
/// TorrentBay cleared and 1337x and EZTV did not — "this address is behind a
/// challenge and the browser could not get past it" — so all three reported no
/// rows while their pages were there for anybody holding a browser open. With
/// it left running the same three answered 2, 34 and 5 rows.
/// </para>
/// <para>
/// What keeps a killed server from leaving a browser behind is the job object
/// in <c>DiesWithTheServer</c>, which the kernel enforces however the process
/// ends. That is the mechanism, and it costs no clearance.
/// </para>
/// <para>
/// Read from the source rather than exercised, because the seam is
/// PuppeteerSharp's own browser and a test that stood one up would need Chrome
/// and a hidden desktop to say something this plain. What matters is that one
/// line does not come back.
/// </para>
/// </remarks>
public class TheBrowserOutlivesItsTabsTests
{
    [Fact]
    public void NothingStopsTheBrowserWhenATabCloses()
    {
        string tabs = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "NoMercy.Plugin.TorrentDownloader",
            "Solver",
            "PuppeteerTabs.cs"));

        Assert.DoesNotContain("browser.Stop()", tabs, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And the one place that does stop it is the one that owns it. A second
    /// caller stopping the browser is the same fault by another route.
    /// </remarks>
    [Fact]
    public void OnlyTheBrowserItselfStopsTheBrowser()
    {
        string[] elsewhere =
        [
            .. Directory
                .EnumerateFiles(
                    Path.Combine(RepositoryRoot(), "src"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => Path.GetFileName(file) != "Browser.cs")
                .Where(file => File.ReadAllText(file).Contains(".Stop();", StringComparison.Ordinal)),
        ];

        Assert.Empty(elsewhere);
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
