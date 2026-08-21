using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Every page this plugin serves reaches every other one.
/// </summary>
/// <remarks>
/// <para>
/// Only two of the eight are mounted in the server's own navigation: the
/// dashboard, beside the libraries, and settings, under the settings list. The
/// other six were reachable by typing their address and by nothing else — a
/// page with no way in and no way out.
/// </para>
/// <para>
/// Asked for by the owner on 21 August 2026, having found the plugin and then
/// found no way to move about inside it.
/// </para>
/// <para>
/// Asserted through <c>GetViewAsync</c>, which is the only way a page ever
/// reaches anybody. Testing the views one at a time would pass for a page the
/// dispatch forgot to wrap.
/// </para>
/// </remarks>
public class EveryPageCanBeLeftTests
{
    [Fact]
    public async Task EveryPageOffersAWayToEveryOtherPage()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        IReadOnlyList<string> everywhere = [.. plugin.Routes.Routes.Select(route => route.Path)];

        foreach (string route in everywhere)
        {
            PluginView page = await plugin.GetViewAsync(new() { Route = route }, CancellationToken.None);

            IReadOnlyList<string> reachable = [.. Destinations(page)];

            foreach (string destination in everywhere)
            {
                Assert.True(
                    reachable.Contains(destination),
                    $"The page at {route} offers no way to {destination}. "
                    + $"It reaches: {string.Join(", ", reachable)}");
            }
        }
    }

    /// <remarks>
    /// Named for where it goes. A row of eight identical buttons is a row of
    /// eight guesses.
    /// </remarks>
    [Fact]
    public async Task EveryWayOutSaysWhereItGoes()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        PluginView page = await plugin.GetViewAsync(
            new() { Route = Pages.DashboardRoute },
            CancellationToken.None);

        foreach (PluginRoute route in plugin.Routes.Routes)
        {
            PluginComponent link = Rendered.ById(page, $"nav-{route.Name}");

            Assert.Equal(route.Label, link.Props.GetValueOrDefault("ariaLabel"));
        }
    }

    /// <summary>Where a page's navigation can take the reader.</summary>
    private static IEnumerable<string> Destinations(PluginView view)
    {
        return Rendered.All(view)
            .Select(component => component.Action)
            .OfType<PluginActionIntent>()
            .Where(action => action.Type == PluginActionType.Navigate)
            .Select(action => action.Payload.GetValueOrDefault("route"))
            .OfType<string>();
    }
}
