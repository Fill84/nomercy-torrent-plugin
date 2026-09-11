using NoMercy.Plugin.TorrentDownloader.Solver;
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
/// <strong>It does not outlive the evening.</strong> Kept for the life of the
/// server, it is ten Chrome processes and two hundred megabytes held by a
/// machine that will not search again until morning — which the owner saw on
/// 31 August 2026 and asked about. So there is now one caller that stops it,
/// and it is behind a quarter of an hour of having nothing open. A tab closing
/// still stops nothing, which is the rule this class is named for.
/// </para>
/// <para>
/// Read from the source rather than exercised, because the seam is
/// PuppeteerSharp's own browser and a test that stood one up would need Chrome
/// and a hidden desktop to say something this plain. What the browser is closed
/// <em>on</em> is decided by <c>IdleBrowser</c>, which is exercised for real.
/// </para>
/// </remarks>
public class TheBrowserOutlivesItsTabsTests
{
    /// <remarks>
    /// The owner's decision of 11 September 2026, after watching ten Chrome
    /// processes and two hundred megabytes sit on their server with nothing
    /// running: the browser goes with the last page that was read.
    /// </remarks>
    [Fact]
    public void TheBrowserIsStoppedByItsLastTab()
    {
        string tabs = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "NoMercy.Plugin.TorrentDownloader",
            "Solver",
            "PuppeteerTabs.cs"));

        int from = tabs.IndexOf("private void Closed()", StringComparison.Ordinal);

        Assert.True(from > 0, "the tab no longer says when it closed");

        string closing = tabs[from..tabs.IndexOf("private void CloseIfIdle", from, StringComparison.Ordinal)];

        // The last tab out asks for the close. Not the tab before it: a browser
        // taken down while another tab is reading a page is a solve that fails
        // on a disposed handle.
        Assert.Contains("if (open == 0)", closing, StringComparison.Ordinal);
        Assert.Contains("CloseIfIdle", closing, StringComparison.Ordinal);

        // Two places, and only two: the last tab out, and the backstop for a run
        // that ended with something still open. A third would be somebody
        // stopping it for a reason nobody wrote down.
        Assert.Equal(2, tabs.Split("_browser.Stop()").Length - 1);
    }

    /// <remarks>
    /// Nothing is kept waiting. It was a quarter of an hour, to keep a
    /// clearance warm for the next gated source — and that turned out to be the
    /// wrong cure for the wrong disease: the gated sources were failing because
    /// <c>CloudflareChallenge</c> read its own detections script as a
    /// challenge, not because the browser had gone.
    /// </remarks>
    [Fact]
    public void NothingIsKeptWaitingOnceTheLastTabHasGone()
    {
        Assert.Equal(TimeSpan.Zero, IdleBrowser.After);
        Assert.True(IdleBrowser.Due(0, Nine, Nine, IdleBrowser.After));
        Assert.False(IdleBrowser.Due(1, Nine, Nine, IdleBrowser.After));
    }

    private static readonly DateTimeOffset Nine = new(2026, 8, 31, 21, 0, 0, TimeSpan.Zero);

    /// <remarks>
    /// And it is stopped in two places in the whole plugin: the browser itself,
    /// and the idle check in the tabs that hand it out. A third would be
    /// somebody stopping it for a reason nobody wrote down, which is how the
    /// clearance was lost the first time.
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

                // The close the last tab asks for, and the backstop timer
                // beside it. Its own test above says so.
                .Where(file => Path.GetFileName(file) != "PuppeteerTabs.cs")
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
