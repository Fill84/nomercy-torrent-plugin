// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The plugin's map of itself, and the bar that walks it.
///
/// <para>
/// These are the assertions that would have caught the reason this slice exists: a tab
/// pointing at a page the plugin never declared reaches the app's 404, because the wildcard
/// that would otherwise catch it only covers the legacy mount.
/// </para>
/// </summary>
public class PagesTests
{
    private static IEnumerable<PluginComponent> TabsOf(string current) =>
        Pages.Tabs(current).Items;

    [Fact]
    public void Routes_ResolveTheirOwnPaths()
    {
        foreach (PluginRoute route in Pages.Routes.Routes)
        {
            Pages.Routes.Resolve(route.Path)?.Route.Name.Should().Be(route.Name);
        }
    }

    // The whole point of the table. A tab is built from it, so a tab can only ever point at
    // a page that exists - but only if the bar is built from the table rather than from a
    // second list beside it.
    [Fact]
    public void Tabs_OnlyPointAtPagesTheTableDeclares()
    {
        foreach (PluginComponent tab in TabsOf(Pages.Overview))
        {
            if (tab.Action is null)
                continue;

            string route = tab.Action.Payload[PluginNavigation.RouteKey] as string ?? "";

            Pages.Routes.Resolve(route).Should().NotBeNull($"the tab for '{tab.Id}' has to lead somewhere");
        }
    }

    // Relative, so the bar works under /video, under /dashboard and on a television without
    // the plugin ever writing a mount prefix down.
    [Fact]
    public void Tabs_NavigateRelativeToThePlugin()
    {
        foreach (PluginComponent tab in TabsOf(Pages.Queue).Where(tab => tab.Action is not null))
        {
            tab.Action!.Type.Should().Be(PluginActionType.Navigate);
            tab.Action.Payload[PluginNavigation.RelativeKey].Should().Be(true);
        }
    }

    [Fact]
    public void Tabs_OfferEveryPageReachedFromTheBar()
    {
        IEnumerable<string> labels = TabsOf(Pages.Overview).SelectMany(PluginNodes.Words);

        labels.Should().BeEquivalentTo(["Overview", "Shows", "Downloads", "Queue", "History", "Sources", "Skipped", "Settings"]);
    }

    /// <summary>
    /// A page reached from a list is declared but not offered in the bar.
    ///
    /// <para>
    /// The table has to carry it - an undeclared page is one the client registers no route
    /// for - but a bar built from the whole table would grow an entry per configured source.
    /// </para>
    /// </summary>
    [Fact]
    public void Routes_DeclareASourcesOwnPageWithoutPuttingItInTheBar()
    {
        Pages.Routes.Resolve("/sources/2")?.Route.Name.Should().Be(Pages.Source);
        Pages.Routes.Resolve("/sources/2")?.Param("index").Should().Be("2");

        TabsOf(Pages.Overview).Should().NotContain(tab => tab.Id == $"tab-{Pages.Source}");
    }

    // Literal segments win over parameters, or /sources itself would be read as a source
    // called "sources".
    [Fact]
    public void Routes_KeepTheListItselfAheadOfOneSource()
    {
        Pages.Routes.Resolve("/sources")?.Route.Name.Should().Be(Pages.Sources);
    }

    /// <summary>
    /// PluginButton draws every variant but "danger" as the same grey button, so a variant
    /// cannot say which tab you are on. A badge can, and reads as a label rather than as
    /// something to press - which is what the page you are already on should look like.
    /// </summary>
    [Fact]
    public void Tabs_DrawTheCurrentPageAsSomethingOtherThanAButton()
    {
        List<PluginComponent> tabs = [.. TabsOf(Pages.History)];
        PluginComponent current = tabs.Single(tab => PluginNodes.Words(tab).Contains("History"));

        current.Component.Should().Be(Ui.BadgeComponent);
        current.Action.Should().BeNull("the page you are on is not somewhere to go");

        tabs.Where(tab => !ReferenceEquals(tab, current))
            .Should().OnlyContain(tab => tab.Component == Ui.ButtonComponent);
    }

    [Fact]
    public void Page_LeadsWithItsHeadingAndThenTheTabs()
    {
        PluginView view = Pages.Page(Pages.Queue, 0, Ui.Text("body", "anything"));

        List<PluginComponent> children = view.Components!.Single().Items;

        PluginNodes.Words(children[0]).Should().Contain("Queue");
        children[1].Should().BeSameAs(children[1]);
        PluginNodes.Words(children[1]).Should().Contain("Overview");
    }

    [Fact]
    public void Page_DrawsOnlyTagsAClientKnows()
    {
        PluginView view = Pages.Page(Pages.Overview, 0);

        PluginNodes.All(view).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    // Every page is headed by its own name, and the name a tab shows is the same one. Two
    // spellings of one page is how a viewer concludes they are on a different one.
    [Fact]
    public void Page_IsHeadedByTheSameLabelItsTabCarries()
    {
        foreach (PluginRoute route in Pages.Routes.Routes)
        {
            PluginView view = Pages.Page(route.Name, 0);

            PluginNodes.Words(view.Components!.Single().Items[0]).Should().Contain(route.Label);
        }
    }

    /// <summary>
    /// A settings page declares zero: nothing on it changes on its own. The pages that watch
    /// something moving cannot, or a progress bar only advances when the viewer reloads.
    /// </summary>
    [Fact]
    public void Page_KeepsTheRefreshItWasGiven()
    {
        Pages.Page(Pages.Downloads, 30).RefreshInterval.Should().Be(30);
        Pages.Page(Pages.Settings, 0).RefreshInterval.Should().Be(0);
    }
}
