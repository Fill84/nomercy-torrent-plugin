// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

/// <summary>
/// The step that decides whether a gated site is unreadable or merely gated.
/// </summary>
public class ChallengeAwareFetchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Page = new("https://gated.test/search?q=some.show");

    private readonly ClearanceStore _clearances = new(() => Now);

    private static HttpResponseMessage Challenge() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("<html><head><title>Just a moment...</title><script src=\"/cdn-cgi/challenge-platform/x\"></script></head></html>"),
    };

    private static HttpResponseMessage Page403() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("<html><body>go away</body></html>"),
    };

    private static HttpResponseMessage Listing(string body = "<html><body>rows</body></html>") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private ChallengeAwareFetch Fetch(ScriptedHandler handler, IChallengeSolver? solver = null) =>
        new(new HttpClient(handler), _clearances, solver);

    // A page that came back fine must cost nothing: no solve, no cookie, no second request.
    [Fact]
    public async Task GetStringAsync_AnOrdinarySiteNeverTouchesTheSolver()
    {
        ScriptedHandler handler = new(Listing());
        RecordingSolver solver = new(new Clearance("cf_clearance=x", "Mozilla/5.0"));

        string body = await Fetch(handler, solver).GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        body.Should().Contain("rows");
        solver.Calls.Should().Be(0);
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task GetStringAsync_SolvesAChallengeAndFetchesAgainWithTheClearance()
    {
        ScriptedHandler handler = new(Challenge(), Listing());
        RecordingSolver solver = new(new Clearance("cf_clearance=abc", "Mozilla/5.0 (solved)"));

        string body = await Fetch(handler, solver).GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        body.Should().Contain("rows");
        solver.Calls.Should().Be(1);

        // Both, together: Cloudflare ties clearance to the agent that earned it, so the
        // cookie alone is challenged again for a reason nothing in the log would explain.
        handler.LastCookie.Should().Be("cf_clearance=abc");
        handler.LastUserAgent.Should().Be("Mozilla/5.0 (solved)");
    }

    // One solve buys the whole host. Solving per URL would mean a solve per search.
    [Fact]
    public async Task GetStringAsync_KeepsTheClearanceForTheNextPageOnThatHost()
    {
        await Fetch(new ScriptedHandler(Challenge(), Listing()), new RecordingSolver(new Clearance("cf=1", "UA")))
            .GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        ScriptedHandler second = new(Listing());
        RecordingSolver solver = new(new Clearance("cf=2", "UA"));

        await Fetch(second, solver).GetStringAsync(new Uri("https://gated.test/other"), "site-a", "search", CancellationToken.None);

        solver.Calls.Should().Be(0, "the host was already cleared");
        second.LastCookie.Should().Be("cf=1");
    }

    // Cloudflare invalidates clearance for reasons no client sees coming, so a challenge
    // while carrying a cookie means that cookie is spent - not that the site is broken.
    [Fact]
    public async Task GetStringAsync_ThrowsAwayClearanceThatWasChallengedAnyway()
    {
        _clearances.Keep(Page, new Clearance("stale=1", "UA"));

        ScriptedHandler handler = new(Challenge(), Listing());

        await Fetch(handler, new RecordingSolver(new Clearance("fresh=1", "UA")))
            .GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        _clearances.For(Page)!.Cookies.Should().Be("fresh=1");
    }

    // Looping would be the obvious mistake here, and it would look like a hung cycle.
    [Fact]
    public async Task GetStringAsync_GivesUpWhenAFreshSolveIsChallengedAgain()
    {
        ScriptedHandler handler = new(Challenge(), Challenge());

        Func<Task> fetch = () => Fetch(handler, new RecordingSolver(new Clearance("cf=1", "UA")))
            .GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        (await fetch.Should().ThrowAsync<IndexerException>())
            .Which.Message.Should().Contain("did not hold");

        handler.Requests.Should().Be(2, "two attempts, not a loop");
        _clearances.For(Page).Should().BeNull();
    }

    // The message has to name the cause. "HTTP 403" sends its reader to look at the site.
    [Fact]
    public async Task GetStringAsync_SaysWhatToDoWhenThereIsNoSolverAtAll()
    {
        Func<Task> fetch = () => Fetch(new ScriptedHandler(Challenge()))
            .GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        (await fetch.Should().ThrowAsync<IndexerException>())
            .Which.Message.Should().Contain("no solver");
    }

    // A site that is genuinely refusing is not a challenge, and must not be reported as one.
    [Fact]
    public async Task GetStringAsync_APlainForbiddenIsStillAnHttpError()
    {
        Func<Task> fetch = () => Fetch(new ScriptedHandler(Page403()), new RecordingSolver(null))
            .GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        (await fetch.Should().ThrowAsync<IndexerException>())
            .Which.Message.Should().Contain("HTTP 403");
    }

    [Fact]
    public async Task GetStringAsync_SaysSoWhenTheSolverCannotGetThrough()
    {
        Func<Task> fetch = () => Fetch(new ScriptedHandler(Challenge()), new RecordingSolver(null))
            .GetStringAsync(Page, "site-a", "search", CancellationToken.None);

        (await fetch.Should().ThrowAsync<IndexerException>())
            .Which.Message.Should().Contain("could not get past");
    }

    /// <summary>Answers each request with the next scripted response, and remembers what it was sent.</summary>
    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        public int Requests { get; private set; }
        public string? LastCookie { get; private set; }
        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            LastCookie = Header(request, "Cookie");
            LastUserAgent = Header(request, "User-Agent");

            return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]);
        }

        // Joined with a space, not "; ": HttpClient parses a user agent into its separate
        // product tokens, and rejoining those with a cookie separator invents a header
        // nothing ever sent.
        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(" ", values) : null;
    }

    private sealed class RecordingSolver(Clearance? clearance) : IChallengeSolver
    {
        public int Calls { get; private set; }

        public Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(clearance);
        }
    }
}
