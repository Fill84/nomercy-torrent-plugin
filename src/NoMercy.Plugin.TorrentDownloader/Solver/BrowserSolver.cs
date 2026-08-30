using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// Meets a managed challenge in a real browser, and hands back the body.
/// </summary>
/// <remarks>
/// All of the judgement lives here: how long to wait, when a navigation is the
/// page working rather than failing, when one reload is enough, and whether
/// what the document shows is the answer or a picture of it. The driver
/// underneath only does as it is told.
/// </remarks>
public sealed class BrowserSolver(
    IBrowserTabs tabs,
    ILogger logger,
    TimeProvider? time = null,
    TimeSpan? solveTimeout = null,
    TimeSpan? pollInterval = null) : IChallengeSolver, IPageSource, IInPagePost
{
    /// <summary>How long a challenge is given to clear.</summary>
    public static readonly TimeSpan DefaultSolveTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The cookie a cleared challenge leaves behind.</summary>
    public const string ClearanceCookie = "cf_clearance";

    /// <summary>
    /// What the driver says when the page navigated out from under it.
    /// </summary>
    /// <remarks>
    /// <strong>D2.</strong> It is the page doing exactly what a challenge page
    /// is supposed to do — reloading itself once it has been satisfied. 0.3.4
    /// logged it four times in one run as a source failure, far away from
    /// anything that could explain it.
    /// </remarks>
    public const string NavigatedAway = "Execution Context was destroyed";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _timeout = solveTimeout ?? DefaultSolveTimeout;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);

    /// <summary>
    /// One caller at a time per host, because one tab is one document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two navigations in a tab do not queue: they abort each other, and Chrome
    /// reports the loser as <c>net::ERR_ABORTED</c>, which reads exactly like a
    /// site refusing to load. Watched on the owner's own server on 22 August
    /// 2026 — the name resolver asks eight seasons at once and the host gate
    /// lets two through per host, so every gated name database failed in the
    /// same second, and the pool went so thin that two episodes of the owner's
    /// own Silo season had no name in it at all.
    /// </para>
    /// <para>
    /// It covers the waiting as well as the navigating. A caller polling for
    /// its challenge to clear still owns that document, and anyone arriving in
    /// the middle takes it away.
    /// </para>
    /// <para>
    /// Per host and not over the browser: there is a tab per host, and one lock
    /// across all of them would make every gated source in a cycle wait out
    /// every other one.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _oneAtATime =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        SemaphoreSlim turn = TurnOn(url.Host);

        await turn.WaitAsync(ct);

        try
        {
            return await SolvingAsync(url, ct);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<Clearance?> SolvingAsync(Uri url, CancellationToken ct)
    {
        // The tab is this method's, and it closes when the method ends however
        // it ends. A tab left open is a Chrome left running: they were kept one
        // per host for the life of the plugin, and sixteen chrome processes were
        // found on the owner's machine with the server already stopped.
        //
        // Nothing is lost by closing it. The clearance is a cookie, and it is
        // read out into the clearance store below before this returns.
        await using IBrowserTab? tab = await tabs.ForAsync(url.Host, ct);

        if (tab is null)
        {
            logger.LogWarning("No browser, so the challenge on {Host} was not attempted.", url.Host);

            return null;
        }

        if (!await Navigate(tab, url, ct))
        {
            return null;
        }

        if (await WaitForClear(tab, url, ct))
        {
            return await ClearanceFrom(tab, url.Host, ct);
        }

        // One reload, then done. A challenge that has not cleared in the time
        // allowed is rarely one more second away, and a loop of reloads is how
        // a site decides we are worth blocking properly.
        logger.LogDebug("Reloading {Host} once; its challenge has not cleared.", url.Host);
        await tab.ReloadAsync(ct);

        if (await WaitForClear(tab, url, ct))
        {
            return await ClearanceFrom(tab, url.Host, ct);
        }

        logger.LogWarning(
            "The challenge on {Host} did not clear after {Seconds} seconds and one reload, so it was not read.",
            url.Host,
            (int)_timeout.TotalSeconds * 2);

        return null;
    }

    public async Task<string?> GetPageAsync(Uri url, CancellationToken ct)
    {
        SemaphoreSlim turn = TurnOn(url.Host);

        await turn.WaitAsync(ct);

        try
        {
            return await ReadingAsync(url, ct);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<string?> ReadingAsync(Uri url, CancellationToken ct)
    {
        // Closed however this returns. The browser stays up between solves —
        // that is what keeps a gated source's clearance — and a tab does not:
        // a page is read on every name of every cycle, so one left open is a
        // tab per search for ever. Ninety Chrome processes were found on the
        // owner's machine with nothing running at all.
        await using IBrowserTab? tab = await tabs.ForAsync(url.Host, ct);

        if (tab is null)
        {
            return null;
        }

        if (!await Navigate(tab, url, ct))
        {
            // Chrome does not show a feed, it downloads one, and a navigation
            // to a download is aborted — so the address carrying the answer is
            // the one address the browser will not go to. Giving up here loses
            // the whole source, and the page reports the site as not answering
            // while the feed sits there for anyone who asks for it instead.
            //
            // The host is reachable; it is this document Chrome refuses to
            // render. So the tab goes to the site itself for the clearance the
            // feed cannot ask for, and the body is fetched from inside the
            // page, which is how every other non-HTML body already comes back.
            Uri site = new(url.GetLeftPart(UriPartial.Authority) + "/");

            if (!await Navigate(tab, site, ct) || !await WaitForClear(tab, site, ct))
            {
                return null;
            }

            return await tab.FetchInPageAsync(url, ct);
        }

        if (!await WaitForClear(tab, url, ct))
        {
            return null;
        }

        // The body, not a picture of it. Anything that is not HTML is being
        // shown by a viewer, and the viewer's markup is what a reader would
        // otherwise parse — which is how every JSON source came back empty.
        string contentType = await tab.ContentTypeAsync(ct);

        return IsHtml(contentType)
            ? await tab.ContentAsync(ct)
            : await tab.FetchInPageAsync(url, ct);
    }

    public async Task<string?> PostAsync(Uri url, string formBody, CancellationToken ct)
    {
        SemaphoreSlim turn = TurnOn(url.Host);

        await turn.WaitAsync(ct);

        try
        {
            return await PostingAsync(url, formBody, ct);
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<string?> PostingAsync(Uri url, string formBody, CancellationToken ct)
    {
        // And here: this is how a magnet is asked for on a site that publishes
        // none, which is once per release taken.
        await using IBrowserTab? tab = await tabs.ForAsync(url.Host, ct);

        if (tab is null)
        {
            // Null, not an attempt. A post sent from this process arrives
            // without the session that earned the right to ask and is refused,
            // and "this site needs a browser" is something the owner can act on
            // where "this site refused us" is not even true.
            logger.LogWarning("No browser, so nothing was posted to {Host}.", url.Host);

            return null;
        }

        return await tab.PostInPageAsync(url, formBody, ct);
    }

    /// <summary>This host's turn-taking, made once and kept.</summary>
    private SemaphoreSlim TurnOn(string host)
    {
        return _oneAtATime.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Goes to <paramref name="url"/>, and answers false rather than throwing
    /// when it will not load.
    /// </summary>
    /// <remarks>
    /// A navigation that times out is an ordinary outcome for a site behind a
    /// challenge — measured against TorrentBay, which simply never finished.
    /// The driver reports it by throwing, and an exception here would leave the
    /// stage above with no failure to report and nothing to skip: it would take
    /// down whatever asked. A site that did not answer is a site that did not
    /// answer.
    /// </remarks>
    private async Task<bool> Navigate(IBrowserTab tab, Uri url, CancellationToken ct)
    {
        try
        {
            await tab.GoToAsync(url, ct);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("{Host} did not load: {Reason}", url.Host, exception.Message);

            return false;
        }
    }

    /// <summary>
    /// Polls until the challenge is gone, or the time runs out.
    /// </summary>
    /// <remarks>
    /// A navigation mid-poll is caught and the poll carries on. That is the
    /// page clearing itself, which is the outcome being waited for — treating
    /// it as a failure gives up at the exact moment it worked.
    /// </remarks>
    private async Task<bool> WaitForClear(IBrowserTab tab, Uri url, CancellationToken ct)
    {
        DateTimeOffset giveUpAt = _time.GetUtcNow() + _timeout;

        while (true)
        {
            try
            {
                string body = await tab.ContentAsync(ct);

                // Not a challenge is not the same as ready. A challenge that has
                // just cleared navigates to the real page, and in between the tab
                // holds a document that is neither — measured against 1337x,
                // which answered a head full of stylesheet links and no body at
                // all.
                if (!CloudflareChallenge.IsChallengePage(body) && await tab.IsLoadedAsync(ct))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception.Message.Contains(NavigatedAway, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("{Host} navigated while being watched, which is what clearing looks like.", url.Host);
            }

            if (_time.GetUtcNow() >= giveUpAt)
            {
                return false;
            }

            await Task.Delay(_poll, _time, ct);
        }
    }

    private async Task<Clearance?> ClearanceFrom(IBrowserTab tab, string host, CancellationToken ct)
    {
        string? cookie = await tab.CookieAsync(ClearanceCookie, ct);

        if (cookie is null)
        {
            // Cleared without a cookie: nothing to replay over plain HTTP, but
            // the tab itself can still hand over pages. Saying so is better
            // than reporting a failure that did not happen.
            logger.LogDebug("{Host} cleared without a {Cookie}; its pages come from the browser.", host, ClearanceCookie);

            return null;
        }

        // The user agent it was issued to travels with it. Replaying the cookie
        // under any other is a refusal that reads like the site changing its
        // mind.
        return new(cookie, await tab.UserAgentAsync(ct));
    }

    private static bool IsHtml(string contentType)
    {
        return contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase);
    }
}
