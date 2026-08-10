// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

/// <summary>
/// The plugin's own solver: no container, no browser, just asking the way a browser asks.
/// </summary>
public class BrowserIdentitySolverTests
{
    private static readonly Uri Page = new("https://gated.test/search");

    private static BrowserIdentitySolver Solver(SpyHandler handler) => new(() => handler);

    [Fact]
    public async Task SolveAsync_AsksAsABrowserRatherThanAsAnHttpClient()
    {
        SpyHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>rows</html>"),
        });

        await Solver(handler).SolveAsync(Page, CancellationToken.None);

        handler.Header("User-Agent").Should().Contain("Chrome/");

        // Sent together because they are checked together: a browser's user agent with
        // none of a browser's other headers is a more obvious forgery than no agent.
        handler.Header("Accept").Should().Contain("text/html");
        handler.Header("Accept-Language").Should().NotBeNull();
        handler.Header("Sec-Fetch-Mode").Should().Be("navigate");
    }

    [Fact]
    public async Task SolveAsync_HandsBackTheCookieTheGateSetAndTheAgentThatEarnedIt()
    {
        HttpResponseMessage answered = new(HttpStatusCode.OK) { Content = new StringContent("<html>rows</html>") };
        answered.Headers.TryAddWithoutValidation("Set-Cookie", "cf_clearance=earned; Path=/; HttpOnly");

        Clearance? clearance = await Solver(new SpyHandler(answered)).SolveAsync(Page, CancellationToken.None);

        clearance.Should().NotBeNull();
        clearance!.Cookies.Should().Be("cf_clearance=earned");
        clearance.UserAgent.Should().Contain("Chrome/");
    }

    // A site that only wanted a plausible caller sets nothing. The identity is the
    // clearance, and refusing to report that would throw away a solve that worked.
    [Fact]
    public async Task SolveAsync_StillSucceedsWhenTheSiteSetsNoCookieAtAll()
    {
        Clearance? clearance = await Solver(new SpyHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>rows</html>"),
        })).SolveAsync(Page, CancellationToken.None);

        clearance.Should().NotBeNull();
        clearance!.Cookies.Should().BeEmpty();
    }

    // Cloudflare's scripted challenge wants a JavaScript engine, and no header work
    // substitutes. Handing back a cookie that did not work buys a second failure.
    [Fact]
    public async Task SolveAsync_ReportsFailureWhenTheChallengeIsStillThere()
    {
        SpyHandler handler = new(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html><title>Just a moment...</title></html>"),
        });

        (await Solver(handler).SolveAsync(Page, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SolveAsync_ReportsFailureRatherThanThrowingWhenTheSiteIsUnreachable()
    {
        (await Solver(new SpyHandler(new HttpRequestException("no route"))).SolveAsync(Page, CancellationToken.None))
            .Should().BeNull();
    }

    private sealed class SpyHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _failure;
        private HttpRequestMessage? _seen;

        public SpyHandler(HttpResponseMessage response) => _response = response;

        public SpyHandler(Exception failure) => _failure = failure;

        public string? Header(string name) =>
            _seen is not null && _seen.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? string.Join("; ", values)
                : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _seen = request;

            return _failure is not null ? Task.FromException<HttpResponseMessage>(_failure) : Task.FromResult(_response!);
        }
    }
}
