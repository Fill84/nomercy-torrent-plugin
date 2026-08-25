using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

public class BrowserTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    /// <remarks>
    /// <strong>C5.</strong> 0.3.4 re-downloaded Chrome on every server start —
    /// 150 MB and a minute — because the check looked in the wrong place, and
    /// logged it as news rather than as the fault it was. Two starts, one
    /// download, and the second start is a different <see cref="Browser"/>
    /// because a server restart is what the fault happened across.
    /// </remarks>
    [Fact]
    public async Task AcrossTwoStartsTheBrowserIsDownloadedOnce()
    {
        FakeBrowserDownloader downloader = new();

        using (Browser first = Build(downloader, out _))
        {
            await first.StartAsync(CancellationToken.None);
        }

        using (Browser second = Build(downloader, out _))
        {
            await second.StartAsync(CancellationToken.None);
        }

        Assert.Equal(1, downloader.Downloads);
    }

    /// <remarks>
    /// <strong>D3.</strong> There is nowhere on macOS to put a window that is
    /// not somebody's Space. A stage that cannot hide starts nothing at all and
    /// says why — the alternative is a browser window on the owner's desktop,
    /// which is a fault only the person sitting at the machine can see.
    /// </remarks>
    [Fact]
    public async Task WhereNothingCanBeHiddenNothingIsStartedAndTheReasonIsGiven()
    {
        FakeBrowserDownloader downloader = new();
        RecordingStages stages = new() { CanHideABrowser = false, WhyNot = "nowhere on macOS to put a window" };
        CapturingLogger log = new();

        using Browser browser = new(
            new BrowserInstall(_folder, downloader, log),
            stages,
            log);

        IBrowserProcess? started = await browser.StartAsync(CancellationToken.None);

        Assert.Null(started);
        Assert.Empty(stages.Events);
        Assert.Equal(0, downloader.Downloads);
        Assert.Contains(log.Lines, line => line.Contains("nowhere on macOS", StringComparison.Ordinal));
    }

    /// <remarks>
    /// macOS is one of the platforms that cannot, and the decision lives in one
    /// place so nothing can answer it a second way.
    /// </remarks>
    [Fact]
    public void MacOsCannotHideABrowserAndTheOtherTwoCan()
    {
        // Neither Windows nor Linux is macOS, and it is the only one of the
        // three with nowhere to put a window.
        Assert.Equal(Hiding.Nowhere, HiddenStages.HidingFor(isWindows: false, isLinux: false));
        Assert.Equal(Hiding.WindowsDesktop, HiddenStages.HidingFor(isWindows: true, isLinux: false));
        Assert.Equal(Hiding.XvfbDisplay, HiddenStages.HidingFor(isWindows: false, isLinux: true));

        // And the reason is given exactly when there is one to give.
        HiddenStages stages = new(new CapturingLogger());
        Assert.Equal(stages.CanHideABrowser, stages.WhyNot is null);
    }

    /// <remarks>
    /// The stage exists before the browser does. Starting Chrome first and
    /// moving it afterwards puts a window on the owner's screen for the half
    /// second in between, and the order is the only thing that prevents it.
    /// </remarks>
    [Fact]
    public async Task TheStageIsCreatedBeforeTheBrowserStarts()
    {
        RecordingStages stages = new();

        using Browser browser = Build(new FakeBrowserDownloader(), out _, stages);

        await browser.StartAsync(CancellationToken.None);

        Assert.Equal(["stage created", "browser launched"], stages.Events);
    }

    /// <remarks>
    /// One browser for the process. Clearance is issued per host and kept in a
    /// tab per host, so a second browser solves every gate a second time and
    /// costs a second Chrome's memory beside the media server for it.
    /// </remarks>
    [Fact]
    public async Task ASecondStartReusesTheRunningBrowser()
    {
        RecordingStages stages = new();
        FakeBrowserDownloader downloader = new();

        using Browser browser = Build(downloader, out _, stages);

        IBrowserProcess? first = await browser.StartAsync(CancellationToken.None);
        IBrowserProcess? second = await browser.StartAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(["stage created", "browser launched"], stages.Events);
        Assert.Equal(1, downloader.Downloads);
    }

    /// <remarks>
    /// A browser that has died is not one to hand a page to. Reuse is "the same
    /// one, still running", never "we started one once".
    /// </remarks>
    [Fact]
    public async Task ABrowserThatHasDiedIsStartedAgain()
    {
        RecordingStages stages = new();

        using Browser browser = Build(new FakeBrowserDownloader(), out _, stages);

        IBrowserProcess? first = await browser.StartAsync(CancellationToken.None);
        ((FakeBrowserProcess)first!).IsRunning = false;

        IBrowserProcess? second = await browser.StartAsync(CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(["stage created", "browser launched", "browser launched"], stages.Events);

        // The same stage, though: the desktop did not go anywhere.
        Assert.Single(stages.Events, entry => entry == "stage created");
    }

    /// <remarks>
    /// A record pointing at a browser that is not there is not an install. A
    /// half-deleted folder is a real state — somebody clearing space, an
    /// antivirus, a failed copy — and answering "installed" for it would fail
    /// later and further away, when Chrome was asked to start and was not
    /// there.
    /// </remarks>
    [Fact]
    public async Task AnInstallWhoseBrowserHasGoneIsNotAnInstall()
    {
        FakeBrowserDownloader downloader = new();
        CapturingLogger log = new();
        BrowserInstall install = new(_folder, downloader, log);

        string executable = await install.EnsureAsync(CancellationToken.None);
        File.Delete(executable);

        Assert.Null(install.Installed());

        await install.EnsureAsync(CancellationToken.None);

        Assert.Equal(2, downloader.Downloads);
    }

    /// <remarks>
    /// Two callers arriving together get one browser. A cadence tick and a page
    /// render both reach a plugin that has just loaded, and two browsers would
    /// each solve every gate and cost a second Chrome's memory beside the media
    /// server for it.
    /// </remarks>
    [Fact]
    public async Task TwoStartsAtOnceStillProduceOneBrowser()
    {
        RecordingStages stages = new();
        BlockingBrowserDownloader downloader = new();
        CapturingLogger log = new();

        using Browser browser = new(new BrowserInstall(_folder, downloader, log), stages, log);

        Task<IBrowserProcess?> first = browser.StartAsync(CancellationToken.None);
        Task<IBrowserProcess?> second = browser.StartAsync(CancellationToken.None);

        // Both are now inside, one holding the download open. Letting it finish
        // is what lets the second discover the browser is already there.
        await downloader.Started.WaitAsync(TimeSpan.FromSeconds(5));
        downloader.Finish();

        IBrowserProcess?[] both = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(both[0], both[1]);
        Assert.Equal(1, downloader.Downloads);
        Assert.Equal(["stage created", "browser launched"], stages.Events);
    }

    /// <remarks>
    /// Headless is not used at all. Measured: headless Chrome does not pass a
    /// managed challenge, and every gated source returns the interstitial for
    /// ever — so an argument that turns it on must never appear.
    /// </remarks>
    [Fact]
    public void TheBrowserIsNeverStartedHeadless()
    {
        Assert.DoesNotContain(
            Browser.Arguments(Browser.DefaultPort),
            argument => argument.Contains("headless", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            Browser.Arguments(9333),
            argument => argument == "--remote-debugging-port=9333");
    }

    /// <remarks>
    /// <para>
    /// <strong>Stopping takes the browser and its stage down, in that order,
    /// and leaves it able to start again.</strong> The browser used to be kept
    /// for the life of the plugin, so a Chrome sat on a hidden desktop for days
    /// between challenges — and since the plugin's cleanup only runs on a
    /// graceful shutdown, which a killed server never gives it, sixteen chrome
    /// processes were found running with the server already stopped.
    /// </para>
    /// <para>
    /// The order is asserted, not just the fact: closing the desktop out from
    /// under a window still on it is the one sequence that leaves a stray
    /// process with nowhere to be.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StoppingTakesTheBrowserDownAndItStartsAgainAfterwards()
    {
        RecordingStages stages = new();

        using Browser browser = Build(new FakeBrowserDownloader(), out _, stages);

        IBrowserProcess? first = await browser.StartAsync(CancellationToken.None);

        Assert.NotNull(first);

        browser.Stop();

        Assert.Equal(
            ["stage created", "browser launched", "browser disposed", "stage disposed"],
            stages.Events);

        // And the next challenge gets a browser rather than nothing: stopping
        // is not the end of it, it is the end of this one.
        IBrowserProcess? second = await browser.StartAsync(CancellationToken.None);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    private Browser Build(
        FakeBrowserDownloader downloader,
        out CapturingLogger log,
        RecordingStages? stages = null)
    {
        log = new();

        return new(new BrowserInstall(_folder, downloader, log), stages ?? new RecordingStages(), log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
