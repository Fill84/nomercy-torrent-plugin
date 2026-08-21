using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// A control asks for a route this plugin actually serves.
/// </summary>
/// <remarks>
/// <para>
/// The client posts a <c>CallPlugin</c> straight at
/// <c>plugins/{id}/{method}</c> — the method <em>is</em> the path. Every action
/// on every page named itself instead: a button said <c>PauseDownload</c> where
/// the controller answers to <c>downloads/{infoHash}/pause</c>. Nothing any
/// page offered reached anything, and the plugin sat there doing nothing
/// whatever the owner pressed.
/// </para>
/// <para>
/// The tests that covered those controls asserted a constant equalled itself —
/// <c>Assert.Equal(DownloadsView.PauseAction, Called(button))</c> — which is
/// true of any string at all, and was true of every one of these.
/// </para>
/// </remarks>
public class ControlsReachTheirEndpointsTests
{
    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public void EveryControlOnEveryPageAsksForARouteThisPluginServes()
    {
        IReadOnlyList<string> routes = Routes();

        // A reflection walk that found nothing would pass this whole test in
        // silence, which is the one way it could lie.
        Assert.NotEmpty(routes);

        foreach ((string page, string method) in Methods())
        {
            Assert.True(
                routes.Any(route => Reaches(route, method)),
                $"The {page} page has a control asking for '{method}', which this plugin does not "
                + "serve. A CallPlugin method is the path the client posts to. Served: "
                + string.Join(", ", routes));
        }
    }

    /// <remarks>
    /// The walk above proves nothing if no page has a control on it, and every
    /// one of these pages has had one since Sprint 6.
    /// </remarks>
    [Fact]
    public void EveryPageThatShouldOfferSomethingDoes()
    {
        foreach ((string page, PluginView view) in Pages())
        {
            Assert.True(
                Controls(view).Any(),
                $"The {page} page offers the owner nothing to press.");
        }
    }

    /// <summary>Every method any page asks for, with the page that asks.</summary>
    private static IEnumerable<(string Page, string Method)> Methods()
    {
        foreach ((string page, PluginView view) in Pages())
        {
            foreach (PluginComponent control in Controls(view))
            {
                yield return (page, (string)control.Action!.Payload["method"]!);
            }
        }
    }

    private static IEnumerable<PluginComponent> Controls(PluginView view)
    {
        return Rendered.All(view)
            .Where(component => component.Action is { Type: PluginActionType.CallPlugin }
                                && component.Action.Payload.GetValueOrDefault("method") is string);
    }

    /// <summary>Every route the plugin's own controllers answer to.</summary>
    private static IReadOnlyList<string> Routes()
    {
        return
        [
            .. typeof(TorrentDownloaderPlugin).Assembly
                .GetTypes()
                .Where(type => typeof(PluginControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .SelectMany(method => method.GetCustomAttributes<HttpPostAttribute>())
                .Select(attribute => attribute.Template)
                .OfType<string>(),
        ];
    }

    /// <summary>
    /// Whether a method reaches a route, treating a <c>{parameter}</c> segment
    /// as the one thing it is: a segment the caller fills in.
    /// </summary>
    private static bool Reaches(string route, string method)
    {
        string pattern = "^" + string.Join(
            "/",
            route.Split('/').Select(segment =>
                segment.StartsWith('{') && segment.EndsWith('}')
                    ? "[^/]+"
                    : Regex.Escape(segment))) + "$";

        return Regex.IsMatch(method, pattern);
    }

    /// <summary>Every page, rendered with enough on it to carry its controls.</summary>
    private static IEnumerable<(string Page, PluginView View)> Pages()
    {
        yield return ("Downloads", DownloadsView.Render([Download()]));

        yield return ("Queue", QueueView.Render(
            [new(new(41, 3, 6), "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing)]));

        yield return ("Skipped", SkippedView.Render(
            [new(new(41, 3, 6), "Silo S03E06 720p WEB", "LimeTorrents", "720p is below the floor")]));

        yield return ("Settings", SettingsView.Render(new(), [], []));

        yield return ("Dashboard", DashboardView.Render(
            ActivitySnapshot.Empty,
            new(false, null, null)));
    }

    private static DownloadRow Download()
    {
        return new(
            new(Hash, $"magnet:?xt=urn:btih:{Hash}", "Silo.S03E06.1080p.WEB.H264-CAKES", GrabState.Downloading),
            new(
                Hash,
                "Silo.S03E06.1080p.WEB.H264-CAKES",
                TorrentState.Downloading,
                BytesDone: 100,
                BytesTotal: 200,
                DownloadRateBytesPerSecond: 1,
                UploadRateBytesPerSecond: 0,
                Peers: 2,
                Seeds: 1,
                Ratio: 0.1,
                Eta: null,
                Error: null),
            @"C:\downloads");
    }
}
