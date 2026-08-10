// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Collections.Concurrent;
using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// Telling a Cloudflare interstitial apart from a real answer.
///
/// <para>
/// This matters because the interstitial is not an error. It arrives as a page, sometimes
/// with a 200, and a parser handed one finds no rows and reports an empty site - which
/// reads exactly like a site that has nothing, and sends whoever is debugging it to look
/// at the wrong thing entirely.
/// </para>
///
/// <para>
/// Detected from the response rather than assumed per host, so a site that is not behind
/// Cloudflare never pays for any of this.
/// </para>
/// </summary>
public static class CloudflareChallenge
{
    /// <summary>
    /// Cloudflare's own marker, sent on every mitigated response. When it is there, there
    /// is nothing to guess about.
    /// </summary>
    private const string MitigatedHeader = "cf-mitigated";

    /// <summary>
    /// What the interstitial page says. Only consulted for the statuses Cloudflare uses to
    /// serve one, so an ordinary page that happens to contain these words is not mistaken
    /// for a challenge.
    /// </summary>
    private static readonly string[] Markers =
    [
        "just a moment",
        "__cf_chl",
        "cf-browser-verification",
        "challenge-platform",
    ];

    private static readonly HttpStatusCode[] Served =
    [
        HttpStatusCode.OK,
        HttpStatusCode.Forbidden,
        HttpStatusCode.ServiceUnavailable,
    ];

    public static bool IsChallenge(HttpResponseMessage response, string body)
    {
        if (response.Headers.Contains(MitigatedHeader))
            return true;

        if (!Served.Contains(response.StatusCode))
            return false;

        // Only the head of the page. The markers live in the document's own scripts, and
        // scanning a multi-megabyte listing for them is a cost every healthy fetch would
        // pay for the sake of the rare blocked one.
        string head = body.Length <= 4096 ? body : body[..4096];

        return Markers.Any(marker => head.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What a solver hands back: enough to repeat the request as the browser that passed.</summary>
public sealed record Clearance(string Cookies, string UserAgent)
{
    /// <summary>
    /// When to stop trusting it. Cloudflare clearance is short-lived, and a stale cookie
    /// produces another challenge rather than an error - so the expiry is a hint that
    /// saves a round trip, never the thing that decides.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Something that can get past a challenge for one URL.
///
/// <para>
/// Behind an interface because a gate is not one thing. Most of what sites put up is a
/// check on who is asking - headers, cookies, whether the caller follows redirects like a
/// browser - and that is answerable in this process. Cloudflare's scripted challenge is a
/// second layer and a harder one. Separating them means the cheap answer runs first and
/// the expensive one is a swap, not a rewrite.
/// </para>
/// </summary>
public interface IChallengeSolver
{
    /// <summary>Clearance for this URL, or null when it could not be had. Never throws for an unsolvable page.</summary>
    Task<Clearance?> SolveAsync(Uri url, CancellationToken ct);
}

/// <summary>
/// Clearance kept per host, because that is the scope Cloudflare issues it in.
///
/// <para>
/// Per host rather than per URL: one solve buys every page on that site until it is
/// refused. Held in memory only - a clearance cookie is a credential, and one written to
/// the plugin's plain configuration would outlive its usefulness by months.
/// </para>
/// </summary>
public sealed class ClearanceStore(Func<DateTimeOffset> now)
{
    private readonly ConcurrentDictionary<string, Clearance> _byHost = new(StringComparer.OrdinalIgnoreCase);

    public Clearance? For(Uri url) =>
        _byHost.TryGetValue(url.Host, out Clearance? held) && !Expired(held) ? held : null;

    public void Keep(Uri url, Clearance clearance) => _byHost[url.Host] = clearance;

    /// <summary>Called when a request carrying clearance was challenged anyway. The cookie is spent.</summary>
    public void Forget(Uri url) => _byHost.TryRemove(url.Host, out _);

    private bool Expired(Clearance clearance) =>
        clearance.ExpiresAt is DateTimeOffset expires && expires <= now();
}
