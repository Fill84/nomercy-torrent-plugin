using System.Net;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
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

    /// <remarks>
    /// <para>
    /// <strong>With no browser, the pass that clears challenges before a run stops at finding them, and says
    /// why.</strong> 1337x answers the plain question with its challenge, so it needs a browser; there is
    /// none to start, and starting one anyway only repeats the refusal the Sources page already shows.
    /// </para>
    /// <para>
    /// 1337x is the one source left on, its interstitial the page it really answered with.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithNoBrowserTheChallengesBeforeARunAreFoundAndNotClearedAndThePassSaysWhy()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();
        Settings settings = new();

        foreach (SourceDefinition other in shipped.Where(one => one.Name != "1337x"))
        {
            settings.DisabledDefaultSources.Add(other.Name);
        }

        SourceDefinition gated = shipped.Single(one => one.Name == "1337x");
        Uri address = new((gated.SearchAddress ?? gated.Url).Replace("{query}", "a", StringComparison.Ordinal));

        FakeHttp http = new FakeHttp().Answers(
            HttpStatusCode.Forbidden,
            Fixture("cloudflare-interstitial-1337x.html"),
            (CloudflareChallenge.MitigatedHeader, "challenge"));

        FakeGrants grants = new();
        grants.Grant(address.Host);

        ActivityJournal journal = new();

        await using Chain chain = new(
            new FakePluginContext { DataFolderPath = _folder, Permits = grants },
            journal,
            shipped,
            http,
            hiding: Hiding.NoXvfb);

        await chain.ClearTheWayAsync(settings, CancellationToken.None);

        IReadOnlyList<ActivityEvent> clearance =
            [.. journal.Snapshot().History.Where(one => one.Stage == ActivityStage.Clearance)];

        Assert.Single(http.Attempts);
        Assert.Contains(clearance, one => one.Detail == Why);
        Assert.DoesNotContain(clearance, one => (one.Detail ?? string.Empty).Contains("cleared", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <strong>The Sources page says which sources the missing browser has switched off, and why.</strong>
    /// Above the table, where the owner looks first, and on each of their rows.
    /// </remarks>
    [Fact]
    public async Task TheSourcesPageSaysWhichSourcesAreOffWithNoBrowserAndWhy()
    {
        using TorrentDownloaderPlugin plugin = new() { BrowserHiding = Hiding.NoXvfb };

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder });

        PluginView view = await plugin.GetViewAsync(Requests.View(Pages.SourcesRoute), CancellationToken.None);

        string words = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains($"{Why} Off:", words, StringComparison.Ordinal);
        Assert.Contains("1337x", words, StringComparison.Ordinal);
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }

    public void Dispose()
    {
        // Through the helper every store test uses: the Sources page reads the plugin's database, and SQLite's
        // pool can still be holding the file when the test is over.
        TemporaryFolder.Forget(_folder);

        GC.SuppressFinalize(this);
    }
}
