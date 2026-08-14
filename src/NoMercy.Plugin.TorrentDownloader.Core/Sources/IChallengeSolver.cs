namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>
/// A cleared challenge: the cookie, and the user agent it was issued to.
/// </summary>
/// <remarks>
/// The user agent travels with it because clearance is issued to one. Sending
/// the cookie with a different user agent is a 403 that reads like the site
/// changing its mind.
/// </remarks>
public sealed record Clearance(string Cookie, string UserAgent);

/// <summary>Meeting a managed challenge, in a real browser.</summary>
public interface IChallengeSolver
{
    /// <summary>
    /// Clears the challenge on <paramref name="url"/>'s host, or answers null
    /// when it will not clear.
    /// </summary>
    Task<Clearance?> SolveAsync(Uri url, CancellationToken ct);
}

/// <summary>Handing over a page the browser has already loaded.</summary>
/// <remarks>
/// Separate from <see cref="IChallengeSolver"/> because a chain that hides the
/// capability makes the fetch ask "can you hand me the page" of something that
/// can, and be told no. Some sites bind clearance to the TLS handshake, so
/// replaying the cookie from an <c>HttpClient</c> is refused anyway — where the
/// solver can hand over the page itself, that is preferred.
/// </remarks>
public interface IPageSource
{
    /// <summary>
    /// The body of <paramref name="url"/>, or null when this source cannot get
    /// it.
    /// </summary>
    /// <remarks>
    /// The body, not a picture of it: a browser asked for a JSON endpoint
    /// renders it in its own viewer, and reading the DOM returns that viewer's
    /// markup. In 0.3.4 every JSON source silently returned an empty array that
    /// way. Whoever implements this re-fetches inside the page.
    /// </remarks>
    Task<string?> GetPageAsync(Uri url, CancellationToken ct);
}
