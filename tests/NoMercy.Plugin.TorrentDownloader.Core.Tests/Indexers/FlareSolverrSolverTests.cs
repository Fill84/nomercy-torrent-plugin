// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

/// <summary>
/// The solver for the gates headers cannot open.
///
/// <para>
/// Measured on a real server: two of three configured sources answered 403 with
/// "Just a moment", cf_chl and "Enable JavaScript", and had produced no release in weeks
/// while the third produced thirty-nine. That is not a parser fault; it is a door.
/// </para>
/// </summary>
public class FlareSolverrSolverTests
{
    private static readonly Uri Endpoint = new("http://localhost:8191/v1");
    private static readonly Uri Site = new("https://extranet.torrentbay.st/browse/?q=Silo");

    /// <summary>A solve, shaped the way FlareSolverr actually answers one.</summary>
    private const string Solved = """
    {
      "status": "ok",
      "message": "Challenge solved!",
      "solution": {
        "url": "https://extranet.torrentbay.st/browse/?q=Silo",
        "status": 200,
        "userAgent": "Mozilla/5.0 (X11; Linux x86_64) Chrome/128.0.0.0 Safari/537.36",
        "cookies": [
          { "name": "cf_clearance", "value": "abc123", "domain": ".torrentbay.st" },
          { "name": "__cf_bm", "value": "def456", "domain": ".torrentbay.st" }
        ],
        "response": "<html>the page</html>"
      }
    }
    """;

    private static FlareSolverrSolver Solver(StubHttpMessageHandler handler) =>
        new(handler.Client(), Endpoint);

    [Fact]
    public async Task SolveAsync_TurnsASolveIntoClearanceTheIndexersCanUse()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Solved);

        Clearance? clearance = await Solver(handler).SolveAsync(Site, CancellationToken.None);

        clearance.Should().NotBeNull();

        // Every cookie, in the form a Cookie header takes - the clearance one is worthless
        // without the bot-management one beside it.
        clearance!.Cookies.Should().Be("cf_clearance=abc123; __cf_bm=def456");
        clearance.UserAgent.Should().Be("Mozilla/5.0 (X11; Linux x86_64) Chrome/128.0.0.0 Safari/537.36");
    }

    [Fact]
    public async Task SolveAsync_AsksFlareSolverrForThePageItIsStuckOn()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Solved);

        await Solver(handler).SolveAsync(Site, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Should().Be(Endpoint);
        handler.Bodies.Should().ContainSingle().Which.Should().Contain("request.get").And.Contain(Site.ToString());
    }

    /// <summary>
    /// Cloudflare ties a clearance cookie to the agent that earned it, so half an answer is
    /// no answer - and reporting one as a solve costs a round trip to learn nothing.
    /// </summary>
    [Theory]
    [InlineData("""{ "status": "ok", "solution": { "userAgent": "Chrome", "cookies": [] } }""")]
    [InlineData("""{ "status": "ok", "solution": { "cookies": [ { "name": "cf_clearance", "value": "x" } ] } }""")]
    [InlineData("""{ "status": "error", "message": "Challenge not solved!" }""")]
    [InlineData("""{ "status": "ok" }""")]
    public async Task SolveAsync_AnswersNothingWhenTheSolveIsNotWhole(string body)
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(body);

        (await Solver(handler).SolveAsync(Site, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// A solver nobody installed is the ordinary case, not an error. It answers null and the
    /// caller says which host it could not pass - an exception from here would name
    /// FlareSolverr, and the owner would go looking at the wrong thing.
    /// </summary>
    [Fact]
    public async Task SolveAsync_AnswersNothingWhenFlareSolverrIsNotRunning()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused"));

        (await Solver(handler).SolveAsync(Site, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SolveAsync_AnswersNothingWhenFlareSolverrItselfFails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning("no", HttpStatusCode.InternalServerError);

        (await Solver(handler).SolveAsync(Site, CancellationToken.None)).Should().BeNull();
    }
}

/// <summary>Cheapest first, and the first clearance wins.</summary>
public class FirstSolverThatWorksTests
{
    private sealed class Answers(Clearance? clearance) : IChallengeSolver
    {
        public int Asked { get; private set; }

        public Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(clearance);
        }
    }

    [Fact]
    public async Task SolveAsync_NeverPaysForTheExpensiveOneWhenTheCheapOneWorks()
    {
        Answers browser = new(new Clearance("cf_clearance=x", "Chrome"));
        Answers flareSolverr = new(new Clearance("cf_clearance=y", "Chrome"));

        Clearance? clearance = await new FirstSolverThatWorks(browser, flareSolverr)
            .SolveAsync(new Uri("https://site.test/"), CancellationToken.None);

        clearance!.Cookies.Should().Be("cf_clearance=x");
        flareSolverr.Asked.Should().Be(0, "a site that never needed a sidecar should not start one");
    }

    [Fact]
    public async Task SolveAsync_FallsThroughToTheOneThatCanRunThePage()
    {
        Answers browser = new(null);
        Answers flareSolverr = new(new Clearance("cf_clearance=y", "Chrome"));

        Clearance? clearance = await new FirstSolverThatWorks(browser, flareSolverr)
            .SolveAsync(new Uri("https://site.test/"), CancellationToken.None);

        clearance!.Cookies.Should().Be("cf_clearance=y");
        browser.Asked.Should().Be(1);
    }

    [Fact]
    public async Task SolveAsync_AnswersNothingWhenNoneOfThemCan()
    {
        FirstSolverThatWorks solvers = new(new Answers(null), new Answers(null));

        (await solvers.SolveAsync(new Uri("https://site.test/"), CancellationToken.None)).Should().BeNull();
    }
}
