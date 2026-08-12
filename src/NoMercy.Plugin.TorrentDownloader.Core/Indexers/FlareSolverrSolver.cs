// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// A challenge solved by something that can actually run the page.
///
/// <para>
/// <see cref="BrowserIdentitySolver"/> gets past the gates that only look at who is
/// asking, which is most of them. It cannot pass Cloudflare's scripted challenge, because
/// that one wants JavaScript executed and no amount of header work substitutes. Measured on
/// a real server: two of three configured sources answered <c>403</c> with
/// <c>Just a moment</c>, <c>cf_chl</c> and <c>Enable JavaScript</c> - and had never produced
/// a single release in weeks of searching, while the third produced thirty-nine.
/// </para>
///
/// <para>
/// FlareSolverr is a browser in a box that does run it. It is a separate service the owner
/// installs and points this at; nothing here starts it, and nothing here needs it. Left
/// unconfigured, this plugin behaves exactly as it did before - the browser identity solver
/// alone, and an honest failure on the sites that need more.
/// </para>
///
/// <para>
/// What comes back is a cookie jar and the user agent that earned it, which is precisely
/// what <see cref="Clearance"/> is. So this drops in behind the same interface the indexers
/// already call, and not one of them learns that anything changed.
/// </para>
/// </summary>
public sealed class FlareSolverrSolver(HttpClient http, Uri endpoint, TimeSpan? timeout = null) : IChallengeSolver
{
    /// <summary>
    /// How long FlareSolverr may spend on one page, in milliseconds.
    ///
    /// <para>
    /// A scripted challenge is several seconds of real browser work, so this is generous by
    /// the standards of an HTTP call and mean by the standards of a browser start-up. Sent
    /// to FlareSolverr rather than enforced here, so it gives up on its own terms and
    /// answers - a solver killed by our own timeout leaves a browser tab open in somebody
    /// else's container.
    /// </para>
    /// </summary>
    private int MaxTimeoutMilliseconds => (int)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds;

    public async Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                endpoint,
                new Request("request.get", url.ToString(), MaxTimeoutMilliseconds),
                ct);

            if (!response.IsSuccessStatusCode)
                return null;

            Answer? answer = await response.Content.ReadFromJsonAsync<Answer>(ct);

            if (answer?.Solution is not { } solution || !string.Equals(answer.Status, "ok", StringComparison.OrdinalIgnoreCase))
                return null;

            string cookies = string.Join(
                "; ",
                (solution.Cookies ?? [])
                    .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
                    .Select(cookie => $"{cookie.Name}={cookie.Value}"));

            // Both or neither. A cookie jar without the agent that earned it is refused on
            // the next request, because Cloudflare ties one to the other - and reporting
            // half a clearance as a solve costs a round trip to learn nothing.
            if (cookies.Length == 0 || string.IsNullOrWhiteSpace(solution.UserAgent))
                return null;

            return new Clearance(cookies, solution.UserAgent);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            // A solver that is not running, not reachable, or slower than its own deadline
            // is a solver that did not solve it. The caller turns that into one sentence
            // naming the host; an exception from here would name FlareSolverr instead, and
            // the owner would go looking at the wrong thing.
            return null;
        }
    }

    private sealed record Request(
        [property: JsonPropertyName("cmd")] string Cmd,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("maxTimeout")] int MaxTimeout);

    private sealed record Answer(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("solution")] Solution? Solution);

    private sealed record Solution(
        [property: JsonPropertyName("userAgent")] string? UserAgent,
        [property: JsonPropertyName("cookies")] IReadOnlyList<Cookie>? Cookies);

    private sealed record Cookie(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] string? Value);
}

/// <summary>
/// Several solvers, cheapest first, and the first clearance wins.
///
/// <para>
/// The order is the point. Sending the request as a browser costs one HTTP call and gets
/// past most sites; asking FlareSolverr costs a browser start-up in another process. Trying
/// the cheap one first means the sites that never needed a sidecar never pay for one, and
/// the owner who has not installed it is no worse off than before.
/// </para>
/// </summary>
public sealed class FirstSolverThatWorks(params IChallengeSolver[] solvers) : IChallengeSolver
{
    public async Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        foreach (IChallengeSolver solver in solvers)
        {
            if (await solver.SolveAsync(url, ct) is { } clearance)
                return clearance;
        }

        return null;
    }
}
