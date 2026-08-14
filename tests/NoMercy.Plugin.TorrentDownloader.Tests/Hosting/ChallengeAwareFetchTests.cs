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
            .GetAsync(new("https://katcr.to/usearch/Silo/"), gated: true, CancellationToken.None);

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
            .GetAsync(new("https://katcr.to/usearch/Silo/"), gated: true, CancellationToken.None);

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
    /// A second challenge straight after a fresh solve is not bad luck to retry
    /// through. It is a site this plugin cannot read, and it says so in a
    /// sentence that is not "the site refused us".
    /// </remarks>
    [Fact]
    public async Task ASecondChallengeAfterAFreshSolveGivesUpAndSaysWhy()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>")
            .Answers(HttpStatusCode.Forbidden, "<html>Just a moment...</html>");
        FakeSolver solver = new(new("cookie", "a user agent"));
        ClearanceStore clearances = new();

        FetchResult result = await Fetch(http, solver, clearances: clearances)
            .GetAsync(new("https://predb.me/?search=Silo"), gated: false, CancellationToken.None);

        Assert.Equal(FetchOutcome.Challenged, result.Failure?.Outcome);
        Assert.Contains("second challenge", result.Failure?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot read", result.Failure?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Solved once, not twice, and only two attempts were made.
        Assert.Equal(1, solver.Solves);
        Assert.Equal(2, http.Attempts.Count);
        Assert.Null(clearances.For("predb.me"));
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
        FakeHttp http = new FakeHttp().Throws(new HttpRequestException("no such host"));

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
    private static readonly string[] Hosts = ["mine.example", "predb.me", "katcr.to"];

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
