using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;

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
        PluginRoute dashboard = Pages.Routes.Routes.Single(route => route.Name == "overview");

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
            PluginView page = await plugin.GetViewAsync(Requests.View(SamplePaths.Of(route)), CancellationToken.None);

            Assert.True(
                page.Layout == PluginLayout.Wide,
                $"The view at {route.Path} sends {page.Layout}. Every page of this plugin is "
                + "the width the dashboard is, or the tab bar moves under the owner's cursor.");
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>Every button on every page sits in a row, and this is why.</strong>
    /// <c>PluginButton</c> is <c>inline-flex</c>: it asks to be exactly as wide
    /// as its words. But a page's component column and a <c>PluginDetail</c>
    /// body are both <c>flex-col</c>, and a flex column stretches its children
    /// across the full width — so a button put straight into either draws as a
    /// strip with its words at the far left, which reads as a section heading
    /// rather than something to press.
    /// </para>
    /// <para>
    /// Seen on the owner's server on 12 September 2026: Show advanced, Run now
    /// and Stop were all bars across the settings page. The dashboard's Run,
    /// the tab bar and the Skipped paging were already in rows and looked
    /// right, which is what made the settings page look broken next to them.
    /// </para>
    /// <para>
    /// The client is not at fault and cannot be: a plugin chooses the container
    /// and the container decides the width. So this walks every route the
    /// plugin serves rather than the page that happened to be wrong, because
    /// the next one will be a different page.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoButtonOnAnyPageIsStretchedAcrossIt()
    {
        using TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        foreach (PluginRoute route in plugin.Routes.Routes)
        {
            PluginView page = await plugin.GetViewAsync(Requests.View(SamplePaths.Of(route)), CancellationToken.None);

            string[] inRows =
            [
                .. Walk(page.Components)
                    .Where(one => one.Component == Ui.RowComponent)
                    .SelectMany(row => Walk(row.Items))
                    .Select(one => one.Id),
            ];

            foreach (PluginComponent button in Walk(page.Components)
                         .Where(one => one.Component == Ui.ButtonComponent))
            {
                Assert.True(
                    inRows.Contains(button.Id),
                    $"'{button.Id}' on {route.Path} is not inside a row, so the flex column it sits "
                    + "in will stretch it across the page and it will not read as a button.");
            }
        }
    }

    /// <summary>Every component of a page, however deeply it is nested.</summary>
    private static IEnumerable<PluginComponent> Walk(IReadOnlyList<PluginComponent>? components)
    {
        foreach (PluginComponent component in components ?? [])
        {
            yield return component;

            foreach (PluginComponent inner in Walk(component.Items))
            {
                yield return inner;
            }
        }
    }
}
