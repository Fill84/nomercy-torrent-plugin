// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// The plugin's own way past a gate: ask again, properly.
///
/// <para>
/// Most of what a site puts in front of a scraper is not a puzzle, it is a look at who is
/// asking. A bare <see cref="HttpClient"/> announces itself as one - no user agent worth
/// the name, no accept headers, no language, no cookie jar, and it does not pick up the
/// cookies a first response hands out. Sending the same request the way a browser sends it
/// gets past that class of gate outright, and it is the class nearly every torrent site
/// uses.
/// </para>
///
/// <para>
/// This runs inside the plugin. No second container, no browser, nothing for the owner to
/// install - which is the whole point: a media server plugin that needs a sidecar to read
/// a web page is a plugin most people will never get working.
/// </para>
///
/// <para>
/// What it does not do is run JavaScript. Cloudflare's scripted challenge and Turnstile
/// both want a real engine, and no amount of header work substitutes. Those are reported
/// as unsolved rather than retried into the ground - see <see cref="ChallengeAwareFetch"/>,
/// which stops after one attempt for exactly that reason. Honest failure now, and the
/// interface it sits behind is what lets a stronger solver replace it later without
/// touching a single indexer.
/// </para>
/// </summary>
public sealed class BrowserIdentitySolver(Func<HttpMessageHandler>? handler = null) : IChallengeSolver
{
    /// <summary>
    /// One consistent identity, not a rotating pool.
    ///
    /// <para>
    /// A rotating agent is what a scraper looks like; a browser is the same browser every
    /// time. It is also what makes clearance reusable - Cloudflare ties a cookie to the
    /// agent that earned it, so an identity that changes between requests throws away its
    /// own clearance on the next one.
    /// </para>
    /// </summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/128.0.0.0 Safari/537.36";

    /// <summary>
    /// Sent together because they are checked together. A request with a browser's user
    /// agent and none of a browser's other headers is a more obvious forgery than one with
    /// no user agent at all.
    /// </summary>
    private static readonly (string Name, string Value)[] BrowserHeaders =
    [
        ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8"),
        ("Accept-Language", "en-GB,en;q=0.9"),
        ("Upgrade-Insecure-Requests", "1"),
        ("Sec-Fetch-Dest", "document"),
        ("Sec-Fetch-Mode", "navigate"),
        ("Sec-Fetch-Site", "none"),
        ("Sec-Fetch-User", "?1"),
    ];

    public async Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        CookieContainer jar = new();

        // A fresh client per solve, with its own jar. The jar is the working part: a gate
        // that sets a cookie and redirects is answered by picking the cookie up and
        // following, which is all a browser does and all this needs to do.
        using HttpMessageHandler transport = handler?.Invoke() ?? new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
        };

        using HttpClient client = new(transport, disposeHandler: false);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);

            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            foreach ((string name, string value) in BrowserHeaders)
                request.Headers.TryAddWithoutValidation(name, value);

            using HttpResponseMessage response = await client.SendAsync(request, ct);

            string body = await response.Content.ReadAsStringAsync(ct);

            // Still gated. Whatever this site wants, it is not something headers answer,
            // and handing back a cookie that did not work would only buy a second failure.
            if (CloudflareChallenge.IsChallenge(response, body))
                return null;

            if (!response.IsSuccessStatusCode)
                return null;

            string cookies = Jar(jar, url, response);

            // No cookie is not a failure. The identity itself was what the site wanted, so
            // repeating the request with this agent is the clearance.
            return new Clearance(cookies, UserAgent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Unreachable, refused, or answering something unparseable. All of them mean
            // the same thing here, and none of them should take down a cycle that has
            // other indexers left to try.
            return null;
        }
    }

    /// <summary>
    /// Every cookie the exchange produced, as one header.
    ///
    /// <para>
    /// Read from the jar rather than from the final response, because a gate typically
    /// sets its cookie on a redirect the jar followed - by the time the last response
    /// arrives, that <c>Set-Cookie</c> is several hops behind. The response is still
    /// consulted for the case where a custom handler was supplied and there is no jar to
    /// read, which is how the tests reach this.
    /// </para>
    /// </summary>
    private static string Jar(CookieContainer jar, Uri url, HttpResponseMessage response)
    {
        List<string> pairs =
        [
            .. jar.GetCookies(url).Select(cookie => $"{cookie.Name}={cookie.Value}"),
        ];

        if (pairs.Count == 0 && response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? set))
            pairs.AddRange(set.Select(header => header.Split(';', 2)[0].Trim()).Where(pair => pair.Length > 0));

        return string.Join("; ", pairs);
    }
}
