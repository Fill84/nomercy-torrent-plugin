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
    /// Markers a challenge page carries.
    /// </summary>
    /// <remarks>
    /// Consulted only after the status has already narrowed it down, and kept
    /// deliberately short. These are not pinned by a captured page yet — there
    /// is no capture of a challenge in <c>tests/fixtures/</c> — so they are the
    /// least a challenge can be identified by rather than everything one
    /// contains.
    /// </remarks>
    private static readonly string[] Markers =
    [
        "cf-browser-verification",
        "challenge-platform",
        "cf_chl_opt",
        "Just a moment...",
    ];

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
