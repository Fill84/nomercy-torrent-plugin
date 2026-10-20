using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Solver;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;

namespace NoMercy.Plugin.TorrentDownloader.Tools.DeveloperBrowser;

/// <summary>
/// The solver's tabs, in a Chrome the developer tools start themselves.
/// </summary>
/// <remarks>
/// <para>
/// The plugin solves a challenge in the hidden browser the media server
/// provides. The tools in <c>tools/</c> run on a developer's machine with no
/// server to ask, so they need a browser of their own; this is it, behind the
/// same <see cref="IBrowserTabs"/> seam, so a capture goes through exactly the
/// solver the plugin ships.
/// </para>
/// <para>
/// Chrome is downloaded once into the folder the tool names and reused on
/// every run after. It is started with a visible window: a headless Chrome
/// does not pass a managed challenge, measured, and a window on a developer's
/// own screen is the one place a window is acceptable.
/// </para>
/// <para>
/// One tab per host, kept for the run. A clearance is issued per host and
/// lives in the browser's cookies, so a tab closed after every page would meet
/// the challenge again on the next address of the same site.
/// <see cref="CloseAllAsync"/> closes the pages and the browser together.
/// </para>
/// </remarks>
public sealed class DeveloperTabs(string folder, ILogger logger) : IBrowserTabs
{
    private readonly Dictionary<string, DeveloperTab> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _guard = new(1, 1);

    private IBrowser? _browser;

    public async Task<IBrowserTab?> ForAsync(string host, CancellationToken ct)
    {
        await _guard.WaitAsync(ct);

        try
        {
            if (_tabs.TryGetValue(host, out DeveloperTab? kept))
            {
                return kept;
            }

            _browser ??= await LaunchAsync();

            logger.LogDebug("Opening a tab for {Host}.", host);

            IPage page = await _browser.NewPageAsync();
            DeveloperTab tab = new(page);
            _tabs[host] = tab;

            return tab;
        }
        finally
        {
            _guard.Release();
        }
    }

    public async Task CloseAllAsync(CancellationToken ct)
    {
        DeveloperTab[] open;
        IBrowser? browser;

        await _guard.WaitAsync(ct);

        try
        {
            open = [.. _tabs.Values];
            _tabs.Clear();

            browser = _browser;
            _browser = null;
        }
        finally
        {
            _guard.Release();
        }

        foreach (DeveloperTab tab in open)
        {
            await tab.CloseAsync();
        }

        if (browser is not null)
        {
            // Closed, not disconnected: this started the process, so this
            // ends it. A tool that returned with Chrome still running would
            // leave a window on the developer's screen for nothing.
            await browser.CloseAsync();
            browser.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync(CancellationToken.None);

        _guard.Dispose();
    }

    /// <summary>Downloads Chrome into the folder if it is not there yet, and starts it.</summary>
    private async Task<IBrowser> LaunchAsync()
    {
        BrowserFetcher fetcher = new(new BrowserFetcherOptions
        {
            // Under "browser", where the deleted BrowserInstall put it: the
            // Chrome already in _capture is reused rather than fetched again.
            Path = Path.Combine(folder, "browser"),
            // Chrome, never the headless shell the fetcher also offers: the
            // shell does not pass a managed challenge, and one that is never
            // downloaded cannot be started by mistake.
            Browser = SupportedBrowser.Chrome,
        });

        InstalledBrowser? installed = fetcher.GetInstalledBrowsers()
            .FirstOrDefault(candidate => candidate.Browser == SupportedBrowser.Chrome);

        if (installed is null)
        {
            logger.LogInformation("Downloading Chrome into {Folder}. This happens once.", folder);

            installed = await fetcher.DownloadAsync();
        }

        return await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = installed.GetExecutablePath(),
            // Visible. Headless Chrome does not pass a managed challenge,
            // measured; a window is the price, and a developer's own desktop
            // is the one place it costs nothing.
            Headless = false,

            // Without the automation flag Chrome sets by default: it announces
            // navigator.webdriver, which is one of the things a managed
            // challenge looks at, and the plugin's own browser never set it.
            IgnoredDefaultArgs = ["--enable-automation"],
        });
    }
}

/// <summary>One real tab, kept for the run by whoever handed it out.</summary>
internal sealed class DeveloperTab(IPage page) : IBrowserTab
{
    public async Task GoToAsync(Uri url, CancellationToken ct)
    {
        await page.GoToAsync(url.ToString(), new NavigationOptions
        {
            // DOMContentLoaded, and the solver's poll decides the rest.
            // Neither of the other two works on a real indexer: "load" waits
            // on adverts that never arrive, and "networkidle" waits out the
            // whole timeout because a challenge page keeps talking to its own
            // endpoint.
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
        // fires. What distinguishes the half-navigated document from the site
        // is that the site has a body with something in it.
        return page.EvaluateExpressionAsync<bool>(
            "document.readyState !== 'loading' && !!document.body && document.body.children.length > 0");
    }

    public Task<string> FetchInPageAsync(Uri url, CancellationToken ct)
    {
        // Inside the page, so it carries the session that was cleared and
        // comes back as the body rather than as Chrome's viewer showing it.
        return page.EvaluateFunctionAsync<string>(
            "async address => { const answer = await fetch(address, { credentials: 'include' }); return await answer.text(); }",
            url.ToString());
    }

    public async Task<string?> CookieAsync(string name, CancellationToken ct)
    {
        CookieParam[] cookies = await page.GetCookiesAsync();

        return cookies.FirstOrDefault(cookie => cookie.Name == name)?.Value;
    }

    public Task<string> UserAgentAsync(CancellationToken ct)
    {
        // Asked of the page rather than of the browser: it is the one the
        // clearance was issued to.
        return page.EvaluateExpressionAsync<string>("navigator.userAgent");
    }

    /// <summary>The run is over; the page goes.</summary>
    internal async Task CloseAsync()
    {
        await page.CloseAsync();

        page.Dispose();
    }

    /// <remarks>
    /// Nothing is closed here. The solver disposes a tab when it has read its
    /// page, but the tab is kept per host for the whole run so the clearance
    /// it earned is still there for the next address of the same site;
    /// <see cref="DeveloperTabs.CloseAllAsync"/> is what closes it.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
