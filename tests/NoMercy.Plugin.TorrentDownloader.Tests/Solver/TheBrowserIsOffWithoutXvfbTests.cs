using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// GitHub issue 6: on the server's Docker images there is no <c>Xvfb</c>, and the plugin chose the Xvfb stage
/// anyway, so every search that needed the browser failed with ".NET could not start 'Xvfb' with working
/// directory /app".
/// </summary>
public class TheBrowserIsOffWithoutXvfbTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    /// <summary>The sentence the owner is told, word for word.</summary>
    private const string Why = "Xvfb is not installed, so sources behind a challenge are off.";

    /// <remarks>
    /// The decision is a function of the operating system and of whether <c>Xvfb</c> was found, so the answer
    /// for a Linux machine without it can be asserted on a machine that is not one.
    /// </remarks>
    [Fact]
    public void LinuxWithoutXvfbHidesNothingAndLinuxWithItHidesOnADisplay()
    {
        Assert.Equal(Hiding.NoXvfb, HiddenStages.HidingFor(isWindows: false, isLinux: true, xvfbFound: false));
        Assert.Equal(Hiding.XvfbDisplay, HiddenStages.HidingFor(isWindows: false, isLinux: true, xvfbFound: true));

        // Windows has no use for Xvfb, found or not, and macOS has nowhere either way.
        Assert.Equal(Hiding.WindowsDesktop, HiddenStages.HidingFor(isWindows: true, isLinux: false, xvfbFound: false));
        Assert.Equal(Hiding.Nowhere, HiddenStages.HidingFor(isWindows: false, isLinux: false, xvfbFound: true));
    }

    [Fact]
    public void LinuxWithoutXvfbCannotHideABrowserAndSaysWhyInPlainWords()
    {
        HiddenStages stages = new(new CapturingLogger(), Hiding.NoXvfb);

        Assert.False(stages.CanHideABrowser);
        Assert.Equal(Why, stages.WhyNot);
    }

    /// <remarks>
    /// The reason is the one that is true. macOS keeps its own sentence; telling a Mac owner to install Xvfb
    /// would send them after something that cannot help.
    /// </remarks>
    [Fact]
    public void EachPlatformThatCannotHideSaysItsOwnReason()
    {
        string? mac = new HiddenStages(new CapturingLogger(), Hiding.Nowhere).WhyNot;

        Assert.NotNull(mac);
        Assert.NotEqual(Why, mac);
        Assert.DoesNotContain("Xvfb", mac, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinuxStageIsNeverBuiltWhereThereIsNoXvfbToStart()
    {
        HiddenStages stages = new(new CapturingLogger(), Hiding.NoXvfb);

        PlatformNotSupportedException refused = Assert.Throws<PlatformNotSupportedException>(stages.Create);

        Assert.Equal(Why, refused.Message);
    }

    /// <remarks>
    /// An executable file named <c>Xvfb</c> in one of <c>PATH</c>'s folders. The first folder has no such
    /// file and the second does, so a lookup that only reads the first is caught.
    /// </remarks>
    [Fact]
    public void XvfbIsFoundWhenAnExecutableOfThatNameIsInAFolderOnPath()
    {
        string empty = Directory.CreateDirectory(Path.Combine(_folder, "empty")).FullName;
        string bin = Directory.CreateDirectory(Path.Combine(_folder, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "Xvfb"), "not really an X server");

        string path = string.Join(Path.PathSeparator, empty, bin);

        Assert.True(XvfbLookup.IsOnPath(path, _ => true));
    }

    [Fact]
    public void XvfbIsNotFoundWhenNoFolderOnPathHoldsOne()
    {
        string empty = Directory.CreateDirectory(Path.Combine(_folder, "empty")).FullName;
        File.WriteAllText(Path.Combine(empty, "Xvfb-run"), "a different program");

        Assert.False(XvfbLookup.IsOnPath(empty, _ => true));
        Assert.False(XvfbLookup.IsOnPath(string.Empty, _ => true));
        Assert.False(XvfbLookup.IsOnPath(null, _ => true));
    }

    /// <remarks>
    /// A file called Xvfb that cannot be run is not an X server, and starting it fails the same way as not
    /// having one. The runnable test is the caller's to say, so it can be asserted without a Linux machine.
    /// </remarks>
    [Fact]
    public void AFileThatCannotBeRunIsNotAnXvfb()
    {
        string bin = Directory.CreateDirectory(Path.Combine(_folder, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "Xvfb"), "not really an X server");

        Assert.False(XvfbLookup.IsOnPath(bin, _ => false));
    }

    [Fact]
    public void OnlyAnExecuteBitMakesAFileRunnableOnLinux()
    {
        Assert.True(XvfbLookup.CanBeRun(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        Assert.True(XvfbLookup.CanBeRun(UnixFileMode.GroupExecute));
        Assert.True(XvfbLookup.CanBeRun(UnixFileMode.OtherExecute));
        Assert.False(XvfbLookup.CanBeRun(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead));
    }

    /// <remarks>
    /// The real file system, on the platforms that have an execute bit. On Windows there is no such bit and
    /// this has nothing to say; the Linux run of this suite is what exercises it.
    /// </remarks>
    [Fact]
    public void ARealFileIsRunnableOnlyWithItsExecuteBitSet()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string bin = Directory.CreateDirectory(Path.Combine(_folder, "bin")).FullName;
        string xvfb = Path.Combine(bin, "Xvfb");
        File.WriteAllText(xvfb, "not really an X server");
        File.SetUnixFileMode(xvfb, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.False(XvfbLookup.IsOnPath(bin));

        File.SetUnixFileMode(xvfb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(XvfbLookup.IsOnPath(bin));
    }

    /// <remarks>
    /// Said once per plugin lifetime. Every search that needs the browser asks it to start, and a warning per
    /// attempt filled the owner's log with the same sentence a hundred times a run.
    /// </remarks>
    [Fact]
    public async Task TheWarningIsLoggedOnceHoweverManyTimesTheBrowserIsAsked()
    {
        CapturingLogger log = new();
        using Browser browser = new(
            new BrowserInstall(_folder, new FakeBrowserDownloader(), log),
            new HiddenStages(log, Hiding.NoXvfb),
            log,
            listeningWithin: TimeSpan.Zero);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.Null(await browser.StartAsync(CancellationToken.None));
        }

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains(Why, StringComparison.Ordinal));
    }

    /// <remarks>
    /// The solver said "no browser" at Warning for every host it was asked about, on top of the browser's own
    /// line, so the once-only warning above would still have been buried in repeats.
    /// </remarks>
    [Fact]
    public async Task TheSolverDoesNotRepeatTheWarningForEveryHost()
    {
        CapturingLogger log = new();
        BrowserSolver solver = new(new FakeTabs { HasBrowser = false }, log);

        await solver.SolveAsync(new("https://predb.me/"), CancellationToken.None);
        await solver.SolveAsync(new("https://1337x.to/"), CancellationToken.None);

        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
