using System.Net;
using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

public class ChallengeAwareFetchTests
{
    private static readonly Uri WithKey = new("https://mine.example/api?t=search&q=Silo&apikey=hunter2&rss_key=abc");

    /// <remarks>
    /// <strong>G1.</strong> Both halves of it. 0.3.4's refusal said "search
    /// returned HTTP 429" and left nobody able to tell which of seventeen
    /// sources meant it; when the address was added, it published the owner's
    /// API key into the log.
    /// </remarks>
    [Fact]
    public async Task ARefusalNamesTheAddressAndBlanksTheSecretsInIt()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.TooManyRequests);

        FetchResult result = await Fetch(http).GetAsync(WithKey, gated: false, CancellationToken.None);

        FetchFailure failure = Assert.IsType<FetchFailure>(result.Failure);

        Assert.Contains("mine.example", failure.Address, StringComparison.Ordinal);
        Assert.Contains("q=Silo", failure.Address, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", failure.Address, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", failure.Address, StringComparison.Ordinal);
        Assert.Contains($"apikey={Addresses.Blanked}", failure.Address, StringComparison.Ordinal);
        Assert.Contains($"rss_key={Addresses.Blanked}", failure.Address, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", failure.ToString(), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A gated address goes to the browser without an HTTP attempt. Finding out
    /// by trying costs a guaranteed refusal before every single fetch of that
    /// host, and it teaches the gate to back off from a site that never said
    /// anything of the kind.
    /// </remarks>
    [Fact]
    public async Task AGatedAddressNeverMakesAnHttpAttempt()
    {
        FakeHttp http = new();
        FakePages pages = new("<html>the real page</html>");

        FetchResult result = await Fetch(http, pages: pages)
            .GetAsync(new("https://www.1337x.to/search/Silo/"), gated: true, CancellationToken.None);

        Assert.Empty(http.Attempts);
        Assert.Single(pages.Asked);
        Assert.Equal("<html>the real page</html>", result.Body);
    }

    /// <remarks>
    /// A gated address with no browser says so as the plugin's own gap. The
    /// owner can act on "this needs a browser" and cannot act on "the site
    /// refused us".
    /// </remarks>
    [Fact]
    public async Task AGatedAddressWithNoBrowserSaysThatIsWhatIsMissing()
    {
        FakeHttp http = new();

        FetchResult result = await Fetch(http)
            .GetAsync(new("https://www.1337x.to/search/Silo/"), gated: true, CancellationToken.None);

        Assert.Empty(http.Attempts);
        Assert.Equal(FetchOutcome.NoBrowser, result.Failure?.Outcome);
        Assert.Contains("browser", result.Failure?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A challenge on an address nobody marked is met once and the fetch tried
    /// again. Gating is per address, so a source not marked still reaches the
    /// solver when one of its addresses needs it.
    /// </remarks>
    [Fact]
    public async Task AChallengeIsSolvedOnceAndTheFetchRetried()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.OK, "the real body");
        FakeSolver solver = new(new("cookie", "a user agent"));
        ClearanceStore clearances = new();

        FetchResult result = await Fetch(http, solver, clearances: clearances)
            .GetAsync(new("https://predb.me/?search=Silo&rss=1"), gated: false, CancellationToken.None);

        Assert.Equal("the real body", result.Body);
        Assert.Equal(1, solver.Solves);
        Assert.Equal(2, http.Attempts.Count);

        // The clearance it earned is kept and sent with the retry.
        Assert.Equal("cookie", clearances.For("predb.me")?.Cookie);
        Assert.Contains(
            http.Attempts[1].Headers.GetValues("Cookie"),
            value => value.Contains("cf_clearance=cookie", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <c>docs/specs/run.md</c> § When a site or a source fails, the owner's rule of 15 September 2026: a
    /// question to a site that stays behind a challenge is asked at most twice, each time after the challenge
    /// is solved again. Here the challenge is still there after the first solve, is solved again, and the
    /// second try is answered.
    /// </remarks>
    [Fact]
    public async Task AChallengeStillThereAfterASolveIsSolvedAgainOnce()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.OK, "the real body");
        FakeSolver solver = new(new("cookie", "a user agent"));

        FetchResult result = await Fetch(http, solver)
            .GetAsync(new("https://predb.me/?search=Silo&rss=1"), gated: false, CancellationToken.None);

        Assert.Equal("the real body", result.Body);
        Assert.Equal(2, solver.Solves);
        Assert.Equal(3, http.Attempts.Count);
    }

    /// <remarks>
    /// Two tries after a solve and no third: a challenge still there after the second solve is a site this
    /// plugin cannot read this run, and it says so in a sentence that is not "the site refused us".
    /// </remarks>
    [Fact]
    public async Task AChallengeStillThereAfterTwoSolvesGivesUpAndSaysWhy()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>");
        FakeSolver solver = new(new("cookie", "a user agent"));
        ClearanceStore clearances = new();

        FetchResult result = await Fetch(http, solver, clearances: clearances)
            .GetAsync(new("https://predb.me/?search=Silo"), gated: false, CancellationToken.None);

        Assert.Equal(FetchOutcome.Challenged, result.Failure?.Outcome);
        Assert.Contains("solved twice", result.Failure?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, solver.Solves);
        Assert.Equal(3, http.Attempts.Count);
        Assert.Null(clearances.For("predb.me"));
    }

    /// <remarks>
    /// And a gated host the same: solved and read, and when the challenge is still there it is solved a second
    /// time and read again — never a third.
    /// </remarks>
    [Fact]
    public async Task AGatedHostStillBehindAChallengeIsSolvedAgainOnce()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.OK, "<html>the real page</html>");
        FakeSolver solver = new(new("cookie", "a user agent"));
        FakePages pages = new("<html>through the browser</html>");

        FetchResult result = await Fetch(http, solver, pages)
            .GetAsync(new("https://www.1337x.to/search/Silo/"), gated: true, CancellationToken.None);

        Assert.Equal("<html>the real page</html>", result.Body);
        Assert.Equal(2, solver.Solves);
        Assert.Empty(pages.Asked);
    }

    /// <remarks>
    /// <c>run.md</c>: a question to a site that does not answer is asked a second time — and only a second.
    /// </remarks>
    [Fact]
    public async Task ASiteThatDoesNotAnswerIsAskedASecondTime()
    {
        FakeHttp answersLate = new FakeHttp()
            .Throws(new HttpRequestException("no answer"))
            .Answers(HttpStatusCode.OK, "the real body");

        FetchResult second = await Fetch(answersLate).GetAsync(new("https://mine.example/x"), gated: false, CancellationToken.None);

        Assert.Equal("the real body", second.Body);
        Assert.Equal(2, answersLate.Attempts.Count);

        FakeHttp neverAnswers = new FakeHttp()
            .Throws(new HttpRequestException("no answer"))
            .Throws(new HttpRequestException("no answer"));

        FetchResult gone = await Fetch(neverAnswers).GetAsync(new("https://mine.example/x"), gated: false, CancellationToken.None);

        Assert.Equal(FetchOutcome.Unreachable, gone.Failure?.Outcome);
        Assert.Equal(2, neverAnswers.Attempts.Count);
    }

    /// <remarks>
    /// Clearance is spent on refusal rather than trusted until it expires. It
    /// is invalidated for reasons no client can see coming, so the refusal is
    /// the only honest signal that it has gone — and holding one that has
    /// stopped working turns every later request into a 403 that looks like the
    /// site.
    /// </remarks>
    [Fact]
    public async Task ClearanceIsSpentOnRefusalRatherThanTrustedUntilItExpires()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.Forbidden, "plainly refused");
        ClearanceStore clearances = new();
        clearances.Keep("mine.example", new("cookie", "a user agent"));

        FetchResult result = await Fetch(http, clearances: clearances)
            .GetAsync(new("https://mine.example/x"), gated: false, CancellationToken.None);

        Assert.Equal(FetchOutcome.Refused, result.Failure?.Outcome);
        Assert.Null(clearances.For("mine.example"));
    }

    /// <remarks>
    /// A host the server has not granted is not asked at all, and the gate is
    /// told it was our own doing — that is B3, and it is why a source does not
    /// stay parked after the owner says yes.
    /// </remarks>
    [Fact]
    public async Task AHostWithNoGrantIsNeverAskedAndEarnsNoBackoff()
    {
        FakeHttp http = new();
        FakeGrants grants = new();
        HostGate gate = new(new FakeTimeProvider());
        gate.Configure("mine.example", TimeSpan.FromSeconds(15));

        ChallengeAwareFetch fetch = new(http.Client(), gate, grants, new ClearanceStore());

        FetchResult result = await fetch.GetAsync(new("https://mine.example/x"), gated: false, CancellationToken.None);

        Assert.Empty(http.Attempts);
        Assert.Equal(FetchOutcome.NotPermitted, result.Failure?.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(15), gate.IntervalFor("mine.example"));
    }

    /// <remarks>
    /// A host that says it has had enough is asked less often; one that answers
    /// is asked at its own rate again.
    /// </remarks>
    [Fact]
    public async Task ARateLimitWidensTheGateAndASuccessNarrowsIt()
    {
        // A millisecond, not ten seconds: the numbers only have to move, and
        // the second fetch really does wait for the widened gap.
        HostGate gate = new(TimeProvider.System);
        gate.Configure("mine.example", TimeSpan.FromMilliseconds(1));

        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.TooManyRequests)
            .Answers(HttpStatusCode.OK, "a body");

        ChallengeAwareFetch fetch = Fetch(http, gate: gate);
        Uri address = new("https://mine.example/x");

        await fetch.GetAsync(address, gated: false, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(2), gate.IntervalFor("mine.example"));

        await fetch.GetAsync(address, gated: false, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(1), gate.IntervalFor("mine.example"));
    }

    /// <remarks>
    /// A host that does not answer at all is unreachable, not refusing, and the
    /// message says which.
    /// </remarks>
    [Fact]
    public async Task AHostThatDoesNotAnswerIsUnreachable()
    {
        FakeHttp http = new FakeHttp()
            .Throws(new HttpRequestException("no such host"))
            .Throws(new HttpRequestException("no such host"));

        FetchResult result = await Fetch(http).GetAsync(WithKey, gated: false, CancellationToken.None);

        Assert.Equal(FetchOutcome.Unreachable, result.Failure?.Outcome);
        Assert.DoesNotContain("hunter2", result.Failure?.Address ?? string.Empty, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The health tool reports the page a source returned so a broken reader
    /// can be repaired from it — and it is the caller's job to clear it between
    /// sources. 0.3.4's health check attributed one source's page to another
    /// because whoever held it never let go.
    /// </remarks>
    [Fact]
    public async Task TheLastBodyIsKeptForTheHealthToolAndCanBeCleared()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.OK, "what the site said");
        ChallengeAwareFetch fetch = Fetch(http);

        await fetch.GetAsync(new("https://mine.example/x"), gated: false, CancellationToken.None);

        Assert.Equal("what the site said", fetch.LastBody);

        fetch.LastBody = null;

        Assert.Null(fetch.LastBody);
    }

    /// <summary>The hosts these tests use, all granted.</summary>
    private static readonly string[] Hosts = ["mine.example", "predb.me", "www.1337x.to", "extranet.torrentbay.st"];

    /// <remarks>
    /// <para>
    /// <strong>A gated host whose challenge has already been solved is read
    /// over plain HTTP, and the browser stays shut.</strong> The owner's
    /// decision of 11 September 2026: Chrome is for solving a challenge and
    /// nothing else. Once the clearance cookie is in hand it travels on an
    /// ordinary request, which is what the unmarked path has always done.
    /// </para>
    /// <para>
    /// It used to go straight to the browser on anything the catalogue marked
    /// gated, and never stored a clearance for those hosts at all — so every
    /// page of every gated site, every cycle, needed Chrome.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGatedHostWithAClearanceIsReadOverHttpAndNeverInTheBrowser()
    {
        FakeHttp http = new();
        http.Answers(System.Net.HttpStatusCode.OK, "<html>the real page</html>");

        FakePages pages = new("<html>through the browser</html>");
        ClearanceStore clearances = new();
        clearances.Keep("www.1337x.to", new("cookie", "a user agent"));

        FetchResult result = await Fetch(http, pages: pages, clearances: clearances)
            .GetAsync(new("https://www.1337x.to/search/Silo/"), gated: true, CancellationToken.None);

        Assert.Empty(pages.Asked);
        Assert.Single(http.Attempts);
        Assert.Equal("<html>the real page</html>", result.Body);
    }

    /// <remarks>
    /// And a gated host with no clearance yet is solved once — in the browser,
    /// which is what a browser is for — and then read over plain HTTP with what
    /// the solve earned.
    /// </remarks>
    [Fact]
    public async Task AGatedHostIsSolvedOnceAndThenReadOverHttp()
    {
        FakeHttp http = new();
        http.Answers(System.Net.HttpStatusCode.OK, "<html>the real page</html>");

        FakeSolver solver = new(new("cookie", "a user agent"));
        FakePages pages = new("<html>through the browser</html>");
        ClearanceStore clearances = new();

        FetchResult result = await Fetch(http, solver: solver, pages: pages, clearances: clearances)
            .GetAsync(new("https://www.1337x.to/search/Silo/"), gated: true, CancellationToken.None);

        Assert.Equal(1, solver.Solves);
        Assert.Empty(pages.Asked);
        Assert.Equal("<html>the real page</html>", result.Body);
        Assert.NotNull(clearances.For("www.1337x.to"));
    }

    /// <remarks>
    /// <para>
    /// <strong>TorrentBay names its torrent over plain HTTP, in the session its page was read in.</strong>
    /// Measured on 15 September 2026: the signed request sent from a fresh browser tab, which is what the
    /// plugin did since 30 August, failed with <c>Failed to fetch</c> — the tab was on no page of the site,
    /// so it was another origin with none of its cookies. The same request sent over HTTP with the
    /// clearance and session the listing was read with answered the magnet.
    /// </para>
    /// <para>
    /// So it goes out like a page does: through the host's gate, with the clearance cookie and the user
    /// agent it was issued to, as the form the page's own script sends.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASignedRequestIsPostedOverHttpWithTheClearanceThePageWasReadWith()
    {
        FakeHttp http = new();
        http.Answers(System.Net.HttpStatusCode.OK, "{\"success\":true,\"url\":\"magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567\"}");

        ClearanceStore clearances = new();
        clearances.Keep("extranet.torrentbay.st", new("the clearance", "the agent it was issued to"));

        string? answered = await Fetch(http, clearances: clearances).PostAsync(
            new("https://extranet.torrentbay.st/ajax/getSearchMagnet.php"),
            "torrent_id=15272923&hash=&name=&timestamp=1&hmac=ab&sessid=cd",
            CancellationToken.None);

        Assert.Contains("0123456789ABCDEF0123456789ABCDEF01234567", answered!, StringComparison.Ordinal);

        HttpRequestMessage sent = Assert.Single(http.Attempts);

        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://extranet.torrentbay.st/ajax/getSearchMagnet.php", sent.RequestUri!.ToString());
        Assert.Equal("application/x-www-form-urlencoded", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("torrent_id=15272923&hash=&name=&timestamp=1&hmac=ab&sessid=cd", Assert.Single(http.Bodies));
        Assert.Contains("cf_clearance=the clearance", sent.Headers.GetValues("Cookie"));
        Assert.Contains("the agent it was issued to", string.Join(" ", sent.Headers.GetValues("User-Agent")), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A post the site refuses, or a host that is not granted, answers nothing: the caller says the site would
    /// not name the torrent, and the row takes no part.
    /// </remarks>
    [Fact]
    public async Task APostTheSiteRefusesOrAHostNotGrantedAnswersNothing()
    {
        FakeHttp http = new();
        http.Answers(System.Net.HttpStatusCode.Forbidden, "no");

        Assert.Null(await Fetch(http).PostAsync(new("https://extranet.torrentbay.st/ajax/getSearchMagnet.php"), "a=1", CancellationToken.None));
        Assert.Null(await Fetch(new FakeHttp()).PostAsync(new("https://not.granted.test/ajax/getSearchMagnet.php"), "a=1", CancellationToken.None));
    }

    private static ChallengeAwareFetch Fetch(
        FakeHttp http,
        IChallengeSolver? solver = null,
        IPageSource? pages = null,
        ClearanceStore? clearances = null,
        HostGate? gate = null)
    {
        FakeGrants grants = new();

        foreach (string host in Hosts)
        {
            grants.Grant(host);
        }

        return new(
            http.Client(),
            gate ?? Ungated(),
            grants,
            clearances ?? new ClearanceStore(),
            solver,
            pages);
    }

    /// <summary>
    /// A gate that never makes anything wait.
    /// </summary>
    /// <remarks>
    /// The real clock with a nought interval, deliberately. A fake clock would
    /// be better in every way except the one that matters here: several of
    /// these tests fetch the same host twice, and a fake clock nothing advances
    /// leaves the second request waiting for ever — the suite hangs instead of
    /// failing. What the gate does with intervals is HostGateTests' business,
    /// not this file's.
    /// </remarks>
    private static HostGate Ungated()
    {
        HostGate gate = new(TimeProvider.System);

        foreach (string host in Hosts)
        {
            gate.Configure(host, TimeSpan.Zero);
        }

        return gate;
    }
}
