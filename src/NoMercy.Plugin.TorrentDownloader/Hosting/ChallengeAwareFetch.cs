using System.Net;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.PluginSdk.Abstractions;

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
    IPageSource? pages = null) : IFetch, ISessionPost
{
    /// <summary>
    /// A form posted the way a page is read: granted, through the host's gate, with the clearance.
    /// </summary>
    /// <remarks>
    /// The signed request TorrentBay names its torrents to — see <see cref="ISessionPost"/> for why it goes
    /// over HTTP. Sent as the page's own script sends it, and in the same client the listing was read with,
    /// so the session the page's tokens belong to travels with it. Null for anything that is not an answer:
    /// no grant, a challenge, a refusal, a host that did not answer.
    /// </remarks>
    public async Task<string?> PostAsync(Uri url, string formBody, CancellationToken ct)
    {
        string host = url.Host;

        if (!await grants.HasAsync(PluginGrantKind.NetworkHost, host, ct))
        {
            gate.NotPermitted(host);

            return null;
        }

        return await gate.RunAsync(host, async token => await Posting(url, host, formBody, token), ct);
    }

    private async Task<string?> Posting(Uri url, string host, string formBody, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url)
            {
                Content = new StringContent(formBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
            };

            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            if (clearances.For(host) is Clearance clearance)
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"cf_clearance={clearance.Cookie}");
                request.Headers.TryAddWithoutValidation("User-Agent", clearance.UserAgent);
            }

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (CloudflareChallenge.IsChallenge(response, body))
            {
                clearances.Spend(host);

                return null;
            }

            if (IsRateLimited(response.StatusCode))
            {
                gate.Refused(host);

                return null;
            }

            return response.IsSuccessStatusCode ? body : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

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

        FetchResult first = await Twice(address, host, ct);

        if (first.Failure?.Outcome != FetchOutcome.Challenged)
        {
            return first;
        }

        // A challenge on an address nobody marked, solved and tried again.
        if (solver is null)
        {
            return first;
        }

        return await SolvedAndTried(address, host, ct);
    }

    /// <summary>How many times a challenge is solved again for one question.</summary>
    /// <remarks>
    /// <c>docs/specs/run.md</c>, the owner's rule of 15 September 2026: a question to a site that stays
    /// behind a challenge is asked at most twice, each time after the challenge is solved again. It was once,
    /// and a site whose clearance simply took a second go was left unread.
    /// </remarks>
    public const int Solves = 2;

    /// <summary>
    /// The challenge solved and the question asked again — twice at most — or the reason it could not be.
    /// </summary>
    private async Task<FetchResult> SolvedAndTried(Uri address, string host, CancellationToken ct)
    {
        for (int solve = 0; solve < Solves; solve++)
        {
            Clearance? clearance = await solver!.SolveAsync(address, ct);

            if (clearance is null)
            {
                return FetchResult.Failed(FetchFailure.For(
                    FetchOutcome.Challenged,
                    address,
                    $"The challenge on {host} did not clear."));
            }

            clearances.Keep(host, clearance);

            FetchResult tried = await Twice(address, host, ct);

            if (tried.Failure?.Outcome != FetchOutcome.Challenged)
            {
                return tried;
            }
        }

        // Not a third go. A challenge still there after it was solved twice is a site this plugin cannot read
        // this run, which is a different sentence from the site refusing us.
        clearances.Spend(host);

        return FetchResult.Failed(FetchFailure.For(
            FetchOutcome.Challenged,
            address,
            $"{host} was still behind a challenge after it was solved twice, so this plugin could not read it."));
    }

    /// <summary>
    /// A plain request, and a second one when the host did not answer the first.
    /// </summary>
    /// <remarks>
    /// <c>run.md</c>: a question to a site that does not answer is asked at most twice. A host that answers
    /// — even with a refusal or a challenge — has answered, and is not asked again here.
    /// </remarks>
    private async Task<FetchResult> Twice(Uri address, string host, CancellationToken ct)
    {
        FetchResult first = await Plain(address, host, ct);

        return first.Failure?.Outcome == FetchOutcome.Unreachable
            ? await Plain(address, host, ct)
            : first;
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
            FetchResult kept = await Twice(address, host, ct);

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
            for (int solve = 0; solve < Solves; solve++)
            {
                Clearance? earned = await solver.SolveAsync(address, ct);

                if (earned is null)
                {
                    // Cleared without a cookie to replay: the page can only come from the tab that cleared it.
                    return await ThroughBrowser(address, ct, "This address is behind a challenge");
                }

                clearances.Keep(host, earned);

                FetchResult after = await Twice(address, host, ct);

                if (after.Failure?.Outcome != FetchOutcome.Challenged)
                {
                    return after;
                }
            }

            clearances.Spend(host);

            return FetchResult.Failed(FetchFailure.For(
                FetchOutcome.Challenged,
                address,
                $"{host} was still behind a challenge after it was solved twice, so this plugin could not read it."));
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
