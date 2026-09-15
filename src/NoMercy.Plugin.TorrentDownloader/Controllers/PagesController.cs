using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

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
        + "/{page:regex(^(activity|queue|downloads|history|skipped|sources|settings)$)}";

    /// <summary>Where the web app fetches one show's settings page.</summary>
    public const string PrefetchedShow =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/shows/{id}";

    /// <summary>Where the web app fetches one library's preferences page.</summary>
    public const string PrefetchedLibrary =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/libraries/{id}";

    /// <summary>Where the web app fetches one page of one library's shows.</summary>
    public const string PrefetchedLibraryShows =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/libraries/{id}/shows/{number}";

    /// <summary>Where the web app fetches a page of Skipped after the first.</summary>
    public const string PrefetchedSkippedPage =
        "~/api/v{version:apiVersion}/dashboard/plugins/" + PluginIdentity.IdText + "/skipped/{number}";

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

    [HttpGet(PrefetchedSkippedPage)]
    public Task<IActionResult> SkippedPage(string number, CancellationToken ct)
    {
        return View($"/skipped/{Uri.EscapeDataString(number)}", ct);
    }

    private async Task<IActionResult> View(string route, CancellationToken ct)
    {
        if (LivePlugin.Of(plugins, PluginId, out string refusal) is not TorrentDownloaderPlugin plugin)
        {
            return NotFound(refusal);
        }

        return Data(await plugin.GetViewAsync(new() { Route = route }, ct));
    }
}
