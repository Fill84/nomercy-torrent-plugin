using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

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
