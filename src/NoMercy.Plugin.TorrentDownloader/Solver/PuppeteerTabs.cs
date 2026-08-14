using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// The real tabs, in the browser this plugin started.
/// </summary>
/// <remarks>
/// It <em>connects</em> to a browser rather than launching one. The driver
/// knows how to start Chrome and knows nothing about hidden desktops or X
/// displays, so letting it start one would put a window on the owner's screen —
/// the fault the whole of S2-03 exists to prevent. The browser comes up on its
/// stage first and this attaches to the port it was told to listen on.
/// </remarks>
public sealed class PuppeteerTabs(Browser browser, ILogger logger) : IBrowserTabs
{
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly Dictionary<string, IBrowserTab> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private IBrowser? _connected;

    public async Task<IBrowserTab?> ForAsync(string host, CancellationToken ct)
    {
        await _connecting.WaitAsync(ct);

        try
        {
            if (_tabs.TryGetValue(host, out IBrowserTab? existing))
            {
                return existing;
            }

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

            IBrowserTab tab = new PuppeteerTab(await _connected.NewPageAsync());
            _tabs[host] = tab;

            return tab;
        }
        finally
        {
            _connecting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IBrowserTab tab in _tabs.Values)
        {
            await tab.DisposeAsync();
        }

        _tabs.Clear();

        if (_connected is not null)
        {
            // Disconnected, not closed. The browser belongs to the plugin's own
            // lifetime and is shut down with it; closing it here would take it
            // away from anything still using it.
            _connected.Disconnect();
            _connected.Dispose();
            _connected = null;
        }

        _connecting.Dispose();
    }
}

/// <summary>One real tab.</summary>
internal sealed class PuppeteerTab(IPage page) : IBrowserTab
{
    public async Task GoToAsync(Uri url, CancellationToken ct)
    {
        await page.GoToAsync(url.ToString(), new NavigationOptions
        {
            // Not "networkidle": a challenge page keeps talking to its own
            // endpoint, so waiting for silence waits for the whole timeout even
            // when the page is ready to be looked at.
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
