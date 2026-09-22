using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// A controller as the host builds one: on a request, on a route.
/// </summary>
/// <remarks>
/// The route carries which plugin was asked for, and it is the only way a
/// controller knows: the convention writes <c>pluginId</c> from the assembly
/// the controller came from, so a caller cannot lie about it. A controller
/// built with no context at all reads <c>RouteData</c> off nothing and throws
/// before it does anything else.
/// </remarks>
public static class Requests
{
    /// <summary>A page asked for as the server asks for one: on a route, by the owner.</summary>
    /// <remarks>
    /// Contract 12 makes every view request carry who is asking. The owner, because the pages draw the same
    /// thing whoever asks and this plugin marks no route owner-only; what the caller decides here is nothing.
    /// </remarks>
    public static PluginViewRequest View(string route)
    {
        return new()
        {
            Route = route,
            Caller = new PluginCaller(
                UserId.Parse(PluginIdentity.IdText),
                "the owner",
                PluginRole.Owner,
                PluginAccess.Owned,
                "en",
                PluginSurface.Web),
        };
    }

    public static T On<T>(this T controller, Ulid pluginId)
        where T : ControllerBase
    {
        RouteData route = new();
        route.Values["pluginId"] = pluginId.ToString();

        controller.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext(),
            RouteData = route,
            ActionDescriptor = new ControllerActionDescriptor(),
        };

        return controller;
    }
}
