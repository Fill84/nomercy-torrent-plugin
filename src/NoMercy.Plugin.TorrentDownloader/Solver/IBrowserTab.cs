namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// One tab, and the handful of things this plugin asks of one.
/// </summary>
/// <remarks>
/// A seam over the driver, kept as small as the policy needs. Everything worth
/// arguing about — how long to wait, when to reload, when to give up, whether
/// the body is the body — lives above this and is testable without a browser.
/// What is below it cannot be tested here at all, so the less of it there is,
/// the better.
/// </remarks>
public interface IBrowserTab : IAsyncDisposable
{
    /// <summary>Navigates, and waits for the page to settle.</summary>
    Task GoToAsync(Uri url, CancellationToken ct);

    Task ReloadAsync(CancellationToken ct);

    /// <summary>The rendered document, which for a JSON endpoint is Chrome's viewer.</summary>
    Task<string> ContentAsync(CancellationToken ct);

    /// <summary>What the document says it is: <c>text/html</c>, <c>application/json</c>, and so on.</summary>
    Task<string> ContentTypeAsync(CancellationToken ct);

    /// <summary>
    /// Whether the document is worth reading yet — parsed, with a body that has
    /// something in it.
    /// </summary>
    /// <remarks>
    /// A challenge that has just cleared navigates to the real page, and for a
    /// moment the tab holds a document that is neither: no longer the
    /// interstitial and not yet the site. Reading then gets a head with no body
    /// — measured against 1337x, which answered 876 bytes of stylesheet links
    /// and nothing else.
    ///
    /// Not "has finished loading". Measured against the same page: an indexer
    /// is full of third-party requests that never complete, so waiting for the
    /// load event times out on a page that has been readable for forty seconds.
    /// </remarks>
    Task<bool> IsLoadedAsync(CancellationToken ct);

    /// <summary>
    /// Fetches <paramref name="url"/> from inside the page and answers the text.
    /// </summary>
    /// <remarks>
    /// The body, not a picture of it. A browser asked for a JSON endpoint
    /// renders it in its own viewer, and reading the document returns that
    /// viewer's markup — in 0.3.4 every JSON source silently answered an empty
    /// array this way, and an XML feed reported a parse error naming a
    /// <c>meta</c> tag the feed never had.
    /// </remarks>
    Task<string> FetchInPageAsync(Uri url, CancellationToken ct);

    /// <summary>Posts a form from inside the page and answers the text.</summary>
    Task<string> PostInPageAsync(Uri url, string formBody, CancellationToken ct);

    /// <summary>A cookie by name, or null.</summary>
    Task<string?> CookieAsync(string name, CancellationToken ct);

    /// <summary>The user agent this tab is presenting.</summary>
    Task<string> UserAgentAsync(CancellationToken ct);
}

/// <summary>
/// The tabs the browser has, one per host.
/// </summary>
/// <remarks>
/// <para>
/// A tab is opened for one task and closed when that task ends — the caller
/// owns it and disposes it. When the last one closes the browser is stopped,
/// so a plugin with nothing to solve has no Chrome running at all.
/// </para>
/// <para>
/// They used to be kept one per host for the life of the plugin, because
/// clearance is issued per host. It is, but the solver reads that cookie into
/// the clearance store the moment it has it, so closing the tab that earned it
/// loses nothing — and keeping them left sixteen chrome processes running on
/// the owner's machine with the server stopped.
/// </para>
/// </remarks>
public interface IBrowserTabs : IAsyncDisposable
{
    /// <summary>
    /// The tab for <paramref name="host"/>, opening one only if there is none.
    /// </summary>
    /// <returns>Null when there is no browser to open a tab in.</returns>
    Task<IBrowserTab?> ForAsync(string host, CancellationToken ct);
}
