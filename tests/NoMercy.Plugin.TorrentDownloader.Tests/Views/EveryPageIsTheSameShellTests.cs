using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Every page of this plugin asks the client for the same shell.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard route is the one that was always right, and the client draws
/// it at full width. Every other page carries the same bar of eight tabs, so a
/// page that asks for a different shell moves the bar and everything under it —
/// the content jumps on every visit, and going back jumps it again.
/// </para>
/// <para>
/// <strong>This has been fixed once and undone once.</strong> On 11 August 2026
/// the pages were made identical, deliberately, because
/// <c>PluginLayout.Form</c> is a 40rem column and eight tabs do not fit in it.
/// Three days later S1-04 gave Shows and Queue <c>ListDetail</c> and gave
/// Settings its <c>Form</c> back, and four more pages followed on 19 August —
/// so the Shows page shipped as a squeezed half-width column with its last
/// number cut off and a dead pane beside it, and it stayed that way through a
/// release.
/// </para>
/// <para>
/// Nothing caught it, because each page's layout was only ever asserted against
/// itself. This asserts them against each other, which is where the fault
/// actually lives: not in any one page, but in two of them disagreeing.
/// </para>
/// <para>
/// <strong>The shell they all ask for is <c>Wide</c>, and was
/// <c>Standard</c>.</strong> Standard is a sixty-four rem measure, which is
/// right for a page of cards and wrong for every page here: these are tables,
/// and a table held to a measure loses its last columns behind a scrollbar. The
/// owner had one across the Downloads page on 31 August 2026. Wide is the shape
/// the client already has for exactly this — "a table wants every column it
/// declared and a dashboard wants the room it has" — and moving them together
/// is what keeps the bar of tabs from jumping.
/// </para>
/// </remarks>
public class EveryPageIsTheSameShellTests
{
    /// <remarks>
    /// The route table is what the client reads to build the shell before a
    /// view is ever fetched, so a wrong layout here moves the page before
    /// anything is drawn in it.
    /// </remarks>
    [Fact]
    public void EveryRouteAsksForTheDashboardsShell()
    {
        PluginRoute dashboard = Pages.Routes.Routes.Single(route => route.Name == "dashboard");

        Assert.Equal(PluginLayout.Wide, dashboard.Layout);

        foreach (PluginRoute route in Pages.Routes.Routes)
        {
            Assert.True(
                route.Layout == dashboard.Layout,
                $"{route.Path} asks for {route.Layout} where the dashboard asks for {dashboard.Layout}. "
                + "Every page carries the same tabs and has to be the same width.");
        }
    }

    /// <remarks>
    /// And the views themselves, because a view carries a layout of its own and
    /// the one it sends is the one that wins. A route table that agrees with
    /// itself while the views disagree is the same fault one level down.
    /// </remarks>
    [Fact]
    public async Task EveryViewThePluginServesSendsTheSameShell()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        foreach (PluginRoute route in plugin.Routes.Routes)
        {
            PluginView page = await plugin.GetViewAsync(new() { Route = route.Path }, CancellationToken.None);

            Assert.True(
                page.Layout == PluginLayout.Wide,
                $"The view at {route.Path} sends {page.Layout}. Every page of this plugin is "
                + "the width the dashboard is, or the tab bar moves under the owner's cursor.");
        }
    }
}
