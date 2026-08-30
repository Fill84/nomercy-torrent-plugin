using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

public class BrowserSolverTests
{
    /// <summary>What Chrome shows instead of a JSON body, and what a DOM reader would parse.</summary>
    private const string Viewer =
        """<html><head><meta name="color-scheme" content="light dark"></head><body><pre>[]</pre><div id="json-viewer"></div></body></html>""";

    private const string RealJson = """{"torrents":[{"title":"Silo.S03E06.1080p.WEB.H264-CAKES","seeds":94}]}""";

    private const string Challenge = """<html><head><title>Just a moment...</title></head><body><div id="cf-browser-verification"></div></body></html>""";

    /// <remarks>
    /// <strong>D1.</strong> A browser asked for a JSON endpoint renders it in
    /// its own viewer, and reading the document returns that viewer's markup.
    /// In 0.3.4 every JSON source silently answered an empty array this way,
    /// and an XML feed reported a parse error naming a <c>meta</c> tag the feed
    /// never had. The document here <em>is</em> the viewer, so a solver that
    /// hands back what the tab is showing fails this test.
    /// </remarks>
    [Fact]
    public async Task AJsonBodyComesBackAsJsonAndNotAsChromesPictureOfIt()
    {
        FakeTabs tabs = new();
        tabs.Tab("apibay.org").Shows(Viewer).ContentType = "application/json";
        tabs.Tab("apibay.org").InPageBody = RealJson;

        string? body = await Solver(tabs).GetPageAsync(new("https://apibay.org/q.php?q=Silo"), CancellationToken.None);

        Assert.Equal(RealJson, body);
        Assert.DoesNotContain("json-viewer", body ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("color-scheme", body ?? string.Empty, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// Chrome does not <em>show</em> a feed, it downloads one, and a navigation
    /// to a download is aborted: <c>net::ERR_ABORTED</c>. So the address that
    /// carries the answer is the one address the browser will not go to, and
    /// giving up there loses the whole source.
    /// </para>
    /// <para>
    /// Measured against SceneSource on 22 August 2026, whose search is RSS:
    /// every query aborted, and the page reported the site as not answering
    /// while the feed was sitting there for anyone who asked for it rather than
    /// navigated to it.
    /// </para>
    /// <para>
    /// The host is still reachable — it is this document Chrome refuses to
    /// render — so the tab goes to the site itself for its clearance and the
    /// feed is fetched from inside the page, which is how every other
    /// non-HTML body already comes back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFeedTheBrowserRefusesToRenderIsFetchedFromInsideThePage()
    {
        const string feed = """<?xml version="1.0"?><rss><channel><item><title>Silo S03E08 1080p WEB H264-CAKES</title></channel></rss>""";

        FakeTabs tabs = new();
        FakeTab tab = tabs.Tab("www.scnsrc.me");
        tab.Shows("<html><body>SceneSource</body></html>");
        tab.ContentType = "application/rss+xml";
        tab.InPageBody = feed;
        tab.FailsToLoadOnly("/feed/", "net::ERR_ABORTED at https://www.scnsrc.me/feed/?s=Silo");

        string? body = await Solver(tabs).GetPageAsync(
            new("https://www.scnsrc.me/feed/?s=Silo"),
            CancellationToken.None);

        Assert.Equal(feed, body);

        // The site itself, for the clearance the feed needs and cannot ask for.
        Assert.Contains(tab.Visited, visited => visited == "https://www.scnsrc.me/");
    }

    /// <remarks>
    /// An HTML page is read from the document, because that is where the site's
    /// own scripts have finished putting it. Fetching it again inside the page
    /// would get the markup before any of that ran.
    /// </remarks>
    [Fact]
    public async Task AnHtmlPageComesBackFromTheDocument()
    {
        FakeTabs tabs = new();
        tabs.Tab("www.1337x.to").Shows("<html><body>the rendered site</body></html>");
        tabs.Tab("www.1337x.to").InPageBody = "the markup before anything ran";

        string? body = await Solver(tabs).GetPageAsync(new("https://www.1337x.to/search/Silo/"), CancellationToken.None);

        Assert.Equal("<html><body>the rendered site</body></html>", body);
    }

    /// <remarks>
    /// <para>
    /// <strong>The tab closes when the solve ends, however it ends.</strong>
    /// Tabs were kept one per host for the life of the plugin, so a browser
    /// stayed open for days between challenges — and because the plugin's
    /// cleanup only runs on a graceful shutdown, which a killed server never
    /// gives it, sixteen chrome processes were found running on the owner's
    /// machine with the server stopped.
    /// </para>
    /// <para>
    /// Nothing is lost by closing it: the clearance is a cookie and it is read
    /// into the clearance store before this returns, which is what the first
    /// assertion is for. A test that only checked the tab had closed would pass
    /// just as well if the solve had stopped working.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTabIsClosedWhenTheSolveIsDone()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("predb.me").Shows("<html>the real page</html>");
        tabs.Tab("predb.me").Clearance = "a cookie";

        Clearance? clearance = await Solver(tabs, clock)
            .SolveAsync(new("https://predb.me/?search=Silo"), CancellationToken.None);

        Assert.Equal("a cookie", clearance?.Cookie);
        Assert.Equal(1, tabs.Tab("predb.me").Closed);
    }

    /// <remarks>
    /// <para>
    /// <strong>And every page read, which is most of what the browser does.</strong>
    /// A solve happens once per host; a gated source is read on every name of
    /// every cycle, so a tab left open there is a tab per search for ever.
    /// </para>
    /// <para>
    /// It was: ninety Chrome processes were found on the owner's machine with
    /// nothing running and no cycle in flight, holding seven hundred megabytes
    /// between them. The browser is meant to stay up between solves — that is
    /// what keeps a gated source's clearance — and a tab is not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTabIsClosedWhenAPageHasBeenRead()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("www.1337x.to").Shows("<html>a listing</html>");

        string? page = await Solver(tabs, clock)
            .GetPageAsync(new("https://www.1337x.to/search/Silo/1/"), CancellationToken.None);

        Assert.Contains("a listing", page);
        Assert.Equal(1, tabs.Tab("www.1337x.to").Closed);
    }

    /// <remarks>
    /// The same for a POST, which is how a torrent's magnet is asked for on a
    /// site that publishes none: once per release taken, and a leak there is
    /// one tab per download.
    /// </remarks>
    [Fact]
    public async Task TheTabIsClosedWhenAFormHasBeenPosted()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("extranet.torrentbay.st").Shows("<html>a magnet</html>");

        await Solver(tabs, clock).PostAsync(
            new("https://extranet.torrentbay.st/ajax/getSearchMagnet.php"),
            "id=1",
            CancellationToken.None);

        Assert.Equal(1, tabs.Tab("extranet.torrentbay.st").Closed);
    }

    /// <remarks>
    /// A challenge that never cleared still leaves no tab behind. This is the
    /// path that leaked most: a site that keeps refusing is asked again every
    /// cycle, so a tab left open by a failure is a tab left open for ever.
    /// </remarks>
    [Fact]
    public async Task TheTabIsClosedEvenWhenTheChallengeNeverClears()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("predb.me").Shows(Challenge);

        Task<Clearance?> solving = Solver(tabs, clock)
            .SolveAsync(new("https://predb.me/?search=Silo"), CancellationToken.None);

        for (int poll = 0; poll < 40; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Null(await solving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, tabs.Tab("predb.me").Closed);
    }

    /// <remarks>
    /// <para>
    /// The tab is closed once and once only. Closing it twice would tell the
    /// tabs one more has gone than was ever opened, and the count is what
    /// decides whether the browser may stop — one off and it stops while a
    /// solve is still running.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTabIsClosedOnceAndNotTwice()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("predb.me").Shows("<html>the real page</html>");
        tabs.Tab("predb.me").Clearance = "a cookie";

        await Solver(tabs, clock).SolveAsync(new("https://predb.me/?search=Silo"), CancellationToken.None);

        Assert.Equal(1, tabs.Tab("predb.me").Closed);
    }

    /// <remarks>
    /// <strong>D2.</strong> A navigation during the poll is the challenge page
    /// doing exactly what it is supposed to do — reloading itself once it has
    /// been satisfied. Treating it as a failure gives up at the moment it
    /// worked, which 0.3.4 did four times in one run.
    /// </remarks>
    [Fact]
    public async Task ANavigationDuringThePollIsNotAFailure()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("predb.me").Shows(Challenge).NavigatesAway().Shows("<html>the real page</html>");
        tabs.Tab("predb.me").Clearance = "a cookie";

        Task<Clearance?> solving = Solver(tabs, clock)
            .SolveAsync(new("https://predb.me/?search=Silo"), CancellationToken.None);

        // Two polls: the challenge, then the throw, then the page.
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));

        Clearance? clearance = await solving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("a cookie", clearance?.Cookie);
        Assert.Equal(0, tabs.Tab("predb.me").Reloads);
    }

    /// <remarks>
    /// Not a challenge is not the same as ready. A challenge clears by
    /// navigating, and in between the tab holds a document that is neither
    /// page — found by the first real capture, where 1337x answered 876 bytes
    /// of stylesheet links and no body at all.
    /// </remarks>
    [Fact]
    public async Task APageStillLoadingIsNotYetCleared()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("www.1337x.to")
            .Shows("<html><head><title>the real page</title></head></html>", "<html><body>all of it</body></html>")
            .StillLoadingFor(1);

        Task<string?> getting = Solver(tabs, clock)
            .GetPageAsync(new("https://www.1337x.to/search/Silo/"), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("<html><body>all of it</body></html>", await getting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <remarks>
    /// One reload, then a sentence naming the host. A loop of reloads is how a
    /// site decides we are worth blocking properly.
    /// </remarks>
    [Fact]
    public async Task AChallengeThatWillNotClearIsReloadedOnceThenGivenUpOnByName()
    {
        FakeTimeProvider clock = new();
        FakeTabs tabs = new();
        tabs.Tab("torrentbay.st").Shows(Challenge);
        CapturingLogger log = new();

        Task<Clearance?> solving = new BrowserSolver(
                tabs,
                log,
                clock,
                solveTimeout: TimeSpan.FromSeconds(3),
                pollInterval: TimeSpan.FromSeconds(1))
            .SolveAsync(new("https://torrentbay.st/browse/"), CancellationToken.None);

        for (int tick = 0; tick < 10; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Null(await solving.WaitAsync(TimeSpan.FromSeconds(5)));

        // Exactly one. A loop of reloads is how a site decides we are worth
        // blocking properly.
        Assert.Equal(1, tabs.Tab("torrentbay.st").Reloads);

        // In the sentence the owner actually sees — the debug line while it was
        // still trying names the host too, and that one nobody reads.
        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Warning
                     && entry.Line.Contains("torrentbay.st", StringComparison.Ordinal));
    }

    /// <remarks>
    /// One tab per host, kept open. Clearance is issued per host, so two tabs
    /// on one host solve the same gate twice and each hold half the answer.
    /// </remarks>
    [Fact]
    public async Task TwoRequestsToOneHostShareATabAndTwoHostsGetTwo()
    {
        FakeTabs tabs = new();
        tabs.Tab("www.1337x.to").Shows("<html>one</html>");
        tabs.Tab("eztvx.to").Shows("<html>two</html>");

        BrowserSolver solver = Solver(tabs);

        await solver.GetPageAsync(new("https://www.1337x.to/search/Silo/"), CancellationToken.None);
        await solver.GetPageAsync(new("https://www.1337x.to/search/Lioness/"), CancellationToken.None);
        await solver.GetPageAsync(new("https://eztvx.to/search/Silo"), CancellationToken.None);

        Assert.Equal(["www.1337x.to", "www.1337x.to", "eztvx.to"], tabs.Asked);
        Assert.Same(tabs.Tab("www.1337x.to"), tabs.Tab("www.1337x.to"));
        Assert.NotSame(tabs.Tab("www.1337x.to"), tabs.Tab("eztvx.to"));
    }

    /// <remarks>
    /// The clearance and the user agent it was issued to travel together.
    /// Replaying the cookie under any other user agent is a refusal that reads
    /// like the site changing its mind.
    /// </remarks>
    [Fact]
    public async Task ClearanceComesBackWithTheUserAgentItWasIssuedTo()
    {
        FakeTabs tabs = new();
        tabs.Tab("predb.me").Shows("<html>the real page</html>");
        tabs.Tab("predb.me").Clearance = "a cookie";
        tabs.Tab("predb.me").UserAgent = "Mozilla/5.0 (the one it was issued to)";

        Clearance? clearance = await Solver(tabs)
            .SolveAsync(new("https://predb.me/?search=Silo"), CancellationToken.None);

        Assert.Equal("a cookie", clearance?.Cookie);
        Assert.Equal("Mozilla/5.0 (the one it was issued to)", clearance?.UserAgent);
    }

    /// <remarks>
    /// <strong>Step 6.</strong> Null, not an attempt. A post sent from this
    /// process arrives without the session that earned the right to ask and is
    /// refused, so the caller can say "this site needs a browser" — which is
    /// actionable — instead of "this site refused us", which is not even true.
    /// </remarks>
    [Fact]
    public async Task PostingWithNoBrowserAnswersNullRatherThanTrying()
    {
        FakeTabs tabs = new() { HasBrowser = false };
        CapturingLogger log = new();

        string? posted = await new BrowserSolver(tabs, log)
            .PostAsync(new("https://torrentbay.st/sign"), "a=1&b=2", CancellationToken.None);

        Assert.Null(posted);
        Assert.Contains(log.Lines, line => line.Contains("browser", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// And with one, the post goes from inside the page that already has the
    /// site open.
    /// </remarks>
    [Fact]
    public async Task PostingGoesFromInsideThePage()
    {
        FakeTabs tabs = new();
        tabs.Tab("torrentbay.st").PostedBody = "magnet:?xt=urn:btih:abc";

        string? posted = await Solver(tabs)
            .PostAsync(new("https://torrentbay.st/sign"), "id=7&token=x", CancellationToken.None);

        Assert.Equal("magnet:?xt=urn:btih:abc", posted);
        Assert.Equal(["id=7&token=x"], tabs.Tab("torrentbay.st").Posted);
    }

    /// <remarks>
    /// A navigation that never finishes is an ordinary outcome for a site
    /// behind a challenge — measured against TorrentBay, which simply did not
    /// load. The driver reports it by throwing, and letting that out leaves the
    /// caller with no failure to report and nothing to skip: it takes down
    /// whatever asked, which is what it did the first time.
    /// </remarks>
    [Fact]
    public async Task APageThatWillNotLoadIsReportedRatherThanThrown()
    {
        FakeTabs tabs = new();
        tabs.Tab("torrentbay.st").FailsToLoad("Navigation timeout of 30000 ms exceeded");
        CapturingLogger log = new();

        BrowserSolver solver = new(tabs, log);

        Assert.Null(await solver.GetPageAsync(new("https://torrentbay.st/browse/"), CancellationToken.None));
        Assert.Null(await solver.SolveAsync(new("https://torrentbay.st/browse/"), CancellationToken.None));
        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Line.Contains("torrentbay.st", StringComparison.Ordinal));
    }

    /// <remarks>
    /// With no browser at all there is nothing to solve with, and saying so is
    /// not the same as saying the challenge failed.
    /// </remarks>
    [Fact]
    public async Task WithNoBrowserNothingIsSolvedAndNothingIsFetched()
    {
        FakeTabs tabs = new() { HasBrowser = false };
        BrowserSolver solver = new(tabs, new CapturingLogger());

        Assert.Null(await solver.SolveAsync(new("https://predb.me/"), CancellationToken.None));
        Assert.Null(await solver.GetPageAsync(new("https://predb.me/"), CancellationToken.None));
    }


    /// <remarks>
    /// <strong>One tab is one document, and two callers at once abort each
    /// other.</strong> Watched on the owner's own server on 22 August 2026: the
    /// name resolver asks eight seasons at once and the gate lets two through
    /// per host, so two navigations landed in the same tab and Chrome reported
    /// the loser as <c>net::ERR_ABORTED</c> — which reads exactly like a site
    /// refusing to load. Every gated name database failed that way in one
    /// second, and the pool went so thin that two episodes of the owner's own
    /// Silo season had no name in it at all.
    ///
    /// The wait is what has to be exclusive, not the navigation: a caller that
    /// navigated and is polling for its challenge to clear still owns the
    /// document, and anyone arriving in the middle takes it away.
    /// </remarks>
    [Fact]
    public async Task OneTabServesOneCallerAtATime()
    {
        FakeTabs tabs = new();
        FakeTab tab = tabs.Tab("predb.me").Shows("<html><body><p>the feed</p></body></html>");

        tab.HoldsTheFirstCaller();

        BrowserSolver solver = Solver(tabs);

        Task<string?> first = solver.GetPageAsync(new("https://predb.me/?search=Silo+S03"), CancellationToken.None);

        await tab.Entered.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Task<string?> second = solver.GetPageAsync(new("https://predb.me/?search=Lucky+S01"), CancellationToken.None);

        // Bounded, and it is proving something did not happen: the second
        // caller must still be waiting while the first holds the tab.
        Assert.False(second.IsCompleted);
        Assert.Equal(1, tab.Navigations);

        tab.LetGo();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(2, tab.Navigations);
        Assert.False(tab.Overlapped);
    }

    /// <remarks>
    /// One host waiting does not hold up another. The lock is the tab's, and
    /// there is a tab per host — a single lock over the browser would make
    /// every gated source in a cycle wait out every other one.
    /// </remarks>
    [Fact]
    public async Task ATabHeldOnOneHostDoesNotHoldUpAnother()
    {
        FakeTabs tabs = new();

        FakeTab held = tabs.Tab("predb.me").Shows("<html><body><p>the feed</p></body></html>");
        held.HoldsTheFirstCaller();

        tabs.Tab("www.scnsrc.me").Shows("<html><body><p>another feed</p></body></html>");

        BrowserSolver solver = Solver(tabs);

        Task<string?> waiting = solver.GetPageAsync(new("https://predb.me/?search=Silo+S03"), CancellationToken.None);

        await held.Entered.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        string? other = await solver
            .GetPageAsync(new("https://www.scnsrc.me/feed/?s=Silo+S03"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains("another feed", other!, StringComparison.Ordinal);

        held.LetGo();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    private static BrowserSolver Solver(FakeTabs tabs, TimeProvider? clock = null)
    {
        return new(
            tabs,
            new CapturingLogger(),
            clock,
            solveTimeout: TimeSpan.FromSeconds(3),
            pollInterval: TimeSpan.FromSeconds(1));
    }
}
