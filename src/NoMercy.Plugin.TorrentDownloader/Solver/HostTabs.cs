using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// The solver's tabs, over the hidden browser the host provides (<c>IPluginContext.Browser</c>).
/// </summary>
/// <remarks>
/// <para>
/// The browser is the host's (media-server #601): it finds a Chrome, hides its window, starts it on the
/// first page and stops it when the last page is disposed. What is decided here is only how long a page
/// lives. One per host, kept for the run (<c>docs/07-solver.md</c> § The browser): a clearance is issued per
/// host and lives in the browser's cookies, which the host throws away with its profile when Chrome stops —
/// so a page closed after every solve would be a challenge solved again on every address of the host.
/// <see cref="CloseAllAsync"/>, at the run's end, disposes them all and lets the host stop Chrome.
/// </para>
/// <para>
/// A page is opened at a tab's first navigation, never at the ask. The host's <c>OpenAsync</c> wants an
/// address and starts a Chrome, and the solver asks for a tab before it knows whether it will go anywhere.
/// </para>
/// <para>
/// Where there is no browser — the host wired none, or it has no Chrome or no <c>Xvfb</c> — a tab is refused
/// and <see cref="WhyNot"/> says why in the host's own words. The owner's approval of <c>browser.headless</c>
/// is checked by the host when a page is <em>opened</em>, not when it says whether it has a browser, so that
/// refusal arrives at a tab's first navigation; it is kept for the rest of the run and forgotten at its end,
/// so an approval given in the meantime takes effect on the next run without a restart. Said once at Warning
/// and at Debug after that: every search that met a challenge used to log it again (GitHub issue 6).
/// </para>
/// </remarks>
public sealed class HostTabs(Func<IPluginBrowser> browser, ILogger logger) : IBrowserTabs
{
    /// <summary>What the host's answer is turned into for the owner, after the host's own reason.</summary>
    public const string Consequence = "so sources behind a challenge are off.";

    private readonly Dictionary<string, HostTab> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _guard = new(1, 1);
    private int _warned;

    /// <summary>The host's refusal of the first page this run asked for, until the run ends.</summary>
    private string? _refused;

    /// <summary>Why no challenge can be solved here, or null while the host has a browser to offer.</summary>
    /// <remarks>
    /// Asked of the host every time, never kept here: a Chrome the owner installs takes effect without a
    /// restart (the host does keep one failed start of its own until the plugin reloads). Reading it starts
    /// nothing. A refusal met at a page is the one thing remembered, and only for the run.
    /// </remarks>
    public string? WhyNot => Volatile.Read(ref _refused) is string refused ? Sentence(refused) : Off(browser);

    /// <summary>The same sentence for anybody holding only the context, such as the Sources page, which starts no browser by asking.</summary>
    public static string? Off(Func<IPluginBrowser> browser)
    {
        return Reason(browser) is string reason ? Sentence(reason) : null;
    }

    /// <summary>The host's reason, its full stop given up for what it costs the owner.</summary>
    private static string Sentence(string reason)
    {
        return $"{reason.TrimEnd('.')}, {Consequence}";
    }

    public async Task<IBrowserTab?> ForAsync(string host, CancellationToken ct)
    {
        if ((Volatile.Read(ref _refused) ?? Reason(browser)) is string reason)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.LogWarning("{Reason} So sources behind a challenge are off.", reason);
            }
            else
            {
                logger.LogDebug("No browser for {Host}: {Reason}", host, reason);
            }

            return null;
        }

        await _guard.WaitAsync(ct);

        try
        {
            if (!_tabs.TryGetValue(host, out HostTab? tab))
            {
                tab = new(browser, logger, Refused);
                _tabs[host] = tab;
            }

            return tab;
        }
        finally
        {
            _guard.Release();
        }
    }

    public async Task CloseAllAsync(CancellationToken ct)
    {
        HostTab[] open;

        await _guard.WaitAsync(ct);

        try
        {
            open = [.. _tabs.Values];
            _tabs.Clear();

            // The run is over, and so is what it was refused: the owner may have
            // approved the capability since, and the next run finds out.
            Volatile.Write(ref _refused, null);
        }
        finally
        {
            _guard.Release();
        }

        foreach (HostTab tab in open)
        {
            try
            {
                await tab.CloseAsync();
            }
            catch (Exception gone) when (gone is not OperationCanceledException)
            {
                // A page whose Chrome died throws on closing. The host has
                // already let go of it, and the pages after it must still be
                // released — the host stops Chrome only once the last one is —
                // and a run's end must not end in an exception.
                logger.LogDebug("A page could not be closed: {Reason}", gone.Message);
            }
        }
    }

    /// <summary>What a tab met when the host refused to open its page.</summary>
    private void Refused(string why)
    {
        Volatile.Write(ref _refused, why);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync(CancellationToken.None);

        _guard.Dispose();
    }

    /// <summary>The host's reason for having no browser, or null when it has one.</summary>
    private static string? Reason(Func<IPluginBrowser> browser)
    {
        try
        {
            IPluginBrowser hidden = browser();

            return hidden.IsAvailable ? null : hidden.UnavailableReason;
        }
        catch (PluginRefusedException refused)
        {
            // The facade itself: a host with no browser wired, or a capability
            // the owner has not approved. The refusal says which.
            return refused.Message;
        }
    }
}

/// <summary>One host's page in the host's browser, opened at its first navigation and kept for the run.</summary>
internal sealed class HostTab(Func<IPluginBrowser> browser, ILogger logger, Action<string> refused) : IBrowserTab
{
    private bool _closed;

    /// <summary>
    /// How long one navigation may take before the host gives up on it.
    /// </summary>
    /// <remarks>
    /// The host waits for <c>DOMContentLoaded</c>, and the solver's own poll does the rest (<c>BrowserSolver
    /// .WaitForClear</c>); a challenge page reaches that event in a moment. The host's default is thirty
    /// seconds, and nothing measured asks for another figure.
    /// </remarks>
    private static readonly PluginBrowserOptions Options = new();

    private IPluginBrowserPage? _page;

    public async Task GoToAsync(Uri url, CancellationToken ct)
    {
        // A tab from before the run's end opens nothing after it: that page
        // would belong to no run, nothing would ever close it, and Chrome would
        // stay up until the plugin unloads. The solver reads this as a page that
        // did not load.
        ObjectDisposedException.ThrowIf(_closed, this);

        if (_page is not null)
        {
            await _page.GotoAsync(url, ct);

            return;
        }

        logger.LogDebug("Opening {Host} in the host's browser.", url.Host);

        try
        {
            _page = await browser().OpenAsync(url, Options, ct);
        }
        catch (PluginRefusedException denied)
        {
            // The owner has not approved browser.headless, or the host will not
            // let a page go to this address. Told to the tabs, so the rest of
            // the run is refused without asking and the pages can say why;
            // thrown on, so this navigation is reported as not having loaded.
            refused(denied.Message);

            throw;
        }
        catch (OperationCanceledException slow) when (!ct.IsCancellationRequested)
        {
            // The host's own start timeout, not the caller's cancellation. Left
            // as it was it ended the whole cycle as though Stop had been
            // pressed; as a timeout it is one page that did not load.
            throw new TimeoutException($"The host's browser did not open a page for {url.Host}: {slow.Message}", slow);
        }
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        return _page?.ReloadAsync(ct) ?? Task.CompletedTask;
    }

    public Task<string> ContentAsync(CancellationToken ct)
    {
        return _page?.ContentAsync(ct) ?? Task.FromResult(string.Empty);
    }

    public Task<string> ContentTypeAsync(CancellationToken ct)
    {
        return _page?.ContentTypeAsync(ct) ?? Task.FromResult(string.Empty);
    }

    public Task<bool> IsLoadedAsync(CancellationToken ct)
    {
        return _page?.IsReadableAsync(ct) ?? Task.FromResult(false);
    }

    public Task<string> FetchInPageAsync(Uri url, CancellationToken ct)
    {
        return _page?.FetchTextAsync(url, ct) ?? Task.FromResult(string.Empty);
    }

    public async Task<string?> CookieAsync(string name, CancellationToken ct)
    {
        if (_page is null)
        {
            return null;
        }

        IReadOnlyList<PluginBrowserCookie> cookies = await _page.CookiesAsync(ct);

        return cookies.FirstOrDefault(cookie => cookie.Name == name)?.Value;
    }

    public Task<string> UserAgentAsync(CancellationToken ct)
    {
        return _page?.UserAgentAsync(ct) ?? Task.FromResult(string.Empty);
    }

    /// <summary>Handed back, not closed: the page is the run's, and <see cref="HostTabs.CloseAllAsync"/> closes it.</summary>
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the page, which lets the host stop Chrome once no page is left.</summary>
    public async Task CloseAsync()
    {
        _closed = true;

        if (_page is IPluginBrowserPage page)
        {
            _page = null;

            await page.DisposeAsync();
        }
    }
}
