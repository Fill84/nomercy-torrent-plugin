using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// The solver's tabs over the host's hidden browser (<c>IPluginBrowser</c>, media-server #601).
/// </summary>
/// <remarks>
/// <c>S16-06</c>. The browser is the host's: it starts Chrome on the first page and stops it when the last
/// page goes, so what these tabs decide is how long a page lives. One per host, for the run
/// (<c>docs/07-solver.md</c> § The browser): a clearance is issued per host, and two tabs on one host would
/// solve the same gate twice.
/// </remarks>
public sealed class HostTabsTests
{
    private static readonly Uri Listing = new("https://www.1337x.to/sort-category-search/a/TV/time/desc/1/");
    private static readonly Uri Detail = new("https://www.1337x.to/torrent/1/some-release/");
    private static readonly Uri Elsewhere = new("https://extranet.torrentbay.st/browse/?q=a");

    [Fact]
    public async Task AHostsTabOpensOnePageAtItsFirstNavigationAndReusesItForTheNext()
    {
        FakeBrowser browser = new();
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab? first = await tabs.ForAsync(Listing.Host, CancellationToken.None);

        Assert.NotNull(first);

        // Nothing is opened for a tab nobody has navigated: a page is a Chrome.
        Assert.Empty(browser.Opened);

        await first.GoToAsync(Listing, CancellationToken.None);
        await first.DisposeAsync();

        IBrowserTab? second = await tabs.ForAsync(Listing.Host, CancellationToken.None);

        Assert.NotNull(second);

        await second.GoToAsync(Detail, CancellationToken.None);

        FakePage page = Assert.Single(browser.Opened);

        Assert.Equal([Listing.ToString(), Detail.ToString()], page.Visited);
        Assert.False(page.Disposed, "the host's page was closed between two solves of the same run");
    }

    [Fact]
    public async Task TwoHostsOpenTwoPages()
    {
        FakeBrowser browser = new();
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab? one = await tabs.ForAsync(Listing.Host, CancellationToken.None);
        IBrowserTab? other = await tabs.ForAsync(Elsewhere.Host, CancellationToken.None);

        await one!.GoToAsync(Listing, CancellationToken.None);
        await other!.GoToAsync(Elsewhere, CancellationToken.None);

        Assert.Equal(2, browser.Opened.Count);
        Assert.Equal([Listing.ToString()], browser.Opened[0].Visited);
        Assert.Equal([Elsewhere.ToString()], browser.Opened[1].Visited);
    }

    /// <remarks>
    /// The run's end. Disposing every page is what lets the host stop Chrome; a page kept past the run is a
    /// Chrome kept past it, which is the fault the plugin's own browser had on the owner's machine.
    /// </remarks>
    [Fact]
    public async Task ClosingAllDisposesEveryPageAndTheNextTabOpensAFreshOne()
    {
        FakeBrowser browser = new();
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab? one = await tabs.ForAsync(Listing.Host, CancellationToken.None);
        IBrowserTab? other = await tabs.ForAsync(Elsewhere.Host, CancellationToken.None);

        await one!.GoToAsync(Listing, CancellationToken.None);
        await other!.GoToAsync(Elsewhere, CancellationToken.None);

        await tabs.CloseAllAsync(CancellationToken.None);

        Assert.All(browser.Opened, page => Assert.True(page.Disposed));

        IBrowserTab? again = await tabs.ForAsync(Listing.Host, CancellationToken.None);

        await again!.GoToAsync(Listing, CancellationToken.None);

        Assert.Equal(3, browser.Opened.Count);
    }

    [Fact]
    public async Task AnUnavailableBrowserGivesNoTabAndSaysWhyInTheHostsWords()
    {
        FakeBrowser browser = new() { Reason = "Chrome is not installed on this host." };
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        Assert.Null(await tabs.ForAsync(Listing.Host, CancellationToken.None));
        Assert.Equal("Chrome is not installed on this host, so sources behind a challenge are off.", tabs.WhyNot);
        Assert.Empty(browser.Opened);
    }

    /// <remarks>
    /// A host that wired no browser, or an owner who has not approved <c>browser.headless</c>: the facade
    /// itself refuses, before there is a browser to ask whether it is there.
    /// </remarks>
    [Fact]
    public async Task ARefusedFacadeGivesNoTabAndCarriesTheRefusal()
    {
        PluginRefusal refusal = PluginRefusalMessages.FacadeNotOnThisHost("test", "IPluginContext.Browser");
        await using HostTabs tabs = new(() => throw new PluginRefusedException(refusal), new CapturingLogger());

        Assert.Null(await tabs.ForAsync(Listing.Host, CancellationToken.None));
        Assert.NotNull(tabs.WhyNot);
        // The host's sentence, its full stop given up for the clause that follows it.
        Assert.StartsWith(refusal.What.TrimEnd('.'), tabs.WhyNot, StringComparison.Ordinal);
        Assert.EndsWith("so sources behind a challenge are off.", tabs.WhyNot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAvailableBrowserHasNothingToSayAgainstItself()
    {
        await using HostTabs tabs = new(() => new FakeBrowser(), new CapturingLogger());

        Assert.Null(tabs.WhyNot);
    }

    /// <remarks>
    /// Once, at Warning, however many hosts ask (<c>docs/07-solver.md</c> § Where the browser is off): every
    /// search that met a challenge used to log it again.
    /// </remarks>
    [Fact]
    public async Task TheReasonIsLoggedOnceAtWarningHoweverOftenATabIsAsked()
    {
        CapturingLogger log = new();
        FakeBrowser browser = new() { Reason = "Xvfb is not installed on this host." };
        await using HostTabs tabs = new(() => browser, log);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(await tabs.ForAsync(Listing.Host, CancellationToken.None));
            Assert.Null(await tabs.ForAsync(Elsewhere.Host, CancellationToken.None));
        }

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains("Xvfb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheClearanceComesFromThePagesCookiesAndUserAgent()
    {
        FakeBrowser browser = new();

        browser.Scripts.Enqueue(page =>
        {
            page.UserAgent = "Mozilla/5.0 (the page's own)";
            page.Cookies.Add(new("something_else", "no", "www.1337x.to", "/", null, true));
            page.Cookies.Add(new("cf_clearance", "the-token", "www.1337x.to", "/", null, true));
        });

        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        await tab.GoToAsync(Listing, CancellationToken.None);

        Assert.Equal("the-token", await tab.CookieAsync("cf_clearance", CancellationToken.None));
        Assert.Null(await tab.CookieAsync("absent", CancellationToken.None));
        Assert.Equal("Mozilla/5.0 (the page's own)", await tab.UserAgentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ThePageAnswersTheTabsQuestionsAndFetchesInsideItself()
    {
        FakeBrowser browser = new();

        browser.Scripts.Enqueue(page =>
        {
            page.Shows("<html><body>a listing</body></html>");
            page.ContentType = "application/json";
            page.Readable = true;
            page.InPageBody = "[{\"id\":1}]";
        });

        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        await tab.GoToAsync(Listing, CancellationToken.None);
        await tab.ReloadAsync(CancellationToken.None);

        FakePage page = Assert.Single(browser.Opened);

        Assert.Equal(1, page.Reloads);
        Assert.Equal("<html><body>a listing</body></html>", await tab.ContentAsync(CancellationToken.None));
        Assert.Equal("application/json", await tab.ContentTypeAsync(CancellationToken.None));
        Assert.True(await tab.IsLoadedAsync(CancellationToken.None));
        Assert.Equal("[{\"id\":1}]", await tab.FetchInPageAsync(Detail, CancellationToken.None));
        Assert.Equal([Detail.ToString()], page.FetchedInPage);
    }

    /// <remarks>
    /// Before its first navigation a tab has no page, and a question put to it is answered as a page that is
    /// not there yet would answer — the solver's poll decides what to do with that.
    /// </remarks>
    [Fact]
    public async Task ATabNotYetNavigatedAnswersAsAnEmptyPage()
    {
        await using HostTabs tabs = new(() => new FakeBrowser(), new CapturingLogger());

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        Assert.Equal(string.Empty, await tab.ContentAsync(CancellationToken.None));
        Assert.Equal(string.Empty, await tab.ContentTypeAsync(CancellationToken.None));
        Assert.False(await tab.IsLoadedAsync(CancellationToken.None));
        Assert.Null(await tab.CookieAsync("cf_clearance", CancellationToken.None));
    }

    /// <remarks>
    /// The host checks the owner's approval of <c>browser.headless</c> when a page is opened, not when it says
    /// whether it has a browser: <c>IsAvailable</c> is true and <c>OpenAsync</c> throws. Left there, every gated
    /// question of every run would open, be refused and log it (GitHub issue 6 again), and no page would ever
    /// say why. So a refusal is kept for the rest of the run — the next tab is refused outright and
    /// <c>WhyNot</c> carries it — and forgotten at the run's end, so an approval given in the meantime takes
    /// effect on the next run without a restart.
    /// </remarks>
    [Fact]
    public async Task ARefusedPageIsRememberedForTheRunAndForgottenWithIt()
    {
        PluginRefusal refusal = PluginRefusalMessages.FacadeNotOnThisHost("test", "browser.headless");
        FakeBrowser browser = new() { OpenFails = new PluginRefusedException(refusal) };
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        Assert.Null(tabs.WhyNot);

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        // The open fails as an ordinary failure, which the solver reports as "did not load".
        Exception failed = await Assert.ThrowsAnyAsync<Exception>(() => tab.GoToAsync(Listing, CancellationToken.None));

        Assert.IsNotType<OperationCanceledException>(failed);
        Assert.NotNull(tabs.WhyNot);
        Assert.StartsWith(refusal.What.TrimEnd('.'), tabs.WhyNot, StringComparison.Ordinal);
        Assert.Null(await tabs.ForAsync(Elsewhere.Host, CancellationToken.None));
        Assert.Equal(1, browser.Opens);

        await tabs.CloseAllAsync(CancellationToken.None);

        Assert.Null(tabs.WhyNot);
        Assert.NotNull(await tabs.ForAsync(Elsewhere.Host, CancellationToken.None));
    }

    /// <remarks>
    /// The host's own start timeout throws a cancellation the plugin never asked for, and a cancellation ends
    /// the whole cycle as though Stop had been pressed. Through a tab it is a page that did not load.
    /// </remarks>
    [Fact]
    public async Task ACancellationTheCallerDidNotAskForIsAPageThatDidNotLoad()
    {
        FakeBrowser browser = new() { OpenFails = new TaskCanceledException("Chrome did not open its control channel in time.") };
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        TimeoutException slow = await Assert.ThrowsAsync<TimeoutException>(() => tab.GoToAsync(Listing, CancellationToken.None));

        Assert.Contains("did not open its control channel", slow.Message, StringComparison.Ordinal);

        // Not a refusal: the next run asks again, and so does the next tab.
        Assert.Null(tabs.WhyNot);
    }

    /// <remarks>The caller's own cancellation stays what it is.</remarks>
    [Fact]
    public async Task TheCallersOwnCancellationIsNotTurnedIntoAnything()
    {
        using CancellationTokenSource stopped = new();
        FakeBrowser browser = new() { OpenFails = new OperationCanceledException(stopped.Token) };
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tab.GoToAsync(Listing, stopped.Token));
    }

    /// <remarks>
    /// A page whose Chrome died throws when it is closed. One such page must not keep the others open: the host
    /// stops Chrome only once every page is released, and a run's end must not end in an exception.
    /// </remarks>
    [Fact]
    public async Task OnePageThatWillNotCloseDoesNotKeepTheOthersOpen()
    {
        FakeBrowser browser = new();

        browser.Scripts.Enqueue(page => page.FailsToClose = true);

        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab? one = await tabs.ForAsync(Listing.Host, CancellationToken.None);
        IBrowserTab? other = await tabs.ForAsync(Elsewhere.Host, CancellationToken.None);

        await one!.GoToAsync(Listing, CancellationToken.None);
        await other!.GoToAsync(Elsewhere, CancellationToken.None);

        await tabs.CloseAllAsync(CancellationToken.None);

        Assert.Equal(2, browser.Opened.Count);
        Assert.All(browser.Opened, page => Assert.True(page.Disposed));
    }

    /// <remarks>
    /// A tab handed out before the run's end cannot open a page after it: that page would belong to no run and
    /// nothing would ever close it, so Chrome would stay up until the plugin unloads.
    /// </remarks>
    [Fact]
    public async Task ATabFromBeforeTheCloseOpensNothingAfterIt()
    {
        FakeBrowser browser = new();
        await using HostTabs tabs = new(() => browser, new CapturingLogger());

        IBrowserTab tab = (await tabs.ForAsync(Listing.Host, CancellationToken.None))!;

        await tab.GoToAsync(Listing, CancellationToken.None);
        await tabs.CloseAllAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => tab.GoToAsync(Detail, CancellationToken.None));
        Assert.Single(browser.Opened);
    }
}
