using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Telling a managed challenge apart from a site that has simply said no.
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters because the two want opposite treatment: a challenge
/// is met in a browser and retried, while a refusal is reported and the source
/// left alone. Calling one the other means either never reading a gated site or
/// hammering one that has told us to stop.
/// </para>
/// <para>
/// It reads the response rather than the page wherever it can. A header is a
/// statement about the response; a marker in the body is a guess about markup
/// that whoever serves it may change tomorrow, so the body is only consulted
/// when the status already says something interesting.
/// </para>
/// </remarks>
public static class CloudflareChallenge
{
    /// <summary>
    /// The header a challenge announces itself with. It exists precisely so a
    /// client does not have to read the page to know.
    /// </summary>
    public const string MitigatedHeader = "cf-mitigated";

    /// <summary>Statuses a managed challenge is served with.</summary>
    private static readonly HashSet<HttpStatusCode> Statuses =
    [
        HttpStatusCode.Forbidden,
        HttpStatusCode.ServiceUnavailable,
    ];

    /// <summary>
    /// Markers a challenge page carries, and no other page does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned by two real captures of 1337x taken on 10 September 2026 — the
    /// interstitial, and the search page five seconds later with the clearance
    /// earned. The interstitial carries <c>cf_chl_opt</c> seven times and a
    /// "Just a moment" title; the page behind it carries neither.
    /// </para>
    /// <para>
    /// <strong><c>challenge-platform</c> is not here, and this is what it
    /// cost.</strong> Cloudflare leaves
    /// <c>/cdn-cgi/challenge-platform/scripts/jsd/main.js</c> on an ordinary
    /// successful response — it is the JavaScript-detections script, not a
    /// challenge. With it in this list, the page the solver had been waiting
    /// for was read as the interstitial it had just cleared: the poll never
    /// finished, the forty-five seconds ran out, and the fetch reported that
    /// the browser could not get past a challenge that was already gone. 1337x
    /// — priority 40, the second highest in the catalogue — and EZTV answered
    /// nothing at all for at least ten days that way, and it hid itself, because
    /// a host whose clearance is still valid is served the page without the
    /// script on it.
    /// </para>
    /// <para>
    /// <c>cf-browser-verification</c> stays: an older Cloudflare serves it, no
    /// page served today carries it, and it costs nothing to keep.
    /// </para>
    /// </remarks>
    private static readonly string[] Markers =
    [
        "cf-browser-verification",
        "cf_chl_opt",
        "Just a moment...",
    ];

    /// <summary>
    /// Whether a page the browser is looking at is still a challenge.
    /// </summary>
    /// <remarks>
    /// In a browser there is no response to read — the tab has one document and
    /// the question is whether it is the site or the interstitial. The same
    /// markers, so the two answers cannot disagree about what a challenge is.
    /// </remarks>
    public static bool IsChallengePage(string? body)
    {
        return !string.IsNullOrEmpty(body)
               && Markers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="response"/> is a challenge rather than a refusal.</summary>
    public static bool IsChallenge(HttpResponseMessage response, string? body)
    {
        // The header first: a statement rather than an inference, and one
        // Cloudflare added for exactly this.
        if (response.Headers.TryGetValues(MitigatedHeader, out IEnumerable<string>? mitigated)
            && mitigated.Any(value => value.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!Statuses.Contains(response.StatusCode) || string.IsNullOrEmpty(body))
        {
            return false;
        }

        return Markers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
