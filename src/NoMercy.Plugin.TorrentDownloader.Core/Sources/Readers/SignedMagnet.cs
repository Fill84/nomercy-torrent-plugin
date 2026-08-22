using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// Asking a site for a torrent it will not print.
/// </summary>
/// <remarks>
/// <para>
/// TorrentBay carries no magnet and no hash anywhere — not on the listing, not
/// on the row's own page. Both hold a button and an id, and pressing it posts a
/// signed request to the site's own endpoint, which answers with the magnet.
/// Everything here is that request, written from the script the page loads.
/// </para>
/// <para>
/// It was deferred once, from <c>S2-06</c> to <c>S6-01</c>, and never written.
/// The cost was not that this site produced nothing: it was that this site
/// produced the <em>best</em> rows. It publishes honest seeder counts and sorts
/// by them, so its copy outranked every other site's, was chosen, was followed,
/// named no torrent, and the episode was reported as though nobody were serving
/// it. That happened to every episode of the owner's library on 22 August 2026.
/// </para>
/// </remarks>
public static class SignedMagnet
{
    /// <summary>The endpoint the page's own script posts to.</summary>
    private const string Path = "/ajax/getSearchMagnet.php";

    /// <summary>
    /// Where to post, on the host the row came from.
    /// </summary>
    /// <remarks>
    /// Built from the row's own address rather than written down, because the
    /// site moves between hosts and the clearance that lets this request
    /// through belongs to the host the page came from.
    /// </remarks>
    public static Uri EndpointOn(Uri page)
    {
        return new(new Uri(page.GetLeftPart(UriPartial.Authority)), Path);
    }

    /// <summary>
    /// The form body, signed for this moment.
    /// </summary>
    /// <param name="claim">The id and the two tokens, off the page the row was read from.</param>
    /// <param name="at">The moment, which is part of what is signed.</param>
    /// <remarks>
    /// <c>hash</c> and <c>name</c> go empty because the button carries neither:
    /// the page's own script reads both with a default of the empty string and
    /// posts them anyway, and a request shaped differently from the site's own
    /// is one nobody has seen the site accept.
    /// </remarks>
    public static string Body(SignedClaim claim, DateTimeOffset at)
    {
        long timestamp = at.ToUnixTimeSeconds();

        return string.Join(
            '&',
            $"torrent_id={Uri.EscapeDataString(claim.TorrentId)}",
            "hash=",
            "name=",
            $"timestamp={timestamp}",
            $"hmac={Signature(claim.TorrentId, timestamp, claim.PageToken)}",
            $"sessid={Uri.EscapeDataString(claim.SessionId)}");
    }

    /// <summary>
    /// The signature the site checks: SHA-256 over the id, the moment and the
    /// page's token, joined by bars.
    /// </summary>
    /// <remarks>
    /// Its script calls this an HMAC and it is not one — there is no key beyond
    /// the token in the message. Written the way the site computes it rather
    /// than the way the name suggests, because what matters is that the two
    /// agree.
    /// </remarks>
    public static string Signature(string torrentId, long timestamp, string pageToken)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{torrentId}|{timestamp}|{pageToken}"));

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// The magnet in what the endpoint answered, or null when it refused.
    /// </summary>
    /// <remarks>
    /// It answers <c>{"success":true,"url":"magnet:?…"}</c>, and on refusal a
    /// body with <c>success</c> false and a reason. A refusal is not a magnet
    /// and must not be read as one: the caller has another copy to try, and
    /// only knows to try it if this says no.
    /// </remarks>
    public static string? MagnetIn(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument answer = JsonDocument.Parse(body);

            if (answer.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!answer.RootElement.TryGetProperty("success", out JsonElement success)
                || success.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            return answer.RootElement.TryGetProperty("url", out JsonElement url)
                   && url.ValueKind == JsonValueKind.String
                   && url.GetString() is string address
                   && address.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
                ? address
                : null;
        }
        catch (JsonException)
        {
            // Not JSON at all, which is what a challenge page looks like from
            // here. Not a magnet either way.
            return null;
        }
    }
}
