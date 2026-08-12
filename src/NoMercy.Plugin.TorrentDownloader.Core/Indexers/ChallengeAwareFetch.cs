// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// Fetching a page that may be behind a Cloudflare gate.
///
/// <para>
/// The whole lifecycle is three steps and deliberately no more: send the request with
/// whatever clearance this host already has, and if the answer is a challenge, solve it
/// once, keep what came back, and send it again. A second challenge after a fresh solve is
/// a site this plugin cannot read, and saying so beats looping.
/// </para>
///
/// <para>
/// Clearance is spent on refusal rather than trusted until its expiry, because Cloudflare
/// invalidates cookies for reasons no client can see coming. The expiry is only there to
/// avoid an obviously pointless first attempt.
/// </para>
/// </summary>
public sealed class ChallengeAwareFetch(
    HttpClient http,
    ClearanceStore clearances,
    IChallengeSolver? solver = null
)
{
    public async Task<string> GetStringAsync(Uri url, string indexerName, string what, CancellationToken ct)
    {
        (bool challenged, string body) = await SendAsync(url, clearances.For(url), ct, indexerName, what);

        if (!challenged)
            return body;

        // Whatever we sent did not work, so it is not worth sending again.
        clearances.Forget(url);

        if (solver is null)
        {
            throw new IndexerException(
                $"{indexerName}: {url.Host} is behind a Cloudflare check and this indexer has no solver.");
        }

        // The page itself, when the solver has one. Replaying the request with its cookies
        // fails on sites that bind clearance to the TLS handshake - measured: cf_clearance
        // obtained, same URL over HttpClient, 403.
        if (solver is IPageSource source && await source.GetPageAsync(url, ct) is { } fetched)
            return fetched;

        Clearance? fresh = await solver.SolveAsync(url, ct);

        if (fresh is null)
            throw new IndexerException($"{indexerName}: could not get past the Cloudflare check on {url.Host}.");

        clearances.Keep(url, fresh);

        (bool stillChallenged, string second) = await SendAsync(url, fresh, ct, indexerName, what);

        if (!stillChallenged)
            return second;

        clearances.Forget(url);

        throw new IndexerException(
            $"{indexerName}: {url.Host} challenged the request again after a solve. Its clearance did not hold.");
    }

    /// <summary>
    /// One attempt. Returns whether it was challenged rather than throwing, because a
    /// challenge is not a failure yet - it is the normal first half of a successful fetch.
    /// </summary>
    private async Task<(bool Challenged, string Body)> SendAsync(
        Uri url,
        Clearance? clearance,
        CancellationToken ct,
        string indexerName,
        string what)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);

            if (clearance is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", clearance.Cookies);
                request.Headers.TryAddWithoutValidation("User-Agent", clearance.UserAgent);
            }

            using HttpResponseMessage response = await http.SendAsync(request, ct);

            string body = await response.Content.ReadAsStringAsync(ct);

            if (CloudflareChallenge.IsChallenge(response, body))
                return (true, body);

            // Checked after the challenge, not before: Cloudflare serves its interstitial
            // with a 403, and reporting that as a plain HTTP error would hide the one
            // thing that explains it.
            if (!response.IsSuccessStatusCode)
            {
                throw new IndexerException(
                    $"{indexerName}: {what} returned HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            return (false, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IndexerException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new IndexerException($"{indexerName}: {what} failed: {error.Message}", error);
        }
    }
}
