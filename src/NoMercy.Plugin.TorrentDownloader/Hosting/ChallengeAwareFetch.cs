using System.Net;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The fetch every stage uses: the grant, the gate, plain HTTP, and the browser
/// when there is no other way.
/// </summary>
/// <remarks>
/// The order is from docs/05-sources.md § Fetching, and each step of it is
/// there because doing it in another order was measured costing something: a
/// gated host tried over HTTP first is a guaranteed refusal before every fetch;
/// a host we have no permission for tried at all is a refusal that reads like
/// the site's.
/// </remarks>
public sealed class ChallengeAwareFetch(
    HttpClient http,
    HostGate gate,
    IPluginGrants grants,
    ClearanceStore clearances,
    IChallengeSolver? solver = null,
    IPageSource? pages = null) : IFetch
{
    /// <summary>
    /// The body of the last fetch, whatever came of it.
    /// </summary>
    /// <remarks>
    /// For the health tool, which reports the page a source returned so a
    /// broken reader can be repaired from it. <strong>Cleared by the caller</strong>
    /// between sources: 0.3.4's health check attributed one source's page to
    /// another because whoever held it never let go.
    /// </remarks>
    public string? LastBody { get; set; }

    public async Task<FetchResult> GetAsync(Uri address, bool gated, CancellationToken ct)
    {
        string host = address.Host;

        if (!await grants.HasAsync(PluginGrantKind.NetworkHost, host, ct))
        {
            // Not an attempt, and not the site's fault. The gate is told so it
            // does not treat our own missing permission as the host misbehaving.
            gate.NotPermitted(host);

            return FetchResult.Failed(FetchFailure.For(
                FetchOutcome.NotPermitted,
                address,
                $"The server has not granted access to {host}, so it was not asked."));
        }

        if (gated)
        {
            return await Gated(address, host, ct);
        }

        FetchResult first = await Plain(address, host, ct);

        if (first.Failure?.Outcome != FetchOutcome.Challenged)
        {
            return first;
        }

        // A challenge on an address nobody marked. Solve it once, then try
        // again — and if a challenge is still there after a fresh solve, this
        // is a site the plugin cannot read, which is a different sentence from
        // the site refusing us.
        if (solver is null)
        {
            return first;
        }

        Clearance? clearance = await solver.SolveAsync(address, ct);

        if (clearance is null)
        {
            return FetchResult.Failed(FetchFailure.For(
                FetchOutcome.Challenged,
                address,
                $"The challenge on {host} did not clear."));
        }

        clearances.Keep(host, clearance);

        FetchResult second = await Plain(address, host, ct);

        if (second.Failure?.Outcome != FetchOutcome.Challenged)
        {
            return second;
        }

        // Deliberately not a third go. A second challenge immediately after a
        // fresh solve is not bad luck to retry through.
        clearances.Spend(host);

        return FetchResult.Failed(FetchFailure.For(
            FetchOutcome.Challenged,
            address,
            $"{host} put up a second challenge straight after one was cleared, so this plugin cannot read it."));
    }

    /// <summary>
    /// A host the catalogue says puts up a challenge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The browser is for solving the challenge and nothing else.</strong>
    /// The owner's decision of 11 September 2026, after watching ten Chrome
    /// processes sit on their server. A clearance cookie travels on an ordinary
    /// request, so once one is in hand every page of that host is read over
    /// plain HTTP and no browser is needed at all.
    /// </para>
    /// <para>
    /// <strong>What this replaces.</strong> A gated address went straight to
    /// the browser, every time, and that path stores no clearance — so the
    /// browser was needed again for the very next page, and again for the one
    /// after that, for as long as the plugin ran.
    /// </para>
    /// <para>
    /// The browser is still the last resort, because some hosts clear without
    /// issuing a cookie: there is then nothing to replay, and the page can only
    /// come from the tab that cleared it.
    /// </para>
    /// </remarks>
    private async Task<FetchResult> Gated(Uri address, string host, CancellationToken ct)
    {
        if (clearances.For(host) is not null)
        {
            FetchResult kept = await Plain(address, host, ct);

            if (kept.Failure?.Outcome != FetchOutcome.Challenged)
            {
                return kept;
            }

            // It was good and is not any more. Spent rather than kept and
            // retried: a clearance that no longer clears is a clearance to
            // throw away.
            clearances.Spend(host);
        }

        if (solver is not null)
        {
            Clearance? earned = await solver.SolveAsync(address, ct);

            if (earned is not null)
            {
                clearances.Keep(host, earned);

                FetchResult after = await Plain(address, host, ct);

                if (after.Failure?.Outcome != FetchOutcome.Challenged)
                {
                    return after;
                }
            }
        }

        return await ThroughBrowser(address, ct, "This address is behind a challenge");
    }

    private async Task<FetchResult> ThroughBrowser(Uri address, CancellationToken ct, string why)
    {
        if (pages is null)
        {
            return FetchResult.Failed(FetchFailure.For(
                FetchOutcome.NoBrowser,
                address,
                // Named as the plugin's own gap, because it is one. The owner
                // can act on "this needs a browser" and cannot act on "the site
                // refused us".
                $"{why} and no browser is available, so it was not read."));
        }

        string? body = await pages.GetPageAsync(address, ct);

        LastBody = body;

        return body is null
            ? FetchResult.Failed(FetchFailure.For(
                FetchOutcome.Challenged,
                address,
                $"{why} and the browser could not get past it."))
            : FetchResult.Fetched(body);
    }

    private Task<FetchResult> Plain(Uri address, string host, CancellationToken ct)
    {
        return gate.RunAsync(host, async token => await Send(address, host, token), ct);
    }

    private async Task<FetchResult> Send(Uri address, string host, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);

            if (clearances.For(host) is Clearance clearance)
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"cf_clearance={clearance.Cookie}");
                // The user agent it was issued to. Sending the cookie under any
                // other is a refusal that reads like the site changing its mind.
                request.Headers.TryAddWithoutValidation("User-Agent", clearance.UserAgent);
            }

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            LastBody = body;

            if (CloudflareChallenge.IsChallenge(response, body))
            {
                // Whatever we were holding did not work, so it is gone. Keeping
                // it turns every later request into the same refusal.
                clearances.Spend(host);

                return FetchResult.Failed(FetchFailure.For(
                    FetchOutcome.Challenged,
                    address,
                    $"{host} answered with a challenge."));
            }

            if (IsRateLimited(response.StatusCode))
            {
                gate.Refused(host);

                return FetchResult.Failed(FetchFailure.For(
                    FetchOutcome.RateLimited,
                    address,
                    $"{host} answered {(int)response.StatusCode} and is being asked less often."));
            }

            if (!response.IsSuccessStatusCode)
            {
                clearances.Spend(host);

                return FetchResult.Failed(FetchFailure.For(
                    FetchOutcome.Refused,
                    address,
                    $"{host} answered {(int)response.StatusCode}."));
            }

            gate.Succeeded(host);

            return FetchResult.Fetched(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // A cancellation the caller asked for is not a fetch failure, so it
            // is let through; anything else is the host not answering.
            return FetchResult.Failed(FetchFailure.For(
                FetchOutcome.Unreachable,
                address,
                $"{host} did not answer: {exception.Message}"));
        }
    }

    private static bool IsRateLimited(HttpStatusCode status)
    {
        return status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or (HttpStatusCode)509;
    }
}
