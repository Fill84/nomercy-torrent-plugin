using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;

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

        // A page with a parameter in its path - one show's settings, one library's page of shows - is
        // left like any other, but it is not a place a tab can point at.
        IReadOnlyList<string> everywhere = [.. Pages.Navigable.Select(route => route.Path)];

        foreach (string route in plugin.Routes.Routes.Select(SamplePaths.Of))
        {
            PluginView page = await plugin.GetViewAsync(Requests.View(route), CancellationToken.None);

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
            Requests.View(Pages.OverviewRoute),
            CancellationToken.None);

        foreach (PluginRoute route in Pages.Navigable)
        {
            PluginComponent link = Rendered.ById(page, $"nav-{route.Name}");

            Assert.Equal(route.Label, link.Props.GetValueOrDefault("label"));
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
