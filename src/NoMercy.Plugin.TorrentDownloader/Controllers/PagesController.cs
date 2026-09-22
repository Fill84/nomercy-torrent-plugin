using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>
/// Every page of the plugin, at the address the web app fetches it by before it
/// draws it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The owner's report of 11 September 2026: every page of the plugin
/// put 404s in the browser's console.</strong> Before the web app draws a page
/// it asks the server for that page's own address —
/// <c>api/v1/dashboard/plugins/{id}/shows</c> for Shows — and nothing answered
/// there, so every page arrived with two errors underneath it. The page itself
/// came through <c>plugins/{id}/view</c> as ever.
/// </para>
/// <para>
/// <strong>An absolute route, on the owner's decision.</strong> The server puts
/// every plugin route under <c>api/v1/plugins/{id}</c>, and that is not where
/// the web app looks. The owner decided the plugin answers where it is asked.
/// The id is this plugin's own and the pages are named one by one, so nothing
/// here can answer for any address but this plugin's seven pages.
/// </para>
/// <para>
/// It answers with the page itself, exactly what <c>plugins/{id}/view</c> hands
/// the same page, so what the web app keeps from it is true.
/// </para>
/// </remarks>
public sealed class PagesController(IPluginManager plugins) : PluginControllerBase
{
    /// <summary>
    /// Where the web app fetches a page of this plugin: the page's own address,
    /// under the API.
    /// </summary>
    public const string Prefetched =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText
        + "/{page:regex(^(activity|queue|downloads|history|sources|settings)$)}";

    /// <summary>Where the web app fetches one show's settings page.</summary>
    public const string PrefetchedShow =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/shows/{id}";

    /// <summary>Where the web app fetches one library's preferences page.</summary>
    public const string PrefetchedLibrary =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/libraries/{id}";

    /// <summary>Where the web app fetches one page of one library's shows.</summary>
    public const string PrefetchedLibraryShows =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/libraries/{id}/shows/{number}";

    [HttpGet(Prefetched)]
    public Task<IActionResult> Page(string page, CancellationToken ct)
    {
        return View("/" + page, ct);
    }

    [HttpGet(PrefetchedShow)]
    public Task<IActionResult> Show(string id, CancellationToken ct)
    {
        return View($"/shows/{Uri.EscapeDataString(id)}", ct);
    }

    [HttpGet(PrefetchedLibrary)]
    public Task<IActionResult> Library(string id, CancellationToken ct)
    {
        return View($"/libraries/{Uri.EscapeDataString(id)}", ct);
    }

    [HttpGet(PrefetchedLibraryShows)]
    public Task<IActionResult> LibraryShows(string id, string number, CancellationToken ct)
    {
        return View($"/libraries/{Uri.EscapeDataString(id)}/shows/{Uri.EscapeDataString(number)}", ct);
    }

    private async Task<IActionResult> View(string route, CancellationToken ct)
    {
        if (LivePlugin.Of(plugins, HttpContext.RequestServices, PluginId, out string refusal) is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(refusal);
        }

        return Data(await plugin.GetViewAsync(new() { Route = route, Caller = Whoever() }, ct));
    }

    /// <summary>
    /// Who is asking, as the server resolved them — or, on these addresses, as the
    /// request's own claims say.
    /// </summary>
    /// <remarks>
    /// A view request on contract 12 carries its caller, and the server writes one
    /// onto every request under the plugin's own route prefix. These addresses
    /// are not under it — they are the ones the web app fetches a page's own
    /// address by, on the owner's decision — so the server leaves nothing there,
    /// and the caller is read off the same claims the server's own view endpoint
    /// reads it off: the name identifier, the role and the name. The pages draw
    /// the same thing whoever asks; what the caller decides is only whether an
    /// owner-only route is admitted, and this plugin marks none.
    /// </remarks>
    private PluginCaller Whoever()
    {
        if (HttpContext.Items[CallerItemKey] is PluginCaller resolved)
        {
            return resolved;
        }

        ClaimsPrincipal user = HttpContext.User;

        UserId id = UserId.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out UserId parsed)
            ? parsed
            : UserId.Empty;

        string name = user.FindFirst("name")?.Value
                      ?? string.Join(
                          ' ',
                          new[] { user.FindFirst(ClaimTypes.GivenName)?.Value, user.FindFirst(ClaimTypes.Surname)?.Value }
                              .Where(part => !string.IsNullOrWhiteSpace(part)));

        return new(
            id,
            name,
            string.Equals(user.FindFirst(ClaimTypes.Role)?.Value, "owner", StringComparison.OrdinalIgnoreCase)
                ? PluginRole.Owner
                : PluginRole.Member,
            PluginAccess.Owned,
            Request.Headers.AcceptLanguage.ToString() is { Length: > 0 } locale ? locale : "en",
            PluginSurface.Web);
    }
}
