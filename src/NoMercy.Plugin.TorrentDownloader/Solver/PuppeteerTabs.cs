using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// The real tabs, in the browser this plugin started.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The browser stays up between solves.</strong> It was stopped when
/// its last tab closed, so that a server which is killed could not leave one
/// behind — but a job object does that now, and the teardown had a cost nobody
/// had measured: a fresh Chrome carries none of the clearance the last one
/// earned, so every gated source met the challenge again from cold. Asked one
/// after another on 26 August 2026, TorrentBay cleared while 1337x and EZTV did
/// not — "the browser could not get past it" — and the same address, asked with
/// a browser that had been left open, answered a full page of rows.
/// </para>
/// <para>
/// It <em>connects</em> to a browser rather than launching one. The driver
/// knows how to start Chrome and knows nothing about hidden desktops or X
/// displays, so letting it start one would put a window on the owner's screen —
/// the fault the whole of S2-03 exists to prevent. The browser comes up on its
/// stage first and this attaches to the port it was told to listen on.
/// </para>
/// </remarks>
public sealed class PuppeteerTabs(Browser browser, ILogger logger) : IBrowserTabs
{
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private IBrowser? _connected;

    public async Task<IBrowserTab?> ForAsync(string host, CancellationToken ct)
    {
        await _connecting.WaitAsync(ct);

        try
        {
            IBrowserProcess? process = await browser.StartAsync(ct);

            if (process is null)
            {
                // No stage to hide it on. Not an error here: the caller says so
                // in words the owner can act on.
                return null;
            }

            _connected ??= await Puppeteer.ConnectAsync(new()
            {
                BrowserURL = $"http://127.0.0.1:{process.Port}",
            });

            logger.LogDebug("Opening a tab for {Host}.", host);

            return new PuppeteerTab(await _connected.NewPageAsync());
        }
        finally
        {
            _connecting.Release();
        }
    }

    /// <remarks>
    /// No tabs are closed here. A tab belongs to the task that opened it and is
    /// closed by it; one still open while this runs is a solve still in flight,
    /// and closing it from underneath would fail that solve rather than tidy it.
    /// The browser is taken down by the chain, immediately after this.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_connected is not null)
        {
            // Disconnected, not closed. Closing from the driver would take the
            // browser down out of order — the stage is this plugin's to close,
            // and after the window that sits on it.
            _connected.Disconnect();
            _connected.Dispose();
            _connected = null;
        }

        _connecting.Dispose();

        return ValueTask.CompletedTask;
    }
}

/// <summary>One real tab.</summary>
internal sealed class PuppeteerTab(IPage page) : IBrowserTab
{
    public async Task GoToAsync(Uri url, CancellationToken ct)
    {
        await page.GoToAsync(url.ToString(), new NavigationOptions
        {
            // DOMContentLoaded, and the poll decides the rest. Neither of the
            // other two works on a real indexer: "load" waits on adverts that
            // never arrive, and "networkidle" waits out the whole timeout
            // because a challenge page keeps talking to its own endpoint.
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
        });
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        await page.ReloadAsync(new ReloadOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
        });
    }

    public Task<string> ContentAsync(CancellationToken ct)
    {
        return page.GetContentAsync();
    }

    public Task<string> ContentTypeAsync(CancellationToken ct)
    {
        return page.EvaluateExpressionAsync<string>("document.contentType ?? ''");
    }

    public Task<bool> IsLoadedAsync(CancellationToken ct)
    {
        // Not readyState 'complete'. Measured against 1337x: its page is full
        // of third-party requests that never finish, so the load event never
        // fires and a wait for it times out on a page that has been readable
        // for forty seconds. What actually distinguishes the half-navigated
        // document from the site is that the site has a body with something in
        // it.
        return page.EvaluateExpressionAsync<bool>(
            "document.readyState !== 'loading' && !!document.body && document.body.children.length > 0");
    }

    public Task<string> FetchInPageAsync(Uri url, CancellationToken ct)
    {
        // Inside the page, so it carries the session that was cleared and comes
        // back as the body rather than as the viewer showing it.
        return page.EvaluateFunctionAsync<string>(
            "async address => { const answer = await fetch(address, { credentials: 'include' }); return await answer.text(); }",
            url.ToString());
    }

    public Task<string> PostInPageAsync(Uri url, string formBody, CancellationToken ct)
    {
        return page.EvaluateFunctionAsync<string>(
            """
            async (address, body) => {
                const answer = await fetch(address, {
                    method: 'POST',
                    credentials: 'include',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body,
                });
                return await answer.text();
            }
            """,
            url.ToString(),
            formBody);
    }

    public async Task<string?> CookieAsync(string name, CancellationToken ct)
    {
        CookieParam[] cookies = await page.GetCookiesAsync();

        return cookies.FirstOrDefault(cookie => cookie.Name == name)?.Value;
    }

    public Task<string> UserAgentAsync(CancellationToken ct)
    {
        // Asked of the page rather than of the browser: it is the one the
        // clearance was issued to, and a page may have been given its own.
        return page.EvaluateExpressionAsync<string>("navigator.userAgent");
    }

    public async ValueTask DisposeAsync()
    {
        await page.CloseAsync();

        page.Dispose();
    }
}

/// <summary>
/// The driver's own downloader, behind this plugin's seam.
/// </summary>
/// <remarks>
/// The headless shell it also offers is never asked for: headless Chrome does
/// not pass a managed challenge, and a shell that is never started is one that
/// cannot be started by mistake.
/// </remarks>
public sealed class PuppeteerBrowserDownloader : IBrowserDownloader
{
    public async Task<string> DownloadAsync(string folder, CancellationToken ct)
    {
        BrowserFetcher fetcher = new(new BrowserFetcherOptions
        {
            Path = folder,
            Browser = SupportedBrowser.Chrome,
        });

        InstalledBrowser installed = await fetcher.DownloadAsync();

        return installed.GetExecutablePath();
    }
}
