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
/// One per host and kept open: clearance is issued per host, so two tabs on one
/// host solve the same gate twice and each hold half the answer.
/// </remarks>
public interface IBrowserTabs : IAsyncDisposable
{
    /// <summary>
    /// The tab for <paramref name="host"/>, opening one only if there is none.
    /// </summary>
    /// <returns>Null when there is no browser to open a tab in.</returns>
    Task<IBrowserTab?> ForAsync(string host, CancellationToken ct);
}
