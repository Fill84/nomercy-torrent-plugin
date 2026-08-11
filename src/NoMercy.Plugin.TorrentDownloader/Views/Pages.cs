// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The plugin's map of itself: every page it serves, and the bar that walks between them.
///
/// <para>
/// Declared rather than matched inside <c>GetViewAsync</c>, because a route that only
/// exists as a case in a switch is one nothing else can see. The server reads this table
/// and serves it as the plugin's pages; the client registers a named route for each. A page
/// nobody declares falls back to a wildcard that covers the legacy <c>/plugins/{id}/…</c>
/// mount and not the <c>/video/plugins/{id}/…</c> one this plugin actually sits behind - so
/// an undeclared page is not a page, it is the app's 404.
/// </para>
///
/// <para>
/// Every link is built from this table by name, so a path is written once. Rewriting a path
/// moves every tab that leads there.
/// </para>
/// </summary>
public static class Pages
{
    public const string Overview = "overview";
    public const string Downloads = "downloads";
    public const string Queue = "queue";
    public const string History = "history";
    public const string Sources = "sources";
    public const string Skipped = "skipped";
    public const string Settings = "settings";

    /// <summary>
    /// The pages, in the order the tab bar offers them: what is happening, then what is
    /// being worked on, then what happened, then what it is all configured from.
    ///
    /// <para>
    /// Shows is deliberately absent. It needs per-show grouping that does not exist yet, and
    /// a tab named Shows that listed only the few the plugin is leaving alone would mislead
    /// worse than no tab. Until it arrives, that list is on the overview under a heading
    /// that says what it is.
    /// </para>
    /// </summary>
    public static PluginRouteTable Routes { get; } = new(
        new PluginRoute { Path = "/", Name = Overview, Label = "Overview" },
        new PluginRoute { Path = "/downloads", Name = Downloads, Label = "Downloads" },
        new PluginRoute { Path = "/queue", Name = Queue, Label = "Queue" },
        new PluginRoute { Path = "/history", Name = History, Label = "History" },
        new PluginRoute { Path = "/sources", Name = Sources, Label = "Sources" },
        new PluginRoute { Path = "/skipped", Name = Skipped, Label = "Skipped" },

        // A form is a shape a remote control handles badly, and the client draws the shell
        // it is told to. The server stamps this onto the view for us, so the settings page
        // itself says nothing about layout.
        new PluginRoute { Path = "/settings", Name = Settings, Label = "Settings", Layout = PluginLayout.Form });

    /// <summary>
    /// A page: its own name, the bar, then whatever it is about.
    ///
    /// <para>
    /// Every view goes through here rather than building its own root, so the bar cannot be
    /// forgotten on one page - which on a plugin whose sidebar section draws nothing would
    /// leave that page with no way out but the browser's back button.
    /// </para>
    /// </summary>
    public static PluginView Page(string name, int refreshSeconds, params PluginComponent[] body) =>
        PluginViews.Declarative(
            refreshSeconds,
            Ui.Container(
                $"{name}-root",
                [
                    Ui.Text($"{name}-heading", LabelOf(name), "title"),
                    Tabs(name),
                    .. body,
                ]));

    /// <summary>
    /// One entry per page, with the one you are on drawn as a badge rather than a button.
    ///
    /// <para>
    /// Not a variant: <c>PluginButton</c> draws everything but "danger" as the same grey
    /// button, so a variant cannot say which tab is current. A badge is a pill in the theme's
    /// own palette, and reads as a label rather than as something to press - which is what
    /// the page you are already on is.
    /// </para>
    /// </summary>
    public static PluginComponent Tabs(string current) =>
        Ui.Row(
            "tabs",
            Routes.Routes.Select(route => route.Name == current
                ? Ui.Badge($"tab-{route.Name}", route.Label!, PluginBadgeVariant.Info)
                : Ui.Button($"tab-{route.Name}", route.Label!, Routes.GoTo(route.Name))));

    /// <summary>
    /// What a page is called. One spelling, so a tab and the heading it leads to cannot
    /// disagree about which page the viewer is looking at.
    /// </summary>
    public static string LabelOf(string name) =>
        Routes.Routes.FirstOrDefault(route => route.Name == name)?.Label ?? name;
}
