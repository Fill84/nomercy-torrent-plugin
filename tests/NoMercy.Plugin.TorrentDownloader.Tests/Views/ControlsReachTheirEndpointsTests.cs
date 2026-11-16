using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

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

    /// <summary>
    /// A control asks for one path segment, because that is all the client sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since 12 October 2026 the web app puts a <c>CallPlugin</c> method into the path only when
    /// it is a single safe segment (<c>nomercy-app-web</c>, <c>src/lib/plugin/pluginPath.ts</c>,
    /// web #149): a slash or a dot segment could walk out of <c>plugins/{id}/</c> and reach
    /// another server route with the viewer's token. Anything else it drops — no request, no
    /// error on the page. Every route here was written with slashes, so Save, Find, the show
    /// switches, the show and library settings and pause, resume and cancel all did nothing
    /// from that day on, and only Run, Stop and adding a torrent by hand still reached the
    /// plugin.
    /// </para>
    /// <para>
    /// The pattern is the client's own, spelled out: <c>\w</c> in JavaScript is ASCII only, so
    /// it is written as the ASCII range rather than trusting .NET's wider one.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryControlAsksForOneSegmentTheClientWillSend()
    {
        foreach ((string page, string method) in Methods())
        {
            Assert.True(
                OneSafeSegment(method),
                $"The {page} page has a control asking for '{method}'. The web app sends a "
                + "CallPlugin method only when it is one path segment of [A-Za-z0-9_.~-], and drops "
                + "anything else without a request or a word on the page.");
        }
    }

    private static bool OneSafeSegment(string method)
    {
        return Regex.IsMatch(method, "^[A-Za-z0-9_.~-]+$") && method is not "." and not "..";
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
            // A button in a table's actions cell counts: the Queue page's are all in its rows.
            Assert.True(
                Controls(view).Any() || Buttons(view).Any(),
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

            // And the buttons in a table's actions cell, which are a row's props rather than
            // components: the overview's switch is one, and a switch posting to nothing is a row that
            // cannot be switched on.
            foreach (PluginTableAction button in Buttons(view))
            {
                yield return (page, (string)button.Action!.Payload["method"]!);
            }
        }
    }

    /// <summary>The buttons in every table row's actions cell that call the plugin.</summary>
    private static IEnumerable<PluginTableAction> Buttons(PluginView view)
    {
        return Rendered.All(view)
            .SelectMany(component => component.Props.Values)
            .OfType<IReadOnlyList<PluginTableAction>>()
            .SelectMany(buttons => buttons)
            .Where(button => button.Action is { Type: PluginActionType.CallPlugin });
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
    /// Whether a method reaches a route, treating a <c>{parameter}</c> as the
    /// one thing it is: text the caller fills in, within its own segment.
    /// </summary>
    /// <remarks>
    /// ASP.NET's own matcher, not a pattern written here: a parameter can share a segment with
    /// literal text — <c>shows-{id:int}-on</c> — and how the router splits such a segment is the
    /// router's business. The server puts these templates under <c>api/v{version}/plugins/{id}</c>
    /// with ordinary attribute routing (media-server <c>PluginRouteConvention</c>), so what
    /// matches here matches there.
    /// </remarks>
    private static bool Reaches(string route, string method)
    {
        TemplateMatcher matcher = new(TemplateParser.Parse(route), new RouteValueDictionary());

        return matcher.TryMatch("/" + method, new RouteValueDictionary());
    }

    /// <summary>Every page, rendered with enough on it to carry its controls.</summary>
    private static IEnumerable<(string Page, PluginView View)> Pages()
    {
        yield return ("Downloads", DownloadsView.Render([Download()]));

        yield return ("Queue", QueueView.Render(
            [new(new(41, 3, 6), "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing)]));

        yield return ("Settings", SettingsView.Render(new(), [], []));

        // With its switches drawn, which is the one control it has: they save through the same
        // action as the Settings page, and were left out of this walk until now.
        yield return ("Sources", SourcesView.Render(
            [],
            DateTimeOffset.UnixEpoch,
            [new("PreDB", "rss", "https://predb.me/?rss=1")],
            new(),
            advanced: true));

        yield return ("Activity", ActivityView.Render(
            ActivitySnapshot.Empty,
            new(false, null, null)));

        const string library = "01HQ5W4AVF30N10RT6XCF6AJHM";
        Show silo = new(41, "Silo", 2023, library, "Series", LibraryKind.Television, "/Silo.(2023)");

        yield return ("Overview", OverviewView.Render(
            new(false, null, null),
            [new(new(library, "Series", LibraryKind.Television), new(library), [new(silo, new(41), EffectiveSettings.Of(new(41), new(library)), null)])]));

        yield return ("Show settings", ShowSettingsView.Render(silo, new(41), new(library)));

        yield return ("Library preferences", LibraryPreferencesView.Render(new(library, "Series", LibraryKind.Television), new(library)));
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
                Peers: 2,
                Seeds: 1,
                Ratio: 0.1,
                Eta: null,
                Error: null));
    }
}
