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
            // Straight to the browser. Trying HTTP first on an address the
            // catalogue says is gated buys a guaranteed refusal every time.
            return await ThroughBrowser(address, ct, "This address is behind a challenge");
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
