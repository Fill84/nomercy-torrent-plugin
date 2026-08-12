// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using PuppeteerSharp;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// A real browser, driven from this process, doing what FlareSolverr's container does.
///
/// <para>
/// FlareSolverr is Node plus Puppeteer plus a set of stealth patches. None of that is
/// Node's doing: PuppeteerSharp is a port of the same library, the patches are page scripts
/// and launch flags, and both speak the same DevTools protocol to the same Chromium. So the
/// same job is done here, without asking the owner to run a second container first.
/// </para>
///
/// <para>
/// Chromium runs as its own process, not inside this one. That is deliberate and it is the
/// reason WebView2 was not used: a browser engine sharing an address space with the encoder
/// means a page can take the media server down with it. It also means no window, no message
/// pump and no desktop session, which is what lets this work at all - the server runs as a
/// service in session 0, where WebView2 does not.
/// </para>
///
/// <para>
/// Off unless the owner turns it on. Driving a browser costs a few hundred megabytes and
/// several seconds per solve, and most sites never need it - the browser identity solver
/// clears them for the price of one request.
/// </para>
/// </summary>
public sealed class HeadlessBrowserSolver(
    ILogger logger,
    Func<string?>? findBrowser = null,
    TimeSpan? patience = null) : IChallengeSolver, IAsyncDisposable
{
    /// <summary>
    /// How long to let a challenge run before giving up.
    ///
    /// <para>
    /// Cloudflare's interstitial is a few seconds of real work and then a redirect. Beyond
    /// this it is not slow, it is not passing - and a solver that waits forever holds a
    /// search cycle open behind it.
    /// </para>
    /// </summary>
    private TimeSpan Patience => patience ?? TimeSpan.FromSeconds(45);

    /// <summary>
    /// One browser, kept between solves.
    ///
    /// <para>
    /// Chromium takes a second or two to start and a fair amount of memory to hold, and a
    /// solve is a page in an existing browser. Starting one per challenge would make the
    /// first search of an evening slower than the searches it is meant to enable.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowser? _browser;

    /// <summary>
    /// What the page is told about itself before any of the site's own script runs.
    ///
    /// <para>
    /// This is the whole of the arms race in one string. Headless Chromium answers a handful
    /// of questions differently from a real one - it admits to being automated, reports no
    /// plugins, no languages, and a software renderer - and a managed challenge asks exactly
    /// those. Each line here is one of them, and they are the same evasions
    /// puppeteer-extra-plugin-stealth applies, which is what FlareSolverr runs.
    /// </para>
    ///
    /// <para>
    /// Injected via evaluate-on-new-document, so it lands before the page's first script
    /// rather than after - patching afterwards is patching something that has already been
    /// read.
    /// </para>
    /// </summary>
    private const string Evasions =
        """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

        window.chrome = window.chrome || { runtime: {}, loadTimes: () => {}, csi: () => {} };

        Object.defineProperty(navigator, 'plugins', {
            get: () => [1, 2, 3, 4, 5].map(index => ({ name: `Plugin ${index}`, filename: `plugin${index}.dll` })),
        });

        Object.defineProperty(navigator, 'languages', { get: () => ['en-GB', 'en'] });

        const query = navigator.permissions.query.bind(navigator.permissions);
        navigator.permissions.query = parameters =>
            parameters.name === 'notifications'
                ? Promise.resolve({ state: Notification.permission })
                : query(parameters);

        const getParameter = WebGLRenderingContext.prototype.getParameter;
        WebGLRenderingContext.prototype.getParameter = function (parameter) {
            if (parameter === 37445) return 'Intel Inc.';
            if (parameter === 37446) return 'Intel Iris OpenGL Engine';
            return getParameter.call(this, parameter);
        };
        """;

    public async Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        try
        {
            IBrowser browser = await BrowserAsync(ct);

            await using IPage page = await browser.NewPageAsync();
            await page.EvaluateExpressionOnNewDocumentAsync(Evasions);

            await page.GoToAsync(url.ToString(), new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = (int)Patience.TotalMilliseconds,
            });

            if (!await ClearedAsync(page, ct))
            {
                logger.LogWarning(
                    "Torrent Downloader drove a browser at {Host} and the challenge was still there after {Seconds}s.",
                    url.Host,
                    (int)Patience.TotalSeconds);

                return null;
            }

            CookieParam[] jar = await page.GetCookiesAsync();

            string cookies = string.Join("; ", jar.Select(cookie => $"{cookie.Name}={cookie.Value}"));
            string agent = await browser.GetUserAgentAsync();

            // Both or neither: Cloudflare ties the clearance to the agent that earned it, so
            // a jar without its agent is refused on the very next request.
            return cookies.Length > 0 && agent.Length > 0 ? new Clearance(cookies, agent) : null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // A browser that will not start, will not navigate, or was killed underneath us
            // is a challenge that was not solved. The caller names the host it could not
            // pass; naming Chromium here sends the owner to the wrong problem.
            logger.LogWarning(failure, "Torrent Downloader could not drive a browser at {Host}.", url.Host);

            return null;
        }
    }

    /// <summary>
    /// Whether the interstitial is gone.
    ///
    /// <para>
    /// Polled rather than waited on with a selector, because what replaces the challenge is
    /// the site's own page and this has no idea what that looks like. What it does know is
    /// the challenge: Cloudflare's says "Just a moment" and carries a challenge form. When
    /// neither is there any more, whatever is on screen is the thing we asked for.
    /// </para>
    /// </summary>
    private async Task<bool> ClearedAsync(IPage page, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            string title = await page.GetTitleAsync() ?? string.Empty;
            string content = await page.GetContentAsync() ?? string.Empty;

            bool challenged =
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                || content.Contains("cf_chl", StringComparison.OrdinalIgnoreCase);

            if (!challenged)
                return true;

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return false;
    }

    private async Task<IBrowser> BrowserAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            if (_browser is { IsClosed: false })
                return _browser;

            LaunchOptions options = new()
            {
                Headless = true,

                // --no-sandbox because this runs as a service, and Chromium's sandbox wants
                // a user session it does not have there. The rest are the flags that stop a
                // headless browser announcing itself as one.
                Args =
                [
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-dev-shm-usage",
                    "--no-first-run",
                    "--no-default-browser-check",
                ],
            };

            if (findBrowser?.Invoke() is { Length: > 0 } executable)
            {
                options.ExecutablePath = executable;
                logger.LogInformation("Torrent Downloader is using the browser already on this machine: {Path}.", executable);
            }
            else
            {
                // Only when the machine has none. A few hundred megabytes is a real cost and
                // it is paid once, on the first challenge, rather than at install.
                logger.LogInformation("Torrent Downloader found no browser on this machine and is downloading one. This happens once.");

                await new BrowserFetcher().DownloadAsync();
            }

            return _browser = await Puppeteer.LaunchAsync(options);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();

        _gate.Dispose();
    }
}
